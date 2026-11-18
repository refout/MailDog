using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MailDog.Contracts.Dto;
using MailDog.Html;

namespace MailDog.UI.ViewModels;

public sealed partial class AttachmentViewModel : ObservableObject
{
    private readonly Func<AttachmentViewModel, Task> _download;
    private readonly Func<AttachmentViewModel, Task>? _preview;

    public AttachmentViewModel(
        long messageId,
        int index,
        AttachmentDto attachment,
        Func<AttachmentViewModel, Task> download,
        Func<AttachmentViewModel, Task>? preview = null)
    {
        MessageId = messageId;
        Index = index;
        FileName = attachment.FileName;
        SizeText = FormatSize(attachment.Size);
        IsInline = attachment.IsInline;
        _download = download;
        _preview = preview;
    }

    public long MessageId { get; }

    public int Index { get; }

    public string FileName { get; }

    public string SizeText { get; }

    public bool IsInline { get; }

    [RelayCommand]
    private Task DownloadAsync() => _download(this);

    [RelayCommand]
    private Task PreviewAsync() => _preview?.Invoke(this) ?? Task.CompletedTask;

    private static string FormatSize(long bytes)
    {
        if (bytes <= 0)
        {
            return string.Empty;
        }

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

public sealed partial class ReaderViewModel : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    [NotifyPropertyChangedFor(nameof(ShowEmpty))]
    private string _subject = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    [NotifyPropertyChangedFor(nameof(ShowEmpty))]
    private bool _isLoading;

    [ObservableProperty]
    private string _senderLine = string.Empty;

    [ObservableProperty]
    private string _dateText = string.Empty;

    [ObservableProperty]
    private string? _htmlDocument;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPlainText))]
    private bool _useHtml;

    [ObservableProperty]
    private string _plainText = string.Empty;

    [ObservableProperty]
    private bool _imagesBlocked;

    [ObservableProperty]
    private bool _isFromCache;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNotice))]
    private string? _notice;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string? _error;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAttachments))]
    private AttachmentViewModel[] _attachments = [];

    public bool HasNotice => !string.IsNullOrEmpty(Notice);

    /// <summary>由主视图模型注入：保存附件到用户选择的路径。</summary>
    public Func<AttachmentViewModel, Task>? DownloadAttachment { get; set; }

    public Func<AttachmentViewModel, Task>? PreviewAttachment { get; set; }

    public bool HasError => !string.IsNullOrEmpty(Error);

    public bool HasAttachments => Attachments.Length > 0;

    public bool HasSelection => IsLoading || !string.IsNullOrEmpty(Subject);

    public bool ShowEmpty => !HasSelection;

    public bool IsPlainText => !UseHtml;

    public void Reset()
    {
        Subject = string.Empty;
        SenderLine = string.Empty;
        DateText = string.Empty;
        HtmlDocument = null;
        UseHtml = false;
        PlainText = string.Empty;
        ImagesBlocked = false;
        IsFromCache = false;
        Notice = null;
        Error = null;
        Attachments = [];
    }

    public void Apply(
        MailBodyDto body,
        string senderLine,
        string dateText,
        HtmlSanitizer sanitizer,
        HtmlDocumentBuilder builder,
        bool allowRemoteImages,
        bool darkMode,
        string backgroundHex,
        bool preferPlainText)
    {
        Subject = body.Subject;
        SenderLine = senderLine;
        DateText = dateText;
        Attachments = body.Attachments
            .Select((attachment, index) => new AttachmentViewModel(
                body.MessageId,
                index,
                attachment,
                DownloadAttachment ?? (_ => Task.CompletedTask),
                PreviewAttachment))
            .ToArray();
        Notice = body.Truncated ? "邮件内容较大，已截断。" : null;
        IsFromCache = body.FromCache;
        Error = null;

        var sanitized = sanitizer.Sanitize(body.Html, allowRemoteImages);
        ImagesBlocked = sanitized.RemoteImagesBlocked;
        PlainText = body.PlainText ?? string.Empty;

        var hasHtml = !string.IsNullOrWhiteSpace(sanitized.Html);

        if (hasHtml && !preferPlainText)
        {
            HtmlDocument = builder.Build(sanitized.Html, darkMode, allowRemoteImages, backgroundHex);
            UseHtml = true;
            return;
        }

        HtmlDocument = null;
        UseHtml = false;

        if (string.IsNullOrWhiteSpace(PlainText) && hasHtml)
        {
            // 纯文本模式下邮件没有 text/plain 段时，从 HTML 提取文本，而不是回退去渲染 HTML。
            PlainText = HtmlTextConverter.ToPlainText(sanitized.Html);
        }

        if (string.IsNullOrWhiteSpace(PlainText))
        {
            PlainText = "此邮件没有可显示的正文。";
        }
    }
}
