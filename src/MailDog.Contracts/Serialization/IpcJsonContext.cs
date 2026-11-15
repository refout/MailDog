using System.Text.Json.Serialization;
using MailDog.Contracts.Dto;
using MailDog.Contracts.Ipc;
using MailDog.Contracts.Messages;

namespace MailDog.Contracts.Serialization;

/// <summary>
/// 覆盖全部 IPC 消息与 OAuth DTO 的源生成上下文。
/// AOT 下不允许反射序列化，新增消息必须同时在此登记。
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(IpcEnvelope))]
[JsonSerializable(typeof(IpcErrorBody))]
[JsonSerializable(typeof(HelloRequest))]
[JsonSerializable(typeof(HelloAck))]
[JsonSerializable(typeof(GetAccountsRequest))]
[JsonSerializable(typeof(AccountsResponse))]
[JsonSerializable(typeof(AddPasswordAccountRequest))]
[JsonSerializable(typeof(AccountResponse))]
[JsonSerializable(typeof(StartOAuthRequest))]
[JsonSerializable(typeof(CancelOAuthRequest))]
[JsonSerializable(typeof(RemoveAccountRequest))]
[JsonSerializable(typeof(GetFoldersRequest))]
[JsonSerializable(typeof(FoldersResponse))]
[JsonSerializable(typeof(GetMessagesRequest))]
[JsonSerializable(typeof(MessagesPageResponse))]
[JsonSerializable(typeof(GetMessageBodyRequest))]
[JsonSerializable(typeof(MessageBodyResponse))]
[JsonSerializable(typeof(DownloadAttachmentRequest))]
[JsonSerializable(typeof(AttachmentSavedResponse))]
[JsonSerializable(typeof(MarkReadRequest))]
[JsonSerializable(typeof(MarkReadBatchRequest))]
[JsonSerializable(typeof(SetFlagRequest))]
[JsonSerializable(typeof(SetFlagBatchRequest))]
[JsonSerializable(typeof(SyncAccountRequest))]
[JsonSerializable(typeof(OkResponse))]
[JsonSerializable(typeof(GetSettingsRequest))]
[JsonSerializable(typeof(SettingsResponse))]
[JsonSerializable(typeof(UpdateSettingsRequest))]
[JsonSerializable(typeof(NewMailEvent))]
[JsonSerializable(typeof(SyncStateEvent))]
[JsonSerializable(typeof(AuthStateEvent))]
[JsonSerializable(typeof(ShowMainWindowEvent))]
[JsonSerializable(typeof(TakePendingNavigationRequest))]
[JsonSerializable(typeof(PendingNavigationResponse))]
[JsonSerializable(typeof(AccountChangedEvent))]
[JsonSerializable(typeof(ErrorResponse))]
[JsonSerializable(typeof(AccountDto))]
[JsonSerializable(typeof(FolderDto))]
[JsonSerializable(typeof(MailSummaryDto))]
[JsonSerializable(typeof(MailBodyDto))]
[JsonSerializable(typeof(AttachmentDto))]
[JsonSerializable(typeof(MailListPage))]
[JsonSerializable(typeof(AppSettingsDto))]
[JsonSerializable(typeof(OAuthTokenResponse))]
[JsonSerializable(typeof(OAuthTokenError))]
internal partial class IpcJsonContext : JsonSerializerContext
{
}
