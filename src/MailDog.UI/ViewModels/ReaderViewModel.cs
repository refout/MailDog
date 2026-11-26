using Avalonia.Media;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MailDog.Contracts.Dto;
using MailDog.Html;

namespace MailDog.UI.ViewModels;

public sealed partial class AttachmentViewModel : ObservableObject, IDisposable
{
    private readonly Func<AttachmentViewModel, Task> _download;
    private readonly Func<AttachmentViewModel, Task>? _preview;
    private readonly Func<AttachmentViewModel, Task<AttachmentBytesDto>>? _loadBytes;
    private readonly Action<AttachmentViewModel>? _selectionChanged;

    public AttachmentViewModel(
        long messageId,
        int index,
        AttachmentDto attachment,
        Func<AttachmentViewModel, Task> download,
        Func<AttachmentViewModel, Task>? preview = null,
        Func<AttachmentViewModel, Task<AttachmentBytesDto>>? loadBytes = null,
        Action<AttachmentViewModel>? selectionChanged = null)
    {
        MessageId = messageId;
        Index = index;
        FileName = attachment.FileName;
        MimeType = attachment.MimeType;
        SizeText = FormatSize(attachment.Size);
        IsInline = attachment.IsInline;
        _download = download;
        _preview = preview;
        _loadBytes = loadBytes;
        _selectionChanged = selectionChanged;
    }

    public long MessageId { get; }

    public int Index { get; }

    public string FileName { get; }

    public string MimeType { get; }

    public string SizeText { get; }

    public bool IsInline { get; }

    public bool IsImage => MimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
        && !MimeType.Contains("svg", StringComparison.OrdinalIgnoreCase);

    public string SelectAutomationName => $"选择附件 {FileName}";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPreviewImage))]
    private Bitmap? _previewImage;

    [ObservableProperty]
    private bool _isPreviewLoading;

    [ObservableProperty]
    private bool _isSelected;

    partial void OnIsSelectedChanged(bool value) => _selectionChanged?.Invoke(this);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPreviewError))]
    private string? _previewError;

    public bool HasPreviewImage => PreviewImage is not null;

    public bool HasPreviewError => !string.IsNullOrWhiteSpace(PreviewError);

    public async Task LoadPreviewImageAsync()
    {
        if (!IsImage || _loadBytes is null || PreviewImage is not null || IsPreviewLoading)
        {
            return;
        }

        IsPreviewLoading = true;
        PreviewError = null;
        try
        {
            var attachment = await _loadBytes(this);
            await using var stream = new MemoryStream(attachment.Content);
            PreviewImage = new Bitmap(stream);
        }
        catch (Exception exception)
        {
            PreviewError = exception.Message;
        }
        finally
        {
            IsPreviewLoading = false;
        }
    }

    [RelayCommand]
    private Task DownloadAsync() => _download(this);

    [RelayCommand]
    private Task PreviewAsync() => _preview?.Invoke(this) ?? Task.CompletedTask;

    public void Dispose()
    {
        PreviewImage?.Dispose();
        PreviewImage = null;
    }

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
    private string _senderName = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSenderAddress))]
    private string _senderAddress = string.Empty;

    [ObservableProperty]
    private string _avatarInitial = "?";

    [ObservableProperty]
    private IBrush? _avatarBrush;

    [ObservableProperty]
    private string _dateText = string.Empty;

    public bool HasSenderAddress => !string.IsNullOrWhiteSpace(SenderAddress);

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

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedAttachments))]
    private int _selectedAttachmentCount;

    public bool HasAttachments => Attachments.Length > 0;

    public bool HasSelectedAttachments => SelectedAttachmentCount > 0;

    public bool HasNotice => !string.IsNullOrEmpty(Notice);

    /// <summary>由主视图模型注入：批量保存选中的或全部附件。</summary>
    public Func<IReadOnlyList<AttachmentViewModel>, Task>? SaveAttachments { get; set; }

    public Func<AttachmentViewModel, Task>? DownloadAttachment { get; set; }

    public Func<AttachmentViewModel, Task>? PreviewAttachment { get; set; }

    /// <summary>由主视图模型注入：读取附件字节用于内嵌图片预览，不落盘。</summary>
    public Func<AttachmentViewModel, Task<AttachmentBytesDto>>? LoadAttachmentBytes { get; set; }

    public bool HasError => !string.IsNullOrEmpty(Error);

    public bool HasSelection => IsLoading || !string.IsNullOrEmpty(Subject);

    public bool ShowEmpty => !HasSelection;

    public bool IsPlainText => !UseHtml;

    public void Reset()
    {
        ClearAttachments();
        Subject = string.Empty;
        SenderLine = string.Empty;
        SenderName = string.Empty;
        SenderAddress = string.Empty;
        AvatarInitial = "?";
        AvatarBrush = null;
        DateText = string.Empty;
        HtmlDocument = null;
        UseHtml = false;
        PlainText = string.Empty;
        ImagesBlocked = false;
        IsFromCache = false;
        Notice = null;
        Error = null;
    }

    private void ClearAttachments()
    {
        foreach (var attachment in Attachments)
        {
            attachment.Dispose();
        }

        Attachments = [];
        SelectedAttachmentCount = 0;
    }

    private void OnAttachmentSelectionChanged(AttachmentViewModel attachment)
    {
        SelectedAttachmentCount = Attachments.Count(item => item.IsSelected);
    }

    [RelayCommand]
    private void SelectAllAttachments()
    {
        foreach (var attachment in Attachments)
        {
            attachment.IsSelected = true;
        }
    }

    [RelayCommand]
    private void ClearAttachmentSelection()
    {
        foreach (var attachment in Attachments)
        {
            attachment.IsSelected = false;
        }
    }

    [RelayCommand]
    private Task SaveSelectedAttachmentsAsync()
    {
        var selected = Attachments.Where(item => item.IsSelected).ToArray();
        return selected.Length == 0
            ? Task.CompletedTask
            : SaveAttachments?.Invoke(selected) ?? Task.CompletedTask;
    }

    [RelayCommand]
    private Task SaveAllAttachmentsAsync()
        => Attachments.Length == 0
            ? Task.CompletedTask
            : SaveAttachments?.Invoke(Attachments) ?? Task.CompletedTask;

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
        Subject = string.IsNullOrWhiteSpace(body.Subject) ? "（无主题）" : body.Subject;
        SenderLine = senderLine;
        DateText = dateText;
        ClearAttachments();
        Attachments = body.Attachments
            .Select((attachment, index) => new AttachmentViewModel(
                body.MessageId,
                index,
                attachment,
                DownloadAttachment ?? (_ => Task.CompletedTask),
                PreviewAttachment,
                LoadAttachmentBytes,
                OnAttachmentSelectionChanged))
            .ToArray();

        SelectedAttachmentCount = 0;

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
