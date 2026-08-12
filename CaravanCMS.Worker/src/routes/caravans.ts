import { Hono } from "hono";
import type { Env } from "../env";
import type {
  CaravanRow,
  CustomerRow,
  JobRow,
  InvoiceRow,
  InvoiceItemRow,
  DocumentRow,
  ConversationRow,
  TagRow,
  CommunicationLogRow,
} from "../lib/rows";
import {
  toCaravanSummaryDto,
  toCustomerDto,
  toJobDetailDto,
  toInvoiceDto,
  toDocumentDto,
  toConversationDto,
} from "../lib/mappers";
import { toIso } from "../lib/dates";

const app = new Hono<{ Bindings: Env }>();

const PLACEHOLDER_CUSTOMER_NUMBER = "PLACEHOLDER";

/**
 * Creates a minimal caravan record for a registration number found on disk (a Caravan History
 * folder) with no matching database row — typically an older vehicle MechanicDesk never imported.
 * Attached to a single shared "Unassigned" customer so it shows up for staff to fix up properly
 * later, rather than blocking document sync from linking files that clearly belong to a real rego.
 */
app.post("/", async (c) => {
  const body = await c.req.json<{ registrationNumber?: string; vin?: string | null }>().catch(() => ({}) as any);
  const rego = body.registrationNumber?.trim().toUpperCase();
  if (!rego) return c.json({ error: "registrationNumber is required." }, 400);

  const existingCaravan = await c.env.DB.prepare("SELECT 1 FROM Caravans WHERE RegistrationNumber = ?")
    .bind(rego).first();
  if (existingCaravan) return c.json({ error: `Caravan ${rego} already exists.` }, 409);

  let placeholderCustomer = await c.env.DB.prepare("SELECT Id FROM Customers WHERE CustomerNumber = ?")
    .bind(PLACEHOLDER_CUSTOMER_NUMBER).first<{ Id: number }>();

  const now = new Date().toISOString();
  if (!placeholderCustomer) {
    const insert = await c.env.DB.prepare(
      `INSERT INTO Customers (Name, CustomerNumber, CreatedAt, UpdatedAt) VALUES (?, ?, ?, ?)`,
    ).bind("Unassigned — Needs Review", PLACEHOLDER_CUSTOMER_NUMBER, now, now).run();
    placeholderCustomer = { Id: insert.meta.last_row_id as number };
  }

  await c.env.DB.prepare(
    `INSERT INTO Caravans (RegistrationNumber, CustomerId, Vin, CreatedAt, UpdatedAt) VALUES (?, ?, ?, ?, ?)`,
  ).bind(rego, placeholderCustomer.Id, body.vin ?? null, now, now).run();

  const caravan = await c.env.DB.prepare("SELECT * FROM Caravans WHERE RegistrationNumber = ?")
    .bind(rego).first<CaravanRow>();

  return c.json(
    toCaravanSummaryDto(caravan!, { name: "Unassigned — Needs Review", phone: null, email: null }, 0, 0),
    201,
  );
});

// Fields editable from the Client app's Vehicle Info tab, mapped from the camelCase request body
// (matching every other endpoint's JSON convention) to the actual D1 column name. RegistrationNumber
// is deliberately excluded — it's the table's primary key and Jobs/Documents/Invoices all reference
// it directly, so renaming it would need a real cascading-update story, not a simple field edit.
const EDITABLE_CARAVAN_FIELDS: Record<string, string> = {
  vin: "Vin",
  make: "Make",
  model: "Model",
  year: "Year",
  wofIssueDate: "WofIssueDate",
  wofDueDate: "WofDueDate",
  electricalWofIssueDate: "ElectricalWofIssueDate",
  electricalWofDueDate: "ElectricalWofDueDate",
  selfContainmentIssueDate: "SelfContainmentIssueDate",
  selfContainmentDue: "SelfContainmentDue",
  lockerKeyNumber: "LockerKeyNumber",
  doorKeyNumber: "DoorKeyNumber",
  notes: "Notes",
};

