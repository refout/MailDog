using CommunityToolkit.Mvvm.ComponentModel;
using MailDog.Contracts.Dto;
using MailDog.Html;

namespace MailDog.UI.ViewModels;

public sealed class AttachmentViewModel
{
    public AttachmentViewModel(AttachmentDto attachment)
    {
        FileName = attachment.FileName;
        SizeText = FormatSize(attachment.Size);
        IsInline = attachment.IsInline;
    }

    public string FileName { get; }

    public string SizeText { get; }

    public bool IsInline { get; }

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
    [NotifyPropertyChangedFor(nameof(HasNotice))]
    private string? _notice;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string? _error;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAttachments))]
    private AttachmentViewModel[] _attachments = [];

    public bool HasNotice => !string.IsNullOrEmpty(Notice);

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
        bool darkMode)
    {
        Subject = body.Subject;
        SenderLine = senderLine;
        DateText = dateText;
        Attachments = body.Attachments.Select(attachment => new AttachmentViewModel(attachment)).ToArray();
        Notice = body.Truncated ? "邮件内容较大，已截断。" : null;
        Error = null;

        var sanitized = sanitizer.Sanitize(body.Html, allowRemoteImages);
        ImagesBlocked = sanitized.RemoteImagesBlocked;

        if (!string.IsNullOrWhiteSpace(sanitized.Html))
        {
            HtmlDocument = builder.Build(sanitized.Html, darkMode, allowRemoteImages);
            PlainText = body.PlainText ?? string.Empty;
            UseHtml = true;
            return;
        }

        HtmlDocument = null;
        UseHtml = false;
        PlainText = string.IsNullOrWhiteSpace(body.PlainText)
            ? "此邮件没有可显示的正文。"
            : body.PlainText;
    }
}
