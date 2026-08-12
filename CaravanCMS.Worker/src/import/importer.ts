import ExcelJS from "exceljs";
import type { CaravanRow, CustomerRow } from "../lib/rows";
import { buildColumnMap, getCell, findSheet, sheetRowCount, parseIntOrNull, parseMoney, parseDateIso } from "./excel";
import { getOrCreateCustomer, getOrCreateCaravan, getOrCreateJob, getOrCreateInvoice, resolveCustomerId } from "./upsert";
import { newImportResult, type ImportResult } from "./types";

/** Port of ExcelImportService.ImportAsync. */
export async function importMechanicDeskExcel(db: D1Database, buffer: ArrayBuffer): Promise<ImportResult> {
  const result = newImportResult();
  const startedAt = Date.now();

  const workbook = new ExcelJS.Workbook();
  await workbook.xlsx.load(buffer);

  const customersSheet = findSheet(workbook, "Customers", "Customer List", "Clients", "Client List");
  const vehiclesSheet = findSheet(workbook, "Vehicles", "Vehicle List", "Assets", "Asset List", "Caravans", "Caravan List");
  const jobsSheet = findSheet(workbook, "Jobs", "Job List", "Service Jobs", "Work Orders");

  const isMultiSheet = customersSheet !== null || vehiclesSheet !== null;

  if (isMultiSheet) {
    await importMultiSheet(db, customersSheet, vehiclesSheet, jobsSheet, result);
  } else {
    const flatSheet = jobsSheet ?? workbook.worksheets.find((s) => s.actualRowCount > 0) ?? null;
    if (!flatSheet) {
      result.errors.push(
        "Could not find a recognisable sheet in the Excel file. Expected sheets named 'Customers', 'Vehicles', or 'Jobs'.",
      );
      return result;
    }
    await importFlatSheet(db, flatSheet, result);
  }

  result.durationMs = Date.now() - startedAt;
  return result;
}

