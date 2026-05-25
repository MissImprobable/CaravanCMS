using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace CaravanCMS.Core;

// ─── Domain Entities ────────────────────────────────────────────────────────

/// <summary>A caravan owner or business customer imported from MechanicDesk.</summary>
public class Customer
{
    public int Id { get; set; }

    [Required, MaxLength(200)]
    public string Name { get; set; } = string.Empty;

    [MaxLength(200)]
    public string? Email { get; set; }

    [MaxLength(50)]
    public string? Phone { get; set; }

    [MaxLength(50)]
    public string? Mobile { get; set; }

    [MaxLength(300)]
    public string? Address { get; set; }

    [MaxLength(100)]
    public string? Suburb { get; set; }

    [MaxLength(50)]
    public string? State { get; set; }

    [MaxLength(10)]
    public string? Postcode { get; set; }

    [MaxLength(50)]
    public string? CustomerNumber { get; set; }

    /// <summary>Unique ID from MechanicDesk — used for safe re-imports without duplication.</summary>
    [MaxLength(100)]
    public string? MechanicDeskId { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public ICollection<Caravan> Caravans { get; set; } = new List<Caravan>();
    public ICollection<Job> Jobs { get; set; } = new List<Job>();
    public ICollection<Invoice> Invoices { get; set; } = new List<Invoice>();
}

/// <summary>A caravan unit tracked by registration number — rego is the primary key.</summary>
public class Caravan
{
    [Required, MaxLength(20)]
    public string RegistrationNumber { get; set; } = string.Empty;

    public int CustomerId { get; set; }
    public Customer Customer { get; set; } = null!;

    [MaxLength(50)]
    public string? Vin { get; set; }

    [MaxLength(100)]
    public string? Make { get; set; }

    [MaxLength(100)]
    public string? Model { get; set; }

    public int? Year { get; set; }

    [MaxLength(50)]
    public string? Color { get; set; }

    [MaxLength(100)]
    public string? Body { get; set; }

    public int? CurrentOdometer { get; set; }
    public DateTime? LastJobDate { get; set; }

    [MaxLength(100)]
    public string? SelfContainment { get; set; }

    public DateTime? SelfContainmentDue { get; set; }

    /// <summary>Unique ID from MechanicDesk — used for safe re-imports without duplication.</summary>
    [MaxLength(100)]
    public string? MechanicDeskId { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public ICollection<Job> Jobs { get; set; } = new List<Job>();
    public ICollection<Document> Documents { get; set; } = new List<Document>();
    public ICollection<Invoice> Invoices { get; set; } = new List<Invoice>();
}

/// <summary>A service job performed on a caravan, imported from MechanicDesk.</summary>
public class Job
{
    public int Id { get; set; }

    [Required, MaxLength(20)]
    public string RegistrationNumber { get; set; } = string.Empty;
    public Caravan Caravan { get; set; } = null!;

    public int CustomerId { get; set; }
    public Customer Customer { get; set; } = null!;

    [MaxLength(50)]
    public string? JobNumber { get; set; }

    [MaxLength(50)]
    public string? Status { get; set; }

    [MaxLength(100)]
    public string? JobType { get; set; }

    [MaxLength(2000)]
    public string? Description { get; set; }

    [MaxLength(4000)]
    public string? Notes { get; set; }

    public DateTime? StartDate { get; set; }
    public DateTime? FinishDate { get; set; }
    public decimal? EstimatedHours { get; set; }

    [MaxLength(100)]
    public string? FinishedBy { get; set; }

    /// <summary>Unique ID from MechanicDesk — used for safe re-imports without duplication.</summary>
    [MaxLength(100)]
    public string? MechanicDeskId { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public ICollection<Invoice> Invoices { get; set; } = new List<Invoice>();
}

/// <summary>An invoice issued for a service job.</summary>
public class Invoice
{
    public int Id { get; set; }

    public int JobId { get; set; }
    public Job Job { get; set; } = null!;

    public int CustomerId { get; set; }
    public Customer Customer { get; set; } = null!;

    [Required, MaxLength(20)]
    public string RegistrationNumber { get; set; } = string.Empty;
    public Caravan Caravan { get; set; } = null!;

    [MaxLength(50)]
    public string? InvoiceNumber { get; set; }

    public DateTime? IssueDate { get; set; }
    public DateTime? DueDate { get; set; }

    [Column(TypeName = "TEXT")]
    public decimal NetAmount { get; set; }

