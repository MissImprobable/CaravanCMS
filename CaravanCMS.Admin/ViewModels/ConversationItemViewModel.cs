using CaravanCMS.Core;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;

namespace CaravanCMS.Admin.ViewModels;

/// <summary>Wraps a ConversationDto with the UI-only state (tag input text, expand/collapse) needed per card.</summary>
public partial class ConversationItemViewModel : ObservableObject
{
    private readonly CustomerConversationsViewModel _parent;

    [ObservableProperty] private ConversationDto _dto;
    [ObservableProperty] private bool _isExpanded;
    [ObservableProperty] private string _newTagText = string.Empty;

    public ObservableCollection<TagDto> Tags { get; } = new();
    public ObservableCollection<CommunicationLogDto> Messages { get; } = new();

    public ConversationItemViewModel(ConversationDto dto, CustomerConversationsViewModel parent)
    {
        _dto = dto;
        _parent = parent;
        Refresh(dto);
    }

    public void Refresh(ConversationDto dto)
    {
        Dto = dto;
        Tags.Clear();
        foreach (TagDto t in dto.Tags) Tags.Add(t);
        Messages.Clear();
        foreach (CommunicationLogDto m in dto.Messages) Messages.Add(m);
    }

    [RelayCommand]
    private void ToggleExpand() => IsExpanded = !IsExpanded;

    [RelayCommand]
    private async Task AddTagAsync()
    {
        string tag = NewTagText.Trim();
        if (tag.Length == 0) return;
        NewTagText = string.Empty;
        await _parent.AddTagAsync(this, tag);
    }

    [RelayCommand]
    private async Task RemoveTagAsync(TagDto tag) => await _parent.RemoveTagAsync(this, tag);
}
