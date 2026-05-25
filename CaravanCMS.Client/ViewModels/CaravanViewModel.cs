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

    public ObservableCollection<JobDetailDto> Jobs { get; } = new();
    public ObservableCollection<DocumentItemViewModel> Documents { get; } = new();

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
