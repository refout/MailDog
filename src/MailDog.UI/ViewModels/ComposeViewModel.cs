using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MailDog.Contracts.Dto;
using MailDog.Contracts.Ipc;
using MailDog.Contracts.Messages;
using MailDog.Html;
using MailDog.UI.Services;

namespace MailDog.UI.ViewModels;

public enum ComposeMode
{
    New,
    Reply,
    ReplyAll,
    Forward
}

public sealed class ComposeAccount
{
    public ComposeAccount(string id, string email, string displayName)
    {
        Id = id;
        Email = email;
        DisplayName = displayName;
    }

    public string Id { get; }

    public string Email { get; }

    public string DisplayName { get; }

    public string Label => string.IsNullOrWhiteSpace(DisplayName) ? Email : $"{DisplayName} <{Email}>";
}

public sealed partial class ComposeAttachmentViewModel : ObservableObject
{
    private readonly Action<ComposeAttachmentViewModel> _remove;

    public ComposeAttachmentViewModel(string path, Action<ComposeAttachmentViewModel> remove)
    {
        Path = path;
        FileName = System.IO.Path.GetFileName(path);
        SizeText = FormatSize(new FileInfo(path).Length);
        _remove = remove;
    }

    public string Path { get; }

    public string FileName { get; }

    public string SizeText { get; }

    [RelayCommand]
    private void Remove() => _remove(this);

    private static string FormatSize(long bytes)
    {
        if (bytes < 1024)
        {
            return $"{bytes} B";
        }

        if (bytes < 1024 * 1024)
        {
            return $"{bytes / 1024.0:0.#} KB";
        }

        return $"{bytes / (1024.0 * 1024.0):0.#} MB";
    }
}

public sealed partial class ComposeViewModel : ObservableObject
{
    private const long MaxAttachmentBytes = 25 * 1024 * 1024;

    private readonly EngineClient _engine;
    private readonly MailBodyDto? _original;
    private bool _sent;
    private bool _draftSaved;

    public ComposeViewModel(
        EngineClient engine,
        IReadOnlyList<ComposeAccount> accounts,
        ComposeMode mode,
        MailBodyDto? original)
    {
        _engine = engine;
        _original = original;
        Mode = mode;
        Accounts = new ObservableCollection<ComposeAccount>(accounts);
        SelectedAccount = Accounts.FirstOrDefault(account =>
            original is not null && string.Equals(account.Email, original.FromAddress, StringComparison.OrdinalIgnoreCase))
            ?? Accounts.FirstOrDefault();

        WindowTitle = mode switch
        {
            ComposeMode.Reply => "回复邮件",
            ComposeMode.ReplyAll => "回复全部",
            ComposeMode.Forward => "转发邮件",
            _ => "写邮件"
        };

        if (original is not null && mode != ComposeMode.New)
        {
            ApplyOriginal(mode, original);
        }
    }

    public ComposeMode Mode { get; }

    public string WindowTitle { get; }

    public ObservableCollection<ComposeAccount> Accounts { get; }

    [ObservableProperty]
    private ComposeAccount? _selectedAccount;

    [ObservableProperty]
    private string _toText = string.Empty;

    [ObservableProperty]
    private string _ccText = string.Empty;

    [ObservableProperty]
    private string _bccText = string.Empty;

    [ObservableProperty]
    private string _subjectText = string.Empty;

