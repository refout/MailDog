namespace MailDog.Contracts.Dto;

public enum MailProvider
{
    Outlook,
    QQ,
    Custom
}

public enum FolderRole
{
    Inbox,
    Sent,
    Drafts,
    Trash,
    Junk,
    Archive,
    Other
}

public enum AccountStatus
{
    Ok,
    AuthRequired,
    Offline,
    Error
}

public enum AuthStateKind
{
    Connected,
    Expired,
    Required,
    BrowserOpened,
    Failed
}

public enum SyncStateKind
{
    Idle,
    Syncing,
    Error
}

[Flags]
public enum MailFlags
{
    None = 0,
    Seen = 1,
    Flagged = 2,
    Answered = 4,
    Deleted = 8,
    Draft = 16
}
