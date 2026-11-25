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
    public const string GetAccountPreferences = "getAccountPreferences";
    public const string AccountPreferences = "accountPreferences";
    public const string UpdateAccountPreferences = "updateAccountPreferences";
    public const string AddPasswordAccount = "addPasswordAccount";
    public const string Account = "account";
    public const string StartOAuth = "startOAuth";
    public const string CancelOAuth = "cancelOAuth";
    public const string RemoveAccount = "removeAccount";
    public const string UpdateAccount = "updateAccount";

    public const string GetFolders = "getFolders";
    public const string Folders = "folders";
    public const string CreateFolder = "createFolder";
    public const string RenameFolder = "renameFolder";
    public const string DeleteFolder = "deleteFolder";

    public const string GetMessages = "getMessages";
    public const string MessagesPage = "messagesPage";
    public const string SearchMessages = "searchMessages";
    public const string MessageSearch = "messageSearch";
    public const string ListContacts = "listContacts";
    public const string Contacts = "contacts";
    public const string GetMessageBody = "getMessageBody";
    public const string MessageBody = "messageBody";
    public const string MoveMessage = "moveMessage";
    public const string DeleteMessage = "deleteMessage";
    public const string MarkRead = "markRead";
    public const string MarkReadBatch = "markReadBatch";
    public const string SetFlag = "setFlag";
    public const string SetFlagBatch = "setFlagBatch";
    public const string DownloadAttachment = "downloadAttachment";
    public const string ReadAttachment = "readAttachment";
    public const string AttachmentBytes = "attachmentBytes";
    public const string AttachmentSaved = "attachmentSaved";
    public const string SendMail = "sendMail";
    public const string SendMailResult = "sendMailResult";
    public const string SaveDraft = "saveDraft";
    public const string DraftSaved = "draftSaved";
    public const string GetDraft = "getDraft";
    public const string DraftContent = "draftContent";
    public const string SyncAccount = "syncAccount";
    public const string Ok = "ok";
    public const string Activate = "activate";
    public const string AddRecent = "addRecent";

    public const string GetSettings = "getSettings";
    public const string Settings = "settings";
    public const string UpdateSettings = "updateSettings";

    public const string GetAccountHealth = "getAccountHealth";
    public const string AccountHealth = "accountHealth";
    public const string GetEmlPreview = "getEmlPreview";
    public const string EmlPreview = "emlPreview";

    public const string GetStorageSummary = "getStorageSummary";
    public const string StorageSummary = "storageSummary";
    public const string CleanStorage = "cleanStorage";
    public const string StorageCleanup = "storageCleanup";
    public const string CreateBackup = "createBackup";
    public const string BackupResult = "backupResult";
    public const string RestoreBackup = "restoreBackup";
    public const string RestoreResult = "restoreResult";
    public const string ExportMessage = "exportMessage";
    public const string MessageExport = "messageExport";
    public const string ListOutbox = "listOutbox";
    public const string OutboxList = "outboxList";
    public const string RetryOutbox = "retryOutbox";
    public const string DeleteOutbox = "deleteOutbox";
    public const string GetRules = "getRules";
    public const string RulesList = "rulesList";
    public const string SaveRule = "saveRule";
    public const string DeleteRule = "deleteRule";

    public const string NewMail = "newMail";
    public const string SyncState = "syncState";
    public const string AuthState = "authState";
    public const string ShowMainWindow = "showMainWindow";
    public const string TakePendingNavigation = "takePendingNavigation";
    public const string PendingNavigation = "pendingNavigation";
    public const string AccountChanged = "accountChanged";
    public const string Shutdown = "shutdown";

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
