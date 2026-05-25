using System.Diagnostics;
using System.Windows.Forms;

namespace CaravanCMS.Api;

internal sealed class TrayApplicationContext : ApplicationContext
{
    private readonly WebApplication _app;
    private readonly NotifyIcon _trayIcon;
    private readonly ToolStripMenuItem _statusItem;
    private readonly ToolStripMenuItem _urlItem;
    private readonly ToolStripMenuItem _toggleItem;

    private bool _stopping;

    internal TrayApplicationContext(WebApplication app)
    {
        _app = app;

        string iconPath = Path.Combine(AppContext.BaseDirectory, "Resources", "api.ico");

        _statusItem = new ToolStripMenuItem("Starting…") { Enabled = false };
        _urlItem    = new ToolStripMenuItem("URL: …")    { Enabled = false };
        _toggleItem = new ToolStripMenuItem("Stop API", null, OnToggle);

        var menu = new ContextMenuStrip();
        menu.Items.Add(_statusItem);
        menu.Items.Add(_urlItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Open Swagger UI", null, OnOpenSwagger));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_toggleItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Exit", null, OnExit));

        _trayIcon = new NotifyIcon
        {
            Text            = "CaravanCMS API",
            Icon            = File.Exists(iconPath) ? new Icon(iconPath) : SystemIcons.Application,
            ContextMenuStrip = menu,
            Visible         = true,
        };
        _trayIcon.DoubleClick += OnOpenSwagger;

        _ = StartApiAsync();
    }

    private async Task StartApiAsync()
    {
        try
        {
            await _app.StartAsync();

            string url = _app.Urls.FirstOrDefault() ?? "http://localhost:5000";
            SetStatus("Running", url);
        }
        catch (Exception ex)
        {
            SetStatus($"Failed: {ex.Message}", "");
            _toggleItem.Enabled = false;
        }
    }

    private void SetStatus(string status, string url)
    {
        var strip = _trayIcon.ContextMenuStrip!;
        if (strip.InvokeRequired)
        {
            strip.Invoke(() => SetStatus(status, url));
            return;
        }

        _statusItem.Text = $"Status: {status}";
        if (!string.IsNullOrEmpty(url))
            _urlItem.Text = $"URL: {url}";

        _trayIcon.Text = $"CaravanCMS API — {status}";
    }

    private void OnToggle(object? sender, EventArgs e)
    {
        if (_stopping) return;

        _stopping = true;
        _toggleItem.Enabled = false;
        _toggleItem.Text = "Stopping…";
        SetStatus("Stopping…", "");

        _ = StopAndRestartAsync();
    }

    private async Task StopAndRestartAsync()
    {
        try
        {
            await _app.StopAsync(TimeSpan.FromSeconds(10));
        }
        catch { /* best effort */ }

        SetStatus("Stopped — restart to resume", "");
        _statusItem.Text = "Stopped. Use 'Start API' to restart.";

        // Replace Stop with a Start item that relaunches the process
        _toggleItem.Text    = "Start API (restarts process)";
        _toggleItem.Click  -= OnToggle;
        _toggleItem.Click  += OnRestart;
        _toggleItem.Enabled = true;
    }

    private void OnRestart(object? sender, EventArgs e)
    {
        string exe = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule!.FileName;
        Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });
        ExitThread();
    }

    private void OnOpenSwagger(object? sender, EventArgs e)
    {
        string url = _app.Urls.FirstOrDefault() ?? "http://localhost:5000";
        Process.Start(new ProcessStartInfo($"{url}/swagger") { UseShellExecute = true });
    }

    private void OnExit(object? sender, EventArgs e)
    {
        _ = GracefulExitAsync();
    }

    private async Task GracefulExitAsync()
    {
        _trayIcon.Visible = false;
        try { await _app.StopAsync(TimeSpan.FromSeconds(5)); } catch { /* best effort */ }
        ExitThread();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
        }
        base.Dispose(disposing);
    }
}
