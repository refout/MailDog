using CommunityToolkit.Mvvm.ComponentModel;
using FluentIcons.Common;
using MailDog.Contracts.Dto;

namespace MailDog.UI.ViewModels;

/// <summary>左侧顶部的统一文件夹（聚合所有账户的同一角色）。</summary>
public sealed partial class UnifiedFolderViewModel : ObservableObject
{
    public UnifiedFolderViewModel(FolderRole role, string name, Icon icon)
    {
        Role = role;
        Name = name;
        IconKind = icon;
    }

    public FolderRole Role { get; }

    public string Name { get; }

    public Icon IconKind { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUnread))]
    [NotifyPropertyChangedFor(nameof(UnreadText))]
    private int _unreadCount;

    [ObservableProperty]
    private bool _isActive;

    public bool HasUnread => UnreadCount > 0;

    public string UnreadText => UnreadCount > 0 ? UnreadCount.ToString() : string.Empty;
}
