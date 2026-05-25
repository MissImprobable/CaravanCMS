using CaravanCMS.Admin.Services;
using CaravanCMS.Admin.ViewModels;
using CaravanCMS.Admin.Views;
using System.Windows;
using System.Windows.Media;

namespace CaravanCMS.Admin;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;

    public MainWindow()
    {
        InitializeComponent();

        _vm = new MainViewModel(App.SettingsService);
        _vm.InitializeApi();
        DataContext = _vm;

        Loaded += async (_, _) =>
        {
            if (App.ApiHost is { IsRunning: true })
            {
                _vm.StatusMessage = "Waiting for API to be ready...";
                await App.ApiHost.WaitUntilReadyAsync(TimeSpan.FromSeconds(15));
            }
            await _vm.RefreshStatsCommand.ExecuteAsync(null);
        };
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        SettingsWindow win = new(App.SettingsService);
        win.Owner = this;
        if (win.ShowDialog() == true)
        {
            _vm.ReloadSettings();
            _ = _vm.RefreshStatsCommand.ExecuteAsync(null);
        }
    }

    private void ImportMechanicDesk_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.ApiClientInstance is null)
        {
            MessageBox.Show("Not connected to API. Check settings.", "CaravanCMS Admin",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        ImportDialog dialog = new(_vm.ApiClientInstance);
        dialog.Owner = this;
        dialog.ShowDialog();
        _ = _vm.RefreshStatsCommand.ExecuteAsync(null);
    }

    private void ScanFiles_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.ApiClientInstance is null)
        {
            MessageBox.Show("Not connected to API. Check settings.", "CaravanCMS Admin",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        ScanFilesDialog dialog = new(_vm.ApiClientInstance);
        dialog.Owner = this;
        dialog.ShowDialog();
        _ = _vm.RefreshStatsCommand.ExecuteAsync(null);
    }
}

/// <summary>Converts a bool to green/red color for the status dot. Used inline in XAML.</summary>
public class BoolToColorConverter : System.Windows.Data.IValueConverter
{
    public static readonly BoolToColorConverter Instance = new();

    public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) =>
        value is true ? Colors.SeaGreen : Colors.IndianRed;

    public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) =>
        throw new NotImplementedException();
}