/** Updates any subset of the editable vehicle-info fields for one caravan. */
app.patch("/:rego", async (c) => {
  const rego = c.req.param("rego");
  const body = await c.req.json<Record<string, unknown>>().catch(() => ({}) as Record<string, unknown>);

  const existing = await c.env.DB.prepare("SELECT 1 FROM Caravans WHERE RegistrationNumber = ?").bind(rego).first();
  if (!existing) return c.json({ error: `Caravan ${rego} not found.` }, 404);

  const setClauses: string[] = [];
  const values: unknown[] = [];
  for (const [jsonKey, column] of Object.entries(EDITABLE_CARAVAN_FIELDS)) {
    if (jsonKey in body) {
      setClauses.push(`${column} = ?`);
      values.push(body[jsonKey] ?? null);
    }
  }

  if (setClauses.length === 0) {
    return c.json({ error: "No editable fields provided." }, 400);
  }

  setClauses.push("UpdatedAt = ?");
  values.push(new Date().toISOString());
  values.push(rego);

  await c.env.DB.prepare(`UPDATE Caravans SET ${setClauses.join(", ")} WHERE RegistrationNumber = ?`)
    .bind(...values)
    .run();

  const updated = await c.env.DB.prepare("SELECT * FROM Caravans WHERE RegistrationNumber = ?")
    .bind(rego)
    .first<CaravanRow>();

  return c.json({
    registrationNumber: updated!.RegistrationNumber,
    vin: updated!.Vin,
    make: updated!.Make,
    model: updated!.Model,
    year: updated!.Year,
    wofIssueDate: toIso(updated!.WofIssueDate),
    wofDueDate: toIso(updated!.WofDueDate),
    electricalWofIssueDate: toIso(updated!.ElectricalWofIssueDate),
    electricalWofDueDate: toIso(updated!.ElectricalWofDueDate),
    selfContainmentIssueDate: toIso(updated!.SelfContainmentIssueDate),
    selfContainmentDue: toIso(updated!.SelfContainmentDue),
    lockerKeyNumber: updated!.LockerKeyNumber,
    doorKeyNumber: updated!.DoorKeyNumber,
    notes: updated!.Notes,
    updatedAt: toIso(updated!.UpdatedAt),
  });
});

/** Port of CaravansController.GetAll — every caravan with customer + counts, ordered Make then Model. */
app.get("/", async (c) => {
  const { results } = await c.env.DB.prepare(
    `SELECT ca.*, cu.Name AS CustomerName, cu.Phone AS CustomerPhone, cu.Email AS CustomerEmail,
            (SELECT COUNT(*) FROM Jobs j WHERE j.RegistrationNumber = ca.RegistrationNumber) AS JobCount,
            (SELECT COUNT(*) FROM Documents d WHERE d.RegistrationNumber = ca.RegistrationNumber) AS DocumentCount
     FROM Caravans ca
     JOIN Customers cu ON cu.Id = ca.CustomerId
     ORDER BY ca.Make, ca.Model`,
  ).all<
    CaravanRow & { CustomerName: string | null; CustomerPhone: string | null; CustomerEmail: string | null; JobCount: number; DocumentCount: number }
  >();

  const dtos = results.map((r) =>
    toCaravanSummaryDto(
      r,
      { name: r.CustomerName, phone: r.CustomerPhone, email: r.CustomerEmail },
      r.JobCount,
      r.DocumentCount,
    ),
  );
  return c.json(dtos);
});

/** Port of CaravansController.GetByRego — the full detail graph. */
app.get("/stats", async (c) => {
  // Mounted before /:rego below so this literal path wins the match.
  const [customers, caravans, jobs, invoices, documents, lastImport] = await Promise.all([
    c.env.DB.prepare("SELECT COUNT(*) c FROM Customers").first<{ c: number }>(),
    c.env.DB.prepare("SELECT COUNT(*) c FROM Caravans").first<{ c: number }>(),
    c.env.DB.prepare("SELECT COUNT(*) c FROM Jobs").first<{ c: number }>(),
    c.env.DB.prepare("SELECT COUNT(*) c FROM Invoices").first<{ c: number }>(),
    c.env.DB.prepare("SELECT COUNT(*) c FROM Documents").first<{ c: number }>(),
    c.env.DB.prepare("SELECT MAX(UpdatedAt) m FROM Caravans").first<{ m: string | null }>(),
  ]);

  // D1 has no PRAGMA page_count (blocked: SQLITE_AUTH), but every query response's
  // meta.size_after already reports the live database file size in bytes — no
  // separate Cloudflare API token/call needed, just read it off any query we run.
  const sizeProbe = await c.env.DB.prepare("SELECT 1").run();

  return c.json({
    totalCustomers: customers?.c ?? 0,
    totalCaravans: caravans?.c ?? 0,
    totalJobs: jobs?.c ?? 0,
    totalInvoices: invoices?.c ?? 0,
    totalDocuments: documents?.c ?? 0,
    lastImportAt: toIso(lastImport?.m),
    databaseSizeBytes: sizeProbe.meta.size_after,
    version: "2.0.0-worker",
  });
});

