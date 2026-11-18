using MailDog.Contracts.Dto;

namespace MailDog.Contracts.Messages;

// 握手

public sealed class HelloRequest : Ipc.IIpcMessage
{
    public int ProtocolVersion { get; init; }

    public string ClientVersion { get; init; } = string.Empty;
}

public sealed class HelloAck : Ipc.IIpcMessage
{
    public int ProtocolVersion { get; init; }

    public string EngineVersion { get; init; } = string.Empty;

    public AccountDto[] Accounts { get; init; } = [];
}

// 账户

public sealed class GetAccountsRequest : Ipc.IIpcMessage
{
}

public sealed class AccountsResponse : Ipc.IIpcMessage
{
    public AccountDto[] Items { get; init; } = [];
}

public sealed class AddPasswordAccountRequest : Ipc.IIpcMessage
{
    public MailProvider Provider { get; init; }

    public string Email { get; init; } = string.Empty;

    public string Secret { get; init; } = string.Empty;

    public string? DisplayName { get; init; }

    /// <summary>自定义服务器时必填。</summary>
    public string? ImapHost { get; init; }

    public int ImapPort { get; init; }

    public bool ImapStartTls { get; init; }

    public string? SmtpHost { get; init; }

    public int SmtpPort { get; init; }

    public bool SmtpStartTls { get; init; }
}

public sealed class AccountResponse : Ipc.IIpcMessage
{
    public AccountDto Account { get; init; } = new();
}

public sealed class StartOAuthRequest : Ipc.IIpcMessage
{
    public MailProvider Provider { get; init; } = MailProvider.Outlook;

    public string? LoginHint { get; init; }
}

public sealed class CancelOAuthRequest : Ipc.IIpcMessage
{
}

public sealed class RemoveAccountRequest : Ipc.IIpcMessage
{
    public string AccountId { get; init; } = string.Empty;
}

public sealed class UpdateAccountRequest : Ipc.IIpcMessage
{
    public string AccountId { get; init; } = string.Empty;

    public string? DisplayName { get; init; }

    /// <summary>密码账户的新授权码；留空表示保持原值。</summary>
    public string? Secret { get; init; }

    /// <summary>自定义服务器的新配置；非自定义账户必须为空。</summary>
    public string? ImapHost { get; init; }

    public int ImapPort { get; init; }

    public bool ImapStartTls { get; init; }

    public string? SmtpHost { get; init; }

    public int SmtpPort { get; init; }

    public bool SmtpStartTls { get; init; }
}

// 文件夹

public sealed class GetFoldersRequest : Ipc.IIpcMessage
{
    public string AccountId { get; init; } = string.Empty;
}

public sealed class FoldersResponse : Ipc.IIpcMessage
{
    public string AccountId { get; init; } = string.Empty;

    public FolderDto[] Items { get; init; } = [];
}

public sealed class CreateFolderRequest : Ipc.IIpcMessage
{
    public string AccountId { get; init; } = string.Empty;

    public string Name { get; init; } = string.Empty;
}

public sealed class RenameFolderRequest : Ipc.IIpcMessage
{
    public long FolderId { get; init; }

    public string Name { get; init; } = string.Empty;
}

public sealed class DeleteFolderRequest : Ipc.IIpcMessage
{
    public long FolderId { get; init; }
}

// 邮件

public sealed class GetMessagesRequest : Ipc.IIpcMessage
{
    public long FolderId { get; init; }

    /// <summary>按角色跨账户查询（如统一收件箱）；设置后忽略 <see cref="FolderId"/>。</summary>
    public FolderRole? Role { get; init; }

    public DateTimeOffset? BeforeDate { get; init; }

    public long? BeforeId { get; init; }

    public int Limit { get; init; } = 50;
}

public sealed class MessagesPageResponse : Ipc.IIpcMessage
{
    public MailSummaryDto[] Items { get; init; } = [];

    public bool HasMore { get; init; }

    public DateTimeOffset? NextDate { get; init; }

    public long? NextId { get; init; }
}

public sealed class GetMessageBodyRequest : Ipc.IIpcMessage
{
    public long MessageId { get; init; }
}

public sealed class MessageBodyResponse : Ipc.IIpcMessage
{
    public MailBodyDto Body { get; init; } = new();
}

public sealed class MoveMessageRequest : Ipc.IIpcMessage
{
    public long MessageId { get; init; }

    public long TargetFolderId { get; init; }
}

public sealed class DeleteMessageRequest : Ipc.IIpcMessage
{
    public long MessageId { get; init; }

    public bool Permanent { get; init; }
}

public sealed class DownloadAttachmentRequest : Ipc.IIpcMessage
{
    public long MessageId { get; init; }

    public int Index { get; init; }

    public string DestinationPath { get; init; } = string.Empty;
}

public sealed class AttachmentSavedResponse : Ipc.IIpcMessage
{
    public string Path { get; init; } = string.Empty;

    public string FileName { get; init; } = string.Empty;

    public long Size { get; init; }
}

