using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
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
        _isRead = (mail.Flags & MailFlags.Seen) != 0;
        _isFlagged = (mail.Flags & MailFlags.Flagged) != 0;
        AvatarBrush = CreateAvatarBrush(mail.FromAddress);
        AvatarInitial = CreateInitial(mail.FromName, mail.FromAddress);
    }

    public long Id { get; }

    public long FolderId { get; }

    public string AccountId { get; }

    public string Subject { get; }

    public string SenderName { get; }

    public string SenderAddress { get; }

    public string Preview { get; }

    public DateTimeOffset Date { get; }

    public bool HasAttachments { get; }

    public IBrush AvatarBrush { get; }

    public string AvatarInitial { get; }

    public string Sender => string.IsNullOrWhiteSpace(SenderName) ? SenderAddress : SenderName;

    public string TimeText => FormatTime(Date);

    public bool IsUnread => !IsRead;

    public FontWeight SenderWeight => IsRead ? FontWeight.Normal : FontWeight.SemiBold;

    public FontWeight SubjectWeight => IsRead ? FontWeight.Normal : FontWeight.SemiBold;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsUnread))]
    [NotifyPropertyChangedFor(nameof(SenderWeight))]
    [NotifyPropertyChangedFor(nameof(SubjectWeight))]
    private bool _isRead;

    [ObservableProperty]
    private bool _isFlagged;

    [ObservableProperty]
    private bool _isSelected;

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