async function importMultiSheet(
  db: D1Database,
  customersSheet: ExcelJS.Worksheet | null,
  vehiclesSheet: ExcelJS.Worksheet | null,
  jobsSheet: ExcelJS.Worksheet | null,
  result: ImportResult,
): Promise<void> {
  const customerMdIdToDbId = new Map<string, number>();
  const vehicleRegoMap = new Map<string, string>();

  // ── Step 1: Customers ──
  if (customersSheet && sheetRowCount(customersSheet) > 0) {
    const cols = buildColumnMap(customersSheet);
    result.warnings.push(`[Columns] ${customersSheet.name}: ${[...cols.keys()].join(", ")}`);

    for (let row = 2; row <= sheetRowCount(customersSheet) + 1; row++) {
      try {
        const mdId = getCell(customersSheet, row, cols, "customer id", "customerid", "client id", "clientid", "id");
        const name = getCell(customersSheet, row, cols, "customer", "customer name", "client", "client name", "name", "full name");
        if (!name) continue;

        const customer = await getOrCreateCustomer(
          db,
          mdId,
          name,
          getCell(customersSheet, row, cols, "email", "email address"),
          getCell(customersSheet, row, cols, "phone", "phone number", "telephone", "work phone"),
          getCell(customersSheet, row, cols, "mobile", "mobile phone", "mobile number", "cell"),
          getCell(customersSheet, row, cols, "address", "street address", "street"),
          getCell(customersSheet, row, cols, "suburb", "city", "town"),
          getCell(customersSheet, row, cols, "state"),
          getCell(customersSheet, row, cols, "postcode", "post code", "zip"),
          getCell(customersSheet, row, cols, "customer number", "customer no", "account number", "account no"),
          result,
        );

        if (mdId) customerMdIdToDbId.set(mdId, customer.Id);
      } catch (ex) {
        result.errors.push(`Customers row ${row}: ${(ex as Error).message}`);
      }
    }
  }

  // ── Step 2: Vehicles ──
  if (vehiclesSheet && sheetRowCount(vehiclesSheet) > 0) {
    const cols = buildColumnMap(vehiclesSheet);
    result.warnings.push(`[Columns] ${vehiclesSheet.name}: ${[...cols.keys()].join(", ")}`);

    for (let row = 2; row <= sheetRowCount(vehiclesSheet) + 1; row++) {
      try {
        const mdId = getCell(vehiclesSheet, row, cols, "vehicle id", "vehicleid", "asset id", "assetid", "id");
        const rego = getCell(
          vehiclesSheet,
          row,
          cols,
          "rego",
          "registration",
          "registration number",
          "reg",
          "reg number",
          "reg no",
          "number plate",
          "plate",
          "license plate",
          "licence plate",
          "numberplate",
        );
        const vin = getCell(vehiclesSheet, row, cols, "vin", "chassis", "chassis number", "serial number", "serial no");

        if (!rego && vin) {
          const identifier = mdId ?? vin ?? `row ${row}`;
          result.warnings.push(
            `Vehicles row ${row} (${identifier}): no registration number (rego) found — this vehicle will be identified by VIN only.`,
          );
        }
        if (!rego && !vin && !mdId) continue;

        const customerMdId = getCell(vehiclesSheet, row, cols, "customer id", "customerid", "client id", "clientid", "owner id", "ownerid");
        let customerId = resolveCustomerId(customerMdId, customerMdIdToDbId);

        if (customerId === null && customerMdId) {
          const existing = await db
            .prepare("SELECT Id FROM Customers WHERE MechanicDeskId = ?")
            .bind(customerMdId)
            .first<{ Id: number }>();
          if (existing) customerId = existing.Id;
        }

        if (customerId === null) {
          const identifier = rego ?? vin ?? mdId ?? `row ${row}`;
          result.warnings.push(
            `Vehicles row ${row} (rego: ${identifier}): skipped — no matching customer found (customer ID '${customerMdId ?? "none"}').`,
          );
          continue;
        }

        const year = parseIntOrNull(getCell(vehiclesSheet, row, cols, "year", "vehicle year", "manufacture year", "model year", "yr"), 1900);
        const odometerStr = getCell(vehiclesSheet, row, cols, "odometer", "current odometer", "odo", "kilometres", "km", "mileage");
        const odometer = odometerStr ? parseIntOrNull(odometerStr.replace(/,/g, "")) : null;
        const selfContainmentDue = parseDateIso(getCell(vehiclesSheet, row, cols, "self containment due"));

        const caravan = await getOrCreateCaravan(
          db,
          mdId,
          customerId,
          vin,
          rego,
          getCell(vehiclesSheet, row, cols, "make", "vehicle make", "manufacturer", "brand"),
          getCell(vehiclesSheet, row, cols, "model", "vehicle model", "model name"),
          year,
          getCell(vehiclesSheet, row, cols, "colour", "color", "vehicle colour"),
          getCell(vehiclesSheet, row, cols, "body", "body type", "type", "category", "style"),
          getCell(vehiclesSheet, row, cols, "self containment"),
          selfContainmentDue,
          result,
        );

        if (!caravan) continue;

        if (odometer && odometer > 0 && caravan.CurrentOdometer === null) {
          await db
            .prepare("UPDATE Caravans SET CurrentOdometer = ?, UpdatedAt = ? WHERE RegistrationNumber = ?")
            .bind(odometer, new Date().toISOString(), caravan.RegistrationNumber)
            .run();
        }

        const key = mdId ?? rego ?? vin!;
        vehicleRegoMap.set(key, caravan.RegistrationNumber);
        if (rego && !vehicleRegoMap.has(rego)) vehicleRegoMap.set(rego, caravan.RegistrationNumber);
        if (vin && !vehicleRegoMap.has(vin)) vehicleRegoMap.set(vin, caravan.RegistrationNumber);
      } catch (ex) {
        result.errors.push(`Vehicles row ${row}: ${(ex as Error).message}`);
      }
    }
  } else {
    result.warnings.push("No Vehicles sheet found in this export — vehicle (caravan) data may be missing.");
  }

  // ── Step 3: Jobs ──
  if (jobsSheet && sheetRowCount(jobsSheet) > 0) {
    const cols = buildColumnMap(jobsSheet);
    for (let row = 2; row <= sheetRowCount(jobsSheet) + 1; row++) {
      try {
        await processJobRow(db, jobsSheet, row, cols, vehicleRegoMap, customerMdIdToDbId, result);
      } catch (ex) {
        result.errors.push(`Jobs row ${row}: ${(ex as Error).message}`);
      }
    }
  }
}

