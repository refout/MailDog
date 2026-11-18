using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using FluentIcons.Common;
using MailDog.Contracts.Dto;

namespace MailDog.UI.ViewModels;

public sealed partial class FolderViewModel : ObservableObject
{
    public FolderViewModel(FolderDto folder)
    {
        Id = folder.Id;
        Name = Localize(folder);
        FullName = folder.FullName;
        Role = folder.Role;
        _unreadCount = folder.UnreadCount;
    }

    private static string Localize(FolderDto folder)
    {
        // 只翻译系统文件夹；服务器自定义文件夹（Mailspring、Notes 等）保留原名。
        var chinese = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "zh";

        return folder.Role switch
        {
            FolderRole.Inbox => chinese ? "收件箱" : "Inbox",
            FolderRole.Sent => chinese ? "已发送" : "Sent",
            FolderRole.Drafts => chinese ? "草稿" : "Drafts",
            FolderRole.Junk => chinese ? "垃圾邮件" : "Junk",
            FolderRole.Trash => chinese ? "已删除" : "Deleted",
            FolderRole.Archive => chinese ? "归档" : "Archive",
            _ => folder.Name
        };
    }

    public long Id { get; }

    public string Name { get; }

    public string FullName { get; }

    public FolderRole Role { get; }

    public Icon IconKind => Role switch
    {
        FolderRole.Inbox => Icon.MailInbox,
        FolderRole.Sent => Icon.Send,
        FolderRole.Drafts => Icon.Drafts,
        FolderRole.Junk => Icon.Warning,
        FolderRole.Trash => Icon.Delete,
        FolderRole.Archive => Icon.Archive,
        _ => Icon.Folder
    };

    /// <summary>是否是当前正在浏览的文件夹（同一时间只有一个）。</summary>
    [ObservableProperty]
    private bool _isActive;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUnread))]
    [NotifyPropertyChangedFor(nameof(UnreadText))]
    private int _unreadCount;

    public bool HasUnread => UnreadCount > 0;

    public string UnreadText => UnreadCount > 0 ? UnreadCount.ToString() : string.Empty;
}
