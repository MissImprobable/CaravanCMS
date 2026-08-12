import type { CustomerRow, CaravanRow, JobRow } from "../lib/rows";
import { decimalToCents } from "../lib/money";
import type { ImportResult } from "./types";

/** Port of ExcelImportService.GetOrCreateCustomerAsync. */
export async function getOrCreateCustomer(
  db: D1Database,
  mdId: string | null,
  name: string,
  email: string | null,
  phone: string | null,
  mobile: string | null,
  address: string | null,
  suburb: string | null,
  state: string | null,
  postcode: string | null,
  customerNumber: string | null,
  result: ImportResult,
): Promise<CustomerRow> {
  let existing: CustomerRow | null = null;

  if (mdId) {
    existing = await db.prepare("SELECT * FROM Customers WHERE MechanicDeskId = ?").bind(mdId).first<CustomerRow>();
  }
  if (!existing && customerNumber) {
    existing = await db
      .prepare("SELECT * FROM Customers WHERE CustomerNumber = ?")
      .bind(customerNumber)
      .first<CustomerRow>();
  }

  if (existing) {
    if (existing.Name !== name || existing.Email !== email) {
      result.conflicts.push({
        entityType: "Customer",
        mechanicDeskId: mdId ?? customerNumber ?? name,
        existingEntityId: existing.Id,
        existingDescription: `${existing.Name} (${existing.Email})`,
        incomingDescription: `${name} (${email})`,
        changedFields: {
          ...(existing.Name !== name ? { Name: [existing.Name, name] as [string, string] } : {}),
          ...(existing.Email !== email ? { Email: [existing.Email ?? "", email ?? ""] as [string, string] } : {}),
        },
      });
    }
    result.customersUpdated++;
    return existing;
  }

  const now = new Date().toISOString();
  const insert = await db
    .prepare(
      `INSERT INTO Customers (Name, Email, Phone, Mobile, Address, Suburb, State, Postcode, CustomerNumber, MechanicDeskId, CreatedAt, UpdatedAt)
       VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)`,
    )
    .bind(name, email, phone, mobile, address, suburb, state, postcode, customerNumber, mdId, now, now)
    .run();

  result.customersImported++;
  return {
    Id: insert.meta.last_row_id,
    Name: name,
    Email: email,
    Phone: phone,
    Mobile: mobile,
    Address: address,
    Suburb: suburb,
    State: state,
    Postcode: postcode,
    CustomerNumber: customerNumber,
    MechanicDeskId: mdId,
    CreatedAt: now,
    UpdatedAt: now,
  };
}

/** Port of ExcelImportService.GetOrCreateCaravanAsync. Returns null when there's genuinely no
 * rego or VIN to identify the vehicle by (silent skip, matches the C# behavior). */
export async function getOrCreateCaravan(
  db: D1Database,
  mdId: string | null,
  customerId: number,
  vin: string | null,
  rego: string | null,
  make: string | null,
  model: string | null,
  year: number | null,
  color: string | null,
  body: string | null,
  selfContainment: string | null,
  selfContainmentDue: string | null,
  result: ImportResult,
): Promise<CaravanRow | null> {
  let existing: CaravanRow | null = null;

  if (mdId) {
    existing = await db.prepare("SELECT * FROM Caravans WHERE MechanicDeskId = ?").bind(mdId).first<CaravanRow>();
  }
  if (!existing && vin) {
    existing = await db.prepare("SELECT * FROM Caravans WHERE Vin = ?").bind(vin).first<CaravanRow>();
  }
  if (!existing && rego) {
    existing = await db
      .prepare("SELECT * FROM Caravans WHERE RegistrationNumber = ?")
      .bind(rego)
      .first<CaravanRow>();
  }

  if (existing) {
    const updates: string[] = [];
    const params: unknown[] = [];
    if (!existing.Vin && vin) {
      updates.push("Vin = ?");
      params.push(vin);
    }
    if (!existing.Make && make) {
      updates.push("Make = ?");
      params.push(make);
    }
    if (!existing.Model && model) {
      updates.push("Model = ?");
      params.push(model);
    }
    if (existing.Year === null && year !== null) {
      updates.push("Year = ?");
      params.push(year);
    }
    if (!existing.SelfContainment && selfContainment) {
      updates.push("SelfContainment = ?");
      params.push(selfContainment);
    }
    if (existing.SelfContainmentDue === null && selfContainmentDue !== null) {
      updates.push("SelfContainmentDue = ?");
      params.push(selfContainmentDue);
    }

    if (updates.length > 0) {
      const now = new Date().toISOString();
      updates.push("UpdatedAt = ?");
      params.push(now);
      params.push(existing.RegistrationNumber);
      await db
        .prepare(`UPDATE Caravans SET ${updates.join(", ")} WHERE RegistrationNumber = ?`)
        .bind(...params)
        .run();
      existing = (await db
        .prepare("SELECT * FROM Caravans WHERE RegistrationNumber = ?")
        .bind(existing.RegistrationNumber)
        .first<CaravanRow>())!;
    }

    result.caravansUpdated++;
    return existing;
  }

  const effectiveRego = rego && rego.trim() ? rego : vin;
  if (!effectiveRego || !effectiveRego.trim()) return null;

  if (!rego || !rego.trim()) {
    result.warnings.push(
      `Caravan ${effectiveRego}: no rego on file, using VIN as the identifier — update the registration number once it's known.`,
    );
  }

  const now = new Date().toISOString();
  await db
    .prepare(
      `INSERT INTO Caravans (RegistrationNumber, CustomerId, Vin, Make, Model, Year, Color, Body, SelfContainment, SelfContainmentDue, MechanicDeskId, CreatedAt, UpdatedAt)
       VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)`,
    )
    .bind(effectiveRego, customerId, vin, make, model, year, color, body, selfContainment, selfContainmentDue, mdId, now, now)
    .run();

  result.caravansImported++;
  return {
    RegistrationNumber: effectiveRego,
    CustomerId: customerId,
    Vin: vin,
    Make: make,
    Model: model,
    Year: year,
    Color: color,
    Body: body,
    CurrentOdometer: null,
    LastJobDate: null,
    SelfContainment: selfContainment,
    SelfContainmentDue: selfContainmentDue,
    SelfContainmentIssueDate: null,
    WofIssueDate: null,
    WofDueDate: null,
    ElectricalWofIssueDate: null,
    ElectricalWofDueDate: null,
    LockerKeyNumber: null,
    DoorKeyNumber: null,
    Notes: null,
    MechanicDeskId: mdId,
    CreatedAt: now,
    UpdatedAt: now,
  };
}