    [ObservableProperty]
    private string _bodyText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BodyPlaceholder))]
    private bool _isMarkdown;

    public string BodyPlaceholder
        => IsMarkdown
            ? "正文（Markdown：标题、列表、粗体、链接、代码块）"
            : "正文";

    public ObservableCollection<ComposeAttachmentViewModel> Attachments { get; } = [];

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

    public bool HasError => !string.IsNullOrEmpty(Error);

    public bool HasStatus => !string.IsNullOrEmpty(Status);

    public bool CanSend => !IsBusy && !IsQueued && Accounts.Count > 0;

    public bool CanEdit => !IsBusy && !IsQueued;

    public bool IsCompleted => _sent || _draftSaved;

    public bool CanCloseSilently => IsQueued || _sent || _draftSaved;

    public bool IsEmpty
        => string.IsNullOrWhiteSpace(ToText)
           && string.IsNullOrWhiteSpace(CcText)
           && string.IsNullOrWhiteSpace(BccText)
           && string.IsNullOrWhiteSpace(SubjectText)
           && string.IsNullOrWhiteSpace(BodyText)
           && Attachments.Count == 0;

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

        var total = Attachments.Sum(item => new FileInfo(item.Path).Length) + new FileInfo(path).Length;
        if (total > MaxAttachmentBytes)
        {
            Error = "附件总大小不能超过 25 MiB。";
            return;
        }

        Attachments.Add(new ComposeAttachmentViewModel(path, RemoveAttachment));
        OnPropertyChanged(nameof(IsEmpty));
    }

    public async Task<bool> SaveDraftAsync()
    {
        if (IsBusy || IsQueued)
        {
            return false;
        }

        if (SelectedAccount is null)
        {
            Error = "请选择发件账户。";
            return false;
        }

        if (IsEmpty)
        {
            _draftSaved = true;
            return true;
        }

        IsBusy = true;
        Error = null;
        Status = "正在保存草稿…";

        try
        {
            var response = await _engine.RequestAsync<DraftSavedResponse>(
                IpcMessageTypes.SaveDraft,
                BuildDraftRequest(),
                TimeSpan.FromSeconds(60));

            _draftSaved = true;
            Status = $"已保存到{response.FolderName}。";
            Completed?.Invoke(this, Status!);
            return true;
        }
        catch (Exception exception) when (exception is IpcException or InvalidOperationException or TimeoutException)
        {
            Error = exception.Message;
            Status = null;
            return false;
        }
        finally
        {
            IsBusy = false;
            OnPropertyChanged(nameof(IsEmpty));
        }
    }

    [RelayCommand]
    private async Task SendAsync()
    {
        if (!CanSend)
        {
            return;
        }

        if (SelectedAccount is null)
        {
            Error = "请选择发件账户。";
            return;
        }

        if (string.IsNullOrWhiteSpace(ToText))
        {
            Error = "请填写至少一个收件人。";
            return;
        }

        IsBusy = true;
        Error = null;
        Status = "正在发送…";

        try
        {
            var response = await _engine.RequestAsync<SendMailResponse>(
                IpcMessageTypes.SendMail,
                BuildSendRequest(),
                TimeSpan.FromSeconds(75));

            if (response.Sent)
            {
                _sent = true;
                IsQueued = false;
                Status = "邮件已发送。";
                Completed?.Invoke(this, Status!);
                CloseRequested?.Invoke();
                return;
            }

            if (response.Queued)
            {
                IsQueued = true;
                Status = "已加入发件箱，将在后台自动重试。";
                Error = response.Error;
                Completed?.Invoke(this, Status!);
                return;
            }

            Error = response.Error ?? "发送失败。";
            Status = null;
        }
        catch (Exception exception) when (exception is IpcException or InvalidOperationException or TimeoutException)
        {
            Error = exception.Message;
            Status = null;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task SaveDraft()
    {
        if (await SaveDraftAsync())
        {
            CloseRequested?.Invoke();
        }
    }

    [RelayCommand]
    private async Task DiscardAsync()
    {
        if (!CanCloseSilently && !IsEmpty && !await SaveDraftAsync())
        {
            return;
        }

        _draftSaved = true;
        CloseRequested?.Invoke();
    }

    private SendMailRequest BuildSendRequest()
        => new()
        {
            AccountId = SelectedAccount!.Id,
            To = ToText.Trim(),
            Cc = CcText.Trim(),
            Bcc = BccText.Trim(),
            Subject = SubjectText.Trim(),
            BodyText = BodyText,
            IsMarkdown = IsMarkdown,
            InReplyTo = Mode is ComposeMode.Reply or ComposeMode.ReplyAll ? _original?.RfcMessageId : null,
            AttachmentPaths = Attachments.Select(item => item.Path).ToArray()
        };

    private SaveDraftRequest BuildDraftRequest()
        => new()
        {
            AccountId = SelectedAccount!.Id,
            To = ToText.Trim(),
            Cc = CcText.Trim(),
            Bcc = BccText.Trim(),
            Subject = SubjectText.Trim(),
            BodyText = BodyText,
            IsMarkdown = IsMarkdown,
            AttachmentPaths = Attachments.Select(item => item.Path).ToArray()
        };

    private void RemoveAttachment(ComposeAttachmentViewModel attachment)
    {
        Attachments.Remove(attachment);
        OnPropertyChanged(nameof(IsEmpty));
    }

    private void ApplyOriginal(ComposeMode mode, MailBodyDto original)
    {
        var subject = original.Subject ?? string.Empty;
        var quote = BuildQuote(original);

        switch (mode)
        {
            case ComposeMode.Reply:
                ToText = original.FromAddress;
                SubjectText = PrefixSubject(subject, "Re:");
                BodyText = quote;
                break;

            case ComposeMode.ReplyAll:
                ToText = original.FromAddress;
                CcText = BuildReplyAllCc(original);
                SubjectText = PrefixSubject(subject, "Re:");
                BodyText = quote;
                break;

            case ComposeMode.Forward:
                SubjectText = PrefixSubject(subject, "Fwd:");
                BodyText = quote;
                break;
        }
    }

    private string BuildReplyAllCc(MailBodyDto original)
    {
        var self = SelectedAccount?.Email ?? string.Empty;
        var recipients = original.ToAddresses
            .Concat(original.CcAddresses)
            .Where(address => !string.IsNullOrWhiteSpace(address))
            .Where(address => !string.Equals(address, self, StringComparison.OrdinalIgnoreCase))
            .Where(address => !string.Equals(address, original.FromAddress, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase);

        return string.Join("; ", recipients);
    }

    private static string PrefixSubject(string subject, string prefix)
        => subject.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? subject
            : $"{prefix} {subject}";

    private static string BuildQuote(MailBodyDto original)
    {
        var plain = original.PlainText;
        if (string.IsNullOrWhiteSpace(plain) && !string.IsNullOrWhiteSpace(original.Html))
        {
            plain = HtmlTextConverter.ToPlainText(original.Html);
        }

        var from = string.IsNullOrWhiteSpace(original.FromName)
            ? original.FromAddress
            : $"{original.FromName} <{original.FromAddress}>";

        var header = $"发件人: {from}\n时间: {original.Date.LocalDateTime:yyyy-MM-dd HH:mm}";

        if (original.ToAddresses.Length > 0)
        {
            header += $"\n收件人: {string.Join("; ", original.ToAddresses)}";
        }

        if (original.CcAddresses.Length > 0)
        {
            header += $"\n抄送: {string.Join("; ", original.CcAddresses)}";
        }

        return $"\n\n-----原始邮件-----\n{header}\n\n{plain ?? string.Empty}";
    }
}
