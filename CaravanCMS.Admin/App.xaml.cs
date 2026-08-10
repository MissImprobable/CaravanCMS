using CaravanCMS.Admin.Services;
using System.Windows;

namespace CaravanCMS.Admin;

public partial class App : Application
{
    public static SettingsService SettingsService { get; } = new();
    public static ApiHostService? ApiHost { get; private set; }

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

        AppSettings settings = SettingsService.Load();
        string exePath = ApiHostService.ResolveExePath(settings.ApiExePath);
        ApiHost = new ApiHostService(exePath, settings.ApiBaseUrl);
        _ = ApiHost.StartAsync();
    }

    /// <summary>
    /// Rebuilds ApiHost from freshly saved settings, so a path/URL change in Settings takes
    /// effect on the next Start without requiring a full Admin app restart. Skipped while the
    /// API is currently running, since swapping the host would orphan the live process handle.
    /// </summary>
    public static void RefreshApiHostSettings(AppSettings settings)
    {
        if (ApiHost is { IsRunning: true }) return;

        ApiHost?.Dispose();
        string exePath = ApiHostService.ResolveExePath(settings.ApiExePath);
        ApiHost = new ApiHostService(exePath, settings.ApiBaseUrl);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        ApiHost?.Stop();
        ApiHost?.Dispose();
        base.OnExit(e);
    }
}
