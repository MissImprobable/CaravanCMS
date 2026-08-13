import { Hono } from "hono";
import type { Env } from "../env";
import type { DocumentRow, CaravanRow } from "../lib/rows";
import { toDocumentDto } from "../lib/mappers";
import { getMimeType } from "../lib/mime";
import { isResizableImage, resizeImage } from "../lib/imageResize";
import { toIso } from "../lib/dates";

const app = new Hono<{ Bindings: Env }>();

/** Port of DocumentsController.GetAll — optional rego/documentType filters. */
app.get("/", async (c) => {
  const rego = c.req.query("rego");
  const documentType = c.req.query("documentType");

  const clauses: string[] = [];
  const params: string[] = [];
  if (rego) {
    clauses.push("RegistrationNumber = ?");
    params.push(rego);
  }
  if (documentType) {
    clauses.push("DocumentType = ?");
    params.push(documentType);
  }
  const where = clauses.length ? `WHERE ${clauses.join(" AND ")}` : "";

  const { results } = await c.env.DB.prepare(
    `SELECT * FROM Documents ${where} ORDER BY COALESCE(UploadedDate, CreatedAt) DESC`,
  )
    .bind(...params)
    .all<DocumentRow>();

  return c.json(results.map(toDocumentDto));
});

/**
 * Port of DocumentsController.Download — the old version streamed from local
 * disk (System.IO.File.OpenRead); this streams from R2 via the document's
 * R2Key instead. A document not yet migrated into R2 (R2Key still null)
 * returns 410 Gone with an explanatory message, same status code the old API
 * used for "file no longer exists on disk", since the practical meaning to
 * the caller is identical — the bytes aren't available at this location.
 */
app.get("/:id/download", async (c) => {
  const id = Number(c.req.param("id"));
  const doc = await c.env.DB.prepare("SELECT * FROM Documents WHERE Id = ?").bind(id).first<DocumentRow>();

  if (!doc) {
    return c.json({ error: `Document ${id} not found.` }, 404);
  }
  if (!doc.R2Key) {
    return c.json({ error: `Document ${id} has not been migrated into storage yet.` }, 410);
  }

  const object = await c.env.DOCUMENTS.get(doc.R2Key);
  if (!object) {
    return c.json({ error: `File no longer exists in storage for document ${id}.` }, 410);
  }

  const contentType = doc.MimeType ?? getMimeType(doc.FileName);
  return new Response(object.body, {
    headers: {
      "Content-Type": contentType,
      "Content-Disposition": `attachment; filename="${doc.FileName}"`,
    },
  });
});

/**
 * Port of DocumentsController.Create — REPLACES the old "link a file already
 * on local disk" model (Workers has no local filesystem access). Accepts a
 * direct multipart upload: the file bytes go straight into R2, and the
 * Document row is created in the same request. See the migration plan for
 * why this is a deliberate, visible workflow change for Admin staff.
 */