    [Column(TypeName = "TEXT")]
    public decimal TaxAmount { get; set; }

    [Column(TypeName = "TEXT")]
    public decimal TotalAmount { get; set; }

    [Column(TypeName = "TEXT")]
    public decimal PaidAmount { get; set; }

    [MaxLength(50)]
    public string? Status { get; set; }

    /// <summary>Unique ID from MechanicDesk — used for safe re-imports without duplication.</summary>
    [MaxLength(100)]
    public string? MechanicDeskId { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public ICollection<InvoiceItem> Items { get; set; } = new List<InvoiceItem>();
}

/// <summary>A line item within an invoice (parts, labour, consumables).</summary>
public class InvoiceItem
{
    public int Id { get; set; }

    public int InvoiceId { get; set; }
    public Invoice Invoice { get; set; } = null!;

    [MaxLength(500)]
    public string? Description { get; set; }

    [MaxLength(100)]
    public string? Category { get; set; }

    [Column(TypeName = "TEXT")]
    public decimal UnitPrice { get; set; }

    [Column(TypeName = "TEXT")]
    public decimal Quantity { get; set; }

    [Column(TypeName = "TEXT")]
    public decimal NetAmount { get; set; }

    [Column(TypeName = "TEXT")]
    public decimal TaxAmount { get; set; }

