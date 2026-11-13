using CommunityToolkit.Mvvm.ComponentModel;
using MailDog.Contracts.Dto;

namespace MailDog.UI.ViewModels;

public sealed partial class FolderViewModel : ObservableObject
{
    public FolderViewModel(FolderDto folder)
    {
        Id = folder.Id;
        Name = folder.Name;
        FullName = folder.FullName;
        Role = folder.Role;
        _unreadCount = folder.UnreadCount;
    }

    public long Id { get; }

    public string Name { get; }

    public string FullName { get; }

    public FolderRole Role { get; }

    public bool IsInbox => Role == FolderRole.Inbox;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUnread))]
    [NotifyPropertyChangedFor(nameof(UnreadText))]
    private int _unreadCount;

    public bool HasUnread => UnreadCount > 0;

    public string UnreadText => UnreadCount > 0 ? UnreadCount.ToString() : string.Empty;
}
