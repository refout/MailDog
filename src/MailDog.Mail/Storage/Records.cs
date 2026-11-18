using MailDog.Contracts.Dto;

namespace MailDog.Mail.Storage;

public sealed record AccountRecord(
    string Id,
    MailProvider Provider,
    string Email,
    string DisplayName,
    string AuthKind,
    string? SecretRef,
    DateTimeOffset CreatedUtc,
    DateTimeOffset? LastSyncUtc,
    AccountStatus Status,
    DateTimeOffset? TokenExpiresUtc);

public sealed record AccountServerRecord(
    string AccountId,
    string ImapHost,
    int ImapPort,
    bool ImapStartTls,
    string SmtpHost,
    int SmtpPort,
    bool SmtpStartTls);

public sealed record FolderRecord(
    long Id,
    string AccountId,
    string FullName,
    string DisplayName,
    FolderRole Role,
    long UidValidity,
    long LastSeenUid,
    int UnreadCount,
    int TotalCount);

public sealed record MessageRecord(
    long Id,
    string AccountId,
    long FolderId,
    long Uid,
    string? MessageId,
    string Subject,
    string FromName,
    string FromAddress,
    string ToAddresses,
    string CcAddresses,
    DateTimeOffset Date,
    int Size,
    MailFlags Flags,
    bool HasAttachments,
    string Preview,
    int BodyState)
{
    public bool IsRead => (Flags & MailFlags.Seen) != 0;
    public bool IsFlagged => (Flags & MailFlags.Flagged) != 0;
}

public sealed record BodyRecord(
    long MessageId,
    string? Html,
    string? PlainText,
    bool Truncated,
    IReadOnlyList<AttachmentRecord> Attachments);

public sealed record AttachmentRecord(
    string FileName,
    string MimeType,
    long Size,
    string? ContentId,
    bool IsInline);

/// <summary>Outbox 中待发送或已发送的邮件。附件在入队时复制到本地目录，保证重试可用。</summary>
public sealed record OutboxRecord(
    long Id,
    string AccountId,
    string ToAddresses,
    string CcAddresses,
    string BccAddresses,
    string Subject,
    string BodyText,
    string? InReplyTo,
    string MessageId,
    string Status,
    int Attempts,
    DateTimeOffset NextAttemptUtc,
    string? LastError,
    DateTimeOffset CreatedUtc,
    DateTimeOffset? SentUtc,
    IReadOnlyList<OutboxAttachmentRecord> Attachments,
    bool IsMarkdown = false);

public sealed record OutboxAttachmentRecord(
    long Id,
    string FileName,
    string StoredPath,
    string MimeType,
    long Size);

public static class OutboxStatuses
{
    public const string Pending = "pending";
    public const string Sending = "sending";
    public const string Sent = "sent";
    public const string Failed = "failed";
}

/// <summary>已读/旗标写回服务器失败后的待重试记录。</summary>
public sealed record PendingFlagRecord(
    long Id,
    long MessageId,
    string Flag,
    bool Value,
    int Attempts,
    DateTimeOffset NextAttemptUtc,
    string? LastError,
    DateTimeOffset CreatedUtc);

public static class BodyStates
{
    public const int NotFetched = 0;
    public const int Cached = 1;
    public const int TooLarge = 2;
    public const int Failed = 3;
}

public sealed record StorageCountsRecord(int Messages, int Bodies, int PendingOutbox);
