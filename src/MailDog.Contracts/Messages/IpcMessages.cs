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
