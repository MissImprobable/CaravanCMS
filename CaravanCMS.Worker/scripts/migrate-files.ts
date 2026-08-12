/**
 * One-time migration of the local Caravan History document archive into R2.
 *
 * - Only .pdf/.jpg/.jpeg/.png/.tiff/.tif/.bmp are considered (matches what
 *   FileScanner.cs ever indexed — .docx/.zip/.mp4/etc in the same folders
 *   were never tracked by the app and are intentionally left alone).
 * - Any file inside a folder literally named "resized" is skipped as a
 *   SOURCE — per the decision to always regenerate resized images fresh
 *   from the originals rather than trust ad-hoc manual resizes. Those old
 *   "resized" folders are deleted at the end of a --commit run.
 * - Images are resized (2000px longest edge, 85% JPEG) via the same Photon
 *   logic the live Worker uses for new uploads — see scripts/lib/imageResize.ts.
 * - Files already linked in the source database (by exact FilePath) keep
 *   their existing Document.Id/RegistrationNumber/DocumentType/LinkMethod —
 *   only R2Key/FileName/MimeType get updated.
 * - Unlinked files are matched via the ported FuzzyMatcher; only matches
 *   with confidence >= 0.5 are migrated as new Document rows. Anything
 *   below that is skipped and reported, not guessed.
 *
 * Usage:
 *   npx tsx scripts/migrate-files.ts <archive-root> <source.db>            # dry run (default)
 *   npx tsx scripts/migrate-files.ts <archive-root> <source.db> --commit   # actually upload + write D1 + delete old resized/ folders
 */
import Database from "better-sqlite3";
import * as fs from "node:fs";
import * as path from "node:path";
import { execFileSync } from "node:child_process";
import { fileURLToPath } from "node:url";
import { matchFile, buildFolderRegoIndex, type CaravanForMatching } from "./lib/fuzzyMatcher";
import { resizeImageBytes } from "./lib/imageResize";

const __dirname = path.dirname(fileURLToPath(import.meta.url));

const SUPPORTED_EXTENSIONS = new Set([".pdf", ".jpg", ".jpeg", ".png", ".tiff", ".tif", ".bmp"]);
const IMAGE_EXTENSIONS = new Set([".jpg", ".jpeg", ".png", ".tiff", ".tif", ".bmp"]);
const CONCURRENCY = 6;
const BUCKET = "caravancms-documents";
// Invoke wrangler's actual JS entry point via node directly, bypassing the npx.cmd/cmd.exe
// shell layer entirely — spawning a .cmd file on Windows without shell:true throws EINVAL,
// but shell:true's own argv->command-line quoting mangles filenames containing spaces
// (e.g. "Damaged Frame.jpg" split into two args). node.exe is a real executable, so argv
// arrays pass through untouched with neither failure mode.
const WRANGLER_ENTRY = path.join(__dirname, "..", "node_modules", "wrangler", "bin", "wrangler.js");

function sqlString(value: string | null | undefined): string {
  if (value === null || value === undefined) return "NULL";
  return `'${value.replace(/'/g, "''")}'`;
}

function getMimeType(fileName: string): string {
  const ext = path.extname(fileName).toLowerCase();
  switch (ext) {
    case ".pdf": return "application/pdf";
    case ".jpg": case ".jpeg": return "image/jpeg";
    case ".png": return "image/png";
    case ".tiff": case ".tif": return "image/tiff";
    case ".bmp": return "image/bmp";
    default: return "application/octet-stream";
  }
}

function walkFiles(root: string, out: string[], resizedFoldersFound: string[]): void {
  for (const entry of fs.readdirSync(root, { withFileTypes: true })) {
    const full = path.join(root, entry.name);
    if (entry.isDirectory()) {
      if (entry.name.toLowerCase() === "resized") {
        resizedFoldersFound.push(full);
        continue; // never descend into / source from a "resized" folder
      }
      try {
        walkFiles(full, out, resizedFoldersFound);
      } catch (e) {
        console.warn(`Skipping inaccessible folder ${full}: ${(e as Error).message}`);
      }
    } else if (SUPPORTED_EXTENSIONS.has(path.extname(entry.name).toLowerCase())) {
      out.push(full);
    }
  }
}

