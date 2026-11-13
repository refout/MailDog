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

public static class BodyStates
{
    public const int NotFetched = 0;
    public const int Cached = 1;
    public const int TooLarge = 2;
    public const int Failed = 3;
}
