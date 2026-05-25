using CaravanCMS.Api.Data;
using CaravanCMS.Core;
using Microsoft.EntityFrameworkCore;

namespace CaravanCMS.Api.Services;

/// <summary>
/// Recursively scans a folder for caravan document files (PDF, images) and uses
/// FuzzyMatcher to suggest the best caravan match for each file found.
/// </summary>
public class FileScanner
{
    private static readonly string[] SupportedExtensions = { ".pdf", ".jpg", ".jpeg", ".png", ".tiff", ".tif", ".bmp" };

    // Matches the known subfolder names directly under each vehicle folder.
    private static readonly HashSet<string> KnownDocumentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "Annual Service Checks",
        "Gas Certificates",
        "Insurance Claims",
        "Insurance Policies",
        "Major Jobs",
        "Photos",
        "Self Containment",
        "Warranty Claims",
        "Window Quotes",
    };

    private readonly ApplicationDbContext _db;
    private readonly FuzzyMatcher _matcher;
    private readonly ILogger<FileScanner> _logger;

    public FileScanner(ApplicationDbContext db, FuzzyMatcher matcher, ILogger<FileScanner> logger)
    {
        _db = db;
        _matcher = matcher;
        _logger = logger;
    }

    /// <summary>
    /// Scans the given folder recursively for supported document files and returns match results.
    /// Loads all caravans once at the start to avoid N+1 database queries.
    /// </summary>
    public async Task<FileScanResultDto> ScanAsync(string folderPath)
    {
        System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
        FileScanResultDto result = new() { ScannedFolder = folderPath };

        _logger.LogInformation("Scanning folder: {Folder}", folderPath);

        // Load all caravans into memory once — avoids N+1 for large scans
        List<Caravan> allCaravans = await _db.Caravans.AsNoTracking().ToListAsync();
        _logger.LogDebug("Loaded {Count} caravans for matching", allCaravans.Count);

        // Enumerate all supported files
        List<string> files = new();
        try
        {
            files = Directory
                .EnumerateFiles(folderPath, "*.*", SearchOption.AllDirectories)
                .Where(f => SupportedExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
                .ToList();
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogWarning("Access denied to some folders under {Folder}: {Message}", folderPath, ex.Message);
        }

        _logger.LogInformation("Found {Count} files to process", files.Count);
        result.TotalFilesFound = files.Count;

        List<FileMatchDto> matches = new(files.Count);

        foreach (string file in files)
        {
            try
            {
                FileMatchDto match = await _matcher.MatchFileAsync(file, allCaravans);
                ParseFolderStructure(file, folderPath, match);
                matches.Add(match);

                if (match.AlreadyLinked)
                    result.AlreadyLinkedFiles++;
                else if (match.IsMatched)
                    result.MatchedFiles++;
                else
                    result.UnmatchedFiles++;
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Failed to process file {File}: {Error}", file, ex.Message);
                result.UnmatchedFiles++;
            }
        }

        // Sort: already-linked first, then by confidence descending, then filename
        result.Files = matches
            .OrderByDescending(m => m.AlreadyLinked)
            .ThenByDescending(m => m.Confidence)
            .ThenBy(m => m.FileName)
            .ToList();

        sw.Stop();
        result.Duration = sw.Elapsed;

        _logger.LogInformation(
            "Scan complete in {Ms}ms — {Total} files, {Matched} matched, {Unmatched} unmatched",
            (int)sw.Elapsed.TotalMilliseconds, result.TotalFilesFound, result.MatchedFiles, result.UnmatchedFiles);

        return result;
    }

    // Extracts DocumentType, Year, and CustomerName from the path segments relative to the scanned root.
    // Expected structure: <root>/<vehicle>/<Document Type>/<Year - Customer Name>/<file>
    private static void ParseFolderStructure(string filePath, string rootFolder, FileMatchDto match)
    {
        string relative = Path.GetRelativePath(rootFolder, filePath);
        string[] parts = relative.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);

        // parts[0] = vehicle folder, parts[1] = doc type, parts[2] = year-customer, parts[3] = filename
        if (parts.Length < 3) return;

        string docTypeFolder = parts[1];
        if (KnownDocumentTypes.Contains(docTypeFolder))
            match.SuggestedDocumentType = docTypeFolder;

        if (parts.Length >= 4)
        {
            // "2024 - John Smith" or "2024 – John Smith"
            string yearCustomer = parts[2];
            int dashIdx = yearCustomer.IndexOfAny(new[] { '-', '–' });
            if (dashIdx > 0)
            {
                match.SuggestedYear = yearCustomer[..dashIdx].Trim();
                match.SuggestedCustomerName = yearCustomer[(dashIdx + 1)..].Trim();
            }
            else
            {
                match.SuggestedYear = yearCustomer.Trim();
            }
        }
    }
}
