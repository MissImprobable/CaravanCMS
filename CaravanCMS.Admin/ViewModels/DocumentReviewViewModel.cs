using CaravanCMS.Admin.Services;
using CaravanCMS.Core;
using CaravanCMS.Core.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;
using System.IO;

namespace CaravanCMS.Admin.ViewModels;

/// <summary>
/// ViewModel for the document review dialog — shows files the sync service couldn't confidently
/// auto-link (fuzzy make/model guesses, same-folder inference, or no match at all), lets the user
/// pick the right caravan (or confirm a weak suggestion) and upload, or ignore the file for good.
/// </summary>
public partial class DocumentReviewViewModel : ObservableObject
{
    private readonly ApiClient _api;
    private readonly PendingReviewStore _reviewStore;
    private readonly string _rootPath;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LinkSelectedCommand))]
    private bool _isBusy;

    [ObservableProperty] private string _statusText = string.Empty;
    [ObservableProperty] private int _selectedCount;

    public ObservableCollection<SelectablePendingDocument> Items { get; } = new();
    public ObservableCollection<CaravanSummaryDto> AllCaravans { get; } = new();

    public DocumentReviewViewModel(ApiClient api, PendingReviewStore reviewStore, string rootPath)
    {
        _api = api;
        _reviewStore = reviewStore;
        _rootPath = rootPath;
    }

    public async Task LoadAsync()
    {
        IsBusy = true;
        StatusText = "Loading...";
        try
        {
            List<CaravanSummaryDto> caravans = await _api.GetCaravansAsync();
            AllCaravans.Clear();
            foreach (CaravanSummaryDto c in caravans.OrderBy(c => c.RegistrationNumber))
                AllCaravans.Add(c);

            Items.Clear();
            foreach (PendingDocument doc in _reviewStore.Pending)
            {
                SelectablePendingDocument item = new(doc, AllCaravans);
                item.SelectionChanged = UpdateSelectedCount;
                Items.Add(item);
            }
            StatusText = Items.Count == 0 ? "Nothing waiting for review." : $"{Items.Count} file(s) awaiting review.";
        }
        finally
        {
            IsBusy = false;
            UpdateSelectedCount();
        }
    }

    [RelayCommand(CanExecute = nameof(CanLink))]
    private async Task LinkSelectedAsync()
    {
        IsBusy = true;
        int linked = 0, failed = 0;

        foreach (SelectablePendingDocument item in Items.Where(i => i.IsSelected && i.SelectedCaravan is not null && !i.IsResolved).ToList())
        {
            try
            {
                StatusText = $"Linking {item.Doc.FileName}...";
                string documentType = DocumentMatcher.InferDocumentType(_rootPath, item.Doc.FilePath);
                await _api.UploadDocumentAsync(item.Doc.FilePath, item.SelectedCaravan!.RegistrationNumber, documentType, "ManualReview");
                _reviewStore.RemovePending(item.Doc.FilePath);
                item.IsResolved = true;
                linked++;
            }
            catch
            {
                failed++;
            }
        }

        StatusText = $"Done — {linked} linked" + (failed > 0 ? $", {failed} failed." : ".");
        IsBusy = false;
        UpdateSelectedCount();
    }

    [RelayCommand]
    private void IgnoreSelected()
    {
        foreach (SelectablePendingDocument item in Items.Where(i => i.IsSelected && !i.IsResolved).ToList())
        {
            _reviewStore.Ignore(item.Doc.FilePath);
            item.IsResolved = true;
        }
        StatusText = "Selected files won't be suggested again.";
        UpdateSelectedCount();
    }

    [RelayCommand]
    private void SelectAll() => SetAll(true);

    [RelayCommand]
    private void SelectNone() => SetAll(false);

    private void SetAll(bool value)
    {
        foreach (SelectablePendingDocument item in Items.Where(i => !i.IsResolved))
            item.IsSelected = value;
        UpdateSelectedCount();
    }

    private void UpdateSelectedCount()
    {
        SelectedCount = Items.Count(i => i.IsSelected && !i.IsResolved);
        LinkSelectedCommand.NotifyCanExecuteChanged();
    }

    private bool CanLink() => !IsBusy && Items.Any(i => i.IsSelected && i.SelectedCaravan is not null && !i.IsResolved);
}

/// <summary>Wraps a PendingDocument with UI selection state for the DataGrid.</summary>
public partial class SelectablePendingDocument : ObservableObject
{
    public PendingDocument Doc { get; }
    public Action? SelectionChanged { get; set; }

    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private bool _isResolved;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedDisplay))]
    private CaravanSummaryDto? _selectedCaravan;

    partial void OnIsSelectedChanged(bool value) => SelectionChanged?.Invoke();

    partial void OnSelectedCaravanChanged(CaravanSummaryDto? value)
    {
        if (value is not null && !IsResolved) IsSelected = true;
        SelectionChanged?.Invoke();
    }

    public string ConfidenceDisplay => Doc.Confidence > 0 ? $"{Doc.Confidence:P0}" : "—";
    public string SelectedDisplay => SelectedCaravan?.RegistrationNumber ?? string.Empty;

    /// <summary>Last three path segments of the file's directory — vehicle folder, doc type, year/customer.</summary>
    public string SourceFolder
    {
        get
        {
            string? dir = Path.GetDirectoryName(Doc.FilePath);
            if (dir is null) return string.Empty;
            string[] parts = dir.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
            return string.Join(@" \ ", parts.TakeLast(3));
        }
    }

    public SelectablePendingDocument(PendingDocument doc, IReadOnlyList<CaravanSummaryDto> allCaravans)
    {
        Doc = doc;
        _isSelected = false;
        _selectedCaravan = doc.SuggestedRegistrationNumber is not null
            ? allCaravans.FirstOrDefault(c => c.RegistrationNumber == doc.SuggestedRegistrationNumber)
            : null;
    }
}
