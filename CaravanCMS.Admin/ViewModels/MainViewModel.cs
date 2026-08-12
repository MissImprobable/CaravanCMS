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
    [ObservableProperty] private string _syncStatusText = "Watching for new documents...";
    [ObservableProperty] private string _reviewButtonLabel = "Review Documents";

    public AppSettings Settings { get; private set; }

    public MainViewModel(SettingsService settingsService)
    {
        _settingsService = settingsService;
        Settings = _settingsService.Load();
        ApiUrl = Settings.ApiBaseUrl;

        App.ReviewStore.Changed += UpdateReviewButtonLabel;
        UpdateReviewButtonLabel();

        if (App.DocumentSync is not null)
            App.DocumentSync.StatusChanged += status => SyncStatusText = status;
    }

    private void UpdateReviewButtonLabel() =>
        ReviewButtonLabel = App.ReviewStore.Count > 0 ? $"Review Documents ({App.ReviewStore.Count})" : "Review Documents";

    public void InitializeApi()
    {
        _api = new ApiClient(Settings.ApiBaseUrl, Settings.ApiKey);
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

        // Settings save may have swapped App.DocumentSync for a fresh instance (RestartDocumentSync) —
        // resubscribe so status updates keep flowing to this ViewModel after that happens.
        if (App.DocumentSync is not null)
            App.DocumentSync.StatusChanged += status => SyncStatusText = status;
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
