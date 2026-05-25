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
        ApiHost.Start();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        ApiHost?.Stop();
        ApiHost?.Dispose();
        base.OnExit(e);
    }
}