/** Port of ExcelImportService.GetOrCreateJobAsync — matched purely by MechanicDeskId, no field updates on match. */
export async function getOrCreateJob(
  db: D1Database,
  mdId: string,
  caravanRego: string,
  customerId: number,
  jobNumber: string | null,
  status: string | null,
  jobType: string | null,
  description: string | null,
  notes: string | null,
  startDate: string | null,
  finishDate: string | null,
  finishedBy: string | null,
  estimatedHours: number | null,
  result: ImportResult,
): Promise<JobRow> {
  const existing = await db.prepare("SELECT * FROM Jobs WHERE MechanicDeskId = ?").bind(mdId).first<JobRow>();
  if (existing) {
    result.jobsUpdated++;
    return existing;
  }

  const now = new Date().toISOString();
  const insert = await db
    .prepare(
      `INSERT INTO Jobs (RegistrationNumber, CustomerId, JobNumber, Status, JobType, Description, Notes, StartDate, FinishDate, FinishedBy, EstimatedHours, MechanicDeskId, CreatedAt, UpdatedAt)
       VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)`,
    )
    .bind(caravanRego, customerId, jobNumber, status, jobType, description, notes, startDate, finishDate, finishedBy, estimatedHours, mdId, now, now)
    .run();

  result.jobsImported++;
  return {
    Id: insert.meta.last_row_id,
    RegistrationNumber: caravanRego,
    CustomerId: customerId,
    JobNumber: jobNumber,
    Status: status,
    JobType: jobType,
    Description: description,
    Notes: notes,
    StartDate: startDate,
    FinishDate: finishDate,
    EstimatedHours: estimatedHours,
    FinishedBy: finishedBy,
    MechanicDeskId: mdId,
    CreatedAt: now,
    UpdatedAt: now,
  };
}

/** Port of ExcelImportService.GetOrCreateInvoiceAsync — skipped entirely if already present (no update). */
export async function getOrCreateInvoice(
  db: D1Database,
  mdId: string,
  jobId: number,
  customerId: number,
  caravanRego: string,
  invoiceNumber: string | null,
  issueDate: string | null,
  net: number,
  tax: number,
  total: number,
  paid: number,
  status: string | null,
  result: ImportResult,
): Promise<void> {
  const exists = await db.prepare("SELECT 1 FROM Invoices WHERE MechanicDeskId = ?").bind(mdId).first();
  if (exists) return;

  const now = new Date().toISOString();
  await db
    .prepare(
      `INSERT INTO Invoices (JobId, CustomerId, RegistrationNumber, InvoiceNumber, IssueDate, NetAmountCents, TaxAmountCents, TotalAmountCents, PaidAmountCents, Status, MechanicDeskId, CreatedAt)
       VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)`,
    )
    .bind(
      jobId,
      customerId,
      caravanRego,
      invoiceNumber,
      issueDate,
      decimalToCents(net),
      decimalToCents(tax),
      decimalToCents(total),
      decimalToCents(paid),
      status,
      mdId,
      now,
    )
    .run();

  result.invoicesImported++;
}

export function resolveCustomerId(mdId: string | null, map: Map<string, number>): number | null {
  if (!mdId) return null;
  return map.get(mdId) ?? null;
}