public sealed class SendMailRequest : Ipc.IIpcMessage
{
    public string AccountId { get; init; } = string.Empty;

    public string To { get; init; } = string.Empty;

    public string Cc { get; init; } = string.Empty;

    public string Bcc { get; init; } = string.Empty;

    public string Subject { get; init; } = string.Empty;

    public string BodyText { get; init; } = string.Empty;

    public bool IsMarkdown { get; init; }

    public string? InReplyTo { get; init; }

    public string[] AttachmentPaths { get; init; } = [];
}

public sealed class SendMailResponse : Ipc.IIpcMessage
{
    public long OutboxId { get; init; }

    /// <summary>本次请求内已经送达 SMTP 服务器。</summary>
    public bool Sent { get; init; }

    /// <summary>已落入 Outbox，后台会继续重试。</summary>
    public bool Queued { get; init; }

    public string Status { get; init; } = "queued";

    public string? Error { get; init; }
}

public sealed class SaveDraftRequest : Ipc.IIpcMessage
{
    public string AccountId { get; init; } = string.Empty;

    public string To { get; init; } = string.Empty;

    public string Cc { get; init; } = string.Empty;

    public string Bcc { get; init; } = string.Empty;

    public string Subject { get; init; } = string.Empty;

    public string BodyText { get; init; } = string.Empty;

    public bool IsMarkdown { get; init; }

    public string[] AttachmentPaths { get; init; } = [];
}

public sealed class DraftSavedResponse : Ipc.IIpcMessage
{
    public long FolderId { get; init; }

    public string FolderName { get; init; } = string.Empty;
}

public sealed class MarkReadRequest : Ipc.IIpcMessage
{
    public long MessageId { get; init; }

    public bool IsRead { get; init; }
}

public sealed class MarkReadBatchRequest : Ipc.IIpcMessage
{
    public long[] MessageIds { get; init; } = [];

    public bool IsRead { get; init; }
}

public sealed class SetFlagRequest : Ipc.IIpcMessage
{
    public long MessageId { get; init; }

    public bool Value { get; init; }
}

public sealed class SetFlagBatchRequest : Ipc.IIpcMessage
{
    public long[] MessageIds { get; init; } = [];

    public bool Value { get; init; }
}

public sealed class SyncAccountRequest : Ipc.IIpcMessage
{
    public string? AccountId { get; init; }
}

public sealed class OkResponse : Ipc.IIpcMessage
{
    public int Affected { get; init; }
}

// 设置

public sealed class GetSettingsRequest : Ipc.IIpcMessage
{
}

public sealed class SettingsResponse : Ipc.IIpcMessage
{
    public AppSettingsDto Settings { get; init; } = new();
}

public sealed class UpdateSettingsRequest : Ipc.IIpcMessage
{
    public AppSettingsDto Settings { get; init; } = new();
}

// 本地文件与存储

public sealed class GetStorageSummaryRequest : Ipc.IIpcMessage
{
}

public sealed class StorageSummaryResponse : Ipc.IIpcMessage
{
    public StorageSummaryDto Summary { get; init; } = new();
}

public sealed class CleanStorageRequest : Ipc.IIpcMessage
{
    public bool CleanAttachmentCache { get; init; }

    public bool CleanLogs { get; init; }

    public bool CleanTempFiles { get; init; }
}

public sealed class StorageCleanupResponse : Ipc.IIpcMessage
{
    public StorageCleanupResultDto Result { get; init; } = new();
}

// 事件

public sealed class NewMailEvent : Ipc.IIpcMessage
{
    public string AccountId { get; init; } = string.Empty;

    public long FolderId { get; init; }

    public int Count { get; init; }

    public MailSummaryDto? Latest { get; init; }
}

public sealed class SyncStateEvent : Ipc.IIpcMessage
{
    public string AccountId { get; init; } = string.Empty;

    public long? FolderId { get; init; }

    public SyncStateKind State { get; init; }

    public double? Progress { get; init; }

    public string? Message { get; init; }
}

public sealed class AuthStateEvent : Ipc.IIpcMessage
{
    public string AccountId { get; init; } = string.Empty;

    public AuthStateKind State { get; init; }

    public string? Message { get; init; }
}

public sealed class ShowMainWindowEvent : Ipc.IIpcMessage
{
    public long? MessageId { get; init; }

    public string? AccountId { get; init; }

    public long? FolderId { get; init; }
}

public sealed class TakePendingNavigationRequest : Ipc.IIpcMessage
{
}

public sealed class PendingNavigationResponse : Ipc.IIpcMessage
{
    public bool HasTarget { get; init; }

    public long? MessageId { get; init; }

    public string? AccountId { get; init; }

    public long? FolderId { get; init; }
}

public sealed class AccountChangedEvent : Ipc.IIpcMessage
{
    public AccountDto Account { get; init; } = new();
}

// 错误

public sealed class ErrorResponse : Ipc.IIpcMessage
{
    public string Code { get; init; } = Ipc.IpcErrorCodes.Internal;

    public string Message { get; init; } = string.Empty;

    public string? Detail { get; init; }
}
