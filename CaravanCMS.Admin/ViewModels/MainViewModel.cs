using CaravanCMS.Admin.Services;
using CaravanCMS.Core;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Windows;

namespace CaravanCMS.Admin.ViewModels;

/// <summary>ViewModel for the Admin dashboard — shows connection status, import stats, and navigation.</summary>
public partial class MainViewModel : ObservableObject
{
    private readonly SettingsService _settingsService;
    private ApiClient? _api;

    [ObservableProperty] private bool _isConnected;
    [ObservableProperty] private string _connectionStatus = "Not connected";
    [ObservableProperty] private string _apiUrl = string.Empty;
    [ObservableProperty] private int _totalCaravans;
    [ObservableProperty] private int _totalCustomers;
    [ObservableProperty] private int _totalJobs;
    [ObservableProperty] private int _totalDocuments;
    [ObservableProperty] private string _databaseSize = "—";
    [ObservableProperty] private string _lastImportTime = "Never";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _statusMessage = "Ready";
    [ObservableProperty] private bool _apiProcessRunning;
    [ObservableProperty] private string _apiProcessLabel = "Start API";

    public AppSettings Settings { get; private set; }

    public MainViewModel(SettingsService settingsService)
    {
        _settingsService = settingsService;
        Settings = _settingsService.Load();
        ApiUrl = Settings.ApiBaseUrl;
    }

    public void InitializeApi()
    {
        _api = new ApiClient(Settings.ApiBaseUrl, Settings.ApiKey);
        SyncApiProcessState();
    }

    private void SyncApiProcessState()
    {
        ApiProcessRunning = App.ApiHost?.IsRunning ?? false;
        ApiProcessLabel = ApiProcessRunning ? "Stop API" : "Start API";
    }

    [RelayCommand]
    private async Task ToggleApiAsync()
    {
        ApiHostService? host = App.ApiHost;
        if (host is null) return;

        if (host.IsRunning)
        {
            host.Stop();
            SyncApiProcessState();
            IsConnected = false;
            ConnectionStatus = "Disconnected";
            StatusMessage = "API stopped.";
        }
        else
        {
            StatusMessage = "Starting API...";
            bool started = host.Start();
            if (!started)
            {
                StatusMessage = "Could not find CaravanCMS.Api.exe — check the path in Settings.";
                return;
            }

            bool ready = await host.WaitUntilReadyAsync(TimeSpan.FromSeconds(15));
            SyncApiProcessState();
            StatusMessage = ready ? "API started." : "API started but is not responding yet.";

            if (ready)
                await RefreshStatsCommand.ExecuteAsync(null);
        }
    }

    [RelayCommand]
    private async Task RefreshStatsAsync()
    {
        if (_api is null) return;
        IsBusy = true;
        try
        {
            ApiStatsDto? stats = await _api.GetStatsAsync();
            if (stats is not null)
            {
                TotalCaravans = stats.TotalCaravans;
                TotalCustomers = stats.TotalCustomers;
                TotalJobs = stats.TotalJobs;
                TotalDocuments = stats.TotalDocuments;
                DatabaseSize = FormatBytes(stats.DatabaseSizeBytes);
                LastImportTime = stats.LastImportAt.HasValue
                    ? stats.LastImportAt.Value.ToLocalTime().ToString("dd MMM yyyy h:mm tt")
                    : "Never";
                IsConnected = true;
                ConnectionStatus = $"Connected — {Settings.ApiBaseUrl}";
                StatusMessage = "Stats refreshed.";
            }
        }
        catch (Exception ex)
        {
            IsConnected = false;
            ConnectionStatus = "Disconnected";
            StatusMessage = $"Error: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task TestConnectionAsync()
    {
        if (_api is null) InitializeApi();
        IsBusy = true;
        StatusMessage = "Testing connection...";
        try
        {
            var (success, message) = await _api!.TestConnectionAsync();
            IsConnected = success;
            ConnectionStatus = success ? $"Connected — {Settings.ApiBaseUrl}" : "Disconnected";
            StatusMessage = message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    public void ReloadSettings()
    {
        Settings = _settingsService.Load();
        ApiUrl = Settings.ApiBaseUrl;
        InitializeApi();
    }

    private static string FormatBytes(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:F1} KB",
        < 1024 * 1024 * 1024 => $"{bytes / 1024.0 / 1024.0:F1} MB",
        _ => $"{bytes / 1024.0 / 1024.0 / 1024.0:F2} GB"
    };

    public ApiClient? ApiClientInstance => _api;
}
