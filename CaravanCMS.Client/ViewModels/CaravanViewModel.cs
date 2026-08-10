using CaravanCMS.Client.Services;
using CaravanCMS.Core;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;

namespace CaravanCMS.Client.ViewModels;

/// <summary>ViewModel for the full caravan detail view — loads history, jobs, invoices, and documents.</summary>
public partial class CaravanViewModel : ObservableObject
{
    private readonly ApiClient _api;

    [ObservableProperty] private CaravanDetailDto? _caravan;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string _statusText = "Loading...";
    [ObservableProperty] private JobDetailDto? _expandedJob;
    [ObservableProperty] private ConversationDto? _expandedConversation;
    [ObservableProperty] private string? _activeTagFilter;
    [ObservableProperty] private IEnumerable<ConversationDto> _visibleConversations = Enumerable.Empty<ConversationDto>();
    [ObservableProperty] private string _newTagText = string.Empty;

    public ObservableCollection<JobDetailDto> Jobs { get; } = new();
    public ObservableCollection<DocumentItemViewModel> Documents { get; } = new();
    public ObservableCollection<ConversationDto> Conversations { get; } = new();

    public CaravanViewModel(ApiClient api)
    {
        _api = api;
    }

    public async Task LoadAsync(string rego)
    {
        IsLoading = true;
        StatusText = "Loading caravan history...";
        Jobs.Clear();
        Documents.Clear();
        Conversations.Clear();
        ActiveTagFilter = null;

        try
        {
            CaravanDetailDto? detail = await _api.GetCaravanDetailAsync(rego);
            if (detail is null)
            {
                StatusText = "Caravan not found.";
                return;
            }

            Caravan = detail;

            foreach (JobDetailDto job in detail.Jobs)
                Jobs.Add(job);

            foreach (DocumentDto doc in detail.Documents)
            {
                DocumentItemViewModel item = new(doc);
                Documents.Add(item);
                // Fire-and-forget thumbnail load — updates UI when ready
                _ = item.LoadThumbnailAsync(_api.DownloadDocumentAsync);
            }

            foreach (ConversationDto conv in detail.Conversations)
                Conversations.Add(conv);
            ApplyTagFilter();

            StatusText = $"{detail.Jobs.Count} jobs · {detail.Documents.Count} documents";
        }
        catch (Exception ex)
        {
            StatusText = $"Failed to load: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>Downloads the file to a temp folder and opens it in the system default application.</summary>
    [RelayCommand]
    private async Task ViewDocumentAsync(DocumentItemViewModel item)
    {
        StatusText = $"Opening {item.FileName}...";
        try
        {
            byte[] data = await _api.DownloadDocumentAsync(item.Doc.Id);
            string tempPath = Path.Combine(Path.GetTempPath(), item.FileName);
            await File.WriteAllBytesAsync(tempPath, data);
            Process.Start(new ProcessStartInfo(tempPath) { UseShellExecute = true });
            StatusText = $"Opened {item.FileName}";
        }
        catch (Exception ex)
        {
            StatusText = $"Open failed: {ex.Message}";
        }
    }

    /// <summary>Prompts the user for a save location, then downloads and saves the file there.</summary>
    [RelayCommand]
    private async Task SaveDocumentAsync(DocumentItemViewModel item)
    {
        string ext = Path.GetExtension(item.FileName);
        Microsoft.Win32.SaveFileDialog dlg = new()
        {
            FileName    = item.FileName,
            DefaultExt  = ext,
            Filter      = BuildSaveFilter(item.MimeType, ext)
        };

        if (dlg.ShowDialog() != true) return;

        StatusText = $"Saving {item.FileName}...";
        try
        {
            byte[] data = await _api.DownloadDocumentAsync(item.Doc.Id);
            await File.WriteAllBytesAsync(dlg.FileName, data);
            StatusText = $"Saved to {Path.GetFileName(dlg.FileName)}";
        }
        catch (Exception ex)
        {
            StatusText = $"Save failed: {ex.Message}";
        }
    }

    [RelayCommand]
    private void ToggleJobExpand(JobDetailDto job)
    {
        ExpandedJob = ExpandedJob == job ? null : job;
    }

    [RelayCommand]
    private void ToggleConversationExpand(ConversationDto conversation)
    {
        ExpandedConversation = ExpandedConversation == conversation ? null : conversation;
    }

    /// <summary>Clicking a tag chip filters the Conversations list to just that tag; clicking it again clears the filter.</summary>
    [RelayCommand]
    private void ToggleTagFilter(string tagName)
    {
        ActiveTagFilter = ActiveTagFilter == tagName ? null : tagName;
        ApplyTagFilter();
    }

    private void ApplyTagFilter()
    {
        VisibleConversations = string.IsNullOrEmpty(ActiveTagFilter)
            ? Conversations.ToList()
            : Conversations.Where(c => c.Tags.Any(t => t.Name == ActiveTagFilter)).ToList();
    }

    /// <summary>Adds a label to the currently-expanded conversation, creating the tag if it doesn't already exist.</summary>
    [RelayCommand]
    private async Task AddTagAsync()
    {
        if (ExpandedConversation is null) return;
        string tag = NewTagText.Trim();
        if (tag.Length == 0) return;

        NewTagText = string.Empty;
        try
        {
            ConversationDto updated = await _api.AddTagAsync(ExpandedConversation.Id, tag);
            ReplaceConversation(updated);
            StatusText = $"Tagged \"{tag}\".";
        }
        catch (Exception ex)
        {
            StatusText = $"Failed to add tag: {ex.Message}";
        }
    }

    private void ReplaceConversation(ConversationDto updated)
    {
        int index = Conversations.ToList().FindIndex(c => c.Id == updated.Id);
        if (index >= 0) Conversations[index] = updated;
        ExpandedConversation = updated;
        ApplyTagFilter();
    }

    private static string BuildSaveFilter(string? mimeType, string ext)
    {
        string upperExt = ext.TrimStart('.').ToUpperInvariant();
        return mimeType switch
        {
            "application/pdf"   => $"PDF Files (*.pdf)|*.pdf|All Files (*.*)|*.*",
            "image/jpeg"        => "JPEG Images (*.jpg;*.jpeg)|*.jpg;*.jpeg|All Files (*.*)|*.*",
            "image/png"         => "PNG Images (*.png)|*.png|All Files (*.*)|*.*",
            var m when m?.StartsWith("image/") == true
                                => $"{upperExt} Images (*.{ext.TrimStart('.')})|*{ext}|All Files (*.*)|*.*",
            _ when !string.IsNullOrEmpty(ext)
                                => $"{upperExt} Files (*{ext})|*{ext}|All Files (*.*)|*.*",
            _                   => "All Files (*.*)|*.*"
        };
    }
}
