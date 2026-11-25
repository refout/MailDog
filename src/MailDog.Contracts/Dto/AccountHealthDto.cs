namespace MailDog.Contracts.Dto;

/// <summary>账号健康快照：只用于界面展示，不含凭据、正文和服务器口令。</summary>
public sealed class AccountHealthDto
{
    public string AccountId { get; init; } = string.Empty;

    public string Email { get; init; } = string.Empty;

    public string DisplayName { get; init; } = string.Empty;

    public AccountStatus Status { get; init; }

    /// <summary>最近一次成功同步的时间；从未成功时为 null。</summary>
    public DateTimeOffset? LastSyncUtc { get; init; }

    /// <summary>IDLE 连接当前是否在线。</summary>
    public bool IdleConnected { get; init; }

    /// <summary>服务器是否声明支持 IDLE；尚未连上时为 null。</summary>
    public bool? IdleSupported { get; init; }

    public DateTimeOffset? IdleSinceUtc { get; init; }

    /// <summary>IDLE 最近一次断开的原因，连上后清空。</summary>
    public string? IdleError { get; init; }

    /// <summary>同步错误分类：authentication / network / timeout / protocol / other。</summary>
    public string? LastSyncErrorKind { get; init; }

    public string? LastSyncError { get; init; }

    public int ConsecutiveFailures { get; init; }

    public DateTimeOffset? NextRetryUtc { get; init; }
}

/// <summary>外部 .eml 的只读预览；附件只给名称和大小，不回传文件内容。</summary>
public sealed class EmlPreviewDto
{
    public string FileName { get; init; } = string.Empty;

    public string Subject { get; init; } = string.Empty;

    public string FromName { get; init; } = string.Empty;

    public string FromAddress { get; init; } = string.Empty;

    public string ToAddresses { get; init; } = string.Empty;

    public string CcAddresses { get; init; } = string.Empty;

    public DateTimeOffset Date { get; init; }

    public string PlainText { get; init; } = string.Empty;

    public bool Truncated { get; init; }

    public string[] AttachmentNames { get; init; } = [];

    public string? Error { get; init; }
}
