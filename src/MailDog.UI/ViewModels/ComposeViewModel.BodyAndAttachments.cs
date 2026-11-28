using System.Collections.ObjectModel;
using System.Net;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MailDog.Contracts.Dto;
using MailDog.Contracts.Ipc;
using MailDog.Contracts.Messages;
using MailDog.Html;
using MailDog.UI.Services;

namespace MailDog.UI.ViewModels;

public sealed partial class ComposeViewModel
{
    /// <summary>富文本编辑器回传内容后同步纯文本回退。</summary>
    public void UpdateRichBody(string? html)
    {
        BodyHtml = html;
        BodyText = HtmlTextConverter.ToPlainText(html);
        OnPropertyChanged(nameof(IsEmpty));
    }

    public void DropRichBody()
    {
        BodyHtml = null;
    }

    public ObservableCollection<ComposeAttachmentViewModel> Attachments { get; } = [];

    public bool HasAttachments => Attachments.Count > 0;

    public string AttachmentSummaryText
    {
        get
        {
            if (Attachments.Count == 0)
            {
                return "未添加附件";
            }

            var total = Attachments.Sum(item => item.SizeBytes);
            return $"附件 {Attachments.Count} 个 · {FormatAttachmentSize(total)}";
        }
    }

    private static string FormatAttachmentSize(long bytes)
    {
        if (bytes < 1024)
        {
            return $"{bytes} B";
        }

        if (bytes < 1024 * 1024)
        {
            return $"{bytes / 1024.0:0.#} KB";
        }

        return $"{bytes / (1024.0 * 1024.0):0.#} MiB";
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSend))]
    [NotifyPropertyChangedFor(nameof(CanEdit))]
    private bool _isBusy;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSend))]
    [NotifyPropertyChangedFor(nameof(CanEdit))]
    private bool _isQueued;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string? _error;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatus))]
    private string? _status;

    public string[] ScheduleOptions { get; } =
    [
        "立即发送",
        "1 小时后",
        "今晚 20:00",
        "明天 08:00",
        "自定义时间"
    ];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCustomSchedule))]
    private string _selectedScheduleOption = "立即发送";

    [ObservableProperty]
    private string _customScheduleText = string.Empty;

    public bool IsCustomSchedule => SelectedScheduleOption == "自定义时间";

    /// <summary>发送前检查命中时的提示（附件缺失 / 体积过大），用户确认后才继续。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSendWarning))]
    private string? _sendWarning;

    private bool _sendChecksConfirmed;
    private Avalonia.Threading.DispatcherTimer? _undoTimer;
    private long _undoOutboxId;

    /// <summary>延迟发送的倒计时提示；窗口结束时清空。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUndo))]
    private string? _undoHint;

    public bool HasUndo => !string.IsNullOrEmpty(UndoHint);

    public bool HasSendWarning => !string.IsNullOrEmpty(SendWarning);

    public bool HasError => !string.IsNullOrEmpty(Error);

    public bool HasStatus => !string.IsNullOrEmpty(Status);

    public bool CanSend => !IsBusy && !IsQueued && Accounts.Count > 0;

    public bool CanEdit => !IsBusy && !IsQueued;

    public bool IsCompleted => _sent || _draftSaved;

    public bool CanCloseSilently => IsQueued || _sent || _draftSaved;

    public bool IsEmpty
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(ToRecipientsValue)
                || !string.IsNullOrWhiteSpace(CcRecipientsValue)
                || !string.IsNullOrWhiteSpace(BccRecipientsValue)
                || !string.IsNullOrWhiteSpace(SubjectText)
                || Attachments.Count > 0)
            {
                return false;
            }

            return (string.IsNullOrWhiteSpace(BodyText) && string.IsNullOrWhiteSpace(BodyHtml))
                   || IsSignatureOnly;
        }
    }

    /// <summary>发送或草稿保存成功、或用户主动放弃时请求关闭窗口。</summary>
    public event Action? CloseRequested;

    /// <summary>成功发送或保存草稿后通知主窗口刷新状态。</summary>
    public event Action<ComposeViewModel, string>? Completed;

    public void AddAttachment(string path)
    {
        Error = null;

        if (Attachments.Any(item => string.Equals(item.Path, path, StringComparison.OrdinalIgnoreCase)))
        {
            Error = "该附件已经添加。";
            return;
        }

        if (!File.Exists(path))
        {
            Error = "附件不存在。";
            return;
        }

        var total = Attachments.Sum(item => item.SizeBytes) + new FileInfo(path).Length;
        if (total > MaxAttachmentBytes)
        {
            Error = "附件总大小不能超过 25 MiB。";
            return;
        }

        Attachments.Add(new ComposeAttachmentViewModel(path, RemoveAttachment));
        SendWarning = null;
        _sendChecksConfirmed = false;
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(HasAttachments));
        OnPropertyChanged(nameof(AttachmentSummaryText));
    }

}
