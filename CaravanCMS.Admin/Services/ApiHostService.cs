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

    /// <summary>True when a healthy API was found already running (started outside this app) rather than launched by us.</summary>
    private bool _externallyManaged;

    public bool IsRunning => _process is { HasExited: false } || _externallyManaged;

    public ApiHostService(string apiExePath, string apiUrl)
    {
        _exePath = apiExePath;
        _apiUrl = apiUrl;
    }

    /// <summary>
    /// Starts the API process if it is not already running — first checking whether a healthy
    /// instance is already reachable at the configured URL (e.g. started manually, or by another
    /// copy of Admin), before trying to locate and launch the exe.
    /// Returns immediately — use WaitUntilReadyAsync to confirm the API is accepting requests.
    /// </summary>
    public async Task<bool> StartAsync()
    {
        if (IsRunning) return true;

        if (await IsHealthyAsync())
        {
            _externallyManaged = true;
            return true;
        }

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

    /// <summary>Single, immediate check of the API's unauthenticated health endpoint.</summary>
    private async Task<bool> IsHealthyAsync()
    {
        try
        {
            using HttpClient http = new() { Timeout = TimeSpan.FromSeconds(2) };
            HttpResponseMessage r = await http.GetAsync(_apiUrl.TrimEnd('/') + "/health");
            return r.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Polls the API's unauthenticated health endpoint until it responds or the timeout elapses.
    /// Call after StartAsync() to know when the API is actually ready.
    /// </summary>
    public async Task<bool> WaitUntilReadyAsync(TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (await IsHealthyAsync()) return true;
            await Task.Delay(300);
        }
        return false;
    }

    /// <summary>
    /// Stops the managed API process. Does nothing if the API is externally managed (we never
    /// launched it, so we have no process handle to stop, and shouldn't assume ownership of it).
    /// </summary>
    public void Stop()
    {
        if (_externallyManaged)
        {
            _externallyManaged = false;
            return;
        }

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