app.get("/search", async (c) => {
  const q = c.req.query("q");
  if (!q || q.trim().length < 2) {
    return c.json({ error: "Search query must be at least 2 characters." }, 400);
  }

  const term = `%${q.trim().toUpperCase()}%`;
  const { results } = await c.env.DB.prepare(
    `SELECT ca.*, cu.Name AS CustomerName, cu.Phone AS CustomerPhone, cu.Email AS CustomerEmail,
            cu.CustomerNumber AS CustomerNumberVal,
            (SELECT COUNT(*) FROM Jobs j WHERE j.RegistrationNumber = ca.RegistrationNumber) AS JobCount,
            (SELECT COUNT(*) FROM Documents d WHERE d.RegistrationNumber = ca.RegistrationNumber) AS DocumentCount
     FROM Caravans ca
     JOIN Customers cu ON cu.Id = ca.CustomerId
     WHERE UPPER(ca.Vin) LIKE ?1 OR UPPER(ca.RegistrationNumber) LIKE ?1
        OR UPPER(ca.Make) LIKE ?1 OR UPPER(ca.Model) LIKE ?1
        OR UPPER(cu.Name) LIKE ?1 OR UPPER(cu.CustomerNumber) LIKE ?1
     LIMIT 50`,
  )
    .bind(term)
    .all<
      CaravanRow & {
        CustomerName: string | null;
        CustomerPhone: string | null;
        CustomerEmail: string | null;
        JobCount: number;
        DocumentCount: number;
      }
    >();

  const dtos = results.map((r) =>
    toCaravanSummaryDto(
      r,
      { name: r.CustomerName, phone: r.CustomerPhone, email: r.CustomerEmail },
      r.JobCount,
      r.DocumentCount,
    ),
  );
  return c.json(dtos);
});

/**
 * Port of CaravansController.FindFuzzyToken — tolerates a space/dash between
 * each character of the token, requires a non-alphanumeric boundary on both
 * sides so it doesn't match inside a longer word.
 */
function escapeRegex(s: string): string {
  return s.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
}

function findFuzzyToken(text: string, token: string): string | null {
  const spaced = [...token].map((ch) => escapeRegex(ch)).join("[ -]?");
  const pattern = new RegExp(`(?<![A-Za-z0-9])${spaced}(?![A-Za-z0-9])`, "i");
  const match = text.match(pattern);
  return match ? match[0] : null;
}

app.post("/detect", async (c) => {
  const body = await c.req.json<{ text?: string }>().catch(() => ({ text: undefined }));
  if (!body.text || !body.text.trim()) {
    return c.json([]);
  }

  const { results: caravans } = await c.env.DB.prepare(
    `SELECT ca.*, cu.Name AS CustomerName FROM Caravans ca JOIN Customers cu ON cu.Id = ca.CustomerId`,
  ).all<CaravanRow & { CustomerName: string | null }>();

  const detected: unknown[] = [];
  for (const ca of caravans) {
    let matched: string | null = null;
    let method = "";

    if (ca.RegistrationNumber && ca.RegistrationNumber.length >= 3) {
      matched = findFuzzyToken(body.text, ca.RegistrationNumber);
      method = "Registration";
    }
    if (!matched && ca.Vin && ca.Vin.length >= 5) {
      matched = findFuzzyToken(body.text, ca.Vin);
      method = "Vin";
    }

    if (matched) {
      detected.push({
        registrationNumber: ca.RegistrationNumber,
        make: ca.Make,
        model: ca.Model,
        year: ca.Year,
        customerId: ca.CustomerId,
        customerName: ca.CustomerName,
        matchedText: matched,
        matchMethod: method,
      });
    }
  }

  return c.json(detected);
});

