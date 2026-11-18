namespace MailDog.Contracts.Dto;

public sealed class MailSummaryDto
{
    public long Id { get; init; }

    public long FolderId { get; init; }

    public string AccountId { get; init; } = string.Empty;

    public string Subject { get; init; } = string.Empty;

    public string FromName { get; init; } = string.Empty;

    public string FromAddress { get; init; } = string.Empty;

    public string Preview { get; init; } = string.Empty;

    public DateTimeOffset Date { get; init; }

    public MailFlags Flags { get; init; }

    public bool HasAttachments { get; init; }

    public int Size { get; init; }
}

public sealed class AttachmentDto
{
    public string FileName { get; init; } = string.Empty;

    public string MimeType { get; init; } = string.Empty;

    public long Size { get; init; }

    public string? ContentId { get; init; }

    public bool IsInline { get; init; }
}

public sealed class MailBodyDto
{
    public long MessageId { get; init; }

    public string Subject { get; init; } = string.Empty;

    public string? Html { get; init; }

    public string? PlainText { get; init; }

    public bool Truncated { get; init; }

    public AttachmentDto[] Attachments { get; init; } = [];

    public string FromName { get; init; } = string.Empty;

    public string FromAddress { get; init; } = string.Empty;

    public string[] ToAddresses { get; init; } = [];

    public string[] CcAddresses { get; init; } = [];

    public DateTimeOffset Date { get; init; }

    /// <summary>原始邮件的 RFC Message-ID，用于回复线程头。</summary>
    public string? RfcMessageId { get; init; }
}

public sealed class MailListPage
{
    public MailSummaryDto[] Items { get; init; } = [];

    public bool HasMore { get; init; }

    public DateTimeOffset? NextDate { get; init; }

    public long? NextId { get; init; }
}
