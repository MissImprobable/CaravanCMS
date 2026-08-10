using CaravanCMS.Api.Data;
using CaravanCMS.Core;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CaravanCMS.Api.Controllers;

/// <summary>Endpoints for resolving customers — primarily for the Outlook add-in's sender matching.</summary>
[ApiController]
[Route("api/[controller]")]
[Produces("application/json")]
public class CustomersController : ControllerBase
{
    private readonly ApplicationDbContext _db;
    private readonly ILogger<CustomersController> _logger;

    public CustomersController(ApplicationDbContext db, ILogger<CustomersController> logger)
    {
        _db = db;
        _logger = logger;
    }

    /// <summary>
    /// Looks up a customer by email address — the core match used when the Outlook add-in
    /// opens an email to decide whether the sender is a known customer.
    /// </summary>
    /// <param name="email">Sender email address (matched case-insensitively, exact).</param>
    [HttpGet("lookup")]
    [ProducesResponseType(typeof(CustomerLookupDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<CustomerLookupDto>> LookupByEmail([FromQuery] string? email)
    {
        if (string.IsNullOrWhiteSpace(email))
            return BadRequest(new { error = "email query parameter is required." });

        string normalized = email.Trim().ToUpper();

        Customer? customer = await _db.Customers
            .Include(c => c.Caravans)
            .Include(c => c.Jobs)
            .FirstOrDefaultAsync(c => c.Email != null && c.Email.ToUpper() == normalized);

        if (customer is null)
        {
            _logger.LogInformation("GET /api/customers/lookup — no match for {Email}", email);
            return NotFound(new { error = $"No customer found with email {email}." });
        }

        CustomerLookupDto dto = new()
        {
            Id = customer.Id,
            Name = customer.Name,
            Email = customer.Email,
            Phone = customer.Phone,
            Mobile = customer.Mobile,
            CustomerNumber = customer.CustomerNumber,
            CaravanCount = customer.Caravans.Count,
            JobCount = customer.Jobs.Count
        };

        _logger.LogInformation("GET /api/customers/lookup — matched {Email} to customer {Id}", email, customer.Id);
        return Ok(dto);
    }

    /// <summary>Searches customers by name, email, phone, or customer number. Returns up to 50 results.</summary>
    [HttpGet("search")]
    [ProducesResponseType(typeof(List<CustomerLookupDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<List<CustomerLookupDto>>> Search([FromQuery] string? q)
    {
        if (string.IsNullOrWhiteSpace(q) || q.Length < 2)
            return BadRequest(new { error = "Search query must be at least 2 characters." });

        string term = q.Trim().ToUpper();

        List<Customer> customers = await _db.Customers
            .Include(c => c.Caravans)
            .Include(c => c.Jobs)
            .ToListAsync();

        List<CustomerLookupDto> matches = customers
            .Where(c =>
                Contains(c.Name, term) ||
                Contains(c.Email, term) ||
                Contains(c.Phone, term) ||
                Contains(c.Mobile, term) ||
                Contains(c.CustomerNumber, term))
            .Take(50)
            .Select(c => new CustomerLookupDto
            {
                Id = c.Id,
                Name = c.Name,
                Email = c.Email,
                Phone = c.Phone,
                Mobile = c.Mobile,
                CustomerNumber = c.CustomerNumber,
                CaravanCount = c.Caravans.Count,
                JobCount = c.Jobs.Count
            })
            .ToList();

        return Ok(matches);
    }

    private static bool Contains(string? value, string term) =>
        value is not null && value.ToUpper().Contains(term);

    /// <summary>Returns every conversation thread for a customer, newest activity first — used by Admin's CRM view.</summary>
    [HttpGet("{id}/conversations")]
    [ProducesResponseType(typeof(List<ConversationDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<List<ConversationDto>>> GetConversations(int id)
    {
        bool exists = await _db.Customers.AnyAsync(c => c.Id == id);
        if (!exists)
            return NotFound(new { error = $"Customer {id} not found." });

        List<Conversation> conversations = await _db.Conversations
            .Include(c => c.Tags)
            .Include(c => c.Messages)
            .Where(c => c.CustomerId == id)
            .OrderByDescending(c => c.LastActivityAt)
            .ToListAsync();

        List<ConversationDto> result = conversations.Select(conv => new ConversationDto
        {
            Id = conv.Id,
            CustomerId = conv.CustomerId,
            RegistrationNumber = conv.RegistrationNumber,
            Subject = conv.Subject,
            ExternalConversationId = conv.ExternalConversationId,
            StartedAt = conv.StartedAt,
            LastActivityAt = conv.LastActivityAt,
            Tags = conv.Tags.Select(t => new TagDto { Id = t.Id, Name = t.Name }).OrderBy(t => t.Name).ToList(),
            Messages = conv.Messages.OrderBy(m => m.OccurredAt).Select(m => new CommunicationLogDto
            {
                Id = m.Id,
                ConversationId = m.ConversationId,
                Type = m.Type,
                Direction = m.Direction,
                FromAddress = m.FromAddress,
                Body = m.Body,
                LoggedBy = m.LoggedBy,
                OccurredAt = m.OccurredAt
            }).ToList()
        }).ToList();

        return Ok(result);
    }
}