app.get("/:rego", async (c) => {
  const rego = c.req.param("rego");

  const caravan = await c.env.DB.prepare("SELECT * FROM Caravans WHERE RegistrationNumber = ?")
    .bind(rego)
    .first<CaravanRow>();
  if (!caravan) {
    return c.json({ error: `Caravan ${rego} not found.` }, 404);
  }

  const customer = await c.env.DB.prepare("SELECT * FROM Customers WHERE Id = ?")
    .bind(caravan.CustomerId)
    .first<CustomerRow>();

  const [jobsResult, documentsResult, conversationsResult] = await Promise.all([
    c.env.DB.prepare(
      "SELECT * FROM Jobs WHERE RegistrationNumber = ? ORDER BY COALESCE(FinishDate, StartDate) DESC",
    )
      .bind(rego)
      .all<JobRow>(),
    c.env.DB.prepare(
      "SELECT * FROM Documents WHERE RegistrationNumber = ? ORDER BY COALESCE(UploadedDate, CreatedAt) DESC",
    )
      .bind(rego)
      .all<DocumentRow>(),
    c.env.DB.prepare(
      "SELECT * FROM Conversations WHERE CustomerId = ? ORDER BY LastActivityAt DESC",
    )
      .bind(caravan.CustomerId)
      .all<ConversationRow>(),
  ]);

  const jobs = jobsResult.results;
  const invoicesByJob = new Map<number, ReturnType<typeof toInvoiceDto>[]>();
  if (jobs.length > 0) {
    const jobIds = jobs.map((j) => j.Id);
    const placeholders = jobIds.map(() => "?").join(",");
    const { results: invoices } = await c.env.DB.prepare(
      `SELECT * FROM Invoices WHERE JobId IN (${placeholders})`,
    )
      .bind(...jobIds)
      .all<InvoiceRow>();

    const itemsByInvoice = new Map<number, InvoiceItemRow[]>();
    if (invoices.length > 0) {
      const invIds = invoices.map((i) => i.Id);
      const invPlaceholders = invIds.map(() => "?").join(",");
      const { results: items } = await c.env.DB.prepare(
        `SELECT * FROM InvoiceItems WHERE InvoiceId IN (${invPlaceholders})`,
      )
        .bind(...invIds)
        .all<InvoiceItemRow>();
      for (const item of items) {
        if (!itemsByInvoice.has(item.InvoiceId)) itemsByInvoice.set(item.InvoiceId, []);
        itemsByInvoice.get(item.InvoiceId)!.push(item);
      }
    }

    for (const inv of invoices) {
      const dto = toInvoiceDto(inv, itemsByInvoice.get(inv.Id) ?? []);
      if (!invoicesByJob.has(inv.JobId)) invoicesByJob.set(inv.JobId, []);
      invoicesByJob.get(inv.JobId)!.push(dto);
    }
  }

  const conversations = conversationsResult.results;
  const conversationDtos = await Promise.all(
    conversations.map(async (conv) => {
      const [tags, messages] = await Promise.all([
        c.env.DB.prepare(
          `SELECT t.* FROM Tags t JOIN ConversationTags ct ON ct.TagsId = t.Id WHERE ct.ConversationsId = ?`,
        )
          .bind(conv.Id)
          .all<TagRow>(),
        c.env.DB.prepare("SELECT * FROM CommunicationLogs WHERE ConversationId = ?")
          .bind(conv.Id)
          .all<CommunicationLogRow>(),
      ]);
      return toConversationDto(conv, tags.results, messages.results);
    }),
  );

  return c.json({
    registrationNumber: caravan.RegistrationNumber,
    vin: caravan.Vin,
    make: caravan.Make,
    model: caravan.Model,
    year: caravan.Year,
    color: caravan.Color,
    body: caravan.Body,
    currentOdometer: caravan.CurrentOdometer,
    lastJobDate: toIso(caravan.LastJobDate),
    selfContainment: caravan.SelfContainment,
    selfContainmentDue: toIso(caravan.SelfContainmentDue),
    selfContainmentIssueDate: toIso(caravan.SelfContainmentIssueDate),
    wofIssueDate: toIso(caravan.WofIssueDate),
    wofDueDate: toIso(caravan.WofDueDate),
    electricalWofIssueDate: toIso(caravan.ElectricalWofIssueDate),
    electricalWofDueDate: toIso(caravan.ElectricalWofDueDate),
    lockerKeyNumber: caravan.LockerKeyNumber,
    doorKeyNumber: caravan.DoorKeyNumber,
    notes: caravan.Notes,
    mechanicDeskId: caravan.MechanicDeskId,
    createdAt: toIso(caravan.CreatedAt),
    updatedAt: toIso(caravan.UpdatedAt),
    customer: customer ? toCustomerDto(customer) : null,
    jobs: jobs.map((j) => toJobDetailDto(j, invoicesByJob.get(j.Id) ?? [])),
    documents: documentsResult.results.map(toDocumentDto),
    conversations: conversationDtos,
  });
});

export default app;
