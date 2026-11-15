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
        Register(IpcMessageTypes.AddPasswordAccount, IpcJsonContext.Default.AddPasswordAccountRequest);
        Register(IpcMessageTypes.Account, IpcJsonContext.Default.AccountResponse);
        Register(IpcMessageTypes.StartOAuth, IpcJsonContext.Default.StartOAuthRequest);
        Register(IpcMessageTypes.CancelOAuth, IpcJsonContext.Default.CancelOAuthRequest);
        Register(IpcMessageTypes.RemoveAccount, IpcJsonContext.Default.RemoveAccountRequest);

        Register(IpcMessageTypes.GetFolders, IpcJsonContext.Default.GetFoldersRequest);
        Register(IpcMessageTypes.Folders, IpcJsonContext.Default.FoldersResponse);

        Register(IpcMessageTypes.GetMessages, IpcJsonContext.Default.GetMessagesRequest);
        Register(IpcMessageTypes.MessagesPage, IpcJsonContext.Default.MessagesPageResponse);
        Register(IpcMessageTypes.GetMessageBody, IpcJsonContext.Default.GetMessageBodyRequest);
        Register(IpcMessageTypes.MessageBody, IpcJsonContext.Default.MessageBodyResponse);
        Register(IpcMessageTypes.DownloadAttachment, IpcJsonContext.Default.DownloadAttachmentRequest);
        Register(IpcMessageTypes.AttachmentSaved, IpcJsonContext.Default.AttachmentSavedResponse);
        Register(IpcMessageTypes.MarkRead, IpcJsonContext.Default.MarkReadRequest);
        Register(IpcMessageTypes.MarkReadBatch, IpcJsonContext.Default.MarkReadBatchRequest);
        Register(IpcMessageTypes.SetFlag, IpcJsonContext.Default.SetFlagRequest);
        Register(IpcMessageTypes.SetFlagBatch, IpcJsonContext.Default.SetFlagBatchRequest);
        Register(IpcMessageTypes.SyncAccount, IpcJsonContext.Default.SyncAccountRequest);
        Register(IpcMessageTypes.Ok, IpcJsonContext.Default.OkResponse);

        Register(IpcMessageTypes.GetSettings, IpcJsonContext.Default.GetSettingsRequest);
        Register(IpcMessageTypes.Settings, IpcJsonContext.Default.SettingsResponse);
        Register(IpcMessageTypes.UpdateSettings, IpcJsonContext.Default.UpdateSettingsRequest);

        Register(IpcMessageTypes.NewMail, IpcJsonContext.Default.NewMailEvent);
        Register(IpcMessageTypes.SyncState, IpcJsonContext.Default.SyncStateEvent);
        Register(IpcMessageTypes.AuthState, IpcJsonContext.Default.AuthStateEvent);
        Register(IpcMessageTypes.ShowMainWindow, IpcJsonContext.Default.ShowMainWindowEvent);
        Register(IpcMessageTypes.TakePendingNavigation, IpcJsonContext.Default.TakePendingNavigationRequest);
        Register(IpcMessageTypes.PendingNavigation, IpcJsonContext.Default.PendingNavigationResponse);
        Register(IpcMessageTypes.AccountChanged, IpcJsonContext.Default.AccountChangedEvent);

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
