using CaravanCMS.Api.Data;
using CaravanCMS.Core;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CaravanCMS.Api.Controllers;

/// <summary>Endpoints for retrieving job details and service history.</summary>
[ApiController]
[Route("api/[controller]")]
[Produces("application/json")]
public class JobsController : ControllerBase
{
    private readonly ApplicationDbContext _db;
    private readonly ILogger<JobsController> _logger;

    public JobsController(ApplicationDbContext db, ILogger<JobsController> logger)
    {
        _db = db;
        _logger = logger;
    }

    /// <summary>Returns full detail for a single job including all invoices and line items.</summary>
    /// <param name="id">Job database ID.</param>
    [HttpGet("{id:int}")]
    [ProducesResponseType(typeof(JobDetailDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<JobDetailDto>> GetById(int id)
    {
        Job? job = await _db.Jobs
            .Include(j => j.Invoices)
                .ThenInclude(i => i.Items)
            .FirstOrDefaultAsync(j => j.Id == id);

        if (job is null)
        {
            _logger.LogWarning("GET /api/jobs/{Id} — not found", id);
            return NotFound(new { error = $"Job {id} not found." });
        }

        JobDetailDto dto = new()
        {
            Id = job.Id,
            RegistrationNumber = job.RegistrationNumber,
            CustomerId = job.CustomerId,
            JobNumber = job.JobNumber,
            Status = job.Status,
            JobType = job.JobType,
            Description = job.Description,
            Notes = job.Notes,
            StartDate = job.StartDate,
            FinishDate = job.FinishDate,
            EstimatedHours = job.EstimatedHours,
            FinishedBy = job.FinishedBy,
            MechanicDeskId = job.MechanicDeskId,
            CreatedAt = job.CreatedAt,
            UpdatedAt = job.UpdatedAt,
            Invoices = job.Invoices.Select(i => new InvoiceDto
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
        };

        _logger.LogInformation("GET /api/jobs/{Id} — job {JobNumber}", id, job.JobNumber);
        return Ok(dto);
    }

    /// <summary>Returns all jobs for a given caravan, ordered newest first.</summary>
    /// <param name="rego">Caravan registration number.</param>
    [HttpGet("by-caravan/{rego}")]
    [ProducesResponseType(typeof(List<JobDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<List<JobDto>>> GetByCaravan(string rego)
    {
        List<JobDto> jobs = await _db.Jobs
            .Where(j => j.RegistrationNumber == rego)
            .OrderByDescending(j => j.FinishDate ?? j.StartDate)
            .Select(j => new JobDto
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
                InvoiceCount = j.Invoices.Count,
                TotalInvoiced = j.Invoices.Sum(i => i.TotalAmount)
            })
            .ToListAsync();

        _logger.LogInformation("GET /api/jobs/by-caravan/{Rego} — {Count} jobs", rego, jobs.Count);
        return Ok(jobs);
    }
}
