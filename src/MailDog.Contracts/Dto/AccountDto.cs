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

    /// <summary>同一批到达的多封邮件合并成一条"有 N 封新邮件"。</summary>
    public bool NotificationMerge { get; init; } = true;

    /// <summary>只给收件箱弹通知，订阅文件夹与自定义文件夹不打扰。</summary>
    public bool NotificationInboxOnly { get; init; } = true;

    /// <summary>免打扰时段：期间只入库不弹窗。</summary>
    public bool NotificationQuietEnabled { get; init; }

    /// <summary>免打扰开始时间，24 小时制 "HH:mm"。</summary>
    public string NotificationQuietStart { get; init; } = "22:00";

    /// <summary>免打扰结束时间，24 小时制 "HH:mm"，可小于开始时间表示跨夜。</summary>
    public string NotificationQuietEnd { get; init; } = "08:00";

    public int SyncIntervalMinutes { get; init; } = 1;

    public int CacheDays { get; init; } = 90;

    public bool MarkReadOnOpen { get; init; } = true;

    public bool RunAtStartup { get; init; }

    public bool ShowRemoteImages { get; init; }

    public bool SaveSentCopy { get; init; } = true;

    /// <summary>纯文本邮件签名，新写、回复和转发时插入正文。</summary>
    public string Signature { get; init; } = string.Empty;

    /// <summary>按账户覆盖的签名；账户没有配置时回退到 <see cref="Signature"/>。</summary>
    public IReadOnlyList<AccountSignatureDto> AccountSignatures { get; init; } = [];

    /// <summary>用户保存的邮件模板；正文支持 {{date}} 等变量。</summary>
    public IReadOnlyList<MailTemplateDto> Templates { get; init; } = [];

    /// <summary>发送前的等待秒数，给出撤销窗口；0 表示立即发送。</summary>
    public int SendDelaySeconds { get; init; } = 10;

    public string RenderMode { get; init; } = "html";
}

public sealed class AccountSignatureDto
{
    public string AccountId { get; init; } = string.Empty;

    public string Signature { get; init; } = string.Empty;
}

public sealed class MailTemplateDto
{
    public string Id { get; init; } = string.Empty;

    public string Name { get; init; } = string.Empty;

    public string Subject { get; init; } = string.Empty;

    public string Body { get; init; } = string.Empty;

    /// <summary>出现在阅读页“快速回复”菜单中，用回复模式直接带出正文。</summary>
    public bool IsQuickReply { get; init; }
}
