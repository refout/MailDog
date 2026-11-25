namespace MailDog.Contracts.Dto;

/// <summary>本地数据目录、缓存占用与清理结果。</summary>
public sealed class StorageSummaryDto
{
    public string DataDirectory { get; init; } = string.Empty;

    public string DatabasePath { get; init; } = string.Empty;

    public string LogDirectory { get; init; } = string.Empty;

    public string AttachmentCacheDirectory { get; init; } = string.Empty;

    public string OutboxDirectory { get; init; } = string.Empty;

    public string NativeDirectory { get; init; } = string.Empty;

    public string TempDirectory { get; init; } = string.Empty;

    public long DatabaseBytes { get; init; }

    public long LogBytes { get; init; }

    public long AttachmentCacheBytes { get; init; }

    public long OutboxBytes { get; init; }

    public long NativeBytes { get; init; }

    public long TempBytes { get; init; }

    public long TotalBytes { get; init; }

    public int MessageCount { get; init; }

    public int BodyCount { get; init; }

    public int PendingOutboxCount { get; init; }
}

public sealed class StorageCleanupResultDto
{
    public long FreedBytes { get; init; }

    public int SkippedFiles { get; init; }

    public string Message { get; init; } = string.Empty;
}

public sealed class MailboxImportResultDto
{
    public int Imported { get; init; }

    public int Skipped { get; init; }

    public string[] Errors { get; init; } = [];

    public string Message { get; init; } = string.Empty;
}

public sealed class MailboxExportResultDto
{
    public string Path { get; init; } = string.Empty;

    public long Size { get; init; }

    public int Messages { get; init; }

    public string Message { get; init; } = string.Empty;
}

public sealed class BackupResultDto
{
    public string Path { get; init; } = string.Empty;

    public long Size { get; init; }

    public DateTimeOffset CreatedUtc { get; init; }

    public bool Encrypted { get; init; }
}

public sealed class RestoreResultDto
{
    public bool Staged { get; init; }

    public string BackupPath { get; init; } = string.Empty;

    public bool RestartRequired { get; init; }
}
