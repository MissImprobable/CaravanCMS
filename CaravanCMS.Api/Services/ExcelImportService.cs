using CaravanCMS.Api.Data;
using CaravanCMS.Core;
using Microsoft.EntityFrameworkCore;
using OfficeOpenXml;

namespace CaravanCMS.Api.Services;

public class ExcelImportService
{
    private readonly ApplicationDbContext _db;
    private readonly ILogger<ExcelImportService> _logger;

    public ExcelImportService(ApplicationDbContext db, ILogger<ExcelImportService> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<ImportResultDto> ImportAsync(Stream excelStream, string fileName)
    {
        ImportResultDto result = new();
        System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();

        using ExcelPackage package = new(excelStream);

        ExcelWorksheet? customersSheet = FindSheet(package, "Customers", "Customer List", "Clients", "Client List");
        ExcelWorksheet? vehiclesSheet = FindSheet(package, "Vehicles", "Vehicle List", "Assets", "Asset List", "Caravans", "Caravan List");
        ExcelWorksheet? jobsSheet = FindSheet(package, "Jobs", "Job List", "Service Jobs", "Work Orders");

        bool isMultiSheet = customersSheet is not null || vehiclesSheet is not null;

        if (isMultiSheet)
        {
            _logger.LogInformation("Multi-sheet export detected. Sheets found: {Sheets}",
                string.Join(", ", new[] { customersSheet?.Name, vehiclesSheet?.Name, jobsSheet?.Name }.Where(n => n is not null)));
            await ImportMultiSheetAsync(customersSheet, vehiclesSheet, jobsSheet, result);
        }
        else
        {
            ExcelWorksheet? flatSheet = jobsSheet ?? package.Workbook.Worksheets.FirstOrDefault(s => s.Dimension is not null);
            if (flatSheet is null)
            {
                result.Errors.Add("Could not find a recognisable sheet in the Excel file. Expected sheets named 'Customers', 'Vehicles', or 'Jobs'.");
                return result;
            }
            _logger.LogInformation("Single flat sheet '{Name}' with {Rows} rows", flatSheet.Name, flatSheet.Dimension?.Rows ?? 0);
            await ImportFlatSheetAsync(flatSheet, result);
        }

        await _db.SaveChangesAsync();

        sw.Stop();
        result.Duration = sw.Elapsed;
        return result;
    }

    // ── Multi-sheet import ────────────────────────────────────────────────────

    private async Task ImportMultiSheetAsync(
        ExcelWorksheet? customersSheet,
        ExcelWorksheet? vehiclesSheet,
        ExcelWorksheet? jobsSheet,
        ImportResultDto result)
    {
        // Maps MechanicDesk IDs / rego → registration numbers (the caravan PK), used to link child records
        Dictionary<string, int> customerMdIdToDbId = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, string> vehicleRegoMap = new(StringComparer.OrdinalIgnoreCase);

        // ── Step 1: Customers ──────────────────────────────────────────────────
        if (customersSheet?.Dimension is not null)
        {
            _logger.LogInformation("Importing Customers sheet '{Name}' ({Rows} rows)", customersSheet.Name, customersSheet.Dimension.Rows);
            Dictionary<string, int> cols = BuildColumnMap(customersSheet);
            result.Warnings.Add($"[Columns] {customersSheet.Name}: {string.Join(", ", cols.Keys)}");

            for (int row = 2; row <= customersSheet.Dimension.Rows; row++)
            {
                try
                {
                    string? mdId = GetCell(customersSheet, row, cols, "customer id", "customerid", "client id", "clientid", "id");
                    string? name = GetCell(customersSheet, row, cols, "customer", "customer name", "client", "client name", "name", "full name");
                    if (string.IsNullOrWhiteSpace(name)) continue;

                    Customer customer = await GetOrCreateCustomerAsync(
                        mdId: mdId,
                        name: name,
                        email: GetCell(customersSheet, row, cols, "email", "email address"),
                        phone: GetCell(customersSheet, row, cols, "phone", "phone number", "telephone", "work phone"),
                        mobile: GetCell(customersSheet, row, cols, "mobile", "mobile phone", "mobile number", "cell"),
                        address: GetCell(customersSheet, row, cols, "address", "street address", "street"),
                        suburb: GetCell(customersSheet, row, cols, "suburb", "city", "town"),
                        state: GetCell(customersSheet, row, cols, "state"),
                        postcode: GetCell(customersSheet, row, cols, "postcode", "post code", "zip"),
                        customerNumber: GetCell(customersSheet, row, cols, "customer number", "customer no", "account number", "account no"),
                        result: result);

                    if (!string.IsNullOrEmpty(mdId))
                        customerMdIdToDbId[mdId] = customer.Id;
                }
                catch (Exception ex)
                {
                    result.Errors.Add($"Customers row {row}: {ex.Message}");
                }
            }
        }

        // ── Step 2: Vehicles ───────────────────────────────────────────────────
        if (vehiclesSheet?.Dimension is not null)
        {
            _logger.LogInformation("Importing Vehicles sheet '{Name}' ({Rows} rows)", vehiclesSheet.Name, vehiclesSheet.Dimension.Rows);
            Dictionary<string, int> cols = BuildColumnMap(vehiclesSheet);
            result.Warnings.Add($"[Columns] {vehiclesSheet.Name}: {string.Join(", ", cols.Keys)}");

            for (int row = 2; row <= vehiclesSheet.Dimension.Rows; row++)
            {
                try
                {
                    string? mdId = GetCell(vehiclesSheet, row, cols, "vehicle id", "vehicleid", "asset id", "assetid", "id");
                    string? rego = GetCell(vehiclesSheet, row, cols,
                        "rego", "registration", "registration number", "reg", "reg number", "reg no",
                        "number plate", "plate", "license plate", "licence plate", "numberplate");
                    string? vin = GetCell(vehiclesSheet, row, cols, "vin", "chassis", "chassis number", "serial number", "serial no");

                    // Rego is the single most important value — warn clearly when absent
                    if (string.IsNullOrWhiteSpace(rego))
                    {
                        string identifier = mdId ?? vin ?? $"row {row}";
                        result.Warnings.Add($"Vehicles row {row} ({identifier}): no registration number (rego) found — this vehicle will be identified by {(string.IsNullOrWhiteSpace(vin) ? "vehicle ID only" : "VIN only")}.");
                    }

                    if (string.IsNullOrWhiteSpace(rego) && string.IsNullOrWhiteSpace(vin) && string.IsNullOrWhiteSpace(mdId))
                    {
                        result.Warnings.Add($"Vehicles row {row}: skipped — no rego, VIN, or vehicle ID. Cannot identify this vehicle.");
                        continue;
                    }

                    // Resolve the owning customer
                    string? customerMdId = GetCell(vehiclesSheet, row, cols, "customer id", "customerid", "client id", "clientid", "owner id", "ownerid");
                    int? customerId = ResolveCustomerId(customerMdId, customerMdIdToDbId);

                    if (customerId is null && !string.IsNullOrEmpty(customerMdId))
                    {
                        // May have been imported in a previous run — check DB
                        Customer? existing = await _db.Customers.FirstOrDefaultAsync(c => c.MechanicDeskId == customerMdId);
                        if (existing is not null) customerId = existing.Id;
                    }

                    if (customerId is null)
                    {
                        string identifier = rego ?? vin ?? mdId ?? $"row {row}";
                        result.Warnings.Add($"Vehicles row {row} (rego: {identifier}): skipped — no matching customer found (customer ID '{customerMdId ?? "none"}').");
                        continue;
                    }

                    string? yearStr = GetCell(vehiclesSheet, row, cols, "year", "vehicle year", "manufacture year", "model year", "yr");
                    string? odometerStr = GetCell(vehiclesSheet, row, cols, "odometer", "current odometer", "odo", "kilometres", "km", "mileage");
                    string? selfContainmentDueStr = GetCell(vehiclesSheet, row, cols, "self containment due");
                    int.TryParse(yearStr, out int year);
                    int.TryParse(odometerStr?.Replace(",", ""), out int odometer);
                    DateTime.TryParse(selfContainmentDueStr, out DateTime selfContainmentDue);

                    Caravan? caravan = await GetOrCreateCaravanAsync(
                        mdId: mdId,
                        customerId: customerId.Value,
                        vin: vin,
                        rego: rego,
                        make: GetCell(vehiclesSheet, row, cols, "make", "vehicle make", "manufacturer", "brand"),
                        model: GetCell(vehiclesSheet, row, cols, "model", "vehicle model", "model name"),
                        year: year > 1900 ? year : null,
                        color: GetCell(vehiclesSheet, row, cols, "colour", "color", "vehicle colour"),
                        body: GetCell(vehiclesSheet, row, cols, "body", "body type", "type", "category", "style"),
                        selfContainment: GetCell(vehiclesSheet, row, cols, "self containment"),
                        selfContainmentDue: selfContainmentDue == default ? null : selfContainmentDue,
                        result: result);

                    if (caravan is null) continue;

                    if (odometer > 0 && caravan.CurrentOdometer is null)
                    {
                        caravan.CurrentOdometer = odometer;
                        caravan.UpdatedAt = DateTime.UtcNow;
                    }

                    string key = mdId ?? rego ?? vin!;
                    vehicleRegoMap[key] = caravan.RegistrationNumber;
                    if (!string.IsNullOrEmpty(rego) && !vehicleRegoMap.ContainsKey(rego))
                        vehicleRegoMap[rego] = caravan.RegistrationNumber;
                }
                catch (Exception ex)
                {
                    result.Errors.Add($"Vehicles row {row}: {ex.Message}");
                }
            }

            await _db.SaveChangesAsync();
        }
        else
        {
            result.Warnings.Add("No Vehicles sheet found in this export — vehicle (caravan) data may be missing.");
        }

        // ── Step 3: Jobs ───────────────────────────────────────────────────────
        if (jobsSheet?.Dimension is not null)
        {
            _logger.LogInformation("Importing Jobs sheet '{Name}' ({Rows} rows)", jobsSheet.Name, jobsSheet.Dimension.Rows);
            Dictionary<string, int> cols = BuildColumnMap(jobsSheet);

            for (int row = 2; row <= jobsSheet.Dimension.Rows; row++)
            {
                try
                {
                    await ProcessJobRowAsync(jobsSheet, row, cols, vehicleRegoMap, customerMdIdToDbId, result);
                }
                catch (Exception ex)
                {
                    result.Errors.Add($"Jobs row {row}: {ex.Message}");
                }
            }
        }
    }

    private async Task ProcessJobRowAsync(
        ExcelWorksheet sheet, int row, Dictionary<string, int> cols,
        Dictionary<string, string> vehicleMap, Dictionary<string, int> customerMap,
        ImportResultDto result)
    {
        string? vehicleMdId = GetCell(sheet, row, cols, "vehicle id", "vehicleid", "asset id", "assetid");
        string? rego = GetCell(sheet, row, cols,
            "rego", "registration", "registration number", "reg", "reg number", "reg no",
            "number plate", "plate", "license plate", "licence plate");

        // Resolve caravan rego: try vehicle ID first (via map), then rego directly
        string? caravanRego = null;
        if (!string.IsNullOrEmpty(vehicleMdId) && vehicleMap.TryGetValue(vehicleMdId, out string? v1))
            caravanRego = v1;
        if (caravanRego is null && !string.IsNullOrEmpty(rego) && vehicleMap.TryGetValue(rego, out string? v2))
            caravanRego = v2;
        if (caravanRego is null && !string.IsNullOrEmpty(rego))
            caravanRego = rego; // rego IS the PK — use it directly
        if (caravanRego is null && !string.IsNullOrEmpty(vehicleMdId))
        {
            Caravan? c = await _db.Caravans.FirstOrDefaultAsync(c => c.MechanicDeskId == vehicleMdId);
            if (c is not null) caravanRego = c.RegistrationNumber;
        }

        if (caravanRego is null)
        {
            result.Warnings.Add($"Jobs row {row}: skipped — could not find matching vehicle (vehicle ID '{vehicleMdId ?? "none"}', rego '{rego ?? "none"}').");
            return;
        }

        string? customerMdId = GetCell(sheet, row, cols, "customer id", "customerid", "client id");
        int? customerId = ResolveCustomerId(customerMdId, customerMap);
        if (customerId is null && !string.IsNullOrEmpty(customerMdId))
        {
            Customer? c = await _db.Customers.FirstOrDefaultAsync(c => c.MechanicDeskId == customerMdId);
            if (c is not null) customerId = c.Id;
        }
        if (customerId is null)
        {
            // Fall back to the caravan's owner
            Caravan? caravan = await _db.Caravans.FirstOrDefaultAsync(c => c.RegistrationNumber == caravanRego);
            customerId = caravan?.CustomerId;
        }
        if (customerId is null)
        {
            result.Warnings.Add($"Jobs row {row}: skipped — could not resolve customer.");
            return;
        }

        string? jobMdId = GetCell(sheet, row, cols, "job id", "jobid", "job number", "job #");
        string? jobNumber = GetCell(sheet, row, cols, "job number", "job no", "work order", "work order number");
        if (jobMdId is null && jobNumber is null)
        {
            result.Warnings.Add($"Jobs row {row}: skipped — no job ID or job number.");
            return;
        }

        string? startDateStr = GetCell(sheet, row, cols, "start date", "date started", "booked date", "booking date");
        string? finishDateStr = GetCell(sheet, row, cols, "finish date", "date completed", "completion date", "completed date");
        string? hoursStr = GetCell(sheet, row, cols, "hours", "labour hours", "labor hours", "estimated hours");
        DateTime.TryParse(startDateStr, out DateTime startDate);
        DateTime.TryParse(finishDateStr, out DateTime finishDate);
        decimal.TryParse(hoursStr, out decimal hours);

        Job job = await GetOrCreateJobAsync(
            mdId: jobMdId ?? $"row-{jobNumber}",
            caravanRego: caravanRego,
            customerId: customerId.Value,
            jobNumber: jobNumber,
            status: GetCell(sheet, row, cols, "status", "job status") ?? "Completed",
            jobType: GetCell(sheet, row, cols, "job type", "type", "service type"),
            description: GetCell(sheet, row, cols, "description", "job description", "work description"),
            notes: GetCell(sheet, row, cols, "notes", "comments", "technician notes"),
            startDate: startDate == default ? null : startDate,
            finishDate: finishDate == default ? null : finishDate,
            finishedBy: GetCell(sheet, row, cols, "technician", "mechanic", "finished by", "assigned to"),
            estimatedHours: hours == 0 ? null : hours,
            result: result);

        if (finishDate != default)
        {
            Caravan? caravan = await _db.Caravans.FirstOrDefaultAsync(c => c.RegistrationNumber == caravanRego);
            if (caravan is not null && (caravan.LastJobDate is null || finishDate > caravan.LastJobDate))
            {
                caravan.LastJobDate = finishDate;
                caravan.UpdatedAt = DateTime.UtcNow;
            }
        }

        // Invoice
        string? invMdId = GetCell(sheet, row, cols, "invoice id", "invoiceid", "invoice number", "invoice #");
        string? invNumber = GetCell(sheet, row, cols, "invoice number", "invoice no");
        if (invMdId is not null || invNumber is not null)
        {
            string? netStr = GetCell(sheet, row, cols, "net", "net amount", "subtotal");
            string? taxStr = GetCell(sheet, row, cols, "tax", "gst", "tax amount");
            string? totalStr = GetCell(sheet, row, cols, "total", "total amount", "invoice total");
            string? paidStr = GetCell(sheet, row, cols, "paid", "amount paid", "payment");
            string? issueDateStr = GetCell(sheet, row, cols, "invoice date", "issue date", "date invoiced");

            decimal.TryParse(netStr?.Replace("$", "").Replace(",", ""), out decimal net);
            decimal.TryParse(taxStr?.Replace("$", "").Replace(",", ""), out decimal tax);
            decimal.TryParse(totalStr?.Replace("$", "").Replace(",", ""), out decimal total);
            decimal.TryParse(paidStr?.Replace("$", "").Replace(",", ""), out decimal paid);
            DateTime.TryParse(issueDateStr, out DateTime issueDate);
            if (total == 0 && net > 0) total = net + tax;

            await GetOrCreateInvoiceAsync(
                mdId: invMdId ?? $"inv-{invNumber}",
                jobId: job.Id,
                customerId: customerId.Value,
                caravanRego: caravanRego,
                invoiceNumber: invNumber,
                issueDate: issueDate == default ? null : issueDate,
                net: net, tax: tax, total: total, paid: paid,
                status: GetCell(sheet, row, cols, "invoice status", "payment status")
                    ?? (paid >= total && total > 0 ? "Paid" : "Outstanding"),
                result: result);
        }
    }

    // ── Flat single-sheet import (original behaviour) ─────────────────────────

    private async Task ImportFlatSheetAsync(ExcelWorksheet sheet, ImportResultDto result)
    {
        if (sheet.Dimension is null)
        {
            result.Warnings.Add("The sheet appears to be empty.");
            return;
        }

        Dictionary<string, int> colMap = BuildColumnMap(sheet);
        _logger.LogDebug("Column map: {Map}", string.Join(", ", colMap.Select(kv => $"{kv.Key}={kv.Value}")));

        for (int row = 2; row <= sheet.Dimension.Rows; row++)
        {
            try
            {
                await ProcessFlatRowAsync(sheet, row, colMap, result);
            }
            catch (Exception ex)
            {
                result.Errors.Add($"Row {row}: {ex.Message}");
                _logger.LogWarning("Import error on row {Row}: {Error}", row, ex.Message);
            }
        }
    }

    private async Task ProcessFlatRowAsync(ExcelWorksheet sheet, int row, Dictionary<string, int> cols, ImportResultDto result)
    {
        string? customerId = GetCell(sheet, row, cols, "customer id", "customerid", "client id");
        string? customerName = GetCell(sheet, row, cols, "customer", "customer name", "client name", "name");

        if (string.IsNullOrWhiteSpace(customerName))
        {
            result.Warnings.Add($"Row {row}: skipped — no customer name.");
            return;
        }

        Customer customer = await GetOrCreateCustomerAsync(
            mdId: customerId,
            name: customerName,
            email: GetCell(sheet, row, cols, "email", "customer email"),
            phone: GetCell(sheet, row, cols, "phone", "customer phone", "telephone"),
            mobile: GetCell(sheet, row, cols, "mobile", "mobile phone"),
            address: GetCell(sheet, row, cols, "address", "street address"),
            suburb: GetCell(sheet, row, cols, "suburb", "city"),
            state: GetCell(sheet, row, cols, "state"),
            postcode: GetCell(sheet, row, cols, "postcode", "post code", "zip"),
            customerNumber: GetCell(sheet, row, cols, "customer number", "account number"),
            result: result);

        string? vehicleId = GetCell(sheet, row, cols, "vehicle id", "vehicleid", "asset id");
        string? vin = GetCell(sheet, row, cols, "vin", "chassis", "chassis number", "serial number");
        string? rego = GetCell(sheet, row, cols,
            "rego", "registration", "registration number", "reg", "reg number", "reg no",
            "number plate", "plate", "license plate", "licence plate", "numberplate");
        string? make = GetCell(sheet, row, cols, "make", "vehicle make");
        string? model = GetCell(sheet, row, cols, "model", "vehicle model");
        string? yearStr = GetCell(sheet, row, cols, "year", "vehicle year", "manufacture year");
        string? color = GetCell(sheet, row, cols, "color", "colour");
        string? body = GetCell(sheet, row, cols, "body", "body type", "type");
        string? selfContainmentDueStr = GetCell(sheet, row, cols, "self containment due");

        int.TryParse(yearStr, out int year);
        DateTime.TryParse(selfContainmentDueStr, out DateTime selfContainmentDue);

        if (vin is not null || rego is not null || vehicleId is not null)
        {
            Caravan? caravan = await GetOrCreateCaravanAsync(
                mdId: vehicleId,
                customerId: customer.Id,
                vin: vin,
                rego: rego,
                make: make,
                model: model,
                year: year > 1900 ? year : null,
                color: color,
                body: body,
                selfContainment: GetCell(sheet, row, cols, "self containment"),
                selfContainmentDue: selfContainmentDue == default ? null : selfContainmentDue,
                result: result);

            if (caravan is null) return;

            string? jobMdId = GetCell(sheet, row, cols, "job id", "jobid", "job number", "job #");
            string? jobNumber = GetCell(sheet, row, cols, "job number", "job no", "work order");
            string? status = GetCell(sheet, row, cols, "status", "job status");
            string? jobType = GetCell(sheet, row, cols, "job type", "type", "service type");
            string? description = GetCell(sheet, row, cols, "description", "job description", "work description");
            string? notes = GetCell(sheet, row, cols, "notes", "comments", "technician notes");
            string? startDateStr = GetCell(sheet, row, cols, "start date", "date started", "booked date");
            string? finishDateStr = GetCell(sheet, row, cols, "finish date", "date completed", "completion date");
            string? techName = GetCell(sheet, row, cols, "technician", "mechanic", "finished by", "assigned to");
            string? hoursStr = GetCell(sheet, row, cols, "hours", "labour hours", "estimated hours");

            DateTime.TryParse(startDateStr, out DateTime startDate);
            DateTime.TryParse(finishDateStr, out DateTime finishDate);
            decimal.TryParse(hoursStr, out decimal hours);

            if (jobMdId is not null || jobNumber is not null)
            {
                Job job = await GetOrCreateJobAsync(
                    mdId: jobMdId ?? $"row-{jobNumber}",
                    caravanRego: caravan.RegistrationNumber,
                    customerId: customer.Id,
                    jobNumber: jobNumber,
                    status: status ?? "Completed",
                    jobType: jobType,
                    description: description,
                    notes: notes,
                    startDate: startDate == default ? null : startDate,
                    finishDate: finishDate == default ? null : finishDate,
                    finishedBy: techName,
                    estimatedHours: hours == 0 ? null : hours,
                    result: result);

                if (finishDate != default && (caravan!.LastJobDate is null || finishDate > caravan.LastJobDate))
                {
                    caravan.LastJobDate = finishDate;
                    caravan.UpdatedAt = DateTime.UtcNow;
                }

                string? invMdId = GetCell(sheet, row, cols, "invoice id", "invoiceid", "invoice number", "invoice #");
                string? invNumber = GetCell(sheet, row, cols, "invoice number", "invoice no");
                string? netStr = GetCell(sheet, row, cols, "net", "net amount", "subtotal");
                string? taxStr = GetCell(sheet, row, cols, "tax", "gst", "tax amount");
                string? totalStr = GetCell(sheet, row, cols, "total", "total amount", "invoice total");
                string? paidStr = GetCell(sheet, row, cols, "paid", "amount paid", "payment");
                string? invStatus = GetCell(sheet, row, cols, "invoice status", "payment status");
                string? issueDateStr = GetCell(sheet, row, cols, "invoice date", "issue date", "date invoiced");

                if (invMdId is not null || invNumber is not null)
                {
                    decimal.TryParse(netStr?.Replace("$", "").Replace(",", ""), out decimal net);
                    decimal.TryParse(taxStr?.Replace("$", "").Replace(",", ""), out decimal tax);
                    decimal.TryParse(totalStr?.Replace("$", "").Replace(",", ""), out decimal total);
                    decimal.TryParse(paidStr?.Replace("$", "").Replace(",", ""), out decimal paid);
                    DateTime.TryParse(issueDateStr, out DateTime issueDate);
                    if (total == 0 && net > 0) total = net + tax;

                    await GetOrCreateInvoiceAsync(
                        mdId: invMdId ?? $"inv-{invNumber}",
                        jobId: job.Id,
                        customerId: customer.Id,
                        caravanRego: caravan.RegistrationNumber,
                        invoiceNumber: invNumber,
                        issueDate: issueDate == default ? null : issueDate,
                        net: net, tax: tax, total: total, paid: paid,
                        status: invStatus ?? (paid >= total && total > 0 ? "Paid" : "Outstanding"),
                        result: result);
                }
            }
        }
    }

    // ── Entity upsert helpers ─────────────────────────────────────────────────

    private async Task<Customer> GetOrCreateCustomerAsync(
        string? mdId, string name, string? email, string? phone, string? mobile,
        string? address, string? suburb, string? state, string? postcode,
        string? customerNumber, ImportResultDto result)
    {
        Customer? existing = null;

        if (!string.IsNullOrEmpty(mdId))
            existing = await _db.Customers.FirstOrDefaultAsync(c => c.MechanicDeskId == mdId);

        if (existing is null && !string.IsNullOrEmpty(customerNumber))
            existing = await _db.Customers.FirstOrDefaultAsync(c => c.CustomerNumber == customerNumber);

        if (existing is not null)
        {
            if (existing.Name != name || existing.Email != email)
            {
                result.Conflicts.Add(new ImportConflictDto
                {
                    EntityType = "Customer",
                    MechanicDeskId = mdId ?? customerNumber ?? name,
                    ExistingEntityId = existing.Id,
                    ExistingDescription = $"{existing.Name} ({existing.Email})",
                    IncomingDescription = $"{name} ({email})",
                    ChangedFields = BuildChangedFields(existing.Name, name, existing.Email, email)
                });
            }
            result.CustomersUpdated++;
            return existing;
        }

        Customer customer = new()
        {
            Name = name,
            Email = email,
            Phone = phone,
            Mobile = mobile,
            Address = address,
            Suburb = suburb,
            State = state,
            Postcode = postcode,
            CustomerNumber = customerNumber,
            MechanicDeskId = mdId,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        _db.Customers.Add(customer);
        await _db.SaveChangesAsync();
        result.CustomersImported++;
        return customer;
    }

    private async Task<Caravan?> GetOrCreateCaravanAsync(
        string? mdId, int customerId, string? vin, string? rego,
        string? make, string? model, int? year, string? color, string? body,
        string? selfContainment, DateTime? selfContainmentDue,
        ImportResultDto result)
    {
        Caravan? existing = null;

        if (!string.IsNullOrEmpty(mdId))
            existing = await _db.Caravans.FirstOrDefaultAsync(c => c.MechanicDeskId == mdId);

        if (existing is null && !string.IsNullOrEmpty(vin))
            existing = await _db.Caravans.FirstOrDefaultAsync(c => c.Vin == vin);

        // Rego is the primary real-world identifier — check it last so mdId/VIN win on exact match,
        // but rego can still find an existing record if the others are absent
        if (existing is null && !string.IsNullOrEmpty(rego))
            existing = await _db.Caravans.FirstOrDefaultAsync(c => c.RegistrationNumber == rego);

        if (existing is not null)
        {
            bool changed = false;

            if (string.IsNullOrEmpty(existing.Vin) && !string.IsNullOrEmpty(vin))
            { existing.Vin = vin; changed = true; }

            if (string.IsNullOrEmpty(existing.Make) && !string.IsNullOrEmpty(make))
            { existing.Make = make; changed = true; }

            if (string.IsNullOrEmpty(existing.Model) && !string.IsNullOrEmpty(model))
            { existing.Model = model; changed = true; }

            if (existing.Year is null && year is not null)
            { existing.Year = year; changed = true; }

            if (string.IsNullOrEmpty(existing.SelfContainment) && !string.IsNullOrEmpty(selfContainment))
            { existing.SelfContainment = selfContainment; changed = true; }

            if (existing.SelfContainmentDue is null && selfContainmentDue is not null)
            { existing.SelfContainmentDue = selfContainmentDue; changed = true; }

            if (changed) existing.UpdatedAt = DateTime.UtcNow;

            result.CaravansUpdated++;
            return existing;
        }

        if (string.IsNullOrWhiteSpace(rego))
        {
            result.Warnings.Add($"Skipping caravan (mdId: {mdId ?? "none"}, vin: {vin ?? "none"}) — no registration number (rego is required as primary key).");
            return null;
        }

        Caravan caravan = new()
        {
            RegistrationNumber = rego,
            CustomerId = customerId,
            Vin = vin,
            Make = make,
            Model = model,
            Year = year,
            Color = color,
            Body = body,
            SelfContainment = selfContainment,
            SelfContainmentDue = selfContainmentDue,
            MechanicDeskId = mdId,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        _db.Caravans.Add(caravan);
        await _db.SaveChangesAsync();
        result.CaravansImported++;
        return caravan;
    }

    private async Task<Job> GetOrCreateJobAsync(
        string mdId, string caravanRego, int customerId, string? jobNumber, string? status,
        string? jobType, string? description, string? notes,
        DateTime? startDate, DateTime? finishDate, string? finishedBy,
        decimal? estimatedHours, ImportResultDto result)
    {
        Job? existing = await _db.Jobs.FirstOrDefaultAsync(j => j.MechanicDeskId == mdId);

        if (existing is not null)
        {
            result.JobsUpdated++;
            return existing;
        }

        Job job = new()
        {
            RegistrationNumber = caravanRego,
            CustomerId = customerId,
            JobNumber = jobNumber,
            Status = status,
            JobType = jobType,
            Description = description,
            Notes = notes,
            StartDate = startDate,
            FinishDate = finishDate,
            FinishedBy = finishedBy,
            EstimatedHours = estimatedHours,
            MechanicDeskId = mdId,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        _db.Jobs.Add(job);
        await _db.SaveChangesAsync();
        result.JobsImported++;
        return job;
    }

    private async Task GetOrCreateInvoiceAsync(
        string mdId, int jobId, int customerId, string caravanRego,
        string? invoiceNumber, DateTime? issueDate,
        decimal net, decimal tax, decimal total, decimal paid,
        string? status, ImportResultDto result)
    {
        bool exists = await _db.Invoices.AnyAsync(i => i.MechanicDeskId == mdId);
        if (exists) return;

        Invoice invoice = new()
        {
            JobId = jobId,
            CustomerId = customerId,
            RegistrationNumber = caravanRego,
            InvoiceNumber = invoiceNumber,
            IssueDate = issueDate,
            NetAmount = net,
            TaxAmount = tax,
            TotalAmount = total,
            PaidAmount = paid,
            Status = status,
            MechanicDeskId = mdId,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        _db.Invoices.Add(invoice);
        result.InvoicesImported++;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static int? ResolveCustomerId(string? mdId, Dictionary<string, int> map)
    {
        if (string.IsNullOrEmpty(mdId)) return null;
        return map.TryGetValue(mdId, out int id) ? id : null;
    }

    private static ExcelWorksheet? FindSheet(ExcelPackage package, params string[] names)
    {
        foreach (string name in names)
        {
            ExcelWorksheet? sheet = package.Workbook.Worksheets
                .FirstOrDefault(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (sheet is not null) return sheet;
        }
        return package.Workbook.Worksheets.FirstOrDefault(s => s.Dimension is not null);
    }

    private static Dictionary<string, int> BuildColumnMap(ExcelWorksheet sheet)
    {
        Dictionary<string, int> map = new(StringComparer.OrdinalIgnoreCase);
        int cols = sheet.Dimension?.Columns ?? 0;

        for (int col = 1; col <= cols; col++)
        {
            string? header = sheet.Cells[1, col].Text?.Trim().ToLower();
            if (!string.IsNullOrEmpty(header) && !map.ContainsKey(header))
                map[header] = col;
        }
        return map;
    }

    private static string? GetCell(ExcelWorksheet sheet, int row, Dictionary<string, int> cols, params string[] names)
    {
        foreach (string name in names)
        {
            if (cols.TryGetValue(name, out int col))
            {
                string? value = sheet.Cells[row, col].Text?.Trim();
                if (!string.IsNullOrEmpty(value))
                    return value;
            }
        }
        return null;
    }

    private static Dictionary<string, string[]> BuildChangedFields(string? existName, string? inName, string? existEmail, string? inEmail)
    {
        Dictionary<string, string[]> changes = new();
        if (existName != inName)
            changes["Name"] = new[] { existName ?? "", inName ?? "" };
        if (existEmail != inEmail)
            changes["Email"] = new[] { existEmail ?? "", inEmail ?? "" };
        return changes;
    }
}
