using CaravanCMS.Admin.Services;
using System.Windows;

namespace CaravanCMS.Admin;

public partial class App : System.Windows.Application
{
    public static SettingsService SettingsService { get; } = new();
    public static PendingReviewStore ReviewStore { get; } = new();
    public static DocumentSyncService? DocumentSync { get; private set; }
    public static TrayIconService? TrayIcon { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += (_, args) =>
        {
            MessageBox.Show(
                $"An unexpected error occurred:\n\n{args.Exception.Message}",
                "CaravanCMS Admin — Error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            args.Handled = true;
        };

        TrayIcon = new TrayIconService();
        TrayIcon.OpenRequested += ShowMainWindow;
        TrayIcon.ExitRequested += () => Shutdown();

        AppSettings settings = SettingsService.Load();
        ApiClient syncApi = new(settings.ApiBaseUrl, settings.ApiKey);
        DocumentSync = new DocumentSyncService(syncApi, ReviewStore, settings.CaravanHistoryPath);
        _ = DocumentSync.StartAsync();
    }

    private void ShowMainWindow()
    {
        Window? window = MainWindow;
        if (window is null) return;

        window.Show();
        window.WindowState = WindowState.Normal;
        window.Activate();
    }

    /// <summary>Restarts the document sync service against freshly saved settings (new folder path/API endpoint).</summary>
    public static void RestartDocumentSync(AppSettings settings)
    {
        DocumentSync?.Dispose();
        ApiClient syncApi = new(settings.ApiBaseUrl, settings.ApiKey);
        DocumentSync = new DocumentSyncService(syncApi, ReviewStore, settings.CaravanHistoryPath);
        _ = DocumentSync.StartAsync();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        DocumentSync?.Dispose();
        TrayIcon?.Dispose();
        base.OnExit(e);
    }
}
