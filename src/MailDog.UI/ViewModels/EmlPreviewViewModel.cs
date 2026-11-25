using MailDog.Contracts.Dto;

namespace MailDog.UI.ViewModels;

/// <summary>外部 .eml 独立预览窗口的数据；正文按纯文本显示，附件只列名称。</summary>
public sealed class EmlPreviewViewModel
{
    public EmlPreviewViewModel(EmlPreviewDto preview)
    {
        Title = string.IsNullOrWhiteSpace(preview.Subject) ? preview.FileName : preview.Subject;
        Subject = Title;
        FileName = preview.FileName;
        FromText = string.IsNullOrWhiteSpace(preview.FromName)
            ? preview.FromAddress
            : $"{preview.FromName} <{preview.FromAddress}>";
        ToText = string.IsNullOrWhiteSpace(preview.ToAddresses) ? "（无收件人）" : preview.ToAddresses;
        DateText = preview.Date.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
        Body = preview.Error is { Length: > 0 }
            ? preview.Error
            : preview.Truncated
                ? preview.PlainText + "\n\n（正文过长，已截断显示。）"
                : preview.PlainText;
        AttachmentsText = preview.AttachmentNames.Length == 0
            ? string.Empty
            : $"附件：{string.Join("、", preview.AttachmentNames)}";
        HasAttachments = preview.AttachmentNames.Length > 0;
        HasError = preview.Error is { Length: > 0 };
    }

    public string Title { get; }

    public string Subject { get; }

    public string FileName { get; }

    public string FromText { get; }

    public string ToText { get; }

    public string DateText { get; }

    public string Body { get; }

    public string AttachmentsText { get; }

    public bool HasAttachments { get; }

    public bool HasError { get; }
}
