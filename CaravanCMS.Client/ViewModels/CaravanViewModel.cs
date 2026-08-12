using CaravanCMS.Client.Services;
using CaravanCMS.Core;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text;

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
    [ObservableProperty] private string _newSubject = string.Empty;
    [ObservableProperty] private string _newType = "Call";
    [ObservableProperty] private string _newDirection = "Outbound";
    [ObservableProperty] private string _newBody = string.Empty;
    [ObservableProperty] private string _newMessageType = "Call";
    [ObservableProperty] private string _newMessageDirection = "Outbound";
    [ObservableProperty] private string _newMessageBody = string.Empty;
    [ObservableProperty] private bool _isSavingVehicleInfo;
    [ObservableProperty] private string? _vehicleInfoStatus;
    [ObservableProperty] private bool _conversationsNewestFirst = true;
    [ObservableProperty] private bool _jobsNewestFirst = true;
    [ObservableProperty] private IEnumerable<JobDetailDto> _visibleJobs = Enumerable.Empty<JobDetailDto>();

    public ObservableCollection<JobDetailDto> Jobs { get; } = new();
    public ObservableCollection<DocumentItemViewModel> Documents { get; } = new();
    public ObservableCollection<ConversationDto> Conversations { get; } = new();

    public static IReadOnlyList<string> TypeOptions { get; } = ["Call", "Email", "Note", "Meeting"];
    public static IReadOnlyList<string> DirectionOptions { get; } = ["Outbound", "Inbound"];

    public CaravanViewModel(ApiClient api)
    {
        _api = api;
    }

    /// <summary>Forces bound Caravan.* fields to re-read after an in-place mutation on the DTO —
    /// CaravanDetailDto doesn't implement INotifyPropertyChanged itself, so a direct field change
    /// (e.g. auto-computing a due date from an issue date) wouldn't otherwise reach the UI.</summary>
    public void RefreshCaravanDisplay() => OnPropertyChanged(nameof(Caravan));

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
            ApplyJobSort();

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

    /// <summary>Flips the Conversations list between newest-first and oldest-first, ordered by last activity.</summary>
    [RelayCommand]
    private void ToggleConversationSort()
    {
        ConversationsNewestFirst = !ConversationsNewestFirst;
        ApplyTagFilter();
    }

    private void ApplyTagFilter()
    {
        IEnumerable<ConversationDto> filtered = string.IsNullOrEmpty(ActiveTagFilter)
            ? Conversations
            : Conversations.Where(c => c.Tags.Any(t => t.Name == ActiveTagFilter));

        VisibleConversations = ConversationsNewestFirst
            ? filtered.OrderByDescending(c => c.LastActivityAt).ToList()
            : filtered.OrderBy(c => c.LastActivityAt).ToList();
    }

    /// <summary>Flips the Job History list between newest-first and oldest-first, ordered by finish date.</summary>
    [RelayCommand]
    private void ToggleJobSort()
    {
        JobsNewestFirst = !JobsNewestFirst;
        ApplyJobSort();
    }

    private void ApplyJobSort()
    {
        VisibleJobs = JobsNewestFirst
            ? Jobs.OrderByDescending(j => j.FinishDate).ToList()
            : Jobs.OrderBy(j => j.FinishDate).ToList();
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

    /// <summary>Logs a call, note, meeting, or manually-entered email against this caravan's customer —
    /// so things that happen on the phone or in person don't get lost, same as inbound emails do.</summary>
    [RelayCommand]
    private async Task LogConversationAsync()
    {
        if (Caravan?.Customer is null) return;
        if (string.IsNullOrWhiteSpace(NewSubject))
        {
            StatusText = "Enter a subject before logging.";
            return;
        }

        try
        {
            ConversationDto conversation = await _api.CreateConversationAsync(new CreateConversationRequest
            {
                CustomerId = Caravan.Customer.Id,
                RegistrationNumber = Caravan.RegistrationNumber,
                Subject = NewSubject.Trim()
            });

            await _api.AddMessageAsync(conversation.Id, new LogMessageRequest
            {
                Type = NewType,
                Direction = NewDirection,
                Body = string.IsNullOrWhiteSpace(NewBody) ? null : NewBody.Trim(),
                OccurredAt = DateTime.UtcNow
            });

            NewSubject = string.Empty;
            NewBody = string.Empty;
            StatusText = "Conversation logged.";
            await LoadAsync(Caravan.RegistrationNumber);
        }
        catch (Exception ex)
        {
            StatusText = $"Failed to log conversation: {ex.Message}";
        }
    }

    /// <summary>Appends a call/note/email to the currently-expanded conversation instead of starting a new
    /// one — e.g. a supplier reply that belongs with an existing thread. Saves re-typing the subject and
    /// avoids splitting related messages across duplicate conversation records.</summary>
    [RelayCommand]
    private async Task AddMessageToConversationAsync()
    {
        if (ExpandedConversation is null) return;

        try
        {
            CommunicationLogDto message = await _api.AddMessageAsync(ExpandedConversation.Id, new LogMessageRequest
            {
                Type = NewMessageType,
                Direction = NewMessageDirection,
                Body = string.IsNullOrWhiteSpace(NewMessageBody) ? null : NewMessageBody.Trim(),
                OccurredAt = DateTime.UtcNow
            });

            ConversationDto updated = new()
            {
                Id = ExpandedConversation.Id,
                CustomerId = ExpandedConversation.CustomerId,
                RegistrationNumber = ExpandedConversation.RegistrationNumber,
                Subject = ExpandedConversation.Subject,
                ExternalConversationId = ExpandedConversation.ExternalConversationId,
                StartedAt = ExpandedConversation.StartedAt,
                LastActivityAt = message.OccurredAt > ExpandedConversation.LastActivityAt ? message.OccurredAt : ExpandedConversation.LastActivityAt,
                Tags = ExpandedConversation.Tags,
                Messages = [.. ExpandedConversation.Messages, message]
            };

            NewMessageBody = string.Empty;
            ReplaceConversation(updated);
            StatusText = "Added to conversation.";
        }
        catch (Exception ex)
        {
            StatusText = $"Failed to add message: {ex.Message}";
        }
    }

    /// <summary>Zips every document (grouped the same way the Documents tab groups them) plus text
    /// exports of the vehicle info, job history, and full conversation log, then opens it as an Outlook
    /// draft attachment for review. Nothing is sent until a person hits Send in Outlook themselves.</summary>
    public async Task PackageAndSendAsync(string recipient)
    {
        if (Caravan is null) return;

        StatusText = "Building package...";
        string zipPath = Path.Combine(Path.GetTempPath(), $"{Caravan.RegistrationNumber}-history-{DateTime.Now:yyyyMMdd-HHmmss}.zip");
        try
        {
            await BuildZipAsync(zipPath, Documents, new Dictionary<string, string>
            {
                ["Vehicle Info.txt"] = FormatVehicleInfoText(),
                ["Job History.txt"] = FormatJobHistoryText(),
                ["Conversations.txt"] = FormatAllConversationsText()
            });

            OutlookEmailService.OpenDraft(
                recipient,
                subject: $"{Caravan.RegistrationNumber} — Full History",
                body: $"Attached: complete history for {Caravan.Year} {Caravan.Make} {Caravan.Model} ({Caravan.RegistrationNumber}).",
                attachmentPaths: [zipPath]);

            StatusText = "Draft opened in Outlook.";
        }
        catch (Exception ex)
        {
            StatusText = $"Failed to build package: {ex.Message}";
        }
        finally
        {
            TryDeleteFile(zipPath);
        }
    }

    /// <summary>Zips just the documents under one type group (Major Jobs, Self Containment, etc.),
    /// preserving the same source-folder sub-grouping shown in the Documents tab, and opens it as an
    /// Outlook draft attachment.</summary>
    public async Task SendDocumentTypeCopyAsync(string documentType, string recipient)
    {
        if (Caravan is null) return;

        StatusText = $"Building {documentType} package...";
        string zipPath = Path.Combine(Path.GetTempPath(), $"{Caravan.RegistrationNumber}-{documentType}-{DateTime.Now:yyyyMMdd-HHmmss}.zip");
        try
        {
            List<DocumentItemViewModel> docs = Documents.Where(d => d.DocumentType == documentType).ToList();
            await BuildZipAsync(zipPath, docs, textEntries: []);

            OutlookEmailService.OpenDraft(
                recipient,
                subject: $"{Caravan.RegistrationNumber} — {documentType}",
                body: $"Attached: {documentType} documents for {Caravan.Year} {Caravan.Make} {Caravan.Model} ({Caravan.RegistrationNumber}).",
                attachmentPaths: [zipPath]);

            StatusText = "Draft opened in Outlook.";
        }
        catch (Exception ex)
        {
            StatusText = $"Failed to build package: {ex.Message}";
        }
        finally
        {
            TryDeleteFile(zipPath);
        }
    }

    /// <summary>Opens an Outlook draft with a conversation's full message history formatted as
    /// readable plain text directly in the email body — no attachment needed, so the recipient
    /// (an insurer, a customer following up) can read it without opening anything.</summary>
    public void SendConversationCopy(ConversationDto conversation, string recipient)
    {
        if (Caravan is null) return;

        OutlookEmailService.OpenDraft(
            recipient,
            subject: $"{Caravan.RegistrationNumber} — {conversation.Subject ?? "Conversation"}",
            body: FormatConversationText(conversation));

        StatusText = "Draft opened in Outlook.";
    }

    private async Task BuildZipAsync(string zipPath, IEnumerable<DocumentItemViewModel> docs, Dictionary<string, string> textEntries)
    {
        using FileStream fileStream = new(zipPath, FileMode.Create);
        using ZipArchive archive = new(fileStream, ZipArchiveMode.Create);

        foreach (DocumentItemViewModel doc in docs)
        {
            byte[] bytes = await _api.DownloadDocumentAsync(doc.Doc.Id);
            string folder = string.IsNullOrWhiteSpace(doc.Category) ? "Other documents" : SanitizePathSegment(doc.Category);
            string entryPath = $"{SanitizePathSegment(doc.DocumentType ?? "Document")}/{folder}/{SanitizePathSegment(doc.FileName)}";
            ZipArchiveEntry entry = archive.CreateEntry(entryPath, CompressionLevel.Optimal);
            using Stream entryStream = entry.Open();
            await entryStream.WriteAsync(bytes);
        }

        foreach ((string fileName, string content) in textEntries)
        {
            ZipArchiveEntry entry = archive.CreateEntry(fileName, CompressionLevel.Optimal);
            using StreamWriter writer = new(entry.Open(), Encoding.UTF8);
            await writer.WriteAsync(content);
        }
    }

    private static string SanitizePathSegment(string value)
    {
        foreach (char c in Path.GetInvalidFileNameChars())
            value = value.Replace(c, '_');
        return value;
    }

    private static void TryDeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* best effort — leaves a stray temp file at worst */ }
    }

    private string FormatVehicleInfoText()
    {
        if (Caravan is null) return string.Empty;
        StringBuilder sb = new();
        sb.AppendLine($"{Caravan.Year} {Caravan.Make} {Caravan.Model}");
        sb.AppendLine($"Registration: {Caravan.RegistrationNumber}");
        sb.AppendLine($"VIN: {Caravan.Vin}");
        sb.AppendLine();
        sb.AppendLine($"WOF: issued {FormatDate(Caravan.WofIssueDate)}, due {FormatDate(Caravan.WofDueDate)}");
        sb.AppendLine($"WOF Electrical: issued {FormatDate(Caravan.ElectricalWofIssueDate)}, due {FormatDate(Caravan.ElectricalWofDueDate)}");
        sb.AppendLine($"Self Containment: issued {FormatDate(Caravan.SelfContainmentIssueDate)}, due {FormatDate(Caravan.SelfContainmentDue)}");
        sb.AppendLine();
        if (Caravan.Customer is { } customer)
        {
            sb.AppendLine($"Owner: {customer.Name}");
            sb.AppendLine($"Email: {customer.Email}");
            sb.AppendLine($"Phone: {customer.Phone}");
            sb.AppendLine($"Address: {customer.Address}, {customer.Suburb} {customer.Postcode}".Trim());
        }
        if (!string.IsNullOrWhiteSpace(Caravan.Notes))
        {
            sb.AppendLine();
            sb.AppendLine($"Notes: {Caravan.Notes}");
        }
        return sb.ToString();
    }

    private string FormatJobHistoryText()
    {
        StringBuilder sb = new();
        foreach (JobDetailDto job in VisibleJobs)
        {
            sb.AppendLine($"Job #{job.JobNumber} — {job.JobType} — {job.Status}");
            sb.AppendLine($"Finished {FormatDate(job.FinishDate)} by {job.FinishedBy}");
            if (!string.IsNullOrWhiteSpace(job.Description)) sb.AppendLine(job.Description);
            if (!string.IsNullOrWhiteSpace(job.Notes)) sb.AppendLine($"Notes: {job.Notes}");
            foreach (InvoiceDto invoice in job.Invoices)
            {
                sb.AppendLine($"  Invoice #{invoice.InvoiceNumber} — {FormatDate(invoice.IssueDate)} — Total ${invoice.TotalAmount:F2}");
                foreach (InvoiceItemDto item in invoice.Items)
                    sb.AppendLine($"    {item.Description} x{item.Quantity}");
            }
            sb.AppendLine(new string('-', 60));
        }
        return sb.ToString();
    }

    private string FormatAllConversationsText()
    {
        StringBuilder sb = new();
        foreach (ConversationDto conversation in Conversations)
        {
            sb.Append(FormatConversationText(conversation));
            sb.AppendLine(new string('=', 60));
            sb.AppendLine();
        }
        return sb.ToString();
    }

    private static string FormatConversationText(ConversationDto conversation)
    {
        StringBuilder sb = new();
        sb.AppendLine(conversation.Subject ?? "(No subject)");
        if (conversation.Tags.Count > 0)
            sb.AppendLine($"Tags: {string.Join(", ", conversation.Tags.Select(t => t.Name))}");
        sb.AppendLine();
        foreach (CommunicationLogDto message in conversation.Messages.OrderBy(m => m.OccurredAt))
        {
            sb.AppendLine($"[{message.Direction}] {message.FromAddress} — {message.OccurredAt:dd MMM yyyy HH:mm}");
            sb.AppendLine(message.Body ?? string.Empty);
            sb.AppendLine();
        }
        return sb.ToString();
    }

    private static string FormatDate(DateTime? date) => date?.ToString("dd MMM yyyy") ?? "—";

    /// <summary>Persists edits made on the Vehicle Info tab (vehicle fields, WOF/electrical/self-containment dates, keys, notes).</summary>
    [RelayCommand]
    private async Task SaveVehicleInfoAsync()
    {
        if (Caravan is null) return;

        IsSavingVehicleInfo = true;
        VehicleInfoStatus = "Saving...";
        try
        {
            CaravanDetailDto updated = await _api.UpdateCaravanAsync(Caravan.RegistrationNumber, new UpdateCaravanRequest
            {
                Vin = Caravan.Vin,
                Make = Caravan.Make,
                Model = Caravan.Model,
                Year = Caravan.Year,
                WofIssueDate = Caravan.WofIssueDate,
                WofDueDate = Caravan.WofDueDate,
                ElectricalWofIssueDate = Caravan.ElectricalWofIssueDate,
                ElectricalWofDueDate = Caravan.ElectricalWofDueDate,
                SelfContainmentIssueDate = Caravan.SelfContainmentIssueDate,
                SelfContainmentDue = Caravan.SelfContainmentDue,
                LockerKeyNumber = Caravan.LockerKeyNumber,
                DoorKeyNumber = Caravan.DoorKeyNumber,
                Notes = Caravan.Notes
            });

            // Preserve the loaded history collections (Jobs/Documents/Conversations) — the PATCH
            // response only carries the caravan record itself.
            updated.Jobs = Caravan.Jobs;
            updated.Documents = Caravan.Documents;
            updated.Conversations = Caravan.Conversations;
            updated.Customer = Caravan.Customer;
            Caravan = updated;

            VehicleInfoStatus = "Saved.";
        }
        catch (Exception ex)
        {
            VehicleInfoStatus = $"Save failed: {ex.Message}";
        }
        finally
        {
            IsSavingVehicleInfo = false;
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
