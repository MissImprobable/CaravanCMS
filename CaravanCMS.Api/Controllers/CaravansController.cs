using CaravanCMS.Api.Data;
using CaravanCMS.Core;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CaravanCMS.Api.Controllers;

/// <summary>Endpoints for browsing and searching the caravan fleet.</summary>
[ApiController]
[Route("api/[controller]")]
[Produces("application/json")]
public class CaravansController : ControllerBase
{
    private readonly ApplicationDbContext _db;
    private readonly ILogger<CaravansController> _logger;

    public CaravansController(ApplicationDbContext db, ILogger<CaravansController> logger)
    {
        _db = db;
        _logger = logger;
    }

    /// <summary>Returns all caravans with summary info and counts.</summary>
    [HttpGet]
    [ProducesResponseType(typeof(List<CaravanSummaryDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<List<CaravanSummaryDto>>> GetAll()
    {
        List<CaravanSummaryDto> result = await _db.Caravans
            .Include(c => c.Customer)
            .Select(c => new CaravanSummaryDto
            {
                RegistrationNumber = c.RegistrationNumber,
                Vin = c.Vin,
                Make = c.Make,
                Model = c.Model,
                Year = c.Year,
                Color = c.Color,
                Body = c.Body,
                CurrentOdometer = c.CurrentOdometer,
                LastJobDate = c.LastJobDate,
                SelfContainment = c.SelfContainment,
                SelfContainmentDue = c.SelfContainmentDue,
                CustomerId = c.CustomerId,
                CustomerName = c.Customer.Name,
                CustomerPhone = c.Customer.Phone,
                CustomerEmail = c.Customer.Email,
                JobCount = c.Jobs.Count,
                DocumentCount = c.Documents.Count,
                CreatedAt = c.CreatedAt,
                UpdatedAt = c.UpdatedAt
            })
            .OrderBy(c => c.Make)
            .ThenBy(c => c.Model)
            .ToListAsync();

        _logger.LogInformation("GET /api/caravans returned {Count} records", result.Count);
        return Ok(result);
    }

    /// <summary>Returns the complete history for a single caravan by registration number.</summary>
    /// <param name="rego">Caravan registration number (the primary key).</param>
    [HttpGet("{rego}")]
    [ProducesResponseType(typeof(CaravanDetailDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<CaravanDetailDto>> GetByRego(string rego)
    {
        Caravan? caravan = await _db.Caravans
            .Include(c => c.Customer)
            .Include(c => c.Documents)
            .Include(c => c.Jobs)
                .ThenInclude(j => j.Invoices)
                    .ThenInclude(i => i.Items)
            .FirstOrDefaultAsync(c => c.RegistrationNumber == rego);

        if (caravan is null)
        {
            _logger.LogWarning("GET /api/caravans/{Rego} — not found", rego);
            return NotFound(new { error = $"Caravan {rego} not found." });
        }

        CaravanDetailDto dto = MapToDetailDto(caravan);
        _logger.LogInformation("GET /api/caravans/{Rego} — {Make} {Model}", rego, caravan.Make, caravan.Model);
        return Ok(dto);
    }

    /// <summary>
    /// Fuzzy-searches caravans by VIN, registration, make/model, or customer name.
    /// Returns up to 50 results ordered by relevance.
    /// </summary>
    /// <param name="q">Search term (minimum 2 characters).</param>
    [HttpGet("search")]
    [ProducesResponseType(typeof(List<CaravanSummaryDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<List<CaravanSummaryDto>>> Search([FromQuery] string? q)
    {
        if (string.IsNullOrWhiteSpace(q) || q.Length < 2)
            return BadRequest(new { error = "Search query must be at least 2 characters." });

        string term = q.Trim().ToUpper();

        // Load candidates then filter in memory — SQLite can't do case-insensitive LIKE on all columns efficiently
        List<Caravan> caravans = await _db.Caravans
            .Include(c => c.Customer)
            .ToListAsync();

        List<CaravanSummaryDto> matches = caravans
            .Where(c =>
                Contains(c.Vin, term) ||
                Contains(c.RegistrationNumber, term) ||
                Contains(c.Make, term) ||
                Contains(c.Model, term) ||
                Contains(c.Customer.Name, term) ||
                Contains(c.Customer.CustomerNumber, term))
            .Take(50)
            .Select(c => new CaravanSummaryDto
            {
                RegistrationNumber = c.RegistrationNumber,
                Vin = c.Vin,
                Make = c.Make,
                Model = c.Model,
                Year = c.Year,
                Color = c.Color,
                Body = c.Body,
                CurrentOdometer = c.CurrentOdometer,
                LastJobDate = c.LastJobDate,
                SelfContainment = c.SelfContainment,
                SelfContainmentDue = c.SelfContainmentDue,
                CustomerId = c.CustomerId,
                CustomerName = c.Customer.Name,
                CustomerPhone = c.Customer.Phone,
                CustomerEmail = c.Customer.Email,
                JobCount = c.Jobs.Count,
                DocumentCount = c.Documents.Count,
                CreatedAt = c.CreatedAt,
                UpdatedAt = c.UpdatedAt
            })
            .ToList();

        _logger.LogInformation("Search '{Term}' returned {Count} caravans", q, matches.Count);
        return Ok(matches);
    }

    /// <summary>Returns summary statistics for the dashboard.</summary>
    [HttpGet("stats")]
    [ProducesResponseType(typeof(ApiStatsDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiStatsDto>> GetStats()
    {
        ApiStatsDto stats = new()
        {
            TotalCustomers = await _db.Customers.CountAsync(),
            TotalCaravans = await _db.Caravans.CountAsync(),
            TotalJobs = await _db.Jobs.CountAsync(),
            TotalInvoices = await _db.Invoices.CountAsync(),
            TotalDocuments = await _db.Documents.CountAsync(),
            LastImportAt = await _db.Caravans.MaxAsync(c => (DateTime?)c.UpdatedAt)
        };

        // Get database file size
        string dbPath = _db.Database.GetDbConnection().DataSource;
        if (System.IO.File.Exists(dbPath))
            stats.DatabaseSizeBytes = new System.IO.FileInfo(dbPath).Length;

        return Ok(stats);
    }

    private static bool Contains(string? value, string term) =>
        value is not null && value.ToUpper().Contains(term);

    private static CaravanDetailDto MapToDetailDto(Caravan c) => new()
    {
        RegistrationNumber = c.RegistrationNumber,
        Vin = c.Vin,
        Make = c.Make,
        Model = c.Model,
        Year = c.Year,
        Color = c.Color,
        Body = c.Body,
        CurrentOdometer = c.CurrentOdometer,
        LastJobDate = c.LastJobDate,
        SelfContainment = c.SelfContainment,
        SelfContainmentDue = c.SelfContainmentDue,
        MechanicDeskId = c.MechanicDeskId,
        CreatedAt = c.CreatedAt,
        UpdatedAt = c.UpdatedAt,
        Customer = c.Customer is null ? null : new CustomerDto
        {
            Id = c.Customer.Id,
            Name = c.Customer.Name,
            Email = c.Customer.Email,
            Phone = c.Customer.Phone,
            Mobile = c.Customer.Mobile,
            Address = c.Customer.Address,
            Suburb = c.Customer.Suburb,
            State = c.Customer.State,
            Postcode = c.Customer.Postcode,
            CustomerNumber = c.Customer.CustomerNumber,
            MechanicDeskId = c.Customer.MechanicDeskId,
            CreatedAt = c.Customer.CreatedAt
        },
        Jobs = c.Jobs.OrderByDescending(j => j.FinishDate ?? j.StartDate).Select(j => new JobDetailDto
        {
            Id = j.Id,
            RegistrationNumber = j.RegistrationNumber,
            CustomerId = j.CustomerId,
            JobNumber = j.JobNumber,
            Status = j.Status,
            JobType = j.JobType,
            Description = j.Description,
            Notes = j.Notes,
            StartDate = j.StartDate,
            FinishDate = j.FinishDate,
            EstimatedHours = j.EstimatedHours,
            FinishedBy = j.FinishedBy,
            MechanicDeskId = j.MechanicDeskId,
            CreatedAt = j.CreatedAt,
            UpdatedAt = j.UpdatedAt,
            Invoices = j.Invoices.Select(i => new InvoiceDto
            {
                Id = i.Id,
                JobId = i.JobId,
                CustomerId = i.CustomerId,
                RegistrationNumber = i.RegistrationNumber,
                InvoiceNumber = i.InvoiceNumber,
                IssueDate = i.IssueDate,
                DueDate = i.DueDate,
                NetAmount = i.NetAmount,
                TaxAmount = i.TaxAmount,
                TotalAmount = i.TotalAmount,
                PaidAmount = i.PaidAmount,
                Status = i.Status,
                MechanicDeskId = i.MechanicDeskId,
                CreatedAt = i.CreatedAt,
                Items = i.Items.Select(ii => new InvoiceItemDto
                {
                    Id = ii.Id,
                    InvoiceId = ii.InvoiceId,
                    Description = ii.Description,
                    Category = ii.Category,
                    UnitPrice = ii.UnitPrice,
                    Quantity = ii.Quantity,
                    NetAmount = ii.NetAmount,
                    TaxAmount = ii.TaxAmount,
                    StockNumber = ii.StockNumber
                }).ToList()
            }).ToList()
        }).ToList(),
        Documents = c.Documents.OrderByDescending(d => d.UploadedDate ?? d.CreatedAt).Select(d => new DocumentDto
        {
            Id = d.Id,
            RegistrationNumber = d.RegistrationNumber,
            DocumentType = d.DocumentType,
            Category = d.Category,
            FilePath = d.FilePath,
            FileName = d.FileName,
            UploadedDate = d.UploadedDate,
            IsLocalPath = d.IsLocalPath,
            MimeType = d.MimeType,
            Notes = d.Notes,
            CreatedAt = d.CreatedAt
        }).ToList()
    };
}
