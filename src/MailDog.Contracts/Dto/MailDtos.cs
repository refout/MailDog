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

    /// <summary>会话根键；由 References / In-Reply-To 解析，旧数据为空。</summary>
    public string? ThreadKey { get; init; }
}

public sealed class AttachmentDto
{
    public string FileName { get; init; } = string.Empty;

    public string MimeType { get; init; } = string.Empty;

    public long Size { get; init; }

    public string? ContentId { get; init; }

    public bool IsInline { get; init; }
}

/// <summary>从服务器读回的附件内容，用于阅读区内嵌预览，不落盘。</summary>
public sealed class AttachmentBytesDto
{
    public string FileName { get; init; } = string.Empty;

    public string MimeType { get; init; } = string.Empty;

    public byte[] Content { get; init; } = [];
}

public sealed class MailBodyDto
{
    public long MessageId { get; init; }

    public string Subject { get; init; } = string.Empty;

    public string? Html { get; init; }

    public string? PlainText { get; init; }

    public bool Truncated { get; init; }

    /// <summary>正文来自本地缓存，而不是本次从服务器下载。</summary>
    public bool FromCache { get; init; }

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

/// <summary>收件人自动补全候选，来自本地邮件历史。</summary>
public sealed class ContactSuggestionDto
{
    public string Name { get; init; } = string.Empty;

    public string Address { get; init; } = string.Empty;

    public DateTimeOffset LastSeen { get; init; }
}

/// <summary>草稿箱中的原始内容，用于重新打开撰写页继续编辑。</summary>
public sealed class DraftContentDto
{
    public long MessageId { get; init; }

    public string To { get; init; } = string.Empty;

    public string Cc { get; init; } = string.Empty;

    public string Bcc { get; init; } = string.Empty;

    public string Subject { get; init; } = string.Empty;

    public string BodyText { get; init; } = string.Empty;

    /// <summary>富文本草稿的 HTML 正文；Markdown 或旧草稿为空。</summary>
    public string? BodyHtml { get; init; }

    public bool IsMarkdown { get; init; }

    public string[] AttachmentNames { get; init; } = [];
}

/// <summary>本地发件箱中的待发送或失败记录。</summary>
public sealed class OutboxItemDto
{
    public long Id { get; init; }

    public string AccountId { get; init; } = string.Empty;

    public string ToAddresses { get; init; } = string.Empty;

    public string Subject { get; init; } = string.Empty;

    public string Status { get; init; } = string.Empty;

    public int Attempts { get; init; }

    public string? LastError { get; init; }

    public DateTimeOffset CreatedUtc { get; init; }

    public DateTimeOffset NextAttemptUtc { get; init; }

    public int AttachmentCount { get; init; }
}
