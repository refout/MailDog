using System.IO.Compression;
using MailDog.Contracts.Dto;

namespace MailDog.Mail.Storage;

/// <summary>
/// 本地数据库备份与恢复。备份使用 SQLite 一致性快照；恢复先落到临时文件，
/// 下次 Engine 启动、尚未打开数据库时再替换，避免运行中覆盖正在使用的数据库。
/// </summary>
public sealed class LocalBackupService
{
    private const string DatabaseEntryName = "maildog.db";
    private const string RestoreMarkerName = "restore.pending";
    private const string RestoreFileName = "maildog.restore.db";

    private readonly SqliteStore _store;
    private readonly string _databasePath;
    private readonly string _dataDirectory;

    public LocalBackupService(SqliteStore store, string databasePath)
    {
        _store = store;
        _databasePath = Path.GetFullPath(databasePath);
        _dataDirectory = Path.GetDirectoryName(_databasePath)
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MailDog");
    }

    public BackupResultDto CreateBackup(string destinationPath)
    {
        var fullPath = Path.GetFullPath(destinationPath);
        if (!string.Equals(Path.GetExtension(fullPath), ".zip", StringComparison.OrdinalIgnoreCase))
        {
            fullPath += ".zip";
        }

        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var workDirectory = Path.Combine(Path.GetTempPath(), "MailDog", $"backup-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workDirectory);

        try
        {
            var snapshot = Path.Combine(workDirectory, DatabaseEntryName);
            _store.BackupTo(snapshot);

            if (File.Exists(fullPath))
            {
                File.Delete(fullPath);
            }

            ZipFile.CreateFromDirectory(workDirectory, fullPath, CompressionLevel.Optimal, false);
            return new BackupResultDto
            {
                Path = fullPath,
                Size = new FileInfo(fullPath).Length,
                CreatedUtc = DateTimeOffset.UtcNow
            };
        }
        finally
        {
            try
            {
                Directory.Delete(workDirectory, true);
            }
            catch
            {
                // 临时目录清理失败不影响备份结果。
            }
        }
    }

    public RestoreResultDto StageRestore(string archivePath)
    {
        var fullPath = Path.GetFullPath(archivePath);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("备份文件不存在。", fullPath);
        }

        var restorePath = Path.Combine(_dataDirectory, RestoreFileName);
        Directory.CreateDirectory(_dataDirectory);
        var markerPath = Path.Combine(_dataDirectory, RestoreMarkerName);
        if (File.Exists(markerPath))
        {
            File.Delete(markerPath);
        }

        using (var archive = ZipFile.OpenRead(fullPath))
        {
            var entry = archive.Entries.FirstOrDefault(item =>
                string.Equals(item.Name, DatabaseEntryName, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException("备份中没有找到 maildog.db。");

            if (File.Exists(restorePath))
            {
                File.Delete(restorePath);
            }

            entry.ExtractToFile(restorePath);
        }

        Span<byte> header = stackalloc byte[16];
        using (var stream = File.OpenRead(restorePath))
        {
            if (stream.Read(header) < header.Length
                || !"SQLite format 3\0"u8.SequenceEqual(header))
            {
                File.Delete(restorePath);
                throw new InvalidOperationException("备份中的数据库文件无效。");
            }
        }

        File.WriteAllText(Path.Combine(_dataDirectory, RestoreMarkerName), fullPath);        return new RestoreResultDto
        {
            Staged = true,
            BackupPath = fullPath,
            RestartRequired = true
        };
    }

    /// <summary>Engine 启动时调用；只有在数据库尚未打开前才允许替换。</summary>
    public static void ApplyPendingRestore(string databasePath)
    {
        var fullDatabasePath = Path.GetFullPath(databasePath);
        var dataDirectory = Path.GetDirectoryName(fullDatabasePath);
        if (string.IsNullOrEmpty(dataDirectory))
        {
            return;
        }

        var markerPath = Path.Combine(dataDirectory, RestoreMarkerName);
        var restorePath = Path.Combine(dataDirectory, RestoreFileName);
        if (!File.Exists(markerPath) || !File.Exists(restorePath))
        {
            return;
        }

        var currentBackup = Path.Combine(
            dataDirectory,
            $"maildog.before-restore-{DateTime.Now:yyyyMMdd-HHmmss}.db");
        if (File.Exists(fullDatabasePath))
        {
            File.Copy(fullDatabasePath, currentBackup, true);
        }

        foreach (var sidecar in new[] { fullDatabasePath + "-wal", fullDatabasePath + "-shm" })
        {
            if (File.Exists(sidecar))
            {
                File.Delete(sidecar);
            }
        }

        File.Move(restorePath, fullDatabasePath, true);
        File.Delete(markerPath);
    }
}
