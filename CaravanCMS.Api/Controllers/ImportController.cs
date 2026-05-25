using CaravanCMS.Api.Data;
using CaravanCMS.Api.Services;
using CaravanCMS.Core;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CaravanCMS.Api.Controllers;

/// <summary>
/// Admin-only endpoints for importing MechanicDesk data and scanning/linking document files.
/// Intended to be called from the Admin WPF application only.
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Produces("application/json")]
public class ImportController : ControllerBase
{
    private readonly ExcelImportService _importer;
    private readonly FileScanner _scanner;
    private readonly ApplicationDbContext _db;
    private readonly ILogger<ImportController> _logger;
    private readonly IConfiguration _config;

    public ImportController(
        ExcelImportService importer,
        FileScanner scanner,
        ApplicationDbContext db,
        ILogger<ImportController> logger,
        IConfiguration config)
    {
        _importer = importer;
        _scanner = scanner;
        _db = db;
        _logger = logger;
        _config = config;
    }

    /// <summary>
    /// Accepts a MechanicDesk Excel export file and imports all records.
    /// Uses MechanicDeskId for deduplication — safe to re-import the same file.
    /// Returns a full result including any conflicts that need manual resolution.
    /// </summary>
    [HttpPost("mechanicdesk")]
    [RequestSizeLimit(104857600)] // 100 MB
    [ProducesResponseType(typeof(ImportResultDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<ImportResultDto>> ImportMechanicDesk(IFormFile file)
    {
        if (file is null || file.Length == 0)
            return BadRequest(new { error = "No file uploaded." });

        string ext = Path.GetExtension(file.FileName).ToLowerInvariant();
        if (ext is not ".xlsx")
            return BadRequest(new { error = "File must be a .xlsx Excel file. Legacy .xls format is not supported — please re-save the file as .xlsx from Excel first." });

        _logger.LogInformation("Starting MechanicDesk import — file: {FileName} ({Size:N0} bytes)",
            file.FileName, file.Length);

        using MemoryStream ms = new();
        await file.CopyToAsync(ms);
        ms.Position = 0;

        try
        {
            ImportResultDto result = await _importer.ImportAsync(ms, file.FileName);
            _logger.LogInformation(
                "Import complete — {Customers} customers, {Caravans} caravans, {Jobs} jobs, {Conflicts} conflicts",
                result.CustomersImported, result.CaravansImported, result.JobsImported, result.Conflicts.Count);

            return Ok(result);
        }
        catch (InvalidDataException ex)
        {
            _logger.LogWarning(ex, "Invalid file format uploaded: {FileName}", file.FileName);
            return BadRequest(new { error = "The uploaded file is not a valid .xlsx file. Make sure you are exporting from MechanicDesk as Excel (.xlsx) and not CSV or another format." });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Import failed for file {FileName}", file.FileName);
            return StatusCode(500, new { error = $"Import failed: {ex.Message}" });
        }
    }

    /// <summary>
    /// Scans a folder on the server's disk for PDF/image files and fuzzy-matches them to caravans.
    /// The folder defaults to the configured CaravanHistoryPath if not specified.
    /// </summary>
    [HttpPost("scan-files")]
    [ProducesResponseType(typeof(FileScanResultDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<FileScanResultDto>> ScanFiles([FromBody] ScanFilesRequest request)
    {
        string folderPath = request.FolderPath
            ?? _config["CaravanCMS:CaravanHistoryPath"]
            ?? string.Empty;

        if (string.IsNullOrWhiteSpace(folderPath))
            return BadRequest(new { error = "No folder path specified and CaravanHistoryPath is not configured." });

        if (!Directory.Exists(folderPath))
            return BadRequest(new { error = $"Folder does not exist: {folderPath}" });

        _logger.LogInformation("Starting file scan in: {Folder}", folderPath);

        try
        {
            FileScanResultDto result = await _scanner.ScanAsync(folderPath);
            _logger.LogInformation(
                "Scan complete — {Total} files found, {Matched} matched, {Unmatched} unmatched",
                result.TotalFilesFound, result.MatchedFiles, result.UnmatchedFiles);
            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "File scan failed for folder {Folder}", folderPath);
            return StatusCode(500, new { error = $"Scan failed: {ex.Message}" });
        }
    }

    /// <summary>Links a specific file path to a caravan record. Used after reviewing scan results.</summary>
    [HttpPost("link-document")]
    [ProducesResponseType(typeof(DocumentDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<DocumentDto>> LinkDocument([FromBody] LinkDocumentRequest request)
    {
        if (!await _db.Caravans.AnyAsync(c => c.RegistrationNumber == request.RegistrationNumber))
            return NotFound(new { error = $"Caravan {request.RegistrationNumber} not found." });

        if (!System.IO.File.Exists(request.FilePath))
            return BadRequest(new { error = $"File not found at path: {request.FilePath}" });

        bool alreadyLinked = await _db.Documents.AnyAsync(d =>
            d.RegistrationNumber == request.RegistrationNumber && d.FilePath == request.FilePath);
        if (alreadyLinked)
            return BadRequest(new { error = "This file is already linked to this caravan." });

        string fileName = Path.GetFileName(request.FilePath);
        string ext = Path.GetExtension(fileName).ToLowerInvariant();
        string mimeType = ext switch
        {
            ".pdf" => "application/pdf",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            ".tiff" or ".tif" => "image/tiff",
            ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            ".doc" => "application/msword",
            ".xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            ".txt" => "text/plain",
            _ => "application/octet-stream"
        };

        Document doc = new()
        {
            RegistrationNumber = request.RegistrationNumber,
            FilePath = request.FilePath,
            FileName = fileName,
            DocumentType = request.DocumentType ?? "Document",
            Category = request.Category,
            Notes = request.Notes,
            MimeType = mimeType,
            UploadedDate = DateTime.UtcNow,
            IsLocalPath = true,
            CreatedAt = DateTime.UtcNow
        };

        _db.Documents.Add(doc);
        await _db.SaveChangesAsync();

        _logger.LogInformation("Linked document {FileName} to caravan {Rego}", fileName, request.RegistrationNumber);

        return Created(string.Empty, new DocumentDto
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
        });
    }
}
