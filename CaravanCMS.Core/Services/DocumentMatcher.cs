using FuzzySharp;

namespace CaravanCMS.Core.Services;

/// <summary>
/// Matches local files to caravans using the same tiered confidence strategy as the original
/// Api-side FuzzyMatcher (see CaravanCMS.Api/Services/FuzzyMatcher.cs, kept as-is since Api is
/// being decommissioned), but decoupled from EF Core / ApplicationDbContext so it can run inside
/// Admin, which only talks to the caravan list over HTTP (CaravanSummaryDto), not a live DB context.
/// Callers own the "already linked" check (Admin does this via a pre-fetched set of known
/// FilePaths from GET /api/documents) — this matcher only computes the best candidate match.
///
/// Tiers:
/// 1. VIN in filename (0.95)
/// 2. Registration in filename (0.88)
/// 3. VIN or reg in folder path (0.72)
/// 3.5. Same vehicle folder as an already-linked document (0.62–0.68)
/// 4. Fuzzy make/model match against filename tokens (0.35–0.60)
/// </summary>
public static class DocumentMatcher
{
    private const int MinFuzzyScore = 65;

    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".jpg", ".jpeg", ".png", ".tiff", ".tif", ".bmp" };

    /// <summary>
    /// Documents are organized as {rootPath}\{Rego} - {VIN}\{Category}\... — the folder immediately
    /// under the caravan's own folder is the category (e.g. "Insurance Claims", "Photos"), matching
    /// the DocumentType values already used throughout the existing data.
    /// </summary>
    public static string InferDocumentType(string rootPath, string filePath)
    {
        string relative = Path.GetRelativePath(rootPath, filePath);
        string[] segments = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (segments.Length >= 2 && !string.IsNullOrWhiteSpace(segments[1]))
            return segments[1];

        return ImageExtensions.Contains(Path.GetExtension(filePath)) ? "Photos" : "Document";
    }

    public static FileMatchDto MatchFile(
        string filePath,
        IEnumerable<CaravanSummaryDto> caravans,
        IReadOnlyDictionary<string, string>? folderRegoIndex = null)
    {
        string fileName = Path.GetFileNameWithoutExtension(filePath).ToUpper();
        string folderPath = (Path.GetDirectoryName(filePath) ?? string.Empty).ToUpper();
        string fileNameWithExt = Path.GetFileName(filePath);

        FileMatchDto best = new()
        {
            FilePath = filePath,
            FileName = fileNameWithExt,
            FileExtension = Path.GetExtension(filePath).ToLowerInvariant(),
            FileSizeBytes = File.Exists(filePath) ? new FileInfo(filePath).Length : 0,
        };

        foreach (CaravanSummaryDto caravan in caravans)
        {
            TryMatch(caravan, fileName, folderPath, best);
        }

        // Tier 3.5: same vehicle-folder as an already-linked document
        if (folderRegoIndex is not null && best.Confidence < 0.68)
        {
            string? dir = Path.GetDirectoryName(filePath);
            for (int depth = 0; depth < 5 && dir is not null; depth++, dir = Path.GetDirectoryName(dir))
            {
                if (folderRegoIndex.TryGetValue(dir, out string? inferredRego))
                {
                    CaravanSummaryDto? c = caravans.FirstOrDefault(x => x.RegistrationNumber == inferredRego);
                    double conf = depth <= 1 ? 0.68 : 0.62;
                    if (conf > best.Confidence)
                    {
                        best.SuggestedRegistrationNumber = inferredRego;
                        best.SuggestedCaravanDescription = c is not null ? DescribeCaravan(c) : inferredRego;
                        best.Confidence = conf;
                        best.MatchMethod = "SameFolderInference";
                    }
                    break;
                }
            }
        }

        return best;
    }

    /// <summary>
    /// Builds a directory→RegistrationNumber lookup from already-linked document paths. A
    /// directory is included only when all linked docs under it share the same single rego.
    /// </summary>
    public static Dictionary<string, string> BuildFolderRegoIndex(
        IEnumerable<(string FilePath, string Rego)> linkedDocs)
    {
        var folderRegos = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

        foreach ((string filePath, string rego) in linkedDocs)
        {
            string? dir = Path.GetDirectoryName(filePath);
            for (int depth = 0; depth < 6 && dir is not null; depth++, dir = Path.GetDirectoryName(dir))
            {
                if (!folderRegos.TryGetValue(dir, out HashSet<string>? regos))
                    folderRegos[dir] = regos = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                regos.Add(rego);
            }
        }

        return folderRegos
            .Where(kv => kv.Value.Count == 1)
            .ToDictionary(kv => kv.Key, kv => kv.Value.First(), StringComparer.OrdinalIgnoreCase);
    }

    private static void TryMatch(CaravanSummaryDto caravan, string fileName, string folderPath, FileMatchDto best)
    {
        if (!string.IsNullOrEmpty(caravan.Vin) && caravan.Vin.Length >= 5)
        {
            string vin = caravan.Vin.ToUpper();
            if (fileName.Contains(vin))
            {
                if (0.95 > best.Confidence) Update(best, caravan, 0.95, "VinInFilename");
                return;
            }
        }

        if (!string.IsNullOrEmpty(caravan.RegistrationNumber) && caravan.RegistrationNumber.Length >= 3)
        {
            string reg = caravan.RegistrationNumber.ToUpper().Replace(" ", "");
            string fileNameClean = fileName.Replace(" ", "").Replace("-", "").Replace("_", "");
            if (fileNameClean.Contains(reg))
            {
                if (0.88 > best.Confidence) Update(best, caravan, 0.88, "RegInFilename");
                return;
            }
        }

        if (!string.IsNullOrEmpty(caravan.Vin) && caravan.Vin.Length >= 5 &&
            folderPath.Contains(caravan.Vin.ToUpper()))
        {
            if (0.72 > best.Confidence) Update(best, caravan, 0.72, "VinInFolderPath");
            return;
        }

        if (!string.IsNullOrEmpty(caravan.RegistrationNumber) && caravan.RegistrationNumber.Length >= 3 &&
            folderPath.Contains(caravan.RegistrationNumber.ToUpper().Replace(" ", "")))
        {
            if (0.72 > best.Confidence) Update(best, caravan, 0.72, "RegInFolderPath");
            return;
        }

        if (!string.IsNullOrEmpty(caravan.Make) && !string.IsNullOrEmpty(caravan.Model))
        {
            string target = $"{caravan.Make} {caravan.Model}".ToUpper();
            int score = Fuzz.PartialRatio(fileName, target);
            if (score >= MinFuzzyScore)
            {
                double confidence = 0.35 + ((score - MinFuzzyScore) / (double)(100 - MinFuzzyScore)) * 0.25;
                if (confidence > best.Confidence) Update(best, caravan, confidence, "FuzzyMakeModel");
            }
        }
    }

    private static void Update(FileMatchDto match, CaravanSummaryDto caravan, double confidence, string method)
    {
        match.SuggestedRegistrationNumber = caravan.RegistrationNumber;
        match.SuggestedCaravanDescription = DescribeCaravan(caravan);
        match.Confidence = confidence;
        match.MatchMethod = method;
    }

    private static string DescribeCaravan(CaravanSummaryDto c) =>
        $"{c.Year} {c.Make} {c.Model} — {c.RegistrationNumber ?? c.Vin ?? "No ID"}".Trim();
}
