using System.IO;
using System.Text.Json;

namespace CaravanCMS.Admin.Services;

/// <summary>A locally-detected file that couldn't be confidently auto-linked, awaiting manual review.</summary>
public class PendingDocument
{
    public string FilePath { get; set; } = string.Empty;
    public string FileName => Path.GetFileName(FilePath);
    public string? SuggestedRegistrationNumber { get; set; }
    public string? SuggestedCaravanDescription { get; set; }
    public double Confidence { get; set; }
    public string MatchMethod { get; set; } = "Unmatched";
    public DateTime DetectedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Persists the document-sync review queue (and permanently-ignored files) to
/// %LOCALAPPDATA%\CaravanCMS\ so they survive Admin restarts — a file the sync
/// service couldn't confidently match stays queued for review until the user
/// resolves or explicitly ignores it, not just for the current session.
/// </summary>
public class PendingReviewStore
{
    private readonly string _pendingPath;
    private readonly string _ignoredPath;
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    private List<PendingDocument> _pending = new();
    private HashSet<string> _ignored = new(StringComparer.OrdinalIgnoreCase);

    public event Action? Changed;

    public PendingReviewStore()
    {
        string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CaravanCMS");
        Directory.CreateDirectory(dir);
        _pendingPath = Path.Combine(dir, "pending-review.json");
        _ignoredPath = Path.Combine(dir, "ignored-files.json");
        Load();
    }

    public IReadOnlyList<PendingDocument> Pending => _pending;
    public int Count => _pending.Count;

    public bool IsPending(string filePath) =>
        _pending.Any(p => p.FilePath.Equals(filePath, StringComparison.OrdinalIgnoreCase));

    public bool IsIgnored(string filePath) => _ignored.Contains(filePath);

    public void AddPending(PendingDocument doc)
    {
        if (IsPending(doc.FilePath)) return;
        _pending.Add(doc);
        SavePending();
        Changed?.Invoke();
    }

    public void RemovePending(string filePath)
    {
        int removed = _pending.RemoveAll(p => p.FilePath.Equals(filePath, StringComparison.OrdinalIgnoreCase));
        if (removed > 0)
        {
            SavePending();
            Changed?.Invoke();
        }
    }

    public void Ignore(string filePath)
    {
        RemovePending(filePath);
        _ignored.Add(filePath);
        SaveIgnored();
    }

    private void Load()
    {
        try
        {
            if (File.Exists(_pendingPath))
                _pending = JsonSerializer.Deserialize<List<PendingDocument>>(File.ReadAllText(_pendingPath)) ?? new();
        }
        catch { _pending = new(); }

        try
        {
            if (File.Exists(_ignoredPath))
            {
                List<string>? list = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(_ignoredPath));
                _ignored = new HashSet<string>(list ?? new(), StringComparer.OrdinalIgnoreCase);
            }
        }
        catch { _ignored = new(StringComparer.OrdinalIgnoreCase); }
    }

    private void SavePending() => File.WriteAllText(_pendingPath, JsonSerializer.Serialize(_pending, JsonOpts));

    private void SaveIgnored() => File.WriteAllText(_ignoredPath, JsonSerializer.Serialize(_ignored.ToList(), JsonOpts));
}
