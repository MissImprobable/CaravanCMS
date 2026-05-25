using CaravanCMS.Admin.Services;
using Microsoft.Win32;
using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace CaravanCMS.Admin.Views;

public partial class SettingsWindow : Window
{
    private readonly SettingsService _settingsService;
    private readonly AppSettings _settings;

    public SettingsWindow(SettingsService settingsService)
    {
        InitializeComponent();
        _settingsService = settingsService;
        _settings = settingsService.Load();
        DataContext = _settings;
        PwdApiKey.Password = _settings.ApiKey;
    }

    private void BrowseFolder_Click(object sender, RoutedEventArgs e)
    {
        OpenFolderDialog dialog = new()
        {
            Title = "Select the Caravan History root folder",
            InitialDirectory = _settings.CaravanHistoryPath
        };

        if (dialog.ShowDialog() == true)
        {
            _settings.CaravanHistoryPath = dialog.FolderName;
            TxtFolderPath.Text = dialog.FolderName;
        }
    }

    private async void TestConnection_Click(object sender, RoutedEventArgs e)
    {
        string url = TxtApiUrl.Text.Trim();
        string key = PwdApiKey.Password.Trim();

        if (string.IsNullOrEmpty(url))
        {
            ShowTestResult("Enter an API endpoint URL first.", false);
            return;
        }

        ApiClient testClient = new(url, key);
        var (success, message) = await testClient.TestConnectionAsync();
        ShowTestResult(success ? $"✅ {message}" : $"❌ {message}", success);
    }

    private void ShowTestResult(string message, bool success)
    {
        TxtTestResult.Text = message;
        TxtTestResult.Foreground = success
            ? new SolidColorBrush(Color.FromRgb(92, 158, 110))
            : new SolidColorBrush(Color.FromRgb(192, 72, 72));
        TestResultBorder.Visibility = Visibility.Visible;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        _settings.ApiBaseUrl = TxtApiUrl.Text.Trim().TrimEnd('/');
        _settings.ApiKey = PwdApiKey.Password.Trim();
        _settings.CaravanHistoryPath = TxtFolderPath.Text.Trim();
        _settingsService.Save(_settings);
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
