/**
 * One-time production data migration: reads every row from the source
 * caravan-cms.db (SQLite, via EF Core) and emits a single SQL file that can
 * be applied to D1 with `wrangler d1 execute caravancms --remote --file=...`.
 *
 * Preserves every primary key exactly as-is (explicit Id columns in each
 * INSERT) — this matters because Conversations/Documents/Tags reference
 * Customers.Id and Caravans.RegistrationNumber by value. Re-deriving
 * Customers/Caravans/Jobs/Invoices via a fresh MechanicDesk xlsx import
 * would NOT reproduce the same auto-increment Customer.Id values as the
 * original database, silently breaking every FK reference from the
 * app-generated tables (Conversations, Documents, Tags) — so this script
 * migrates ALL tables directly from the source, not just the app-generated
 * ones, even though Customers/Caravans/Jobs/Invoices could theoretically be
 * re-imported from the same MechanicDesk export.
 *
 * Money fields are converted from decimal-as-TEXT (e.g. "123.45") to
 * integer cents (12345) to match the D1 schema (see migrations/0001_init.sql
 * for why). Run `npm run migrate:validate` after applying to confirm
 * invoice totals sum to the same value before/after, to the cent.
 *
 * Usage:
 *   npx tsx scripts/migrate-db.ts <path-to-source.db> <output.sql>
 */
import Database from "better-sqlite3";
import * as fs from "node:fs";

function sqlString(value: string | null | undefined): string {
  if (value === null || value === undefined) return "NULL";
  return `'${value.replace(/'/g, "''")}'`;
}

function sqlNumber(value: number | null | undefined): string {
  return value === null || value === undefined ? "NULL" : String(value);
}

function centsFromDecimalString(value: string | null | undefined): number {
  if (!value) return 0;
  const n = parseFloat(value);
  return Number.isFinite(n) ? Math.round(n * 100) : 0;
}

function hundredthsFromDecimalString(value: string | null | undefined): number {
  return centsFromDecimalString(value); // same scaling, different semantic field
}

