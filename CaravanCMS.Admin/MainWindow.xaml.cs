using CaravanCMS.Admin.Services;
using CaravanCMS.Admin.ViewModels;
using CaravanCMS.Admin.Views;
using System.Windows;
using System.Windows.Media;

namespace CaravanCMS.Admin;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;
    private bool _balloonShown;

    public MainWindow()
    {
        InitializeComponent();

        _vm = new MainViewModel(App.SettingsService);
        _vm.InitializeApi();
        DataContext = _vm;

        Loaded += async (_, _) =>
        {
            await _vm.RefreshStatsCommand.ExecuteAsync(null);
        };

        // Minimizing hides to tray instead of showing a taskbar-minimized window — the document
        // sync service keeps running either way, this is just about not cluttering the taskbar.
        StateChanged += (_, _) =>
        {
            if (WindowState == WindowState.Minimized) MinimizeToTray();
        };

        // Closing the window (the X button) also just goes to tray — sync only runs while the
        // process is alive, so a real quit is reserved for the tray icon's explicit "Exit".
        Closing += (_, e) =>
        {
            e.Cancel = true;
            MinimizeToTray();
        };
    }

    private void MinimizeToTray()
    {
        Hide();
        ShowInTaskbar = false;

        if (!_balloonShown)
        {
            App.TrayIcon?.ShowBalloon("CaravanCMS Admin", "Still running and watching for new documents. Right-click the tray icon to exit.");
            _balloonShown = true;
        }
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

    private void ReviewDocuments_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.ApiClientInstance is null)
        {
            MessageBox.Show("Not connected to API. Check settings.", "CaravanCMS Admin",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        DocumentReviewDialog dialog = new(_vm.ApiClientInstance, App.ReviewStore, _vm.Settings.CaravanHistoryPath);
        dialog.Owner = this;
        dialog.ShowDialog();
        _ = _vm.RefreshStatsCommand.ExecuteAsync(null);
    }

    private void InferredLinks_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.ApiClientInstance is null)
        {
            MessageBox.Show("Not connected to API. Check settings.", "CaravanCMS Admin",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        InferredLinksDialog dialog = new(_vm.ApiClientInstance);
        dialog.Owner = this;
        dialog.ShowDialog();
    }

    private void CustomerConversations_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.ApiClientInstance is null)
        {
            MessageBox.Show("Not connected to API. Check settings.", "CaravanCMS Admin",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        CustomerConversationsDialog dialog = new(_vm.ApiClientInstance);
        dialog.Owner = this;
        dialog.ShowDialog();
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
