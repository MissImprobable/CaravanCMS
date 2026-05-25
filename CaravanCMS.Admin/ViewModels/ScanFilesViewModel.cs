using CaravanCMS.Admin.Services;
using CaravanCMS.Core;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;
using System.IO;

namespace CaravanCMS.Admin.ViewModels;

/// <summary>ViewModel for the file scan dialog — initiates folder scan and bulk-links matched documents.</summary>
public partial class ScanFilesViewModel : ObservableObject
{
    private readonly ApiClient _api;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartScanCommand))]
    [NotifyCanExecuteChangedFor(nameof(LinkSelectedCommand))]
    private bool _isScanning;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartScanCommand))]
    [NotifyCanExecuteChangedFor(nameof(LinkSelectedCommand))]
    private bool _isLinking;

    [ObservableProperty] private string _progressText = "Click 'Start Scan' to scan the Caravan History folder.";
    [ObservableProperty] private bool _hasResults;
    [ObservableProperty] private string _resultSummary = string.Empty;
    [ObservableProperty] private string? _customFolderPath;
    [ObservableProperty] private int _selectedCount;

    public ObservableCollection<SelectableFileMatch> Files { get; } = new();

    /// <summary>Full caravan list loaded alongside each scan — used to populate the caravan picker.</summary>
    public ObservableCollection<CaravanSummaryDto> AllCaravans { get; } = new();

    public ScanFilesViewModel(ApiClient api)
    {
        _api = api;
    }

    [RelayCommand(CanExecute = nameof(CanScan))]
    private async Task StartScanAsync()
    {
        IsScanning = true;
        HasResults = false;
        Files.Clear();
        AllCaravans.Clear();
        ProgressText = "Scanning server folder for document files...";

        Progress<string> progress = new(msg => ProgressText = msg);

        try
        {
            // Run scan and caravan list in parallel
            Task<FileScanResultDto> scanTask = _api.ScanFilesAsync(CustomFolderPath, progress);
            Task<List<CaravanSummaryDto>> caravansTask = _api.GetCaravansAsync();

            await Task.WhenAll(scanTask, caravansTask);

            FileScanResultDto result = scanTask.Result;
            List<CaravanSummaryDto> caravans = caravansTask.Result;

            foreach (CaravanSummaryDto c in caravans.OrderBy(c => c.RegistrationNumber))
                AllCaravans.Add(c);

            foreach (FileMatchDto file in result.Files)
            {
                SelectableFileMatch item = new(file, AllCaravans);
                item.SelectionChanged = OnFileSelectionChanged;
                Files.Add(item);
            }

            HasResults = true;
            ResultSummary = $"{result.TotalFilesFound} files found · " +
                            $"{result.MatchedFiles} matched · " +
                            $"{result.UnmatchedFiles} unmatched · " +
                            $"{result.AlreadyLinkedFiles} already linked · " +
                            $"Took {result.Duration.TotalSeconds:F1}s";
            ProgressText = "Scan complete. Review matches below, then click 'Link Selected'.";
        }
        catch (Exception ex)
        {
            ProgressText = $"Scan failed: {ex.Message}";
        }
        finally
        {
            IsScanning = false;
            UpdateSelectedCount();
        }
    }

    [RelayCommand(CanExecute = nameof(CanLink))]
    private async Task LinkSelectedAsync()
    {
        IsLinking = true;
        int linked = 0;
        int failed = 0;

        foreach (SelectableFileMatch item in Files.Where(f => f.IsSelected && !f.File.AlreadyLinked && f.SelectedCaravan is not null))
        {
            try
            {
                ProgressText = $"Linking {item.File.FileName}...";
                string? yearCustomer = item.File.SuggestedYear is not null && item.File.SuggestedCustomerName is not null
                    ? $"{item.File.SuggestedYear} - {item.File.SuggestedCustomerName}"
                    : item.File.SuggestedYear;

                await _api.LinkDocumentAsync(new LinkDocumentRequest
                {
                    RegistrationNumber = item.SelectedCaravan!.RegistrationNumber,
                    FilePath = item.File.FilePath,
                    DocumentType = item.File.SuggestedDocumentType ?? "Document",
                    Category = yearCustomer
                });
                item.IsLinked = true;
                linked++;
            }
            catch
            {
                failed++;
            }
        }

        ProgressText = $"Done — {linked} documents linked" + (failed > 0 ? $", {failed} failed." : ".");
        IsLinking = false;
        UpdateSelectedCount();
    }

    [RelayCommand]
    private void SelectAll() => SetAll(true);

    [RelayCommand]
    private void SelectNone() => SetAll(false);

    private void SetAll(bool value)
    {
        foreach (SelectableFileMatch item in Files.Where(f => !f.File.AlreadyLinked && f.SelectedCaravan is not null))
            item.IsSelected = value;
        UpdateSelectedCount();
    }

    public void UpdateSelectedCount() =>
        SelectedCount = Files.Count(f => f.IsSelected && !f.File.AlreadyLinked);

    private void OnFileSelectionChanged()
    {
        UpdateSelectedCount();
        LinkSelectedCommand.NotifyCanExecuteChanged();
    }

    private bool CanScan() => !IsScanning && !IsLinking;
    private bool CanLink() => !IsLinking && !IsScanning && Files.Any(f => f.IsSelected && !f.File.AlreadyLinked && f.SelectedCaravan is not null);
}

/// <summary>Wraps a FileMatchDto with UI selection state for the DataGrid.</summary>
public partial class SelectableFileMatch : ObservableObject
{
    public FileMatchDto File { get; }
    public Action? SelectionChanged { get; set; }

    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private bool _isLinked;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedDisplay))]
    private CaravanSummaryDto? _selectedCaravan;

    partial void OnIsSelectedChanged(bool value) => SelectionChanged?.Invoke();

    partial void OnSelectedCaravanChanged(CaravanSummaryDto? value)
    {
        // Auto-check the row when the user picks a caravan
        if (value is not null && !File.AlreadyLinked)
            IsSelected = true;
        SelectionChanged?.Invoke();
    }

    public string ConfidenceDisplay => $"{File.Confidence:P0}";

    /// <summary>Registration number of the selected caravan, shown in the cell template.</summary>
    public string SelectedDisplay => SelectedCaravan?.RegistrationNumber ?? string.Empty;

    /// <summary>Last three path segments of the file's directory — vehicle folder, doc type, year/customer.</summary>
    public string SourceFolder
    {
        get
        {
            string? dir = Path.GetDirectoryName(File.FilePath);
            if (dir is null) return string.Empty;
            string[] parts = dir.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
            return string.Join(@" \ ", parts.TakeLast(3));
        }
    }

    public SelectableFileMatch(FileMatchDto file, IReadOnlyList<CaravanSummaryDto> allCaravans)
    {
        File = file;
        _isSelected = file.IsMatched && !file.AlreadyLinked;
        _isLinked = file.AlreadyLinked;
        // Pre-select the suggested caravan so the picker shows the auto-matched one
        _selectedCaravan = file.SuggestedRegistrationNumber is not null
            ? allCaravans.FirstOrDefault(c => c.RegistrationNumber == file.SuggestedRegistrationNumber)
            : null;
    }
}