app.post("/", async (c) => {
  const form = await c.req.formData();
  const file = form.get("file");
  const registrationNumber = form.get("registrationNumber")?.toString();
  const documentType = form.get("documentType")?.toString() ?? "Document";
  const category = form.get("category")?.toString() ?? null;
  const notes = form.get("notes")?.toString() ?? null;
  const linkMethod = form.get("linkMethod")?.toString() ?? "Manual";
  const alreadyResized = form.get("alreadyResized")?.toString() === "true";
  // Callers that know the original source location (Admin's document sync) send it so re-scans
  // can tell "already uploaded" apart from "genuinely new" by comparing against this — without it,
  // every restart re-uploads everything, since there'd be nothing real to dedupe against.
  const sourceFilePath = form.get("filePath")?.toString() || null;

  if (!registrationNumber) {
    return c.json({ error: "registrationNumber is required." }, 400);
  }
  if (!(file instanceof File)) {
    return c.json({ error: "file is required (multipart/form-data)." }, 400);
  }

  const caravanExists = await c.env.DB.prepare("SELECT 1 FROM Caravans WHERE RegistrationNumber = ?")
    .bind(registrationNumber)
    .first<CaravanRow>();
  if (!caravanExists) {
    return c.json({ error: `Caravan ${registrationNumber} not found.` }, 404);
  }

  // Server-side dedup backstop — Admin's own _knownPaths cache is the primary guard against
  // re-uploading a file it already synced, but that cache is per-process and rebuilt only at
  // startup, so it can't see uploads another concurrently-running Admin instance just made. This
  // check closes that gap: if a row already exists for this exact (registrationNumber, filePath),
  // hand back the existing row instead of creating (and re-uploading the bytes for) a duplicate.
  // Only meaningful when a real sourceFilePath was sent — without one there's no stable identity
  // to dedupe against, so every such call still creates a new row, same as before.
  if (sourceFilePath) {
    const existing = await c.env.DB.prepare(
      "SELECT * FROM Documents WHERE RegistrationNumber = ? AND FilePath = ?",
    )
      .bind(registrationNumber, sourceFilePath)
      .first<DocumentRow>();
    if (existing) {
      return c.json(toDocumentDto(existing), 200);
    }
  }

  let fileName = file.name;
  let mimeType = file.type || getMimeType(fileName);
  const now = new Date().toISOString();

  // Photos get resized down to a sane max dimension (2000px longest edge, 85% JPEG) before
  // ever reaching R2 — the office's OneDrive-synced camera photos are often multi-MB originals
  // that don't need to be that large for service/reference documentation. Runs in-Worker via
  // Photon (WASM), no separate Cloudflare Images product needed. Non-image files (PDFs etc.)
  // pass through untouched. See CaravanCMS.Worker/scripts/migrate-files.ts for the same
  // resizing applied to the historical backlog.
  //
  // Callers that already resized client-side (Admin's document sync — see ImageResizeService.cs)
  // set alreadyResized=true to skip this entirely: WASM image decode/resize/encode is CPU-heavy
  // enough to trip the free tier's 10ms-per-request CPU limit (Cloudflare error 1102), and
  // repeatedly hitting that limit is the most likely cause of @cf-wasm/photon's WASM state
  // getting corrupted for an isolate (see the catch block below) — Cloudflare forcibly
  // interrupting a WASM call mid-execution for going over budget would plausibly leave its
  // internal ownership tracking inconsistent for every later call reusing that isolate. Skipping
  // the redundant resize avoids burning CPU on an already-optimized image and avoids the bug.
  let bodyBytes: Uint8Array | ReadableStream = file.stream();
  if (isResizableImage(mimeType) && !alreadyResized) {
    const original = new Uint8Array(await file.arrayBuffer());
    try {
      const resized = resizeImage(original);
      bodyBytes = resized.bytes;
      mimeType = resized.contentType;
      fileName = fileName.replace(/\.[^.]+$/, "") + ".jpg";
    } catch (err) {
      // @cf-wasm/photon has a known WASM lifecycle bug under Workers' isolate reuse
      // ("attempted to take ownership of Rust value while it was borrowed") that can make
      // every resize call fail once a given isolate hits it — not just the failing file.
      // Uploading the original unresized rather than hard-failing keeps the actual goal
      // (get the document into R2, linked) working even when resize itself is broken.
      console.error(`Resize failed for ${fileName}, uploading original unresized:`, err);
    }
  }

  // Insert first to get the Document.Id, then key the R2 object off it —
  // keeps keys stable and traceable back to their row.
  const insertResult = await c.env.DB.prepare(
    `INSERT INTO Documents (RegistrationNumber, DocumentType, Category, FilePath, FileName, UploadedDate, IsLocalPath, MimeType, Notes, LinkMethod, CreatedAt)
     VALUES (?, ?, ?, ?, ?, ?, 0, ?, ?, ?, ?)`,
  )
    .bind(registrationNumber, documentType, category, sourceFilePath ?? `r2://${registrationNumber}/${fileName}`, fileName, now, mimeType, notes, linkMethod, now)
    .run();

  const id = insertResult.meta.last_row_id;
  const r2Key = `documents/${registrationNumber}/${id}-${fileName}`;

  await c.env.DOCUMENTS.put(r2Key, bodyBytes, {
    httpMetadata: { contentType: mimeType },
    customMetadata: { documentType, linkMethod, registrationNumber },
  });
  await c.env.DB.prepare("UPDATE Documents SET R2Key = ?, FileName = ?, MimeType = ? WHERE Id = ?")
    .bind(r2Key, fileName, mimeType, id)
    .run();

  const doc = await c.env.DB.prepare("SELECT * FROM Documents WHERE Id = ?").bind(id).first<DocumentRow>();
  return c.json(toDocumentDto(doc!), 201);
});

/** Port of DocumentsController.GetInferredReport. */
app.get("/inferred-report", async (c) => {
  const { results } = await c.env.DB.prepare(
    `SELECT d.*, ca.Year AS CaravanYear, ca.Make AS CaravanMake, ca.Model AS CaravanModel
     FROM Documents d
     LEFT JOIN Caravans ca ON ca.RegistrationNumber = d.RegistrationNumber
     WHERE d.LinkMethod IN ('SameFolderInference', 'FuzzyMakeModel', 'RegInFolderPath', 'VinInFolderPath')
     ORDER BY d.LinkMethod, d.RegistrationNumber, d.FileName`,
  ).all<DocumentRow & { CaravanYear: number | null; CaravanMake: string | null; CaravanModel: string | null }>();

  const items = results.map((d) => ({
    documentId: d.Id,
    fileName: d.FileName,
    filePath: d.FilePath,
    registrationNumber: d.RegistrationNumber,
    caravanDescription: d.CaravanMake
      ? `${d.CaravanYear ?? ""} ${d.CaravanMake} ${d.CaravanModel ?? ""}`.trim()
      : null,
    documentType: d.DocumentType,
    linkMethod: d.LinkMethod,
    createdAt: toIso(d.CreatedAt),
  }));

  return c.json({ totalCount: items.length, items });
});

/** Port of DocumentsController.Delete — unlinks only; does not delete the R2 object. */
app.delete("/:id", async (c) => {
  const id = Number(c.req.param("id"));
  const doc = await c.env.DB.prepare("SELECT Id FROM Documents WHERE Id = ?").bind(id).first();
  if (!doc) {
    return c.json({ error: `Document ${id} not found.` }, 404);
  }

  await c.env.DB.prepare("DELETE FROM Documents WHERE Id = ?").bind(id).run();
  return c.body(null, 204);
});

export default app;
