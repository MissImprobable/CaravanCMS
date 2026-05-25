using CaravanCMS.Client.Services;
using CaravanCMS.Core;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;

namespace CaravanCMS.Client.ViewModels;

/// <summary>ViewModel for the main window — handles both plate lookup and caravan list modes.</summary>
public partial class MainViewModel : ObservableObject
{
    private readonly ApiClient _api;

    // ── Lookup mode ───────────────────────────────────────────────────────────
    [ObservableProperty] private bool _isLookupMode = true;
    [ObservableProperty] private string _regoQuery = string.Empty;
    [ObservableProperty] private string _lookupStatus = string.Empty;
    [ObservableProperty] private bool _hasLookupStatus;

    // ── List mode ─────────────────────────────────────────────────────────────
    [ObservableProperty] private string _searchQuery = string.Empty;
    [ObservableProperty] private bool _isSearching;
    [ObservableProperty] private string _statusText = string.Empty;
    [ObservableProperty] private bool _hasResults;

    public ObservableCollection<CaravanSummaryDto> SearchResults { get; } = new();

    /// <summary>Fired when a rego lookup finds exactly one caravan — codebehind opens the detail window.</summary>
    public event Action<CaravanSummaryDto>? CaravanLookupSuccess;

    public MainViewModel(ApiClient api) => _api = api;

    partial void OnLookupStatusChanged(string value) =>
        HasLookupStatus = !string.IsNullOrEmpty(value);

    // ── Lookup commands ───────────────────────────────────────────────────────

    [RelayCommand]
    private async Task LookupRegoAsync()
    {
        string rego = RegoQuery.Trim();
        if (string.IsNullOrEmpty(rego)) return;

        LookupStatus = string.Empty;
        try
        {
            List<CaravanSummaryDto> results = await _api.SearchAsync(rego);

            if (results.Count == 0)
            {
                LookupStatus = $"No caravan found for \"{rego.ToUpperInvariant()}\".";
            }
            else if (results.Count == 1)
            {
                CaravanLookupSuccess?.Invoke(results[0]);
            }
            else
            {
                // Multiple hits — fall through to list view showing the matches
                SearchQuery = rego;
                SearchResults.Clear();
                foreach (CaravanSummaryDto item in results)
                    SearchResults.Add(item);
                HasResults = true;
                StatusText = $"{results.Count} caravans matched \"{rego.ToUpperInvariant()}\".";
                IsLookupMode = false;
            }
        }
        catch (Exception ex)
        {
            LookupStatus = $"Lookup failed: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task ShowListAsync()
    {
        IsLookupMode = false;
        if (SearchResults.Count == 0)
            await LoadAllAsync();
    }

    [RelayCommand]
    private void ShowLookup()
    {
        IsLookupMode = true;
        RegoQuery = string.Empty;
        LookupStatus = string.Empty;
    }

    // ── List commands ─────────────────────────────────────────────────────────

    [RelayCommand]
    private async Task SearchAsync()
    {
        IsSearching = true;
        SearchResults.Clear();
        HasResults = false;
        StatusText = "Searching...";
        try
        {
            string query = SearchQuery.Trim();
            List<CaravanSummaryDto> results = string.IsNullOrEmpty(query)
                ? await _api.GetAllCaravansAsync()
                : await _api.SearchAsync(query);

            foreach (CaravanSummaryDto item in results)
                SearchResults.Add(item);

            HasResults = results.Count > 0;
            StatusText = results.Count > 0
                ? $"{results.Count} caravan{(results.Count == 1 ? "" : "s")} found."
                : "No caravans matched your search.";
        }
        catch (Exception ex)
        {
            StatusText = $"Search failed: {ex.Message}";
        }
        finally
        {
            IsSearching = false;
        }
    }

    private async Task LoadAllAsync()
    {
        IsSearching = true;
        SearchResults.Clear();
        HasResults = false;
        StatusText = "Loading all caravans...";
        try
        {
            List<CaravanSummaryDto> results = await _api.GetAllCaravansAsync();
            foreach (CaravanSummaryDto item in results)
                SearchResults.Add(item);
            HasResults = results.Count > 0;
            StatusText = $"{results.Count} caravan{(results.Count == 1 ? "" : "s")} found.";
        }
        catch (Exception ex)
        {
            StatusText = $"Failed to load: {ex.Message}";
        }
        finally
        {
            IsSearching = false;
        }
    }
}
