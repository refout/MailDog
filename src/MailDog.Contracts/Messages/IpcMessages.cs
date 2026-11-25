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

public sealed class GetAccountPreferencesRequest : Ipc.IIpcMessage
{
}

public sealed class AccountPreferencesResponse : Ipc.IIpcMessage
{
    public AccountPreferencesDto Preferences { get; init; } = new();
}

public sealed class UpdateAccountPreferencesRequest : Ipc.IIpcMessage
{
    public AccountPreferencesDto Preferences { get; init; } = new();
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

public sealed class SearchMessagesRequest : Ipc.IIpcMessage
{
    public long FolderId { get; init; }

    public FolderRole? Role { get; init; }

    public string Query { get; init; } = string.Empty;

    public int Limit { get; init; } = 200;

    /// <summary>跨全部账户和文件夹搜索；设置后忽略 FolderId/Role。</summary>
    public bool AllFolders { get; init; }
}

public sealed class MessageSearchResponse : Ipc.IIpcMessage
{
    public long[] MessageIds { get; init; } = [];

    /// <summary>跨文件夹搜索时附带摘要，供 UI 合并列表。</summary>
    public MailSummaryDto[] Items { get; init; } = [];
}

public sealed class ListContactsRequest : Ipc.IIpcMessage
{
    public int Limit { get; init; } = 500;
}

public sealed class ContactsResponse : Ipc.IIpcMessage
{
    public ContactSuggestionDto[] Items { get; init; } = [];
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

public sealed class ReadAttachmentRequest : Ipc.IIpcMessage
{
    public long MessageId { get; init; }

    public int Index { get; init; }

    public int MaxBytes { get; init; } = 8 * 1024 * 1024;
}

public sealed class AttachmentBytesResponse : Ipc.IIpcMessage
{
    public AttachmentBytesDto Attachment { get; init; } = new();
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

    /// <summary>富文本模式下的 HTML 正文；Markdown 模式留空。</summary>
    public string? BodyHtml { get; init; }

    public bool IsMarkdown { get; init; }

    public string? InReplyTo { get; init; }

    public string[] AttachmentPaths { get; init; } = [];

    /// <summary>用户已经看过附件缺失 / 体积提醒并确认继续，跳过发送前检查。</summary>
    public bool SkipSendChecks { get; init; }
}

public sealed class SendMailResponse : Ipc.IIpcMessage
{
    public long OutboxId { get; init; }

    /// <summary>本次请求内已经送达 SMTP 服务器。</summary>
    public bool Sent { get; init; }

    /// <summary>已落入 Outbox，后台会继续重试。</summary>
    public bool Queued { get; init; }

    public string Status { get; init; } = "queued";

    /// <summary>延迟发送的撤销窗口秒数；大于 0 表示邮件还停在发件箱，可以撤回。</summary>
    public int DelaySeconds { get; init; }

    public string? Error { get; init; }
}

/// <summary>发送结果的附加状态；与 OutboxStatuses 区分，只表达“还没进发件箱”的中间态。</summary>
public static class SendStatuses
{
    /// <summary>发送前检查命中，需要用户确认后再带 SkipSendChecks 重发。</summary>
    public const string ConfirmRequired = "confirm_required";
}

public sealed class SaveDraftRequest : Ipc.IIpcMessage
{
    public string AccountId { get; init; } = string.Empty;

    public string To { get; init; } = string.Empty;

    public string Cc { get; init; } = string.Empty;

    public string Bcc { get; init; } = string.Empty;

    public string Subject { get; init; } = string.Empty;

    public string BodyText { get; init; } = string.Empty;

    /// <summary>富文本模式下的 HTML 正文；Markdown 模式留空。</summary>
    public string? BodyHtml { get; init; }

    public bool IsMarkdown { get; init; }

    public long? ReplaceDraftMessageId { get; init; }

    public string[] AttachmentPaths { get; init; } = [];
}

public sealed class DraftSavedResponse : Ipc.IIpcMessage
{
    public long FolderId { get; init; }

    public string FolderName { get; init; } = string.Empty;
}

public sealed class GetDraftRequest : Ipc.IIpcMessage
{
    public long MessageId { get; init; }
}

public sealed class DraftContentResponse : Ipc.IIpcMessage
{
    public DraftContentDto Draft { get; init; } = new();
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

public sealed class MoveMessagesRequest : Ipc.IIpcMessage
{
    public long[] MessageIds { get; init; } = [];

    public long TargetFolderId { get; init; }
}

public sealed class DeleteMessagesRequest : Ipc.IIpcMessage
{
    public long[] MessageIds { get; init; } = [];

    public bool Permanent { get; init; }
}

public sealed class GetTagsRequest : Ipc.IIpcMessage
{
}

public sealed class TagsResponse : Ipc.IIpcMessage
{
    public TagDto[] Items { get; init; } = [];
}

public sealed class SaveTagRequest : Ipc.IIpcMessage
{
    public TagDto Tag { get; init; } = new();
}

public sealed class DeleteTagRequest : Ipc.IIpcMessage
{
    public long TagId { get; init; }
}

public sealed class GetMessageTagsRequest : Ipc.IIpcMessage
{
    public long[] MessageIds { get; init; } = [];
}

public sealed class MessageTagsResponse : Ipc.IIpcMessage
{
    public MessageTagAssignmentDto[] Items { get; init; } = [];
}

public sealed class SetMessageTagsRequest : Ipc.IIpcMessage
{
    public long[] MessageIds { get; init; } = [];

