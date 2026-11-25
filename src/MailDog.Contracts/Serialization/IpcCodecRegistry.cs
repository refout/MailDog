using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using MailDog.Contracts.Ipc;
using MailDog.Contracts.Messages;

namespace MailDog.Contracts.Serialization;

/// <summary>
/// 显式注册表：类型名 → 编解码器。禁止依赖反射或程序集扫描。
/// </summary>
public static class IpcCodecRegistry
{
    private static readonly Dictionary<string, IIpcCodec> CodecMap = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, Type> TypeMap = new(StringComparer.Ordinal);

    static IpcCodecRegistry()
    {
        Register(IpcMessageTypes.Hello, IpcJsonContext.Default.HelloRequest);
        Register(IpcMessageTypes.HelloAck, IpcJsonContext.Default.HelloAck);

        Register(IpcMessageTypes.GetAccounts, IpcJsonContext.Default.GetAccountsRequest);
        Register(IpcMessageTypes.Accounts, IpcJsonContext.Default.AccountsResponse);
        Register(IpcMessageTypes.GetAccountPreferences, IpcJsonContext.Default.GetAccountPreferencesRequest);
        Register(IpcMessageTypes.AccountPreferences, IpcJsonContext.Default.AccountPreferencesResponse);
        Register(IpcMessageTypes.UpdateAccountPreferences, IpcJsonContext.Default.UpdateAccountPreferencesRequest);
        Register(IpcMessageTypes.AddPasswordAccount, IpcJsonContext.Default.AddPasswordAccountRequest);
        Register(IpcMessageTypes.Account, IpcJsonContext.Default.AccountResponse);
        Register(IpcMessageTypes.StartOAuth, IpcJsonContext.Default.StartOAuthRequest);
        Register(IpcMessageTypes.CancelOAuth, IpcJsonContext.Default.CancelOAuthRequest);
        Register(IpcMessageTypes.RemoveAccount, IpcJsonContext.Default.RemoveAccountRequest);
        Register(IpcMessageTypes.UpdateAccount, IpcJsonContext.Default.UpdateAccountRequest);

        Register(IpcMessageTypes.GetFolders, IpcJsonContext.Default.GetFoldersRequest);
        Register(IpcMessageTypes.Folders, IpcJsonContext.Default.FoldersResponse);
        Register(IpcMessageTypes.CreateFolder, IpcJsonContext.Default.CreateFolderRequest);
        Register(IpcMessageTypes.RenameFolder, IpcJsonContext.Default.RenameFolderRequest);
        Register(IpcMessageTypes.DeleteFolder, IpcJsonContext.Default.DeleteFolderRequest);

        Register(IpcMessageTypes.GetMessages, IpcJsonContext.Default.GetMessagesRequest);
        Register(IpcMessageTypes.MessagesPage, IpcJsonContext.Default.MessagesPageResponse);
        Register(IpcMessageTypes.SearchMessages, IpcJsonContext.Default.SearchMessagesRequest);
        Register(IpcMessageTypes.MessageSearch, IpcJsonContext.Default.MessageSearchResponse);
        Register(IpcMessageTypes.ListContacts, IpcJsonContext.Default.ListContactsRequest);
        Register(IpcMessageTypes.Contacts, IpcJsonContext.Default.ContactsResponse);
        Register(IpcMessageTypes.GetMessageBody, IpcJsonContext.Default.GetMessageBodyRequest);
        Register(IpcMessageTypes.MessageBody, IpcJsonContext.Default.MessageBodyResponse);
        Register(IpcMessageTypes.MoveMessage, IpcJsonContext.Default.MoveMessageRequest);
        Register(IpcMessageTypes.DeleteMessage, IpcJsonContext.Default.DeleteMessageRequest);
        Register(IpcMessageTypes.MoveMessages, IpcJsonContext.Default.MoveMessagesRequest);
        Register(IpcMessageTypes.DeleteMessages, IpcJsonContext.Default.DeleteMessagesRequest);
        Register(IpcMessageTypes.DownloadAttachment, IpcJsonContext.Default.DownloadAttachmentRequest);
        Register(IpcMessageTypes.ReadAttachment, IpcJsonContext.Default.ReadAttachmentRequest);
        Register(IpcMessageTypes.AttachmentBytes, IpcJsonContext.Default.AttachmentBytesResponse);
        Register(IpcMessageTypes.AttachmentSaved, IpcJsonContext.Default.AttachmentSavedResponse);
        Register(IpcMessageTypes.SendMail, IpcJsonContext.Default.SendMailRequest);
        Register(IpcMessageTypes.SendMailResult, IpcJsonContext.Default.SendMailResponse);
        Register(IpcMessageTypes.SaveDraft, IpcJsonContext.Default.SaveDraftRequest);
        Register(IpcMessageTypes.DraftSaved, IpcJsonContext.Default.DraftSavedResponse);
        Register(IpcMessageTypes.GetDraft, IpcJsonContext.Default.GetDraftRequest);
        Register(IpcMessageTypes.DraftContent, IpcJsonContext.Default.DraftContentResponse);
        Register(IpcMessageTypes.MarkRead, IpcJsonContext.Default.MarkReadRequest);
        Register(IpcMessageTypes.MarkReadBatch, IpcJsonContext.Default.MarkReadBatchRequest);
        Register(IpcMessageTypes.SnoozeMessage, IpcJsonContext.Default.SnoozeMessageRequest);
        Register(IpcMessageTypes.GetUnsubscribeInfo, IpcJsonContext.Default.GetUnsubscribeInfoRequest);
        Register(IpcMessageTypes.UnsubscribeInfo, IpcJsonContext.Default.UnsubscribeInfoResponse);
        Register(IpcMessageTypes.SetFlag, IpcJsonContext.Default.SetFlagRequest);
        Register(IpcMessageTypes.SetFlagBatch, IpcJsonContext.Default.SetFlagBatchRequest);
        Register(IpcMessageTypes.GetTags, IpcJsonContext.Default.GetTagsRequest);
        Register(IpcMessageTypes.Tags, IpcJsonContext.Default.TagsResponse);
        Register(IpcMessageTypes.SaveTag, IpcJsonContext.Default.SaveTagRequest);
        Register(IpcMessageTypes.DeleteTag, IpcJsonContext.Default.DeleteTagRequest);
        Register(IpcMessageTypes.GetMessageTags, IpcJsonContext.Default.GetMessageTagsRequest);
        Register(IpcMessageTypes.MessageTags, IpcJsonContext.Default.MessageTagsResponse);
        Register(IpcMessageTypes.SetMessageTags, IpcJsonContext.Default.SetMessageTagsRequest);
        Register(IpcMessageTypes.SyncAccount, IpcJsonContext.Default.SyncAccountRequest);
        Register(IpcMessageTypes.Ok, IpcJsonContext.Default.OkResponse);

        Register(IpcMessageTypes.GetSettings, IpcJsonContext.Default.GetSettingsRequest);
        Register(IpcMessageTypes.Settings, IpcJsonContext.Default.SettingsResponse);
        Register(IpcMessageTypes.UpdateSettings, IpcJsonContext.Default.UpdateSettingsRequest);
        Register(IpcMessageTypes.GetStorageSummary, IpcJsonContext.Default.GetStorageSummaryRequest);
        Register(IpcMessageTypes.StorageSummary, IpcJsonContext.Default.StorageSummaryResponse);
        Register(IpcMessageTypes.CleanStorage, IpcJsonContext.Default.CleanStorageRequest);
        Register(IpcMessageTypes.StorageCleanup, IpcJsonContext.Default.StorageCleanupResponse);
        Register(IpcMessageTypes.CreateBackup, IpcJsonContext.Default.CreateBackupRequest);
        Register(IpcMessageTypes.BackupResult, IpcJsonContext.Default.BackupResponse);
        Register(IpcMessageTypes.RestoreBackup, IpcJsonContext.Default.RestoreBackupRequest);
        Register(IpcMessageTypes.RestoreResult, IpcJsonContext.Default.RestoreResponse);
        Register(IpcMessageTypes.ExportMessage, IpcJsonContext.Default.ExportMessageRequest);
        Register(IpcMessageTypes.MessageExport, IpcJsonContext.Default.MessageExportResponse);
        Register(IpcMessageTypes.ListOutbox, IpcJsonContext.Default.ListOutboxRequest);
        Register(IpcMessageTypes.OutboxList, IpcJsonContext.Default.OutboxListResponse);
        Register(IpcMessageTypes.RetryOutbox, IpcJsonContext.Default.RetryOutboxRequest);
        Register(IpcMessageTypes.DeleteOutbox, IpcJsonContext.Default.DeleteOutboxRequest);
        Register(IpcMessageTypes.GetRules, IpcJsonContext.Default.GetRulesRequest);
        Register(IpcMessageTypes.RulesList, IpcJsonContext.Default.RulesResponse);
        Register(IpcMessageTypes.SaveRule, IpcJsonContext.Default.SaveRuleRequest);
        Register(IpcMessageTypes.DeleteRule, IpcJsonContext.Default.DeleteRuleRequest);

        Register(IpcMessageTypes.NewMail, IpcJsonContext.Default.NewMailEvent);
        Register(IpcMessageTypes.MailChanged, IpcJsonContext.Default.MailChangedEvent);
        Register(IpcMessageTypes.SyncState, IpcJsonContext.Default.SyncStateEvent);
        Register(IpcMessageTypes.AuthState, IpcJsonContext.Default.AuthStateEvent);
        Register(IpcMessageTypes.ShowMainWindow, IpcJsonContext.Default.ShowMainWindowEvent);
        Register(IpcMessageTypes.TakePendingNavigation, IpcJsonContext.Default.TakePendingNavigationRequest);
        Register(IpcMessageTypes.PendingNavigation, IpcJsonContext.Default.PendingNavigationResponse);
        Register(IpcMessageTypes.AccountChanged, IpcJsonContext.Default.AccountChangedEvent);
        Register(IpcMessageTypes.Shutdown, IpcJsonContext.Default.ShutdownRequest);
        Register(IpcMessageTypes.Activate, IpcJsonContext.Default.ActivateRequest);
        Register(IpcMessageTypes.AddRecent, IpcJsonContext.Default.AddRecentRequest);
        Register(IpcMessageTypes.GetAccountHealth, IpcJsonContext.Default.GetAccountHealthRequest);
        Register(IpcMessageTypes.AccountHealth, IpcJsonContext.Default.AccountHealthResponse);
        Register(IpcMessageTypes.GetEmlPreview, IpcJsonContext.Default.GetEmlPreviewRequest);
        Register(IpcMessageTypes.EmlPreview, IpcJsonContext.Default.EmlPreviewResponse);

        Register(IpcMessageTypes.Error, IpcJsonContext.Default.ErrorResponse);
    }

