using System.Collections.ObjectModel;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using FluentIcons.Common;
using MailDog.Contracts.Dto;

namespace MailDog.UI.ViewModels;

public sealed partial class MailSummaryViewModel : ObservableObject
{
    private static readonly Color[] AvatarColors =
    [
        Color.Parse("#0F6CBD"),
        Color.Parse("#8764B8"),
        Color.Parse("#0E7A5F"),
        Color.Parse("#B24745"),
        Color.Parse("#8E562E"),
        Color.Parse("#5C5C5C")
    ];

    public MailSummaryViewModel(MailSummaryDto mail)
    {
        Id = mail.Id;
        FolderId = mail.FolderId;
        AccountId = mail.AccountId;
        Subject = mail.Subject;
        SenderName = mail.FromName;
        SenderAddress = mail.FromAddress;
        Preview = mail.Preview;
        Date = mail.Date;
        HasAttachments = mail.HasAttachments;
        Size = mail.Size;
        _isRead = (mail.Flags & MailFlags.Seen) != 0;
        _isFlagged = (mail.Flags & MailFlags.Flagged) != 0;
        AvatarBrush = CreateAvatarBrush(mail.FromAddress);
        AvatarInitial = CreateInitial(mail.FromName, mail.FromAddress);
        ThreadKey = mail.ThreadKey ?? string.Empty;
        _threadMessages = [this];
    }

    public long Id { get; }

    public long FolderId { get; }

    public string AccountId { get; }

    public string Subject { get; }

    /// <summary>列表中无主题邮件显示占位标题，避免整行出现空白。</summary>
    public string DisplaySubject => string.IsNullOrWhiteSpace(Subject) ? "（无主题）" : Subject;

    public string SenderName { get; }

    public string SenderAddress { get; }

    public string Preview { get; }

    /// <summary>服务端线程根键；为空时由 UI 用回复主题前缀回退。</summary>
    public string ThreadKey { get; }

    public ObservableCollection<MessageTagChipViewModel> Tags { get; } = [];

    public bool HasTags => Tags.Count > 0;

    public bool ShowPreviewRow => ShowPreview && !HasTags;

    public bool ShowTagRow => HasTags;

    public bool HasTag(long tagId) => Tags.Any(tag => tag.Id == tagId);

    public void SetTags(IEnumerable<TagDto> tags)
    {
        Tags.Clear();
        foreach (var tag in tags)
        {
            Tags.Add(new MessageTagChipViewModel(tag));
        }

        OnPropertyChanged(nameof(HasTags));
        OnPropertyChanged(nameof(ShowPreviewRow));
        OnPropertyChanged(nameof(ShowTagRow));
    }

    public DateTimeOffset Date { get; }

    public bool HasAttachments { get; }

    public int Size { get; }

    public IBrush AvatarBrush { get; }

    public string AvatarInitial { get; }

    public string Sender => string.IsNullOrWhiteSpace(SenderName) ? SenderAddress : SenderName;

    public string SecondarySenderAddress => string.IsNullOrWhiteSpace(SenderName)
        || string.Equals(SenderName.Trim(), SenderAddress.Trim(), StringComparison.OrdinalIgnoreCase)
            ? string.Empty
            : SenderAddress;

    public string TimeText => FormatTime(Date);

    public bool IsUnread => !IsRead;

    public FontWeight SenderWeight => IsRead ? FontWeight.Normal : FontWeight.SemiBold;

    public FontWeight SubjectWeight => IsRead ? FontWeight.Normal : FontWeight.SemiBold;

    public string AccessibilityName => $"{(IsUnread ? "未读，" : string.Empty)}{Sender}，{DisplaySubject}";

    /// <summary>悬停操作条上的已读切换文案、图标与无障碍名称随当前状态变化。</summary>
    public string ReadToggleLabel => IsRead ? "标记为未读" : "标记为已读";

    public string ReadToggleAutomationName => IsRead
        ? "将这封邮件标记为未读"
        : "将这封邮件标记为已读";

    public Icon ReadToggleIcon => IsRead ? Icon.MailUnread : Icon.MailRead;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsThreaded))]
    [NotifyPropertyChangedFor(nameof(ThreadLabel))]
    private int _threadCount = 1;

    [ObservableProperty]
    private IReadOnlyList<MailSummaryViewModel> _threadMessages = [];

    [ObservableProperty]
    private string _searchQuery = string.Empty;

    /// <summary>紧凑密度下隐藏摘要第三行，避免 64px 行高裁掉预览。</summary>
    [ObservableProperty]
    private bool _showPreview = true;

    partial void OnShowPreviewChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowPreviewRow));
    }

    [ObservableProperty]
    private bool _isHovered;

    public bool IsThreaded => ThreadCount > 1;

    public string ThreadLabel => $"会话 {ThreadCount} 封";

    public string ThreadMenuLabel => $"{Sender} · {Date.ToLocalTime():M月d日 HH:mm} · {DisplaySubject}";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsUnread))]
    [NotifyPropertyChangedFor(nameof(SenderWeight))]
    [NotifyPropertyChangedFor(nameof(SubjectWeight))]
    [NotifyPropertyChangedFor(nameof(AccessibilityName))]
    [NotifyPropertyChangedFor(nameof(ReadToggleLabel))]
    [NotifyPropertyChangedFor(nameof(ReadToggleAutomationName))]
    [NotifyPropertyChangedFor(nameof(ReadToggleIcon))]
    private bool _isRead;

    [ObservableProperty]
    private bool _isFlagged;

    [ObservableProperty]
    private bool _isSelected;

    [ObservableProperty]
    private bool _showSelection;

    private static string FormatTime(DateTimeOffset date)
    {
        var local = date.ToLocalTime();
        var now = DateTimeOffset.Now;

        if (local.Date == now.Date)
        {
            return local.ToString("HH:mm");
        }

        if (local.Date == now.Date.AddDays(-1))
        {
            return "昨天";
        }

        return local.Year == now.Year
            ? local.ToString("M月d日")
            : local.ToString("yyyy/M/d");
    }

    private static IBrush CreateAvatarBrush(string address)
    {
        var hash = 0;
        foreach (var character in address)
        {
            hash = unchecked(hash * 31 + character);
        }

        var index = Math.Abs(hash) % AvatarColors.Length;
        return new SolidColorBrush(AvatarColors[index]);
    }

    private static string CreateInitial(string name, string address)
    {
        if (!string.IsNullOrWhiteSpace(name))
        {
            return name[..1].ToUpperInvariant();
        }

        return string.IsNullOrWhiteSpace(address) ? "?" : address[..1].ToUpperInvariant();
    }
}