    public long[] TagIds { get; init; } = [];

    /// <summary>true 添加，false 移除。</summary>
    public bool Add { get; init; } = true;
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

public sealed class CreateBackupRequest : Ipc.IIpcMessage
{
    public string DestinationPath { get; init; } = string.Empty;

    /// <summary>非空时生成 AES-256-GCM 加密备份，扩展名为 .mdog。</summary>
    public string? Password { get; init; }
}

public sealed class BackupResponse : Ipc.IIpcMessage
{
    public BackupResultDto Result { get; init; } = new();
}

public sealed class RestoreBackupRequest : Ipc.IIpcMessage
{
    public string ArchivePath { get; init; } = string.Empty;

    /// <summary>加密备份需要密码；普通 ZIP 可留空。</summary>
    public string? Password { get; init; }
}

public sealed class RestoreResponse : Ipc.IIpcMessage
{
    public RestoreResultDto Result { get; init; } = new();
}

public sealed class ExportMessageRequest : Ipc.IIpcMessage
{
    public long MessageId { get; init; }

    public string DestinationPath { get; init; } = string.Empty;
}

public sealed class MessageExportResponse : Ipc.IIpcMessage
{
    public string Path { get; init; } = string.Empty;

    public long Size { get; init; }
}

public sealed class ListOutboxRequest : Ipc.IIpcMessage
{
}

public sealed class OutboxListResponse : Ipc.IIpcMessage
{
    public OutboxItemDto[] Items { get; init; } = [];
}

public sealed class RetryOutboxRequest : Ipc.IIpcMessage
{
    public long OutboxId { get; init; }
}

public sealed class DeleteOutboxRequest : Ipc.IIpcMessage
{
    public long OutboxId { get; init; }
}

public sealed class GetRulesRequest : Ipc.IIpcMessage
{
}

public sealed class RulesResponse : Ipc.IIpcMessage
{
    public RuleDto[] Items { get; init; } = [];
}

public sealed class SaveRuleRequest : Ipc.IIpcMessage
{
    public RuleDto Rule { get; init; } = new();
}

public sealed class DeleteRuleRequest : Ipc.IIpcMessage
{
    public long Id { get; init; }
}

// 事件

public sealed class NewMailEvent : Ipc.IIpcMessage
{
    public string AccountId { get; init; } = string.Empty;

    public long FolderId { get; init; }

    public int Count { get; init; }

    public MailSummaryDto? Latest { get; init; }

    /// <summary>这一批新入库的邮件 id，供本地规则逐条判断。</summary>
    public long[] NewMessageIds { get; init; } = [];
}

/// <summary>通知快捷动作等后台操作改动了邮件；界面据此刷新当前文件夹。</summary>
public sealed class MailChangedEvent : Ipc.IIpcMessage
{
    public string AccountId { get; init; } = string.Empty;

    public long MessageId { get; init; }

    /// <summary>改动前的文件夹；移动后界面仍按这个文件夹判断是否需要刷新。</summary>
    public long FolderId { get; init; }

    /// <summary>flags / moved。</summary>
    public string Change { get; init; } = "flags";
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

    /// <summary>mailto: 链接，界面打开后用它预填撰写窗口。</summary>
    public string? ComposeUri { get; init; }

    /// <summary>要直接打开的 .eml 文件路径。</summary>
    public string? EmlPath { get; init; }

    /// <summary>只要窗口并把左侧设置页切到「关于」，用于托盘里的"检查更新"。</summary>
    public bool OpenAbout { get; init; }

    /// <summary>打开关于页后顺带跑一次检查。</summary>
    public bool CheckUpdate { get; init; }
}

public sealed class TakePendingNavigationRequest : Ipc.IIpcMessage
{
}

public sealed class PendingNavigationResponse : Ipc.IIpcMessage
{
    public bool HasTarget { get; init; }

    /// <summary>冷启动时由 mailto: 链接带来的撰写内容。</summary>
    public string? ComposeUri { get; init; }

    /// <summary>冷启动时要直接打开的 .eml 文件。</summary>
    public string? EmlPath { get; init; }

    public long? MessageId { get; init; }

    public string? AccountId { get; init; }

    public long? FolderId { get; init; }

    /// <summary>冷启动时点的是"检查更新"，界面起来后直接打开关于页。</summary>
    public bool OpenAbout { get; init; }

    public bool CheckUpdate { get; init; }
}

/// <summary>请求后台进程收尾并退出；只应由本机同一用户的界面或命令行发出。</summary>
public sealed class ActivateRequest : Ipc.IIpcMessage
{
    public string Uri { get; init; } = string.Empty;
}

public sealed class AddRecentRequest : Ipc.IIpcMessage
{
    public long MessageId { get; init; }

    public string AccountId { get; init; } = string.Empty;

    public long FolderId { get; init; }

    public string Subject { get; init; } = string.Empty;

    public string From { get; init; } = string.Empty;
}

public sealed class ShutdownRequest : Ipc.IIpcMessage
{
}

public sealed class GetAccountHealthRequest : Ipc.IIpcMessage
{
}

public sealed class AccountHealthResponse : Ipc.IIpcMessage
{
    public AccountHealthDto[] Items { get; init; } = [];
}

public sealed class GetEmlPreviewRequest : Ipc.IIpcMessage
{
    public string Path { get; init; } = string.Empty;
}

public sealed class EmlPreviewResponse : Ipc.IIpcMessage
{
    public EmlPreviewDto Preview { get; init; } = new();
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
