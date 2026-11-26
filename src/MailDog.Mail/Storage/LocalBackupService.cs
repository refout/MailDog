using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using MailDog.Contracts.Dto;
using MailDog.Contracts.Platform;

namespace MailDog.Mail.Storage;

/// <summary>
/// 本地数据库备份与恢复。备份使用 SQLite 一致性快照；恢复先落到临时文件，
/// 下次 Engine 启动、尚未打开数据库时再替换，避免运行中覆盖正在使用的数据库。
/// 带密码的备份使用 AES-256-GCM 加密整个 ZIP，格式为 .mdog。
/// </summary>
public sealed class LocalBackupService
{
    private const string DatabaseEntryName = "maildog.db";
    private const string RestoreMarkerName = "restore.pending";
    private const string RestoreFileName = "maildog.restore.db";
    private static readonly byte[] EncryptedMagic = "MDLOBK1\0"u8.ToArray();
    private const int SaltBytes = 16;
    private const int NonceBytes = 12;
    private const int TagBytes = 16;
    private const int KeyBytes = 32;
    private const int Pbkdf2Iterations = 120_000;

    private readonly SqliteStore _store;
    private readonly string _databasePath;
    private readonly string _dataDirectory;

    public LocalBackupService(SqliteStore store, string databasePath)
    {
        _store = store;
        _databasePath = Path.GetFullPath(databasePath);
        _dataDirectory = Path.GetDirectoryName(_databasePath)
            ?? ProductPaths.DataDirectory;
    }

    public BackupResultDto CreateBackup(string destinationPath, string? password = null)
    {
        var fullPath = Path.GetFullPath(destinationPath);
        var encrypted = !string.IsNullOrWhiteSpace(password);
        if (encrypted)
        {
            if (!fullPath.EndsWith(".mdog", StringComparison.OrdinalIgnoreCase))
            {
                fullPath += ".mdog";
            }
        }
        else if (!string.Equals(Path.GetExtension(fullPath), ".zip", StringComparison.OrdinalIgnoreCase))
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
        var tempOutput = fullPath + ".tmp-" + Guid.NewGuid().ToString("N");

        try
        {
            var snapshot = Path.Combine(workDirectory, DatabaseEntryName);
            _store.BackupTo(snapshot);

            if (!encrypted)
            {
                ZipFile.CreateFromDirectory(workDirectory, tempOutput, CompressionLevel.Optimal, false);
            }
            else
            {
                // 不要把 ZIP 放进被压缩的目录，避免归档包含自身。
                var zipPath = Path.Combine(
                    Path.GetTempPath(),
                    $"maildog-backup-{Guid.NewGuid():N}.zip");
                try
                {
                    ZipFile.CreateFromDirectory(workDirectory, zipPath, CompressionLevel.Optimal, false);
                    EncryptFile(zipPath, tempOutput, password!);
                }
                finally
                {
                    try
                    {
                        File.Delete(zipPath);
                    }
                    catch
                    {
                        // 临时 ZIP 清理失败不影响备份结果。
                    }
                }
            }

            // 新备份完整落盘后再替换旧文件，避免中途失败把上一份备份弄丢。
            File.Move(tempOutput, fullPath, true);

            return new BackupResultDto
            {
                Path = fullPath,
                Size = new FileInfo(fullPath).Length,
                CreatedUtc = DateTimeOffset.UtcNow,
                Encrypted = encrypted
            };
        }
        finally
        {
            try
            {
                File.Delete(tempOutput);
            }
            catch
            {
                // 临时文件不存在或清理失败不影响备份结果。
            }

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

    public RestoreResultDto StageRestore(string archivePath, string? password = null)
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

        if (IsEncryptedArchive(fullPath))
        {
            if (string.IsNullOrWhiteSpace(password))
            {
                throw new InvalidOperationException("这是加密备份，请输入备份密码。");
            }

            var decrypted = DecryptFile(fullPath, password);
            using var stream = new MemoryStream(decrypted, writable: false);
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
            ExtractDatabase(archive, restorePath);
        }
        else
        {
            using var archive = ZipFile.OpenRead(fullPath);
            ExtractDatabase(archive, restorePath);
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

        File.WriteAllText(Path.Combine(_dataDirectory, RestoreMarkerName), fullPath);
        return new RestoreResultDto
        {
            Staged = true,
            BackupPath = fullPath,
            RestartRequired = true
        };
    }

    private static void ExtractDatabase(ZipArchive archive, string restorePath)
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

    private static bool IsEncryptedArchive(string path)
    {
        using var stream = File.OpenRead(path);
        Span<byte> header = stackalloc byte[EncryptedMagic.Length];
        if (stream.Read(header) < header.Length)
        {
            return false;
        }

        return header.SequenceEqual(EncryptedMagic);
    }

    private static void EncryptFile(string sourcePath, string destinationPath, string password)
    {
        var plaintext = File.ReadAllBytes(sourcePath);
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var nonce = RandomNumberGenerator.GetBytes(NonceBytes);
        var key = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password),
            salt,
            Pbkdf2Iterations,
            HashAlgorithmName.SHA256,
            KeyBytes);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[TagBytes];

        using (var aes = new AesGcm(key, TagBytes))
        {
            aes.Encrypt(nonce, plaintext, ciphertext, tag);
        }

        using var output = File.Create(destinationPath);
        output.Write(EncryptedMagic);
        output.Write(salt);
        output.Write(nonce);
        output.Write(tag);
        output.Write(ciphertext);
    }

    private static byte[] DecryptFile(string path, string password)
    {
        var payload = File.ReadAllBytes(path);
        var headerLength = EncryptedMagic.Length + SaltBytes + NonceBytes + TagBytes;
        if (payload.Length <= headerLength || !IsEncryptedArchive(path))
        {
            throw new InvalidOperationException("加密备份文件已损坏。");
        }

        var salt = payload.AsSpan(EncryptedMagic.Length, SaltBytes).ToArray();
        var nonce = payload.AsSpan(EncryptedMagic.Length + SaltBytes, NonceBytes).ToArray();
        var tag = payload.AsSpan(EncryptedMagic.Length + SaltBytes + NonceBytes, TagBytes).ToArray();
        var ciphertext = payload.AsSpan(headerLength);
        var key = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password),
            salt,
            Pbkdf2Iterations,
            HashAlgorithmName.SHA256,
            KeyBytes);
        var plaintext = new byte[ciphertext.Length];

        try
        {
            using var aes = new AesGcm(key, TagBytes);
            aes.Decrypt(nonce, ciphertext, tag, plaintext);
        }
        catch (CryptographicException)
        {
            throw new InvalidOperationException("备份密码不正确，或备份文件已损坏。");
        }

        return plaintext;
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
