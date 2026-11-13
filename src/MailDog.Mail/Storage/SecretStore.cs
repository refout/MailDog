using System.Security.Cryptography;
using System.Text;

namespace MailDog.Mail.Storage;

/// <summary>Windows DPAPI（CurrentUser）保护的凭据存储。</summary>
public sealed class SecretStore
{
    private static readonly byte[] Entropy = SHA256.HashData(Encoding.UTF8.GetBytes("MailDog.SecretStore.v1"));

    private readonly SqliteStore _store;

    public SecretStore(SqliteStore store)
    {
        _store = store;
    }

    public void Save(string reference, string kind, string plaintext)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("v1 的凭据存储仅支持 Windows DPAPI。");
        }

        var data = Encoding.UTF8.GetBytes(plaintext);
        var blob = ProtectedData.Protect(data, Entropy, DataProtectionScope.CurrentUser);
        _store.SaveSecret(reference, kind, blob);
        CryptographicOperations.ZeroMemory(data);
    }

    public string? TryLoad(string reference, out string kind)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("v1 的凭据存储仅支持 Windows DPAPI。");
        }

        if (!_store.TryGetSecret(reference, out kind, out var blob))
        {
            return null;
        }

        try
        {
            var data = ProtectedData.Unprotect(blob, Entropy, DataProtectionScope.CurrentUser);
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
    }

    public void Delete(string reference)
    {
        _store.DeleteSecret(reference);
    }
}
