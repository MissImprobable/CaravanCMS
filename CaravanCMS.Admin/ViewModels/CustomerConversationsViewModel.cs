using CaravanCMS.Admin.Services;
using CaravanCMS.Core;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;

namespace CaravanCMS.Admin.ViewModels;

/// <summary>ViewModel for the Customer Conversations dialog — the CRM view for reviewing and logging customer communications.</summary>
public partial class CustomerConversationsViewModel : ObservableObject
{
    private readonly ApiClient _api;

    [ObservableProperty] private string _searchQuery = string.Empty;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _statusText = "Search for a customer to view their conversation history.";
    [ObservableProperty] private CustomerLookupDto? _selectedCustomer;

    [ObservableProperty] private string _newSubject = string.Empty;
    [ObservableProperty] private string _newType = "Call";
    [ObservableProperty] private string _newDirection = "Outbound";
    [ObservableProperty] private string _newBody = string.Empty;

    public ObservableCollection<CustomerLookupDto> SearchResults { get; } = new();
    public ObservableCollection<ConversationItemViewModel> Conversations { get; } = new();

    public static IReadOnlyList<string> TypeOptions { get; } = ["Call", "Email", "Note", "Meeting"];
    public static IReadOnlyList<string> DirectionOptions { get; } = ["Outbound", "Inbound"];

    public CustomerConversationsViewModel(ApiClient api)
    {
        _api = api;
    }

    [RelayCommand]
    private async Task SearchAsync()
    {
        if (SearchQuery.Trim().Length < 2)
        {
            StatusText = "Enter at least 2 characters to search.";
            return;
        }

        IsBusy = true;
        StatusText = "Searching...";
        SearchResults.Clear();
        try
        {
            List<CustomerLookupDto> results = await _api.SearchCustomersAsync(SearchQuery.Trim());
            foreach (CustomerLookupDto c in results)
                SearchResults.Add(c);

            StatusText = results.Count == 0 ? "No matching customers." : $"{results.Count} customer(s) found.";
        }
        catch (Exception ex)
        {
            StatusText = $"Search failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task SelectCustomerAsync(CustomerLookupDto customer)
    {
        SelectedCustomer = customer;
        SearchResults.Clear();
        await LoadConversationsAsync();
    }

    private async Task LoadConversationsAsync()
    {
        if (SelectedCustomer is null) return;

        IsBusy = true;
        StatusText = "Loading conversations...";
        Conversations.Clear();
        try
        {
            List<ConversationDto> conversations = await _api.GetCustomerConversationsAsync(SelectedCustomer.Id);
            foreach (ConversationDto c in conversations)
                Conversations.Add(new ConversationItemViewModel(c, this));

            StatusText = conversations.Count == 0
                ? $"No conversations logged for {SelectedCustomer.Name} yet."
                : $"{conversations.Count} conversation(s) for {SelectedCustomer.Name}.";
        }
        catch (Exception ex)
        {
            StatusText = $"Failed to load conversations: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Logs a new conversation (e.g. a phone call or note) against the selected customer.</summary>
    [RelayCommand]
    private async Task LogConversationAsync()
    {
        if (SelectedCustomer is null) return;
        if (string.IsNullOrWhiteSpace(NewSubject))
        {
            StatusText = "Enter a subject before logging.";
            return;
        }

        IsBusy = true;
        StatusText = "Logging conversation...";
        try
        {
            ConversationDto conversation = await _api.CreateConversationAsync(new CreateConversationRequest
            {
                CustomerId = SelectedCustomer.Id,
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
            await LoadConversationsAsync();
        }
        catch (Exception ex)
        {
            StatusText = $"Failed to log conversation: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    internal async Task AddTagAsync(ConversationItemViewModel item, string tagName)
    {
        try
        {
            ConversationDto updated = await _api.AddTagAsync(item.Dto.Id, tagName);
            item.Refresh(updated);
            StatusText = $"Tagged \"{tagName}\".";
        }
        catch (Exception ex)
        {
            StatusText = $"Failed to add tag: {ex.Message}";
        }
    }

    internal async Task RemoveTagAsync(ConversationItemViewModel item, TagDto tag)
    {
        try
        {
            ConversationDto updated = await _api.RemoveTagAsync(item.Dto.Id, tag.Id);
            item.Refresh(updated);
        }
        catch (Exception ex)
        {
            StatusText = $"Failed to remove tag: {ex.Message}";
        }
    }
}
