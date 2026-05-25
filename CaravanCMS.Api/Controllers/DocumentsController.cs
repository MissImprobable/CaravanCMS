using CaravanCMS.Api.Data;
using CaravanCMS.Core;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CaravanCMS.Api.Controllers;

/// <summary>Endpoints for listing, downloading, uploading, and removing document links.</summary>
[ApiController]
[Route("api/[controller]")]
[Produces("application/json")]
public class DocumentsController : ControllerBase
{
    private readonly ApplicationDbContext _db;
    private readonly ILogger<DocumentsController> _logger;

    public DocumentsController(ApplicationDbContext db, ILogger<DocumentsController> logger)
    {
        _db = db;
        _logger = logger;
    }

    /// <summary>Lists documents, optionally filtered by caravan or document type.</summary>
    [HttpGet]
    [ProducesResponseType(typeof(List<DocumentDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<List<DocumentDto>>> GetAll(
        [FromQuery] string? rego,
        [FromQuery] string? documentType)
    {
        IQueryable<Document> query = _db.Documents.AsQueryable();

        if (!string.IsNullOrEmpty(rego))
            query = query.Where(d => d.RegistrationNumber == rego);

        if (!string.IsNullOrEmpty(documentType))
            query = query.Where(d => d.DocumentType == documentType);

        List<DocumentDto> docs = await query
            .OrderByDescending(d => d.UploadedDate ?? d.CreatedAt)
            .Select(d => new DocumentDto
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
            })
            .ToListAsync();

        return Ok(docs);
    }

    /// <summary>
    /// Streams a document file to the client for download.
    /// The file must exist at its registered path on the server's disk.
    /// </summary>
    /// <param name="id">Document database ID.</param>
    [HttpGet("{id:int}/download")]
    [ProducesResponseType(typeof(FileStreamResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status410Gone)]
    public async Task<IActionResult> Download(int id)
    {
        Document? doc = await _db.Documents.FindAsync(id);

        if (doc is null)
        {
            _logger.LogWarning("Download requested for document {Id} — not found in database", id);
            return NotFound(new { error = $"Document {id} not found." });
        }

        if (!System.IO.File.Exists(doc.FilePath))
        {
            _logger.LogWarning("Download requested for document {Id} — file missing at {Path}", id, doc.FilePath);
            return StatusCode(StatusCodes.Status410Gone,
                new { error = $"File no longer exists at: {doc.FilePath}" });
        }

        string contentType = doc.MimeType ?? GetMimeType(doc.FileName);
        FileStream stream = System.IO.File.OpenRead(doc.FilePath);

        _logger.LogInformation("Downloading document {Id} ({FileName}) for caravan {Rego}",
            id, doc.FileName, doc.RegistrationNumber);

        return File(stream, contentType, doc.FileName);
    }

    /// <summary>Links a file (by path) to a caravan as a new document record.</summary>
    [HttpPost]
    [ProducesResponseType(typeof(DocumentDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<DocumentDto>> Create([FromBody] LinkDocumentRequest request)
    {
        if (!await _db.Caravans.AnyAsync(c => c.RegistrationNumber == request.RegistrationNumber))
            return NotFound(new { error = $"Caravan {request.RegistrationNumber} not found." });

        if (!System.IO.File.Exists(request.FilePath))
            return BadRequest(new { error = $"File not found at path: {request.FilePath}" });

        // Prevent duplicate links to the same file
        bool alreadyLinked = await _db.Documents.AnyAsync(d =>
            d.RegistrationNumber == request.RegistrationNumber && d.FilePath == request.FilePath);
        if (alreadyLinked)
            return BadRequest(new { error = "This file is already linked to this caravan." });

        string fileName = Path.GetFileName(request.FilePath);
        Document doc = new()
        {
            RegistrationNumber = request.RegistrationNumber,
            FilePath = request.FilePath,
            FileName = fileName,
            DocumentType = request.DocumentType ?? "Document",
            Category = request.Category,
            Notes = request.Notes,
            MimeType = GetMimeType(fileName),
            UploadedDate = DateTime.UtcNow,
            IsLocalPath = true,
            CreatedAt = DateTime.UtcNow
        };

        _db.Documents.Add(doc);
        await _db.SaveChangesAsync();

        _logger.LogInformation("Linked document {FileName} to caravan {Rego}", fileName, request.RegistrationNumber);

        DocumentDto dto = new()
        {
            Id = doc.Id,
            RegistrationNumber = doc.RegistrationNumber,
            DocumentType = doc.DocumentType,
            Category = doc.Category,
            FilePath = doc.FilePath,
            FileName = doc.FileName,
            UploadedDate = doc.UploadedDate,
            IsLocalPath = doc.IsLocalPath,
            MimeType = doc.MimeType,
            Notes = doc.Notes,
            CreatedAt = doc.CreatedAt
        };

        return CreatedAtAction(nameof(GetAll), new { rego = doc.RegistrationNumber }, dto);
    }

    /// <summary>Removes a document link (does NOT delete the file from disk).</summary>
    /// <param name="id">Document database ID.</param>
    [HttpDelete("{id:int}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(int id)
    {
        Document? doc = await _db.Documents.FindAsync(id);
        if (doc is null)
            return NotFound(new { error = $"Document {id} not found." });

        _db.Documents.Remove(doc);
        await _db.SaveChangesAsync();

        _logger.LogInformation("Removed document link {Id} ({FileName}) from caravan {Rego}",
            id, doc.FileName, doc.RegistrationNumber);

        return NoContent();
    }

    private static string GetMimeType(string fileName)
    {
        string ext = Path.GetExtension(fileName).ToLowerInvariant();
        return ext switch
        {
            ".pdf" => "application/pdf",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            ".tiff" or ".tif" => "image/tiff",
            ".bmp" => "image/bmp",
            ".gif" => "image/gif",
            ".xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            ".xls" => "application/vnd.ms-excel",
            ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            ".doc" => "application/msword",
            ".txt" => "text/plain",
            _ => "application/octet-stream"
        };
    }
}
