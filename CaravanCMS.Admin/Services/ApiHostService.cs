using System.Diagnostics;
using System.IO;
using System.Net.Http;

namespace CaravanCMS.Admin.Services;

/// <summary>
/// Manages the CaravanCMS.Api process lifetime — start on Admin launch, stop on Admin close.
/// Looks for the API exe next to the Admin exe, then falls back to the configured path.
/// </summary>
public class ApiHostService : IDisposable
{
    private Process? _process;
    private readonly string _exePath;
    private readonly string _apiUrl;
    private bool _disposed;

    public bool IsRunning => _process is { HasExited: false };

    public ApiHostService(string apiExePath, string apiUrl)
    {
        _exePath = apiExePath;
        _apiUrl = apiUrl;
    }

    /// <summary>
    /// Starts the API process if it is not already running.
    /// Returns immediately — use WaitUntilReadyAsync to confirm the API is accepting requests.
    /// </summary>
    public bool Start()
    {
        if (IsRunning) return true;
        if (!File.Exists(_exePath)) return false;

        ProcessStartInfo psi = new(_exePath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = false,
            RedirectStandardError = false,
            WorkingDirectory = Path.GetDirectoryName(_exePath)!
        };

        _process = Process.Start(psi);
        return _process is not null;
    }

    /// <summary>
    /// Polls the API health endpoint until it responds or the timeout elapses.
    /// Call after Start() to know when the API is actually ready.
    /// </summary>
    public async Task<bool> WaitUntilReadyAsync(TimeSpan timeout)
    {
        using HttpClient http = new() { Timeout = TimeSpan.FromSeconds(2) };
        string healthUrl = _apiUrl.TrimEnd('/') + "/api/caravans/stats";

        DateTime deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                HttpResponseMessage r = await http.GetAsync(healthUrl);
                if (r.IsSuccessStatusCode) return true;
            }
            catch { }

            await Task.Delay(300);
        }
        return false;
    }

    /// <summary>Gracefully stops the managed API process.</summary>
    public void Stop()
    {
        if (_process is null || _process.HasExited) return;

        try
        {
            _process.CloseMainWindow();
            if (!_process.WaitForExit(3000))
                _process.Kill(entireProcessTree: true);
        }
        catch { }
        finally
        {
            _process.Dispose();
            _process = null;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }

    /// <summary>
    /// Resolves the API exe path: same directory as the Admin exe first,
    /// then the explicitly configured path.
    /// </summary>
    public static string ResolveExePath(string configuredPath)
    {
        string sameDir = Path.Combine(
            AppContext.BaseDirectory, "CaravanCMS.Api.exe");

        if (File.Exists(sameDir)) return sameDir;
        return configuredPath;
    }
}