async function processJobRow(
  db: D1Database,
  sheet: ExcelJS.Worksheet,
  row: number,
  cols: Map<string, number>,
  vehicleMap: Map<string, string>,
  customerMap: Map<string, number>,
  result: ImportResult,
): Promise<void> {
  const vehicleMdId = getCell(sheet, row, cols, "vehicle id", "vehicleid", "asset id", "assetid");
  const rego = getCell(
    sheet,
    row,
    cols,
    "rego",
    "registration",
    "registration number",
    "reg",
    "reg number",
    "reg no",
    "number plate",
    "plate",
    "license plate",
    "licence plate",
  );
  const vin = getCell(sheet, row, cols, "vin", "chassis", "chassis number", "serial number", "serial no");

  let caravanRego: string | null = null;
  if (vehicleMdId && vehicleMap.has(vehicleMdId)) caravanRego = vehicleMap.get(vehicleMdId)!;
  if (!caravanRego && rego && vehicleMap.has(rego)) caravanRego = vehicleMap.get(rego)!;
  if (!caravanRego && rego) caravanRego = rego;
  if (!caravanRego && vin && vehicleMap.has(vin)) caravanRego = vehicleMap.get(vin)!;
  if (!caravanRego && vehicleMdId) {
    const c = await db.prepare("SELECT RegistrationNumber FROM Caravans WHERE MechanicDeskId = ?").bind(vehicleMdId).first<CaravanRow>();
    if (c) caravanRego = c.RegistrationNumber;
  }
  if (!caravanRego && vin) {
    const c = await db.prepare("SELECT RegistrationNumber FROM Caravans WHERE Vin = ?").bind(vin).first<CaravanRow>();
    if (c) caravanRego = c.RegistrationNumber;
  }

  if (!caravanRego) return;

  const customerMdId = getCell(sheet, row, cols, "customer id", "customerid", "client id");
  let customerId = resolveCustomerId(customerMdId, customerMap);
  if (customerId === null && customerMdId) {
    const c = await db.prepare("SELECT Id FROM Customers WHERE MechanicDeskId = ?").bind(customerMdId).first<CustomerRow>();
    if (c) customerId = c.Id;
  }
  if (customerId === null) {
    const caravan = await db.prepare("SELECT CustomerId FROM Caravans WHERE RegistrationNumber = ?").bind(caravanRego).first<CaravanRow>();
    customerId = caravan?.CustomerId ?? null;
  }
  if (customerId === null) {
    result.warnings.push(`Jobs row ${row}: skipped — could not resolve customer.`);
    return;
  }

  const jobMdId = getCell(sheet, row, cols, "job id", "jobid", "job number", "job #");
  const jobNumber = getCell(sheet, row, cols, "job number", "job no", "work order", "work order number");
  if (!jobMdId && !jobNumber) {
    result.warnings.push(`Jobs row ${row}: skipped — no job ID or job number.`);
    return;
  }

  const startDate = parseDateIso(getCell(sheet, row, cols, "start date", "date started", "booked date", "booking date"));
  const finishDate = parseDateIso(getCell(sheet, row, cols, "finish date", "date completed", "completion date", "completed date"));
  const hoursStr = getCell(sheet, row, cols, "hours", "labour hours", "labor hours", "estimated hours");
  const hours = hoursStr ? parseFloat(hoursStr) : NaN;

  const job = await getOrCreateJob(
    db,
    jobMdId ?? `row-${jobNumber}`,
    caravanRego,
    customerId,
    jobNumber,
    getCell(sheet, row, cols, "status", "job status") ?? "Completed",
    getCell(sheet, row, cols, "job type", "type", "service type"),
    getCell(sheet, row, cols, "description", "job description", "work description"),
    getCell(sheet, row, cols, "notes", "comments", "technician notes"),
    startDate,
    finishDate,
    getCell(sheet, row, cols, "technician", "mechanic", "finished by", "assigned to"),
    Number.isFinite(hours) && hours !== 0 ? hours : null,
    result,
  );

  if (finishDate) {
    const caravan = await db.prepare("SELECT * FROM Caravans WHERE RegistrationNumber = ?").bind(caravanRego).first<CaravanRow>();
    if (caravan && (!caravan.LastJobDate || finishDate > caravan.LastJobDate)) {
      await db
        .prepare("UPDATE Caravans SET LastJobDate = ?, UpdatedAt = ? WHERE RegistrationNumber = ?")
        .bind(finishDate, new Date().toISOString(), caravanRego)
        .run();
    }
  }

  const invMdId = getCell(sheet, row, cols, "invoice id", "invoiceid", "invoice number", "invoice #");
  const invNumber = getCell(sheet, row, cols, "invoice number", "invoice no");
  if (invMdId !== null || invNumber !== null) {
    const net = parseMoney(getCell(sheet, row, cols, "net", "net amount", "subtotal"));
    const tax = parseMoney(getCell(sheet, row, cols, "tax", "gst", "tax amount"));
    let total = parseMoney(getCell(sheet, row, cols, "total", "total amount", "invoice total"));
    const paid = parseMoney(getCell(sheet, row, cols, "paid", "amount paid", "payment"));
    const issueDate = parseDateIso(getCell(sheet, row, cols, "invoice date", "issue date", "date invoiced"));
    if (total === 0 && net > 0) total = net + tax;

    await getOrCreateInvoice(
      db,
      invMdId ?? `inv-${invNumber}`,
      job.Id,
      customerId,
      caravanRego,
      invNumber,
      issueDate,
      net,
      tax,
      total,
      paid,
      getCell(sheet, row, cols, "invoice status", "payment status") ?? (paid >= total && total > 0 ? "Paid" : "Outstanding"),
      result,
    );
  }
}