async function runPool<T>(items: T[], concurrency: number, worker: (item: T, index: number) => Promise<void>): Promise<void> {
  let next = 0;
  async function runNext(): Promise<void> {
    while (next < items.length) {
      const i = next++;
      await worker(items[i], i);
    }
  }
  await Promise.all(Array.from({ length: concurrency }, runNext));
}

async function main() {
  const args = process.argv.slice(2);
  const commit = args.includes("--commit");
  const limitFlagIndex = args.indexOf("--limit");
  const limit = limitFlagIndex >= 0 ? Number(args[limitFlagIndex + 1]) : null;
  const positional = args.filter((a, i) => !a.startsWith("--") && args[i - 1] !== "--limit");
  const [archiveRoot, dbPath] = positional;

  if (!archiveRoot || !dbPath) {
    console.error("Usage: npx tsx scripts/migrate-files.ts <archive-root> <source.db> [--commit]");
    process.exit(1);
  }

  const db = new Database(dbPath, { readonly: true });
  const caravans = (db.prepare("SELECT RegistrationNumber, Vin, Make, Model, Year FROM Caravans").all() as any[]).map(
    (c): CaravanForMatching => ({
      registrationNumber: c.RegistrationNumber,
      vin: c.Vin,
      make: c.Make,
      model: c.Model,
      year: c.Year,
    }),
  );
  const existingDocs = db.prepare("SELECT Id, RegistrationNumber, FilePath, DocumentType, LinkMethod FROM Documents").all() as any[];
  const existingByPath = new Map(existingDocs.map((d) => [d.FilePath, d]));
  const folderRegoIndex = buildFolderRegoIndex(existingDocs.map((d) => ({ filePath: d.FilePath, rego: d.RegistrationNumber })));
  db.close();

  console.log(`Loaded ${caravans.length} caravans, ${existingDocs.length} existing document links.`);

  const allFiles: string[] = [];
  const resizedFolders: string[] = [];
  walkFiles(archiveRoot, allFiles, resizedFolders);
  console.log(`Found ${allFiles.length} candidate files, ${resizedFolders.length} old "resized" folders to ignore/clean up.`);

  interface Plan {
    filePath: string;
    fileName: string;
    ext: string;
    mimeType: string;
    registrationNumber: string;
    documentType: string | null;
    linkMethod: string;
    existingDocumentId: number | null;
    confidence: number;
  }

  const plans: Plan[] = [];
  const skipped: string[] = [];

  for (const filePath of allFiles) {
    const existing = existingByPath.get(filePath);
    const ext = path.extname(filePath).toLowerCase();

    if (existing) {
      plans.push({
        filePath,
        fileName: path.basename(filePath),
        ext,
        mimeType: getMimeType(filePath),
        registrationNumber: existing.RegistrationNumber,
        documentType: existing.DocumentType,
        linkMethod: existing.LinkMethod ?? "Manual",
        existingDocumentId: existing.Id,
        confidence: 1.0,
      });
      continue;
    }

    const match = matchFile(filePath, caravans, null, folderRegoIndex);
    if (match.confidence >= 0.5 && match.suggestedRegistrationNumber) {
      plans.push({
        filePath,
        fileName: path.basename(filePath),
        ext,
        mimeType: getMimeType(filePath),
        registrationNumber: match.suggestedRegistrationNumber,
        documentType: null,
        linkMethod: match.matchMethod,
        existingDocumentId: null,
        confidence: match.confidence,
      });
    } else {
      skipped.push(`${filePath} (best confidence ${match.confidence.toFixed(2)}, method ${match.matchMethod || "none"})`);
    }
  }

  const byMethod = new Map<string, number>();
  for (const p of plans) byMethod.set(p.linkMethod, (byMethod.get(p.linkMethod) ?? 0) + 1);

  console.log(`\n=== Plan summary ===`);
  console.log(`Will migrate: ${plans.length} files. Skipped (no confident match): ${skipped.length}.`);
  for (const [method, count] of byMethod) console.log(`  ${method}: ${count}`);

  if (skipped.length > 0) {
    console.log(`\n=== Skipped files (first 20) ===`);
    for (const s of skipped.slice(0, 20)) console.log(`  ${s}`);
    if (skipped.length > 20) console.log(`  ...and ${skipped.length - 20} more`);
  }

  if (!commit) {
    console.log(`\nDry run only — nothing uploaded, nothing changed. Re-run with --commit to actually migrate.`);
    return;
  }

  const toCommit = limit ? plans.slice(0, limit) : plans;
  console.log(`\n=== Committing: uploading ${toCommit.length} files to R2${limit ? ` (limited to ${limit} for testing)` : ""} ===`);
  const sqlLines: string[] = [];
  let uploaded = 0;
  let failed = 0;
  let totalBytesUploaded = 0;

  await runPool(toCommit, CONCURRENCY, async (plan) => {
    try {
      const originalBytes = fs.readFileSync(plan.filePath);
      let bodyBytes: Buffer | Uint8Array = originalBytes;
      let mimeType = plan.mimeType;
      let fileName = plan.fileName;

      if (IMAGE_EXTENSIONS.has(plan.ext)) {
        bodyBytes = resizeImageBytes(new Uint8Array(originalBytes));
        mimeType = "image/jpeg";
        fileName = fileName.replace(/\.[^.]+$/, "") + ".jpg";
      }

      const id = plan.existingDocumentId ?? `new-${plans.indexOf(plan)}`;
      const r2Key = `documents/${plan.registrationNumber}/${id}-${fileName}`;

      const tmpFile = path.join(process.env.TEMP ?? ".", `migrate-${Date.now()}-${Math.random().toString(36).slice(2)}${path.extname(fileName)}`);
      fs.writeFileSync(tmpFile, bodyBytes);
      try {
        execFileSync(
          process.execPath,
          [WRANGLER_ENTRY, "r2", "object", "put", `${BUCKET}/${r2Key}`, "--file", tmpFile, "--content-type", mimeType, "--remote"],
          { stdio: "pipe" },
        );
      } finally {
        fs.unlinkSync(tmpFile);
      }

      totalBytesUploaded += bodyBytes.length;
      uploaded++;

      if (plan.existingDocumentId) {
        sqlLines.push(
          `UPDATE Documents SET R2Key = ${sqlString(r2Key)}, FileName = ${sqlString(fileName)}, MimeType = ${sqlString(mimeType)} WHERE Id = ${plan.existingDocumentId};`,
        );
      } else {
        const now = new Date().toISOString();
        sqlLines.push(
          `INSERT INTO Documents (RegistrationNumber, DocumentType, Category, FilePath, FileName, R2Key, UploadedDate, IsLocalPath, MimeType, Notes, LinkMethod, CreatedAt) VALUES (${sqlString(plan.registrationNumber)}, ${sqlString(plan.documentType ?? "Photos")}, NULL, ${sqlString(plan.filePath)}, ${sqlString(fileName)}, ${sqlString(r2Key)}, ${sqlString(now)}, 0, ${sqlString(mimeType)}, NULL, ${sqlString(plan.linkMethod)}, ${sqlString(now)});`,
        );
      }

      if (uploaded % 100 === 0) console.log(`  ${uploaded}/${plans.length} uploaded...`);
    } catch (e) {
      failed++;
      console.error(`FAILED: ${plan.filePath}: ${(e as Error).message}`);
    }
  });

  console.log(`\nUploaded ${uploaded} files (${failed} failed), ${(totalBytesUploaded / 1024 / 1024).toFixed(1)}MB total.`);

  const sqlPath = path.join(__dirname, limit ? "file-migration-test-output.sql" : "file-migration-output.sql");
  fs.writeFileSync(sqlPath, sqlLines.join("\n"));
  console.log(`Wrote ${sqlLines.length} D1 statements to ${sqlPath}`);

  if (limit) {
    console.log(`\nLimited test run — skipping "resized" folder cleanup and D1 apply instructions.`);
    return;
  }

  console.log(`Now run: npx wrangler d1 execute caravancms --remote --file=scripts/file-migration-output.sql`);

  console.log(`\n=== Cleaning up ${resizedFolders.length} old "resized" folders ===`);
  for (const folder of resizedFolders) {
    try {
      fs.rmSync(folder, { recursive: true, force: true });
      console.log(`  Removed ${folder}`);
    } catch (e) {
      console.error(`  Failed to remove ${folder}: ${(e as Error).message}`);
    }
  }
}

main();
