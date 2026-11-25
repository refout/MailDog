using System.Security.Cryptography;
using System.Text;

namespace MailDog.Mail.Storage;

/// <summary>
/// 凭据存储。Windows 用 DPAPI（CurrentUser）；Linux/macOS 用本机随机密钥的 AES-GCM，
/// 密钥文件放在数据目录并收紧到 0600，避免把密码明文写进 SQLite。
/// </summary>
public sealed class SecretStore
{
    private const string KeyFileName = "secrets.key";
    private const int KeySize = 32;
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private static readonly byte[] Entropy = SHA256.HashData(Encoding.UTF8.GetBytes("MailDog.SecretStore.v1"));
    private static readonly byte[] BlobMagic = "MDS1"u8.ToArray();

    private readonly SqliteStore _store;
    private readonly Lazy<byte[]> _localKey;

    public SecretStore(SqliteStore store)
    {
        _store = store;
        _localKey = new Lazy<byte[]>(() => LoadOrCreateKey(ResolveKeyDirectory(store)));
    }

    public void Save(string reference, string kind, string plaintext)
    {
        var data = Encoding.UTF8.GetBytes(plaintext);

        try
        {
            var blob = OperatingSystem.IsWindows()
                ? ProtectedData.Protect(data, Entropy, DataProtectionScope.CurrentUser)
                : EncryptLocal(data);
            _store.SaveSecret(reference, kind, blob);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(data);
        }
    }

    public string? TryLoad(string reference, out string kind)
    {
        if (!_store.TryGetSecret(reference, out kind, out var blob))
        {
            return null;
        }

        try
        {
            var data = OperatingSystem.IsWindows()
                ? ProtectedData.Unprotect(blob, Entropy, DataProtectionScope.CurrentUser)
                : DecryptLocal(blob);

            try
            {
                return Encoding.UTF8.GetString(data);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(data);
            }
        }
        catch (CryptographicException)
        {
            return null;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    public void Delete(string reference)
    {
        _store.DeleteSecret(reference);
    }

    private byte[] EncryptLocal(byte[] plaintext)
    {
        var key = _localKey.Value;
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[TagSize];

        using (var aes = new AesGcm(key, TagSize))
        {
            aes.Encrypt(nonce, plaintext, ciphertext, tag);
        }

        var blob = new byte[BlobMagic.Length + NonceSize + TagSize + ciphertext.Length];
        var offset = 0;
        BlobMagic.CopyTo(blob, offset);
        offset += BlobMagic.Length;
        nonce.CopyTo(blob, offset);
        offset += NonceSize;
        tag.CopyTo(blob, offset);
        offset += TagSize;
        ciphertext.CopyTo(blob, offset);
        CryptographicOperations.ZeroMemory(ciphertext);
        return blob;
    }

    private byte[] DecryptLocal(byte[] blob)
    {
        if (blob.Length < BlobMagic.Length + NonceSize + TagSize
            || !blob.AsSpan(0, BlobMagic.Length).SequenceEqual(BlobMagic))
        {
            throw new CryptographicException("凭据格式不正确。");
        }

        var key = _localKey.Value;
        var offset = BlobMagic.Length;
        var nonce = blob.AsSpan(offset, NonceSize);
        offset += NonceSize;
        var tag = blob.AsSpan(offset, TagSize);
        offset += TagSize;
        var ciphertext = blob.AsSpan(offset);
        var plaintext = new byte[ciphertext.Length];

        using (var aes = new AesGcm(key, TagSize))
        {
            aes.Decrypt(nonce, ciphertext, tag, plaintext);
        }

        return plaintext;
    }

    private static string ResolveKeyDirectory(SqliteStore store)
    {
        var directory = Path.GetDirectoryName(store.DatabasePath);

        if (!string.IsNullOrWhiteSpace(directory))
        {
            return directory;
        }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MailDog");
    }

    private static byte[] LoadOrCreateKey(string directory)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, KeyFileName);

        if (File.Exists(path))
        {
            return ReadKey(path);
        }

        var options = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None
        };

        if (!OperatingSystem.IsWindows())
        {
            // 目录里只有当前用户能读到密钥；文件权限比目录权限更直接。
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        try
        {
            var key = RandomNumberGenerator.GetBytes(KeySize);

            using (var stream = new FileStream(path, options))
            {
                stream.Write(key);
            }

            return key;
        }
        catch (IOException) when (File.Exists(path))
        {
            return ReadKey(path);
        }
    }

    private static byte[] ReadKey(string path)
    {
        var key = File.ReadAllBytes(path);

        if (key.Length != KeySize)
        {
            throw new CryptographicException("凭据密钥长度不正确。");
        }

        return key;
    }
}
