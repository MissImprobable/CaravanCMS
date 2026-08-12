using CaravanCMS.Core;
using CaravanCMS.Core.Services;
using System.IO;
using System.IO.Compression;
using System.Text.RegularExpressions;

namespace CaravanCMS.Admin.Services;

/// <summary>
/// Watches the Caravan History folder for new documents and keeps R2/D1 in sync automatically,
/// replacing the old manual "Scan Files" workflow (which can't work anymore — Workers has no
/// local filesystem, so the old "point at a path already on disk" model is gone; every document
/// now has to be genuinely uploaded). Runs a live FileSystemWatcher while Admin is open, plus a
/// full catch-up scan on startup for anything added while Admin was closed.
///
/// Files that match a caravan confidently (VIN/rego in filename or folder path, >= AutoLinkThreshold)
/// are uploaded and linked automatically. Everything else — fuzzy make/model guesses, same-folder
/// inference, or no match at all — goes to the PendingReviewStore for the user to resolve by hand,
/// rather than risk silently mis-filing a photo into the wrong caravan's history.
///
/// .zip files are extracted rather than uploaded as-is (they're commonly a bundle of gas/electrical
/// appliance certs for one caravan — see the real example: RegoXXXX.zip containing several PDFs and
/// docx files, no internal folder structure). Each qualifying entry is matched and uploaded
/// individually, as if it were sitting in the same folder as the zip itself.
/// </summary>
public class DocumentSyncService : IDisposable
{
    private const double AutoLinkThreshold = 0.72;

    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".pdf", ".jpg", ".jpeg", ".png", ".tiff", ".tif", ".bmp", ".docx", ".doc", ".txt", ".xls", ".xlsx", ".pub" };

    private const string ZipExtension = ".zip";

    // Matches the "{Rego} - {rest}" top-level folder naming convention used throughout the
    // archive (e.g. "108A2 - SGBTV05BYBPS01222"). Rego kept short/alphanumeric-only to avoid
    // misreading a "Make Model - CustomerName" folder (no rego prefix at all) as one — a few
    // older folders in the archive follow that different pattern instead.
    private static readonly Regex TopLevelFolderPattern = new(@"^(?<rego>[A-Za-z0-9]{2,8})\s*-\s*(?<rest>.*)$");

    private readonly ApiClient _api;
    private readonly PendingReviewStore _reviewStore;
    private readonly string _rootPath;
    private readonly SemaphoreSlim _processLock = new(1, 1);

    private FileSystemWatcher? _watcher;
    private List<CaravanSummaryDto> _caravanCache = new();
    private HashSet<string> _knownPaths = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, string> _folderRegoIndex = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _failedPlaceholderRegos = new(StringComparer.OrdinalIgnoreCase);

    public event Action<string>? StatusChanged;

    public DocumentSyncService(ApiClient api, PendingReviewStore reviewStore, string rootPath)
    {
        _api = api;
        _reviewStore = reviewStore;
        _rootPath = rootPath;
    }

    /// <summary>Loads current server state, starts live watching, and kicks off a background catch-up scan.</summary>
    public async Task StartAsync()
    {
        if (!Directory.Exists(_rootPath))
        {
            StatusChanged?.Invoke($"Caravan History folder not found: {_rootPath}");
            return;
        }

        try
        {
            await RefreshCachesAsync();
        }
        catch (Exception ex)
        {
            // Fire-and-forget callers (App.OnStartup uses "_ = StartAsync()") would otherwise
            // lose this as an unobserved task exception — surface it instead of silently
            // leaving _caravanCache/_knownPaths empty, which would make every real file look
            // "unmatched" and flood the review queue with false positives.
            StatusChanged?.Invoke($"Document sync failed to start: {ex.Message}");
            return;
        }

        if (_caravanCache.Count == 0)
        {
            StatusChanged?.Invoke("Document sync: server returned 0 caravans — not scanning to avoid false unmatched entries.");
            return;
        }

        StartWatcher();
        _ = Task.Run(CatchUpScanAsync);
    }

    private async Task RefreshCachesAsync()
    {
        _caravanCache = await _api.GetCaravansAsync();
        List<DocumentDto> allDocs = await _api.GetAllDocumentsAsync();
        _knownPaths = new HashSet<string>(allDocs.Select(d => d.FilePath), StringComparer.OrdinalIgnoreCase);
        _folderRegoIndex = DocumentMatcher.BuildFolderRegoIndex(
            allDocs.Select(d => (d.FilePath, d.RegistrationNumber)));
    }

    private void StartWatcher()
    {
        _watcher = new FileSystemWatcher(_rootPath)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite,
        };
        _watcher.Created += async (_, e) => await SafeProcessPathAsync(e.FullPath);
        _watcher.Renamed += async (_, e) => await SafeProcessPathAsync(e.FullPath);
        _watcher.EnableRaisingEvents = true;
    }

    /// <summary>Walks the whole tree once — catches anything added while Admin wasn't running.</summary>
    public async Task CatchUpScanAsync()
    {
        StatusChanged?.Invoke("Scanning Caravan History for new documents...");
        int autoLinked = 0, queuedForReview = 0, noMatch = 0;

        foreach (string file in EnumerateCandidateFiles(_rootPath))
        {
            ProcessResult result = await SafeProcessPathAsync(file);
            switch (result)
            {
                case ProcessResult.AutoLinked: autoLinked++; break;
                case ProcessResult.QueuedForReview: queuedForReview++; break;
                case ProcessResult.NoMatch: noMatch++; break;
            }
        }

        if (autoLinked == 0 && queuedForReview == 0 && noMatch == 0)
        {
            StatusChanged?.Invoke("Document sync: nothing new.");
        }
        else
        {
            StatusChanged?.Invoke(
                $"Scan complete — {autoLinked} auto-linked, {queuedForReview} need review, " +
                $"{noMatch} skipped (no matching caravan found).");
        }
    }

    private enum ProcessResult { AutoLinked, QueuedForReview, NoMatch, AlreadyKnown, Error }

    private static IEnumerable<string> EnumerateCandidateFiles(string root)
    {
        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories);
        }
        catch
        {
            yield break;
        }

        foreach (string file in files)
        {
            string ext = Path.GetExtension(file);
            bool candidate = SupportedExtensions.Contains(ext) || ext.Equals(ZipExtension, StringComparison.OrdinalIgnoreCase);
            if (!candidate) continue;
            if (IsInResizedFolder(file)) continue;
            yield return file;
        }
    }

    private static bool IsInResizedFolder(string filePath) =>
        filePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Any(segment => segment.Equals("resized", StringComparison.OrdinalIgnoreCase));

    /// <summary>Entry point for both the watcher and the catch-up scan — routes zips through extraction.</summary>
    private async Task<ProcessResult> SafeProcessPathAsync(string filePath)
    {
        string ext = Path.GetExtension(filePath);
        if (IsInResizedFolder(filePath)) return ProcessResult.Error;

        if (ext.Equals(ZipExtension, StringComparison.OrdinalIgnoreCase))
            return await SafeProcessZipAsync(filePath);

        if (!SupportedExtensions.Contains(ext)) return ProcessResult.Error;
        return await SafeProcessAsync(matchPath: filePath, readPath: filePath);
    }

    /// <summary>
    /// Extracts a zip to a temp folder and processes each qualifying entry as if it were sitting
    /// next to the zip itself (so folder-based VIN/rego matching still works). The zip's own
    /// filename is folded into each entry's synthetic match path so same-named entries in
    /// different zips (e.g. two "Cooker.docx") don't collide in the known/pending/ignored sets.
    /// </summary>
    private async Task<ProcessResult> SafeProcessZipAsync(string zipPath)
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "CaravanCMS-zip-" + Guid.NewGuid());
        ProcessResult overall = ProcessResult.AlreadyKnown;

        try
        {
            if (!await WaitUntilFileReadyAsync(zipPath)) return ProcessResult.Error;

            Directory.CreateDirectory(tempDir);
            ZipFile.ExtractToDirectory(zipPath, tempDir);

            string? zipDir = Path.GetDirectoryName(zipPath);
            string zipStem = Path.GetFileNameWithoutExtension(zipPath);
            if (zipDir is null) return ProcessResult.Error;

            foreach (string entryPath in Directory.EnumerateFiles(tempDir, "*", SearchOption.AllDirectories))
            {
                string entryExt = Path.GetExtension(entryPath);
                if (!SupportedExtensions.Contains(entryExt)) continue;

                string entryFileName = Path.GetFileName(entryPath);
                string matchPath = Path.Combine(zipDir, $"{zipStem}_{entryFileName}");

                ProcessResult result = await SafeProcessAsync(matchPath, entryPath);
                if (result is ProcessResult.AutoLinked or ProcessResult.QueuedForReview or ProcessResult.NoMatch)
                    overall = result;
            }

            return overall;
        }
        catch (Exception ex)
        {
            StatusChanged?.Invoke($"Could not extract {Path.GetFileName(zipPath)}: {ex.Message}");
            return ProcessResult.Error;
        }
        finally
        {
            try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true); }
            catch { /* best effort cleanup */ }
        }
    }

    private async Task<ProcessResult> SafeProcessAsync(string matchPath, string readPath)
    {
        try
        {
            return await ProcessFileAsync(matchPath, readPath);
        }
        catch (Exception ex)
        {
            StatusChanged?.Invoke($"Sync error on {Path.GetFileName(matchPath)}: {ex.Message}");
            return ProcessResult.Error;
        }
    }

    /// <param name="matchPath">
    /// Path used for dedup, fuzzy-matching, and the D1 audit trail. For a real file this equals
    /// readPath; for a zip entry it's synthetic (zip's own folder + zip name + entry name) so
    /// folder-based matching still sees the zip's real location.
    /// </param>
    /// <param name="readPath">Real path to read bytes from (the actual file, or a temp-extracted zip entry).</param>
    private async Task<ProcessResult> ProcessFileAsync(string matchPath, string readPath)
    {
        await _processLock.WaitAsync();
        try
        {
            if (_knownPaths.Contains(matchPath)) return ProcessResult.AlreadyKnown;
            if (_reviewStore.IsIgnored(matchPath)) return ProcessResult.AlreadyKnown;
            if (_reviewStore.IsPending(matchPath)) return ProcessResult.AlreadyKnown;

            if (!await WaitUntilFileReadyAsync(readPath)) return ProcessResult.Error;

            FileMatchDto match = DocumentMatcher.MatchFile(matchPath, _caravanCache, _folderRegoIndex);

            if (match.Confidence == 0)
            {
                CaravanSummaryDto? placeholder = await TryCreatePlaceholderCaravanAsync(matchPath);
                if (placeholder is not null)
                {
                    _caravanCache.Add(placeholder);
                    match = DocumentMatcher.MatchFile(matchPath, _caravanCache, _folderRegoIndex);
                }
            }

            if (match.Confidence >= AutoLinkThreshold && match.SuggestedRegistrationNumber is not null)
            {
                string documentType = DocumentMatcher.InferDocumentType(_rootPath, matchPath);
                await _api.UploadDocumentAsync(readPath, match.SuggestedRegistrationNumber, documentType, match.MatchMethod, sourceFilePath: matchPath);
                _knownPaths.Add(matchPath);
                StatusChanged?.Invoke($"Auto-linked {Path.GetFileName(matchPath)} → {match.SuggestedRegistrationNumber}");
                return ProcessResult.AutoLinked;
            }

            if (match.Confidence > 0)
            {
                // Some signal, just not enough to auto-link — worth a human decision.
                _reviewStore.AddPending(new PendingDocument
                {
                    FilePath = matchPath,
                    SuggestedRegistrationNumber = match.SuggestedRegistrationNumber,
                    SuggestedCaravanDescription = match.SuggestedCaravanDescription,
                    Confidence = match.Confidence,
                    MatchMethod = match.MatchMethod,
                });
                StatusChanged?.Invoke($"{Path.GetFileName(matchPath)} needs review — added to queue.");
                return ProcessResult.QueuedForReview;
            }

            // Zero confidence means no caravan folder/VIN/rego/make-model signal matched at all —
            // most commonly a folder for a caravan no longer in the database (sold, deleted).
            // Nothing to confirm, so don't clutter the interactive review queue; marking it
            // ignored (via the IsIgnored check above) stops it from being reprocessed every scan.
            _reviewStore.Ignore(matchPath);
            return ProcessResult.NoMatch;
        }
        finally
        {
            _processLock.Release();
        }
    }

    /// <summary>
    /// If the file's top-level Caravan History folder looks like a real "{Rego} - {VIN}" folder
    /// but no Caravan row exists for that rego yet (an older vehicle MechanicDesk never imported),
    /// creates a minimal placeholder caravan so the file — and any siblings in the same folder —
    /// can link normally instead of being treated as permanently unmatched.
    /// </summary>
    private async Task<CaravanSummaryDto?> TryCreatePlaceholderCaravanAsync(string matchPath)
    {
        (string Rego, string? Vin)? parsed = TryParseCaravanFolder(matchPath);
        if (parsed is null) return null;

        string rego = parsed.Value.Rego;
        if (_caravanCache.Any(c => c.RegistrationNumber.Equals(rego, StringComparison.OrdinalIgnoreCase))) return null;
        if (_failedPlaceholderRegos.Contains(rego)) return null;

        try
        {
            CaravanSummaryDto? created = await _api.CreatePlaceholderCaravanAsync(rego, parsed.Value.Vin);
            if (created is not null)
                StatusChanged?.Invoke($"Created placeholder caravan {rego} (found in Caravan History but not in MechanicDesk).");
            return created;
        }
        catch (Exception ex)
        {
            _failedPlaceholderRegos.Add(rego);
            StatusChanged?.Invoke($"Could not create placeholder caravan {rego}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Parses the top-level folder segment (immediately under the Caravan History root) for the
    /// "{Rego} - {VIN}" naming convention. VIN is only extracted when it looks like real data —
    /// long enough and alphanumeric, not a redaction placeholder like "XXXXXXXXXX".
    /// </summary>
    private (string Rego, string? Vin)? TryParseCaravanFolder(string matchPath)
    {
        string relative = Path.GetRelativePath(_rootPath, matchPath);
        string topLevel = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0];

        Match m = TopLevelFolderPattern.Match(topLevel);
        if (!m.Success) return null;

        string rego = m.Groups["rego"].Value.ToUpperInvariant();
        string rest = m.Groups["rest"].Value.Trim();
        string? vin = null;

        if (rest.Length > 0)
        {
            string firstToken = rest.Split(new[] { ' ', '-' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
            bool looksLikeRedaction = firstToken.Length > 0 && firstToken.All(ch => ch is 'X' or 'x');
            if (firstToken.Length >= 5 && firstToken.All(char.IsLetterOrDigit) && !looksLikeRedaction)
                vin = firstToken.ToUpperInvariant();
        }

        return (rego, vin);
    }

    /// <summary>
    /// New files often arrive mid-write (OneDrive sync, camera app still copying) — retry opening
    /// exclusively for a few seconds before giving up, rather than uploading a truncated file.
    /// </summary>
    private static async Task<bool> WaitUntilFileReadyAsync(string filePath, int maxAttempts = 10)
    {
        for (int attempt = 0; attempt < maxAttempts; attempt++)
        {
            try
            {
                using FileStream stream = File.Open(filePath, FileMode.Open, FileAccess.Read, FileShare.None);
                return true;
            }
            catch (FileNotFoundException)
            {
                return false;
            }
            catch (IOException)
            {
                await Task.Delay(500);
            }
        }
        return false;
    }

    public void Dispose()
    {
        _watcher?.Dispose();
        _processLock.Dispose();
    }
}