    public static IReadOnlyDictionary<string, Type> RegisteredTypes => TypeMap;

    public static bool TryGet(string type, out IIpcCodec codec)
        => CodecMap.TryGetValue(type, out codec!);

    public static IIpcCodec Get(string type)
        => CodecMap.TryGetValue(type, out var codec)
            ? codec
            : throw new NotSupportedException($"Unknown IPC message type '{type}'.");

    public static IpcEnvelope Create<T>(string type, T message, long requestId = 0)
        where T : IIpcMessage
    {
        var codec = Get(type);
        return new IpcEnvelope
        {
            Type = type,
            RequestId = requestId,
            Body = codec.Serialize(message)
        };
    }

    public static IIpcMessage Read(IpcEnvelope envelope)
        => Get(envelope.Type).Deserialize(envelope.Body);

    public static ErrorResponse Error(string code, string message, string? detail = null)
        => new()
        {
            Code = code,
            Message = message,
            Detail = detail
        };

    private static void Register<T>(string type, JsonTypeInfo<T> typeInfo) where T : IIpcMessage
    {
        if (!CodecMap.TryAdd(type, new IpcCodec<T>(type, typeInfo)))
        {
            throw new InvalidOperationException($"Duplicate IPC message type '{type}'.");
        }

        TypeMap.Add(type, typeof(T));
    }
}