// ── Flat single-sheet import (original behaviour) ──

async function importFlatSheet(db: D1Database, sheet: ExcelJS.Worksheet, result: ImportResult): Promise<void> {
  if (sheetRowCount(sheet) === 0) {
    result.warnings.push("The sheet appears to be empty.");
    return;
  }

  const cols = buildColumnMap(sheet);
  for (let row = 2; row <= sheetRowCount(sheet) + 1; row++) {
    try {
      await processFlatRow(db, sheet, row, cols, result);
    } catch (ex) {
      result.errors.push(`Row ${row}: ${(ex as Error).message}`);
    }
  }
}

async function processFlatRow(db: D1Database, sheet: ExcelJS.Worksheet, row: number, cols: Map<string, number>, result: ImportResult): Promise<void> {
  const customerId = getCell(sheet, row, cols, "customer id", "customerid", "client id");
  const customerName = getCell(sheet, row, cols, "customer", "customer name", "client name", "name");

  if (!customerName) {
    result.warnings.push(`Row ${row}: skipped — no customer name.`);
    return;
  }

  const customer = await getOrCreateCustomer(
    db,
    customerId,
    customerName,
    getCell(sheet, row, cols, "email", "customer email"),
    getCell(sheet, row, cols, "phone", "customer phone", "telephone"),
    getCell(sheet, row, cols, "mobile", "mobile phone"),
    getCell(sheet, row, cols, "address", "street address"),
    getCell(sheet, row, cols, "suburb", "city"),
    getCell(sheet, row, cols, "state"),
    getCell(sheet, row, cols, "postcode", "post code", "zip"),
    getCell(sheet, row, cols, "customer number", "account number"),
    result,
  );

  const vehicleId = getCell(sheet, row, cols, "vehicle id", "vehicleid", "asset id");
  const vin = getCell(sheet, row, cols, "vin", "chassis", "chassis number", "serial number");
  const rego = getCell(
    sheet,
    row,
    cols,
    "rego",
    "registration",
    "registration number",
    "reg",
    "reg number",
    "reg no",
    "number plate",
    "plate",
    "license plate",
    "licence plate",
    "numberplate",
  );
  const make = getCell(sheet, row, cols, "make", "vehicle make");
  const model = getCell(sheet, row, cols, "model", "vehicle model");
  const year = parseIntOrNull(getCell(sheet, row, cols, "year", "vehicle year", "manufacture year"), 1900);
  const color = getCell(sheet, row, cols, "color", "colour");
  const body = getCell(sheet, row, cols, "body", "body type", "type");
  const selfContainmentDue = parseDateIso(getCell(sheet, row, cols, "self containment due"));

  if (!vin && !rego && !vehicleId) return;

  const caravan = await getOrCreateCaravan(
    db,
    vehicleId,
    customer.Id,
    vin,
    rego,
    make,
    model,
    year,
    color,
    body,
    getCell(sheet, row, cols, "self containment"),
    selfContainmentDue,
    result,
  );
  if (!caravan) return;

  const jobMdId = getCell(sheet, row, cols, "job id", "jobid", "job number", "job #");
  const jobNumber = getCell(sheet, row, cols, "job number", "job no", "work order");
  if (jobMdId === null && jobNumber === null) return;

  const status = getCell(sheet, row, cols, "status", "job status");
  const jobType = getCell(sheet, row, cols, "job type", "type", "service type");
  const description = getCell(sheet, row, cols, "description", "job description", "work description");
  const notes = getCell(sheet, row, cols, "notes", "comments", "technician notes");
  const startDate = parseDateIso(getCell(sheet, row, cols, "start date", "date started", "booked date"));
  const finishDate = parseDateIso(getCell(sheet, row, cols, "finish date", "date completed", "completion date"));
  const techName = getCell(sheet, row, cols, "technician", "mechanic", "finished by", "assigned to");
  const hoursStr = getCell(sheet, row, cols, "hours", "labour hours", "estimated hours");
  const hours = hoursStr ? parseFloat(hoursStr) : NaN;

  const job = await getOrCreateJob(
    db,
    jobMdId ?? `row-${jobNumber}`,
    caravan.RegistrationNumber,
    customer.Id,
    jobNumber,
    status ?? "Completed",
    jobType,
    description,
    notes,
    startDate,
    finishDate,
    techName,
    Number.isFinite(hours) && hours !== 0 ? hours : null,
    result,
  );

  if (finishDate && (!caravan.LastJobDate || finishDate > caravan.LastJobDate)) {
    await db
      .prepare("UPDATE Caravans SET LastJobDate = ?, UpdatedAt = ? WHERE RegistrationNumber = ?")
      .bind(finishDate, new Date().toISOString(), caravan.RegistrationNumber)
      .run();
  }

  const invMdId = getCell(sheet, row, cols, "invoice id", "invoiceid", "invoice number", "invoice #");
  const invNumber = getCell(sheet, row, cols, "invoice number", "invoice no");
  if (invMdId !== null || invNumber !== null) {
    const net = parseMoney(getCell(sheet, row, cols, "net", "net amount", "subtotal"));
    const tax = parseMoney(getCell(sheet, row, cols, "tax", "gst", "tax amount"));
    let total = parseMoney(getCell(sheet, row, cols, "total", "total amount", "invoice total"));
    const paid = parseMoney(getCell(sheet, row, cols, "paid", "amount paid", "payment"));
    const invStatus = getCell(sheet, row, cols, "invoice status", "payment status");
    const issueDate = parseDateIso(getCell(sheet, row, cols, "invoice date", "issue date", "date invoiced"));
    if (total === 0 && net > 0) total = net + tax;

    await getOrCreateInvoice(
      db,
      invMdId ?? `inv-${invNumber}`,
      job.Id,
      customer.Id,
      caravan.RegistrationNumber,
      invNumber,
      issueDate,
      net,
      tax,
      total,
      paid,
      invStatus ?? (paid >= total && total > 0 ? "Paid" : "Outstanding"),
      result,
    );
  }
}
