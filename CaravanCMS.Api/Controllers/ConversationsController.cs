using CaravanCMS.Api.Data;
using CaravanCMS.Core;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CaravanCMS.Api.Controllers;

/// <summary>Endpoints for logging customer conversations and tagging them by purpose.</summary>
[ApiController]
[Route("api/[controller]")]
[Produces("application/json")]
public class ConversationsController : ControllerBase
{
    private readonly ApplicationDbContext _db;
    private readonly ILogger<ConversationsController> _logger;

    public ConversationsController(ApplicationDbContext db, ILogger<ConversationsController> logger)
    {
        _db = db;
        _logger = logger;
    }

    /// <summary>
    /// Starts a new conversation thread, or returns the existing one if ExternalConversationId
    /// already matches a thread for this customer — so replies attach automatically.
    /// </summary>
    [HttpPost]
    [ProducesResponseType(typeof(ConversationDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ConversationDto>> Create([FromBody] CreateConversationRequest request)
    {
        bool customerExists = await _db.Customers.AnyAsync(c => c.Id == request.CustomerId);
        if (!customerExists)
            return NotFound(new { error = $"Customer {request.CustomerId} not found." });

        Conversation? existing = null;
        if (!string.IsNullOrWhiteSpace(request.ExternalConversationId))
        {
            existing = await _db.Conversations
                .Include(c => c.Tags)
                .Include(c => c.Messages)
                .FirstOrDefaultAsync(c =>
                    c.CustomerId == request.CustomerId &&
                    c.ExternalConversationId == request.ExternalConversationId);
        }

        if (existing is not null)
            return Ok(MapToDto(existing));

        if (!string.IsNullOrWhiteSpace(request.RegistrationNumber))
        {
            bool caravanExists = await _db.Caravans.AnyAsync(c => c.RegistrationNumber == request.RegistrationNumber);
            if (!caravanExists)
                return NotFound(new { error = $"Caravan {request.RegistrationNumber} not found." });
        }

        Conversation conversation = new()
        {
            CustomerId = request.CustomerId,
            RegistrationNumber = request.RegistrationNumber,
            Subject = request.Subject,
            ExternalConversationId = request.ExternalConversationId
        };

        _db.Conversations.Add(conversation);
        await _db.SaveChangesAsync();

        _logger.LogInformation("Created conversation {Id} for customer {CustomerId}", conversation.Id, request.CustomerId);
        return Ok(MapToDto(conversation));
    }

    /// <summary>Deletes an empty conversation thread (no messages) — for cleaning up mistakes, e.g. one
    /// started with the wrong customer. A conversation with logged messages must have them removed
    /// first via DELETE .../messages/{messageId}.</summary>
    [HttpDelete("{id}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Delete(int id)
    {
        Conversation? conversation = await _db.Conversations
            .Include(c => c.Messages)
            .FirstOrDefaultAsync(c => c.Id == id);
        if (conversation is null)
            return NotFound(new { error = $"Conversation {id} not found." });

        if (conversation.Messages.Count > 0)
            return Conflict(new { error = $"Conversation {id} has {conversation.Messages.Count} message(s) — remove them first." });

        _db.Conversations.Remove(conversation);
        await _db.SaveChangesAsync();

        _logger.LogInformation("Deleted empty conversation {Id}", id);
        return NoContent();
    }

    /// <summary>Appends a message (email, call, note) to an existing conversation.</summary>
    [HttpPost("{id}/messages")]
    [ProducesResponseType(typeof(CommunicationLogDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<CommunicationLogDto>> AddMessage(int id, [FromBody] LogMessageRequest request)
    {
        Conversation? conversation = await _db.Conversations.FindAsync(id);
        if (conversation is null)
            return NotFound(new { error = $"Conversation {id} not found." });

        if (!string.IsNullOrWhiteSpace(request.ExternalMessageId))
        {
            CommunicationLog? dupe = await _db.CommunicationLogs
                .FirstOrDefaultAsync(m => m.ExternalMessageId == request.ExternalMessageId);
            if (dupe is not null)
                return Ok(MapToDto(dupe));
        }

        DateTime occurredAt = request.OccurredAt ?? DateTime.UtcNow;
        CommunicationLog message = new()
        {
            ConversationId = id,
            Type = request.Type,
            Direction = request.Direction,
            FromAddress = request.FromAddress,
            Body = request.Body,
            ExternalMessageId = request.ExternalMessageId,
            LoggedBy = request.LoggedBy,
            OccurredAt = occurredAt
        };

        conversation.LastActivityAt = occurredAt > conversation.LastActivityAt ? occurredAt : conversation.LastActivityAt;

        _db.CommunicationLogs.Add(message);
        await _db.SaveChangesAsync();

        _logger.LogInformation("Logged {Type} message on conversation {Id}", request.Type, id);
        return Ok(MapToDto(message));
    }

    /// <summary>
    /// Removes a logged message — used for the Outlook add-in's "undo" after an auto-send.
    /// If the conversation ends up with no messages left, it is deleted too.
    /// </summary>
    [HttpDelete("{id}/messages/{messageId}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> RemoveMessage(int id, int messageId)
    {
        CommunicationLog? message = await _db.CommunicationLogs
            .FirstOrDefaultAsync(m => m.Id == messageId && m.ConversationId == id);
        if (message is null)
            return NotFound(new { error = $"Message {messageId} not found on conversation {id}." });

        _db.CommunicationLogs.Remove(message);
        await _db.SaveChangesAsync();

        bool anyRemaining = await _db.CommunicationLogs.AnyAsync(m => m.ConversationId == id);
        if (!anyRemaining)
        {
            Conversation? conversation = await _db.Conversations.FindAsync(id);
            if (conversation is not null)
                _db.Conversations.Remove(conversation);
            await _db.SaveChangesAsync();
        }

        _logger.LogInformation("Undo — removed message {MessageId} from conversation {Id}", messageId, id);
        return NoContent();
    }

    /// <summary>Attaches a purpose tag to a conversation, creating the tag if it doesn't already exist.</summary>
    [HttpPost("{id}/tags")]
    [ProducesResponseType(typeof(ConversationDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ConversationDto>> AddTag(int id, [FromBody] AttachTagRequest request)
    {
        Conversation? conversation = await _db.Conversations
            .Include(c => c.Tags)
            .Include(c => c.Messages)
            .FirstOrDefaultAsync(c => c.Id == id);
        if (conversation is null)
            return NotFound(new { error = $"Conversation {id} not found." });

        string name = request.Name.Trim();
        Tag? tag = await _db.Tags.FirstOrDefaultAsync(t => t.CustomerId == conversation.CustomerId && t.Name == name);
        if (tag is null)
        {
            tag = new Tag { CustomerId = conversation.CustomerId, Name = name };
            _db.Tags.Add(tag);
        }

        if (!conversation.Tags.Any(t => t.Name == name))
            conversation.Tags.Add(tag);

        await _db.SaveChangesAsync();
        return Ok(MapToDto(conversation));
    }

    /// <summary>Removes a tag from a conversation. The tag itself remains available for reuse.</summary>
    [HttpDelete("{id}/tags/{tagId}")]
    [ProducesResponseType(typeof(ConversationDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ConversationDto>> RemoveTag(int id, int tagId)
    {
        Conversation? conversation = await _db.Conversations
            .Include(c => c.Tags)
            .Include(c => c.Messages)
            .FirstOrDefaultAsync(c => c.Id == id);
        if (conversation is null)
            return NotFound(new { error = $"Conversation {id} not found." });

        conversation.Tags.Remove(conversation.Tags.FirstOrDefault(t => t.Id == tagId)!);
        await _db.SaveChangesAsync();
        return Ok(MapToDto(conversation));
    }

    /// <summary>Lists a customer's own tags, for autocomplete when labelling one of their conversations.
    /// Tags are scoped per customer, so this never returns another customer's labels.</summary>
    [HttpGet("tags")]
    [ProducesResponseType(typeof(List<TagDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<List<TagDto>>> GetTags([FromQuery] int customerId)
    {
        if (customerId <= 0)
            return BadRequest(new { error = "customerId query parameter is required." });

        List<TagDto> tags = await _db.Tags
            .Where(t => t.CustomerId == customerId)
            .OrderBy(t => t.Name)
            .Select(t => new TagDto { Id = t.Id, Name = t.Name })
            .ToListAsync();
        return Ok(tags);
    }

    private static ConversationDto MapToDto(Conversation c) => new()
    {
        Id = c.Id,
        CustomerId = c.CustomerId,
        RegistrationNumber = c.RegistrationNumber,
        Subject = c.Subject,
        ExternalConversationId = c.ExternalConversationId,
        StartedAt = c.StartedAt,
        LastActivityAt = c.LastActivityAt,
        Tags = c.Tags.Select(t => new TagDto { Id = t.Id, Name = t.Name }).OrderBy(t => t.Name).ToList(),
        Messages = c.Messages.OrderBy(m => m.OccurredAt).Select(MapToDto).ToList()
    };

    private static CommunicationLogDto MapToDto(CommunicationLog m) => new()
    {
        Id = m.Id,
        ConversationId = m.ConversationId,
        Type = m.Type,
        Direction = m.Direction,
        FromAddress = m.FromAddress,
        Body = m.Body,
        LoggedBy = m.LoggedBy,
        OccurredAt = m.OccurredAt
    };
}
