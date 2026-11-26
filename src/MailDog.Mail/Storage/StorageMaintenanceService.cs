using MailDog.Contracts.Dto;
using MailDog.Contracts.Messages;
using MailDog.Contracts.Platform;

namespace MailDog.Mail.Storage;

public sealed record StorageMaintenancePaths(
    string DataDirectory,
    string DatabasePath,
    string LogDirectory,
    string AttachmentCacheDirectory,
    string OutboxDirectory,
    string NativeDirectory,
    string TempDirectory);

/// <summary>
/// 本地数据目录统计与安全清理。只清理可再生缓存，不删除数据库、草稿、旗标邮件或待发送记录。
/// </summary>
public sealed class StorageMaintenanceService
{
    private readonly SqliteStore _store;
    private readonly string _databasePath;
    private readonly string _dataDirectory;
    private readonly string _logDirectory;
    private readonly string _attachmentCacheDirectory;
    private readonly string _outboxDirectory;
    private readonly string _nativeDirectory;
    private readonly string _tempDirectory;

    public StorageMaintenanceService(SqliteStore store, string databasePath)
        : this(store, CreateDefaultPaths(databasePath))
    {
    }

    public StorageMaintenanceService(SqliteStore store, StorageMaintenancePaths paths)
    {
        _store = store;
        _databasePath = Path.GetFullPath(paths.DatabasePath);
        _dataDirectory = paths.DataDirectory;
        _logDirectory = paths.LogDirectory;
        _attachmentCacheDirectory = paths.AttachmentCacheDirectory;
        _outboxDirectory = paths.OutboxDirectory;
        _nativeDirectory = paths.NativeDirectory;
        _tempDirectory = paths.TempDirectory;
    }

    private static StorageMaintenancePaths CreateDefaultPaths(string databasePath)
    {
        var fullDatabasePath = Path.GetFullPath(databasePath);
        var dataDirectory = Path.GetDirectoryName(fullDatabasePath)
            ?? ProductPaths.DataDirectory;

        return new StorageMaintenancePaths(
            dataDirectory,
            fullDatabasePath,
            Path.Combine(ProductPaths.DataDirectory, "logs"),
            Path.Combine(ProductPaths.DataDirectory, "attachments"),
            Path.Combine(dataDirectory, "outbox"),
            Path.Combine(ProductPaths.DataDirectory, "native"),
            Path.Combine(Path.GetTempPath(), "MailDog"));
    }

    public StorageSummaryDto GetSummary()
    {
        var counts = _store.GetStorageCounts();
        var databaseBytes = DatabaseBytes();
        var logBytes = DirectoryBytes(_logDirectory);
        var attachmentBytes = DirectoryBytes(_attachmentCacheDirectory);
        var outboxBytes = DirectoryBytes(_outboxDirectory);
        var nativeBytes = DirectoryBytes(_nativeDirectory);
        var tempBytes = DirectoryBytes(_tempDirectory);

        return new StorageSummaryDto
        {
            DataDirectory = _dataDirectory,
            DatabasePath = _databasePath,
            LogDirectory = _logDirectory,
            AttachmentCacheDirectory = _attachmentCacheDirectory,
            OutboxDirectory = _outboxDirectory,
            NativeDirectory = _nativeDirectory,
            TempDirectory = _tempDirectory,
            DatabaseBytes = databaseBytes,
            LogBytes = logBytes,
            AttachmentCacheBytes = attachmentBytes,
            OutboxBytes = outboxBytes,
            NativeBytes = nativeBytes,
            TempBytes = tempBytes,
            TotalBytes = databaseBytes + logBytes + attachmentBytes + outboxBytes + nativeBytes + tempBytes,
            MessageCount = counts.Messages,
            BodyCount = counts.Bodies,
            PendingOutboxCount = counts.PendingOutbox
        };
    }

    public StorageCleanupResultDto Clean(CleanStorageRequest request)
    {
        var result = new CleanupAccumulator();
        var parts = new List<string>();

        if (request.CleanAttachmentCache)
        {
            CleanDirectory(_attachmentCacheDirectory, result);
            parts.Add("附件缓存");
        }

        if (request.CleanLogs)
        {
            CleanLogs(result);
            parts.Add("旧日志");
        }

        if (request.CleanTempFiles)
        {
            CleanDirectory(_tempDirectory, result);
            parts.Add("临时文件");
        }

        if (request.CompactDatabase)
        {
            var before = DatabaseBytes();
            _store.Compact();
            var after = DatabaseBytes();
            result.FreedBytes += Math.Max(0, before - after);
            parts.Add("数据库压缩");
        }

        return new StorageCleanupResultDto
        {
            FreedBytes = result.FreedBytes,
            SkippedFiles = result.SkippedFiles,
            Message = parts.Count == 0
                ? "没有选择要清理的内容。"
                : $"已清理{string.Join("、", parts)}。"
        };
    }

    private void CleanLogs(CleanupAccumulator result)
    {
        if (!Directory.Exists(_logDirectory))
        {
            return;
        }

        var currentLog = $"engine-{DateTimeOffset.Now:yyyyMMdd}.log";

        foreach (var file in Directory.EnumerateFiles(_logDirectory, "*.log", SearchOption.TopDirectoryOnly))
        {
            if (string.Equals(Path.GetFileName(file), currentLog, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            DeleteFile(file, result);
        }
    }

    private static void CleanDirectory(string root, CleanupAccumulator result)
    {
        if (!Directory.Exists(root))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            DeleteFile(file, result);
        }

        foreach (var directory in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories)
                     .OrderByDescending(path => path.Length))
        {
            try
            {
                if (!Directory.EnumerateFileSystemEntries(directory).Any())
                {
                    Directory.Delete(directory);
                }
            }
            catch
            {
                result.SkippedFiles++;
            }
        }
    }

    private static void DeleteFile(string path, CleanupAccumulator result)
    {
        long length;

        try
        {
            length = new FileInfo(path).Length;
        }
        catch
        {
            result.SkippedFiles++;
            return;
        }

        try
        {
            File.Delete(path);
            result.FreedBytes += length;
        }
        catch
        {
            result.SkippedFiles++;
        }
    }

    private long DatabaseBytes()
        => FileBytes(_databasePath)
           + FileBytes(_databasePath + "-wal")
           + FileBytes(_databasePath + "-shm");

    private static long FileBytes(string path)
    {
        try
        {
            return File.Exists(path) ? new FileInfo(path).Length : 0;
        }
        catch
        {
            return 0;
        }
    }

    private static long DirectoryBytes(string path)
    {
        if (!Directory.Exists(path))
        {
            return 0;
        }

        long total = 0;

        try
        {
            foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                try
                {
                    total += new FileInfo(file).Length;
                }
                catch
                {
                    // 单个文件读取失败不影响整体统计。
                }
            }
        }
        catch
        {
            // 目录在统计过程中被删除或不可访问时返回已统计部分。
        }

        return total;
    }

    private sealed class CleanupAccumulator
    {
        public long FreedBytes { get; set; }

        public int SkippedFiles { get; set; }
    }
}