    [MaxLength(100)]
    public string? StockNumber { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// A document (PDF, image) physically stored on disk and linked to a caravan.
/// The file stays in its original location; we store the path reference only.
/// </summary>
public class Document
{
    public int Id { get; set; }

    [Required, MaxLength(20)]
    public string RegistrationNumber { get; set; } = string.Empty;
    public Caravan Caravan { get; set; } = null!;

    /// <summary>E.g. "Invoice", "Photo", "Report", "Manual", "Warranty".</summary>
    [MaxLength(100)]
    public string? DocumentType { get; set; }

    [MaxLength(100)]
    public string? Category { get; set; }

    /// <summary>Full path on server disk, e.g. C:\...\Caravan History\ABC123\invoice.pdf</summary>
    [Required, MaxLength(1000)]
    public string FilePath { get; set; } = string.Empty;

    [Required, MaxLength(300)]
    public string FileName { get; set; } = string.Empty;

    public DateTime? UploadedDate { get; set; }

    /// <summary>True for server-local paths; false for network/UNC paths (future).</summary>
    public bool IsLocalPath { get; set; } = true;

    [MaxLength(100)]
    public string? MimeType { get; set; }

    [MaxLength(500)]
    public string? Notes { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

// ─── Response DTOs ───────────────────────────────────────────────────────────

/// <summary>Lightweight caravan summary for list views and search results.</summary>
public class CaravanSummaryDto
{
    public string RegistrationNumber { get; set; } = string.Empty;
    public string? Vin { get; set; }
    public string? Make { get; set; }
    public string? Model { get; set; }
    public int? Year { get; set; }
    public string? Color { get; set; }
    public string? Body { get; set; }
    public int? CurrentOdometer { get; set; }
    public DateTime? LastJobDate { get; set; }
    public string? SelfContainment { get; set; }
    public DateTime? SelfContainmentDue { get; set; }
    public string? CustomerName { get; set; }
    public string? CustomerPhone { get; set; }
    public string? CustomerEmail { get; set; }
    public int CustomerId { get; set; }
    public int JobCount { get; set; }
    public int DocumentCount { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    /// <summary>Human-readable label for dropdowns: "ABC123  Make Model  (Customer)".</summary>
    public string DisplayText =>
        string.Join("  ", new[] { RegistrationNumber, $"{Make} {Model}".Trim(), CustomerName }
            .Where(s => !string.IsNullOrWhiteSpace(s)));
}

/// <summary>Complete caravan detail including full service history, documents, and owner info.</summary>
public class CaravanDetailDto
{
    public string RegistrationNumber { get; set; } = string.Empty;
    public string? Vin { get; set; }
    public string? Make { get; set; }
    public string? Model { get; set; }
    public int? Year { get; set; }
    public string? Color { get; set; }
    public string? Body { get; set; }
    public int? CurrentOdometer { get; set; }
    public DateTime? LastJobDate { get; set; }
    public string? SelfContainment { get; set; }
    public DateTime? SelfContainmentDue { get; set; }
    public string? MechanicDeskId { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public CustomerDto? Customer { get; set; }
    public List<JobDetailDto> Jobs { get; set; } = new();
    public List<DocumentDto> Documents { get; set; } = new();
}

/// <summary>Customer information for display in caravan detail and search results.</summary>
public class CustomerDto
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Email { get; set; }
    public string? Phone { get; set; }
    public string? Mobile { get; set; }
    public string? Address { get; set; }
    public string? Suburb { get; set; }
    public string? State { get; set; }
    public string? Postcode { get; set; }
    public string? CustomerNumber { get; set; }
    public string? MechanicDeskId { get; set; }
    public DateTime CreatedAt { get; set; }
}

/// <summary>Job summary without invoice detail (used in lists).</summary>
public class JobDto
{
    public int Id { get; set; }
    public string RegistrationNumber { get; set; } = string.Empty;
    public int CustomerId { get; set; }
    public string? JobNumber { get; set; }
    public string? Status { get; set; }
    public string? JobType { get; set; }
    public string? Description { get; set; }
    public string? Notes { get; set; }
    public DateTime? StartDate { get; set; }
    public DateTime? FinishDate { get; set; }
    public decimal? EstimatedHours { get; set; }
    public string? FinishedBy { get; set; }
    public string? MechanicDeskId { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public int InvoiceCount { get; set; }
    public decimal TotalInvoiced { get; set; }
}

/// <summary>Full job detail including all associated invoices and line items.</summary>
public class JobDetailDto
{
    public int Id { get; set; }
    public string RegistrationNumber { get; set; } = string.Empty;
    public int CustomerId { get; set; }
    public string? JobNumber { get; set; }
    public string? Status { get; set; }
    public string? JobType { get; set; }
    public string? Description { get; set; }
    public string? Notes { get; set; }
    public DateTime? StartDate { get; set; }
    public DateTime? FinishDate { get; set; }
    public decimal? EstimatedHours { get; set; }
    public string? FinishedBy { get; set; }
    public string? MechanicDeskId { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public List<InvoiceDto> Invoices { get; set; } = new();
}

/// <summary>Invoice summary with total amounts and payment status.</summary>
public class InvoiceDto
{
    public int Id { get; set; }
    public int JobId { get; set; }
    public int CustomerId { get; set; }
    public string RegistrationNumber { get; set; } = string.Empty;
    public string? InvoiceNumber { get; set; }
    public DateTime? IssueDate { get; set; }
    public DateTime? DueDate { get; set; }
    public decimal NetAmount { get; set; }
    public decimal TaxAmount { get; set; }
    public decimal TotalAmount { get; set; }
    public decimal PaidAmount { get; set; }
    public decimal BalanceDue => TotalAmount - PaidAmount;
    public string? Status { get; set; }
    public string? MechanicDeskId { get; set; }
    public DateTime CreatedAt { get; set; }
    public List<InvoiceItemDto> Items { get; set; } = new();
}

/// <summary>A single line item on an invoice.</summary>
public class InvoiceItemDto
{
    public int Id { get; set; }
    public int InvoiceId { get; set; }
    public string? Description { get; set; }
    public string? Category { get; set; }
    public decimal UnitPrice { get; set; }
    public decimal Quantity { get; set; }
    public decimal NetAmount { get; set; }
    public decimal TaxAmount { get; set; }
    public string? StockNumber { get; set; }
}

/// <summary>A document linked to a caravan — path reference, not file content.</summary>
public class DocumentDto
{
    public int Id { get; set; }
    public string RegistrationNumber { get; set; } = string.Empty;
    public string? DocumentType { get; set; }
    public string? Category { get; set; }
    public string FilePath { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public DateTime? UploadedDate { get; set; }
    public bool IsLocalPath { get; set; }
    public string? MimeType { get; set; }
    public string? Notes { get; set; }
    public DateTime CreatedAt { get; set; }
}

// ─── Import DTOs ─────────────────────────────────────────────────────────────

/// <summary>Overall result of a MechanicDesk Excel import operation.</summary>
public class ImportResultDto
{
    public int CustomersImported { get; set; }
    public int CaravansImported { get; set; }
    public int JobsImported { get; set; }
    public int InvoicesImported { get; set; }
    public int InvoiceItemsImported { get; set; }
    public int CustomersUpdated { get; set; }
    public int CaravansUpdated { get; set; }
    public int JobsUpdated { get; set; }
    public List<ImportConflictDto> Conflicts { get; set; } = new();
    public List<string> Errors { get; set; } = new();
    public List<string> Warnings { get; set; } = new();
    public TimeSpan Duration { get; set; }
}

/// <summary>
/// A data conflict detected during import where an existing record differs from the incoming data.
/// The user must choose: keep existing, update with new data, or skip entirely.
/// </summary>
public class ImportConflictDto
{
    public string ConflictId { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>The type of entity in conflict: "Customer", "Caravan", "Job", "Invoice".</summary>
    public string EntityType { get; set; } = string.Empty;

    public string MechanicDeskId { get; set; } = string.Empty;
    public int ExistingEntityId { get; set; }
    public string ExistingDescription { get; set; } = string.Empty;
    public string IncomingDescription { get; set; } = string.Empty;

    /// <summary>Field-level differences: key = field name, value = [existing, incoming].</summary>
    public Dictionary<string, string[]> ChangedFields { get; set; } = new();
}

/// <summary>User's decision on how to resolve a single import conflict.</summary>
public class ResolveConflictRequest
{
    [Required]
    public string ConflictId { get; set; } = string.Empty;

    /// <summary>"KeepExisting", "UseIncoming", or "Skip".</summary>
    [Required]
    public string Resolution { get; set; } = string.Empty;

    /// <summary>Optional serialized incoming entity data for "UseIncoming" resolution.</summary>
    public string? IncomingDataJson { get; set; }
}

// ─── File Scan DTOs ──────────────────────────────────────────────────────────

/// <summary>Request to trigger a file system scan for caravan documents.</summary>
public class ScanFilesRequest
{
    /// <summary>Root folder to scan. Defaults to the configured CaravanHistoryPath if null.</summary>
    public string? FolderPath { get; set; }
}

/// <summary>A single file found during a folder scan, with its best caravan match.</summary>
public class FileMatchDto
{
    public string FilePath { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public string FileExtension { get; set; } = string.Empty;
    public long FileSizeBytes { get; set; }

    /// <summary>Confidence score 0.0–1.0 for the suggested caravan match.</summary>
    public double Confidence { get; set; }

    /// <summary>How the match was found: "VinInFilename", "RegInFilename", "VinInFolderPath", "FuzzyMakeModel", "Unmatched".</summary>
    public string MatchMethod { get; set; } = "Unmatched";

    public bool IsMatched => Confidence >= 0.5;

    public string? SuggestedRegistrationNumber { get; set; }
    public string? SuggestedCaravanDescription { get; set; }

    /// <summary>Set when the file is already linked to a caravan in the database.</summary>
    public string? LinkedRegistrationNumber { get; set; }
    public bool AlreadyLinked { get; set; }

    /// <summary>Document type derived from the parent folder name (e.g. "Annual Service Checks", "Gas Certificates").</summary>
    public string? SuggestedDocumentType { get; set; }

    /// <summary>Year parsed from the year–customer subfolder (e.g. "2024").</summary>
    public string? SuggestedYear { get; set; }

    /// <summary>Customer name parsed from the year–customer subfolder (e.g. "John Smith").</summary>
    public string? SuggestedCustomerName { get; set; }
}

/// <summary>Result of a full folder scan operation.</summary>
public class FileScanResultDto
{
    public int TotalFilesFound { get; set; }
    public int MatchedFiles { get; set; }
    public int AlreadyLinkedFiles { get; set; }
    public int UnmatchedFiles { get; set; }
    public List<FileMatchDto> Files { get; set; } = new();
    public string ScannedFolder { get; set; } = string.Empty;
    public TimeSpan Duration { get; set; }
}

/// <summary>Request to manually link a scanned file to a caravan record.</summary>
public class LinkDocumentRequest
{
    [Required, MaxLength(20)]
    public string RegistrationNumber { get; set; } = string.Empty;

    [Required, MaxLength(1000)]
    public string FilePath { get; set; } = string.Empty;

    [MaxLength(100)]
    public string? DocumentType { get; set; }

    [MaxLength(100)]
    public string? Category { get; set; }

    [MaxLength(500)]
    public string? Notes { get; set; }
}

/// <summary>Stats for the API health/dashboard endpoint.</summary>
public class ApiStatsDto
{
    public int TotalCustomers { get; set; }
    public int TotalCaravans { get; set; }
    public int TotalJobs { get; set; }
    public int TotalInvoices { get; set; }
    public int TotalDocuments { get; set; }
    public DateTime? LastImportAt { get; set; }
    public long DatabaseSizeBytes { get; set; }
    public string Version { get; set; } = "1.0.0";
}
