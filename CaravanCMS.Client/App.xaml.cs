using CaravanCMS.Client.Services;
using System.Windows;

namespace CaravanCMS.Client;

public partial class App : Application
{
    public static SettingsService SettingsService { get; } = new();
    public static ClientSettings Settings { get; private set; } = new();

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        Settings = SettingsService.Load();

        DispatcherUnhandledException += (_, args) =>
        {
            MessageBox.Show(
                $"An unexpected error occurred:\n\n{args.Exception.Message}",
                "CaravanCMS — Error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            args.Handled = true;
        };
    }
}
