namespace MailDog.Contracts.Ipc;

public static class IpcProtocol
{
    public const int Version = 1;

    public const string PipeName = "MailDog.Engine.IPC.v1";

    public const int MaxFrameBytes = 16 * 1024 * 1024;

    public const int DefaultRequestTimeoutSeconds = 30;

    public const int BodyRequestTimeoutSeconds = 60;
}

public static class IpcMessageTypes
{
    public const string Hello = "hello";
    public const string HelloAck = "helloAck";

    public const string GetAccounts = "getAccounts";
    public const string Accounts = "accounts";
    public const string AddPasswordAccount = "addPasswordAccount";
    public const string Account = "account";
    public const string StartOAuth = "startOAuth";
    public const string CancelOAuth = "cancelOAuth";
    public const string RemoveAccount = "removeAccount";

    public const string GetFolders = "getFolders";
    public const string Folders = "folders";

    public const string GetMessages = "getMessages";
    public const string MessagesPage = "messagesPage";
    public const string GetMessageBody = "getMessageBody";
    public const string MessageBody = "messageBody";
    public const string MarkRead = "markRead";
    public const string MarkReadBatch = "markReadBatch";
    public const string SetFlag = "setFlag";
    public const string SetFlagBatch = "setFlagBatch";
    public const string DownloadAttachment = "downloadAttachment";
    public const string AttachmentSaved = "attachmentSaved";
    public const string SyncAccount = "syncAccount";
    public const string Ok = "ok";

    public const string GetSettings = "getSettings";
    public const string Settings = "settings";
    public const string UpdateSettings = "updateSettings";

    public const string NewMail = "newMail";
    public const string SyncState = "syncState";
    public const string AuthState = "authState";
    public const string ShowMainWindow = "showMainWindow";
    public const string TakePendingNavigation = "takePendingNavigation";
    public const string PendingNavigation = "pendingNavigation";
    public const string AccountChanged = "accountChanged";

    public const string Error = "error";
}

public static class IpcErrorCodes
{
    public const string InvalidRequest = "invalid_request";
    public const string NotFound = "not_found";
    public const string AuthRequired = "auth_required";
    public const string UpstreamUnavailable = "upstream_unavailable";
    public const string Timeout = "timeout";
    public const string Internal = "internal";
    public const string ProtocolMismatch = "protocol_mismatch";
}
