using CaravanCMS.Admin.Services;
using CaravanCMS.Core;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;

namespace CaravanCMS.Admin.ViewModels;

/// <summary>ViewModel for the Inferred Links Report dialog — shows documents matched via fuzzy/inferred methods.</summary>
public partial class InferredLinksViewModel : ObservableObject
{
    private readonly ApiClient _api;

    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string _statusText = "Click 'Load Report' to fetch inferred document links.";
    [ObservableProperty] private string _summaryText = string.Empty;
    [ObservableProperty] private bool _hasResults;
    [ObservableProperty] private string _filterMethod = "All";

    public ObservableCollection<InferredLinkItemDto> AllItems { get; } = new();
    public ObservableCollection<InferredLinkItemDto> FilteredItems { get; } = new();

    public static IReadOnlyList<string> MethodFilters { get; } =
        ["All", "SameFolderInference", "FuzzyMakeModel", "RegInFolderPath", "VinInFolderPath"];

    public InferredLinksViewModel(ApiClient api)
    {
        _api = api;
    }

    [RelayCommand]
    private async Task LoadReportAsync()
    {
        IsLoading = true;
        HasResults = false;
        AllItems.Clear();
        FilteredItems.Clear();
        StatusText = "Loading inferred links report...";

        try
        {
            InferredLinksReportDto report = await _api.GetInferredLinksReportAsync();

            foreach (InferredLinkItemDto item in report.Items)
                AllItems.Add(item);

            SummaryText = report.TotalCount == 0
                ? "No inferred links found. Documents linked via high-confidence methods (VIN/rego in filename) are not shown."
                : $"{report.TotalCount} documents linked via inferred matching — review below to verify accuracy.";

            HasResults = report.TotalCount > 0;
            StatusText = $"Report loaded — {report.TotalCount} documents.";
            ApplyFilter();
        }
        catch (Exception ex)
        {
            StatusText = $"Failed to load report: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    partial void OnFilterMethodChanged(string value) => ApplyFilter();

    private void ApplyFilter()
    {
        FilteredItems.Clear();
        foreach (InferredLinkItemDto item in AllItems)
        {
            if (FilterMethod == "All" || item.LinkMethod == FilterMethod)
                FilteredItems.Add(item);
        }
    }

    public static string FriendlyMethod(string method) => method switch
    {
        "SameFolderInference" => "Same Folder",
        "FuzzyMakeModel"      => "Fuzzy Make/Model",
        "RegInFolderPath"     => "Rego in Path",
        "VinInFolderPath"     => "VIN in Path",
        _                     => method
    };
}
