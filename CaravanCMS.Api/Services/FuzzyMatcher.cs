using CaravanCMS.Api.Data;
using CaravanCMS.Core;
using FuzzySharp;
using Microsoft.EntityFrameworkCore;

namespace CaravanCMS.Api.Services;

/// <summary>
/// Matches files to caravans using a tiered confidence strategy:
/// 1. VIN found in filename (0.95 confidence)
/// 2. Registration found in filename (0.88 confidence)
/// 3. VIN or reg found in folder path (0.72 confidence)
/// 4. Fuzzy make/model match against filename tokens (0.35–0.60 confidence)
/// </summary>
public class FuzzyMatcher
{
    private readonly ApplicationDbContext _db;
    private readonly ILogger<FuzzyMatcher> _logger;

    // Minimum fuzzy ratio (0–100) for a make/model match to be considered meaningful
    private const int MinFuzzyScore = 65;

    public FuzzyMatcher(ApplicationDbContext db, ILogger<FuzzyMatcher> logger)
    {
        _db = db;
        _logger = logger;
    }

    /// <summary>
    /// Attempts to match a file to a caravan. Returns the best match found.
    /// The FileMatchDto.IsMatched is true when Confidence >= 0.5.
    /// </summary>
    public async Task<FileMatchDto> MatchFileAsync(string filePath, List<Caravan>? caravanCache = null)
    {
        caravanCache ??= await _db.Caravans.AsNoTracking().ToListAsync();

        string fileName = Path.GetFileNameWithoutExtension(filePath).ToUpper();
        string folderPath = (Path.GetDirectoryName(filePath) ?? string.Empty).ToUpper();
        string fileNameWithExt = Path.GetFileName(filePath);

        // Check if already linked in DB
        Document? existingLink = await _db.Documents.FirstOrDefaultAsync(d => d.FilePath == filePath);
        if (existingLink is not null)
        {
            Caravan? linked = caravanCache.FirstOrDefault(c => c.RegistrationNumber == existingLink.RegistrationNumber);
            return new FileMatchDto
            {
                FilePath = filePath,
                FileName = fileNameWithExt,
                FileExtension = Path.GetExtension(filePath).ToLowerInvariant(),
                FileSizeBytes = new FileInfo(filePath).Length,
                Confidence = 1.0,
                MatchMethod = "AlreadyLinked",
                SuggestedRegistrationNumber = existingLink.RegistrationNumber,
                SuggestedCaravanDescription = linked is not null ? DescribeCaravan(linked) : null,
                LinkedRegistrationNumber = existingLink.RegistrationNumber,
                AlreadyLinked = true
            };
        }

        FileMatchDto best = new()
        {
            FilePath = filePath,
            FileName = fileNameWithExt,
            FileExtension = Path.GetExtension(filePath).ToLowerInvariant(),
            FileSizeBytes = new FileInfo(filePath).Length
        };

        foreach (Caravan caravan in caravanCache)
        {
            TryMatch(caravan, fileName, folderPath, best);
        }

        if (best.Confidence < 0.3)
            _logger.LogDebug("No match found for: {File}", fileNameWithExt);
        else
            _logger.LogDebug("Matched {File} → caravan {Rego} via {Method} ({Score:P0})",
                fileNameWithExt, best.SuggestedRegistrationNumber, best.MatchMethod, best.Confidence);

        return best;
    }

    private static void TryMatch(Caravan caravan, string fileName, string folderPath, FileMatchDto best)
    {
        // ── Tier 1: VIN in filename (highest confidence) ──────────────────────
        if (!string.IsNullOrEmpty(caravan.Vin) && caravan.Vin.Length >= 5)
        {
            string vin = caravan.Vin.ToUpper();
            if (fileName.Contains(vin))
            {
                if (0.95 > best.Confidence)
                    Update(best, caravan, 0.95, "VinInFilename");
                return;
            }
        }

        // ── Tier 2: Registration in filename ─────────────────────────────────
        if (!string.IsNullOrEmpty(caravan.RegistrationNumber) && caravan.RegistrationNumber.Length >= 3)
        {
            string reg = caravan.RegistrationNumber.ToUpper().Replace(" ", "");
            string fileNameClean = fileName.Replace(" ", "").Replace("-", "").Replace("_", "");

            if (fileNameClean.Contains(reg))
            {
                if (0.88 > best.Confidence)
                    Update(best, caravan, 0.88, "RegInFilename");
                return;
            }
        }

        // ── Tier 3: VIN or reg in folder path ────────────────────────────────
        if (!string.IsNullOrEmpty(caravan.Vin) && caravan.Vin.Length >= 5 &&
            folderPath.Contains(caravan.Vin.ToUpper()))
        {
            if (0.72 > best.Confidence)
                Update(best, caravan, 0.72, "VinInFolderPath");
            return;
        }

        if (!string.IsNullOrEmpty(caravan.RegistrationNumber) && caravan.RegistrationNumber.Length >= 3 &&
            folderPath.Contains(caravan.RegistrationNumber.ToUpper().Replace(" ", "")))
        {
            if (0.72 > best.Confidence)
                Update(best, caravan, 0.72, "RegInFolderPath");
            return;
        }

        // ── Tier 4: Fuzzy make/model match ────────────────────────────────────
        if (!string.IsNullOrEmpty(caravan.Make) && !string.IsNullOrEmpty(caravan.Model))
        {
            string target = $"{caravan.Make} {caravan.Model}".ToUpper();
            int score = Fuzz.PartialRatio(fileName, target);

            if (score >= MinFuzzyScore)
            {
                // Normalise FuzzySharp 0-100 score into 0.35–0.60 range
                double confidence = 0.35 + ((score - MinFuzzyScore) / (double)(100 - MinFuzzyScore)) * 0.25;
                if (confidence > best.Confidence)
                    Update(best, caravan, confidence, "FuzzyMakeModel");
            }
        }
    }

    private static void Update(FileMatchDto match, Caravan caravan, double confidence, string method)
    {
        match.SuggestedRegistrationNumber = caravan.RegistrationNumber;
        match.SuggestedCaravanDescription = DescribeCaravan(caravan);
        match.Confidence = confidence;
        match.MatchMethod = method;
    }

    private static string DescribeCaravan(Caravan c) =>
        $"{c.Year} {c.Make} {c.Model} — {c.RegistrationNumber ?? c.Vin ?? "No ID"}".Trim();
}