function main() {
  const [, , srcPath, outPath] = process.argv;
  if (!srcPath || !outPath) {
    console.error("Usage: npx tsx scripts/migrate-db.ts <path-to-source.db> <output.sql>");
    process.exit(1);
  }

  const db = new Database(srcPath, { readonly: true });
  // No BEGIN TRANSACTION/COMMIT wrapper — real remote D1 (Durable Objects-backed) rejects raw SQL
  // transaction statements and wants the JS-level transaction API instead; local Miniflare tolerates
  // them but remote doesn't, so this must stay plain statement-by-statement to run against both.
  const lines: string[] = ["PRAGMA foreign_keys = OFF;", ""];

  // ── Customers ──
  const customers = db.prepare("SELECT * FROM Customers").all() as any[];
  for (const c of customers) {
    lines.push(
      `INSERT INTO Customers (Id, Name, Email, Phone, Mobile, Address, Suburb, State, Postcode, CustomerNumber, MechanicDeskId, CreatedAt, UpdatedAt) VALUES (${c.Id}, ${sqlString(c.Name)}, ${sqlString(c.Email)}, ${sqlString(c.Phone)}, ${sqlString(c.Mobile)}, ${sqlString(c.Address)}, ${sqlString(c.Suburb)}, ${sqlString(c.State)}, ${sqlString(c.Postcode)}, ${sqlString(c.CustomerNumber)}, ${sqlString(c.MechanicDeskId)}, ${sqlString(c.CreatedAt)}, ${sqlString(c.UpdatedAt)});`,
    );
  }
  lines.push(`-- ${customers.length} customers`, "");

  // ── Caravans ──
  const caravans = db.prepare("SELECT * FROM Caravans").all() as any[];
  for (const c of caravans) {
    lines.push(
      `INSERT INTO Caravans (RegistrationNumber, CustomerId, Vin, Make, Model, Year, Color, Body, CurrentOdometer, LastJobDate, SelfContainment, SelfContainmentDue, MechanicDeskId, CreatedAt, UpdatedAt) VALUES (${sqlString(c.RegistrationNumber)}, ${c.CustomerId}, ${sqlString(c.Vin)}, ${sqlString(c.Make)}, ${sqlString(c.Model)}, ${sqlNumber(c.Year)}, ${sqlString(c.Color)}, ${sqlString(c.Body)}, ${sqlNumber(c.CurrentOdometer)}, ${sqlString(c.LastJobDate)}, ${sqlString(c.SelfContainment)}, ${sqlString(c.SelfContainmentDue)}, ${sqlString(c.MechanicDeskId)}, ${sqlString(c.CreatedAt)}, ${sqlString(c.UpdatedAt)});`,
    );
  }
  lines.push(`-- ${caravans.length} caravans`, "");

  // ── Jobs ──
  const jobs = db.prepare("SELECT * FROM Jobs").all() as any[];
  for (const j of jobs) {
    lines.push(
      `INSERT INTO Jobs (Id, RegistrationNumber, CustomerId, JobNumber, Status, JobType, Description, Notes, StartDate, FinishDate, EstimatedHours, FinishedBy, MechanicDeskId, CreatedAt, UpdatedAt) VALUES (${j.Id}, ${sqlString(j.RegistrationNumber)}, ${j.CustomerId}, ${sqlString(j.JobNumber)}, ${sqlString(j.Status)}, ${sqlString(j.JobType)}, ${sqlString(j.Description)}, ${sqlString(j.Notes)}, ${sqlString(j.StartDate)}, ${sqlString(j.FinishDate)}, ${sqlNumber(j.EstimatedHours)}, ${sqlString(j.FinishedBy)}, ${sqlString(j.MechanicDeskId)}, ${sqlString(j.CreatedAt)}, ${sqlString(j.UpdatedAt)});`,
    );
  }
  lines.push(`-- ${jobs.length} jobs`, "");

  // ── Invoices (decimal-as-TEXT -> integer cents) ──
  const invoices = db.prepare("SELECT * FROM Invoices").all() as any[];
  let invoiceTotalCentsBefore = 0;
  for (const i of invoices) {
    const netCents = centsFromDecimalString(i.NetAmount);
    const taxCents = centsFromDecimalString(i.TaxAmount);
    const totalCents = centsFromDecimalString(i.TotalAmount);
    const paidCents = centsFromDecimalString(i.PaidAmount);
    invoiceTotalCentsBefore += totalCents;
    lines.push(
      `INSERT INTO Invoices (Id, JobId, CustomerId, RegistrationNumber, InvoiceNumber, IssueDate, DueDate, NetAmountCents, TaxAmountCents, TotalAmountCents, PaidAmountCents, Status, MechanicDeskId, CreatedAt) VALUES (${i.Id}, ${i.JobId}, ${i.CustomerId}, ${sqlString(i.RegistrationNumber)}, ${sqlString(i.InvoiceNumber)}, ${sqlString(i.IssueDate)}, ${sqlString(i.DueDate)}, ${netCents}, ${taxCents}, ${totalCents}, ${paidCents}, ${sqlString(i.Status)}, ${sqlString(i.MechanicDeskId)}, ${sqlString(i.CreatedAt)});`,
    );
  }
  lines.push(`-- ${invoices.length} invoices, total ${(invoiceTotalCentsBefore / 100).toFixed(2)} (sum of TotalAmountCents) — verify this matches post-migration`, "");

  // ── InvoiceItems ──
  const invoiceItems = db.prepare("SELECT * FROM InvoiceItems").all() as any[];
  for (const ii of invoiceItems) {
    lines.push(
      `INSERT INTO InvoiceItems (Id, InvoiceId, Description, Category, UnitPriceCents, QuantityHundredths, NetAmountCents, TaxAmountCents, StockNumber, CreatedAt) VALUES (${ii.Id}, ${ii.InvoiceId}, ${sqlString(ii.Description)}, ${sqlString(ii.Category)}, ${centsFromDecimalString(ii.UnitPrice)}, ${hundredthsFromDecimalString(ii.Quantity)}, ${centsFromDecimalString(ii.NetAmount)}, ${centsFromDecimalString(ii.TaxAmount)}, ${sqlString(ii.StockNumber)}, ${sqlString(ii.CreatedAt)});`,
    );
  }
  lines.push(`-- ${invoiceItems.length} invoice items`, "");

  // ── Documents (FilePath kept as historical audit trail; R2Key stays NULL until scripts/migrate-files.ts runs) ──
  const documents = db.prepare("SELECT * FROM Documents").all() as any[];
  for (const d of documents) {
    lines.push(
      `INSERT INTO Documents (Id, RegistrationNumber, DocumentType, Category, FilePath, FileName, R2Key, UploadedDate, IsLocalPath, MimeType, Notes, LinkMethod, CreatedAt) VALUES (${d.Id}, ${sqlString(d.RegistrationNumber)}, ${sqlString(d.DocumentType)}, ${sqlString(d.Category)}, ${sqlString(d.FilePath)}, ${sqlString(d.FileName)}, NULL, ${sqlString(d.UploadedDate)}, ${d.IsLocalPath ? 1 : 0}, ${sqlString(d.MimeType)}, ${sqlString(d.Notes)}, ${sqlString(d.LinkMethod)}, ${sqlString(d.CreatedAt)});`,
    );
  }
  lines.push(`-- ${documents.length} documents (R2Key NULL — run migrate-files.ts next)`, "");

  // ── Conversations ──
  const conversations = db.prepare("SELECT * FROM Conversations").all() as any[];
  for (const c of conversations) {
    lines.push(
      `INSERT INTO Conversations (Id, CustomerId, RegistrationNumber, Subject, ExternalConversationId, StartedAt, LastActivityAt) VALUES (${c.Id}, ${c.CustomerId}, ${sqlString(c.RegistrationNumber)}, ${sqlString(c.Subject)}, ${sqlString(c.ExternalConversationId)}, ${sqlString(c.StartedAt)}, ${sqlString(c.LastActivityAt)});`,
    );
  }
  lines.push(`-- ${conversations.length} conversations`, "");

  // ── CommunicationLogs ──
  const logs = db.prepare("SELECT * FROM CommunicationLogs").all() as any[];
  for (const m of logs) {
    lines.push(
      `INSERT INTO CommunicationLogs (Id, ConversationId, Type, Direction, FromAddress, Body, ExternalMessageId, LoggedBy, OccurredAt, CreatedAt) VALUES (${m.Id}, ${m.ConversationId}, ${sqlString(m.Type)}, ${sqlString(m.Direction)}, ${sqlString(m.FromAddress)}, ${sqlString(m.Body)}, ${sqlString(m.ExternalMessageId)}, ${sqlString(m.LoggedBy)}, ${sqlString(m.OccurredAt)}, ${sqlString(m.CreatedAt)});`,
    );
  }
  lines.push(`-- ${logs.length} communication logs`, "");

  // ── Tags ──
  const tags = db.prepare("SELECT * FROM Tags").all() as any[];
  for (const t of tags) {
    lines.push(`INSERT INTO Tags (Id, CustomerId, Name) VALUES (${t.Id}, ${t.CustomerId}, ${sqlString(t.Name)});`);
  }
  lines.push(`-- ${tags.length} tags`, "");

  // ── ConversationTags ──
  const convTags = db.prepare("SELECT * FROM ConversationTags").all() as any[];
  for (const ct of convTags) {
    lines.push(`INSERT INTO ConversationTags (ConversationsId, TagsId) VALUES (${ct.ConversationsId}, ${ct.TagsId});`);
  }
  lines.push(`-- ${convTags.length} conversation-tag links`, "");

  lines.push("PRAGMA foreign_keys = ON;");

  fs.writeFileSync(outPath, lines.join("\n"));
  db.close();

  console.log(`Wrote ${outPath}`);
  console.log(
    `Customers=${customers.length} Caravans=${caravans.length} Jobs=${jobs.length} Invoices=${invoices.length} InvoiceItems=${invoiceItems.length} Documents=${documents.length} Conversations=${conversations.length} CommunicationLogs=${logs.length} Tags=${tags.length} ConversationTags=${convTags.length}`,
  );
  console.log(`Invoice total (sum of TotalAmountCents, pre-migration): $${(invoiceTotalCentsBefore / 100).toFixed(2)}`);
}

main();
