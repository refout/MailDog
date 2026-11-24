namespace MailDog.Contracts.Dto;

public sealed class AccountDto
{
    public string Id { get; init; } = string.Empty;

    public MailProvider Provider { get; init; }

    public string Email { get; init; } = string.Empty;

    public string DisplayName { get; init; } = string.Empty;

    public AccountStatus Status { get; init; }

    /// <summary>自定义服务器配置；使用服务商默认地址时为 null。</summary>
    public AccountServerDto? Server { get; init; }
}

/// <summary>账户显示顺序与默认发信账户；账户本身仍由账户表管理。</summary>
public sealed class AccountPreferencesDto
{
    public string? DefaultAccountId { get; init; }

    /// <summary>逗号分隔的账户 ID，按用户调整后的顺序排列。</summary>
    public string AccountOrder { get; init; } = string.Empty;
}

public sealed class AccountServerDto
{
    public string ImapHost { get; init; } = string.Empty;

    public int ImapPort { get; init; }

    public bool ImapStartTls { get; init; }

    public string SmtpHost { get; init; } = string.Empty;

    public int SmtpPort { get; init; }

    public bool SmtpStartTls { get; init; }
}

public sealed class FolderDto
{
    public long Id { get; init; }

    public string AccountId { get; init; } = string.Empty;

    public string Name { get; init; } = string.Empty;

    public string FullName { get; init; } = string.Empty;

    public FolderRole Role { get; init; }

    public int UnreadCount { get; init; }

    public int TotalCount { get; init; }
}

public sealed class AppSettingsDto
{
    public string Theme { get; init; } = "system";

    public string Density { get; init; } = "compact";

    public string Material { get; init; } = "mica";

    public string? BackgroundColor { get; init; }

    public int BackgroundOpacity { get; init; } = 85;

    public bool NotificationsEnabled { get; init; } = true;

    public bool NotificationSoundEnabled { get; init; } = true;

    public string NotificationSound { get; init; } = "default";

    /// <summary>通知正文是否显示邮件主题。</summary>
    public bool NotificationShowSubject { get; init; } = true;

    /// <summary>通知是否附带正文摘要（只取开头一小段）。</summary>
    public bool NotificationShowPreview { get; init; }

    public int SyncIntervalMinutes { get; init; } = 1;

    public int CacheDays { get; init; } = 90;

    public bool MarkReadOnOpen { get; init; } = true;

    public bool RunAtStartup { get; init; }

    public bool ShowRemoteImages { get; init; }

    public bool SaveSentCopy { get; init; } = true;

    /// <summary>纯文本邮件签名，新写、回复和转发时插入正文。</summary>
    public string Signature { get; init; } = string.Empty;

    public string RenderMode { get; init; } = "html";
}
