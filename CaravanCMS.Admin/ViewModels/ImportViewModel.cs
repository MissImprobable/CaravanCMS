using CaravanCMS.Admin.Services;
using CaravanCMS.Core;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using System.Collections.ObjectModel;
using System.IO;
using System.Windows;

namespace CaravanCMS.Admin.ViewModels;

/// <summary>ViewModel for the MechanicDesk Excel import dialog.</summary>
public partial class ImportViewModel : ObservableObject
{
    private readonly ApiClient _api;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartImportCommand))]
    private string? _selectedFilePath;

    [ObservableProperty] private string? _selectedFileName;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartImportCommand))]
    private bool _isImporting;
    [ObservableProperty] private string _progressText = "Select an Excel file to begin.";
    [ObservableProperty] private bool _hasResult;
    [ObservableProperty] private bool _hasConflicts;
    [ObservableProperty] private string _resultSummary = string.Empty;
    [ObservableProperty] private bool _importSucceeded;
    [ObservableProperty] private string _warningsText = string.Empty;
    [ObservableProperty] private string _errorsText = string.Empty;

    public ObservableCollection<ImportConflictDto> Conflicts { get; } = new();
    public ObservableCollection<string> Errors { get; } = new();
    public ObservableCollection<string> Warnings { get; } = new();

    public ImportResultDto? LastResult { get; private set; }

    public ImportViewModel(ApiClient api)
    {
        _api = api;
    }

    [RelayCommand]
    private void BrowseFile()
    {
        OpenFileDialog dialog = new()
        {
            Title = "Select MechanicDesk Export",
            Filter = "Excel Files (*.xlsx)|*.xlsx|All Files (*.*)|*.*",
            Multiselect = false
        };

        if (dialog.ShowDialog() == true)
        {
            SelectedFilePath = dialog.FileName;
            SelectedFileName = Path.GetFileName(dialog.FileName);
            ProgressText = $"Ready to import: {SelectedFileName}";
            HasResult = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanImport))]
    private async Task StartImportAsync()
    {
        if (string.IsNullOrEmpty(SelectedFilePath)) return;

        IsImporting = true;
        HasResult = false;
        Conflicts.Clear();
        Errors.Clear();
        Warnings.Clear();
        WarningsText = string.Empty;
        ErrorsText = string.Empty;

        Progress<string> progress = new(msg => ProgressText = msg);

        try
        {
            ImportResultDto result = await _api.ImportMechanicDeskAsync(SelectedFilePath, progress);
            LastResult = result;

            // Populate observable collections
            foreach (ImportConflictDto conflict in result.Conflicts)
                Conflicts.Add(conflict);
            foreach (string error in result.Errors)
                Errors.Add(error);
            foreach (string warning in result.Warnings)
                Warnings.Add(warning);

            WarningsText = string.Join(Environment.NewLine, result.Warnings);
            ErrorsText = string.Join(Environment.NewLine, result.Errors);

            HasConflicts = Conflicts.Count > 0;

            ResultSummary = BuildSummary(result);
            ImportSucceeded = result.Errors.Count == 0;
            HasResult = true;
            ProgressText = ImportSucceeded ? "Import complete!" : "Import completed with errors.";
        }
        catch (Exception ex)
        {
            Errors.Add(ex.Message);
            ProgressText = "Import failed.";
            ResultSummary = $"Error: {ex.Message}";
            HasResult = true;
            ImportSucceeded = false;
        }
        finally
        {
            IsImporting = false;
        }
    }

    private bool CanImport() => !string.IsNullOrEmpty(SelectedFilePath) && !IsImporting;

    private static string BuildSummary(ImportResultDto r)
    {
        List<string> parts = new();
        if (r.CustomersImported > 0) parts.Add($"{r.CustomersImported} customers imported");
        if (r.CustomersUpdated > 0) parts.Add($"{r.CustomersUpdated} customers found (existing)");
        if (r.CaravansImported > 0) parts.Add($"{r.CaravansImported} caravans imported");
        if (r.JobsImported > 0) parts.Add($"{r.JobsImported} jobs imported");
        if (r.InvoicesImported > 0) parts.Add($"{r.InvoicesImported} invoices imported");
        if (r.Conflicts.Count > 0) parts.Add($"{r.Conflicts.Count} conflicts need review");
        if (r.Errors.Count > 0) parts.Add($"{r.Errors.Count} errors");
        if (parts.Count == 0) parts.Add("No new data to import.");
        return string.Join(" · ", parts) + $" (took {r.Duration.TotalSeconds:F1}s)";
    }
}
