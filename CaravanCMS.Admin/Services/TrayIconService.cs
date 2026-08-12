using System.Windows.Forms;
using Application = System.Windows.Application;

namespace CaravanCMS.Admin.Services;

/// <summary>
/// System tray icon so Admin can minimize instead of closing — the document sync service only
/// runs while the process is alive, so "always watching" in practice means staying resident in
/// the tray rather than fully quitting when the window is closed.
/// </summary>
public class TrayIconService : IDisposable
{
    private readonly NotifyIcon _notifyIcon;

    public event Action? OpenRequested;
    public event Action? ExitRequested;

    public TrayIconService()
    {
        // Reuse the exe's own embedded icon (ApplicationIcon = admin.ico) rather than loading
        // a separate resource stream — one less thing to keep in sync with the actual icon file.
        System.Drawing.Icon? icon = System.Drawing.Icon.ExtractAssociatedIcon(Application.ResourceAssembly.Location);

        var menu = new ContextMenuStrip();
        var openItem = menu.Items.Add("Open CaravanCMS Admin");
        openItem.Font = new System.Drawing.Font(openItem.Font, System.Drawing.FontStyle.Bold);
        openItem.Click += (_, _) => OpenRequested?.Invoke();
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit").Click += (_, _) => ExitRequested?.Invoke();

        _notifyIcon = new NotifyIcon
        {
            Icon = icon,
            Text = "CaravanCMS Admin — watching for new documents",
            Visible = true,
            ContextMenuStrip = menu,
        };
        _notifyIcon.DoubleClick += (_, _) => OpenRequested?.Invoke();
    }

    public void ShowBalloon(string title, string text) =>
        _notifyIcon.ShowBalloonTip(3000, title, text, ToolTipIcon.Info);

    public void Dispose()
    {
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
    }
}
