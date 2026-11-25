using System.Security.Cryptography;
using System.Text;

namespace MailDog.UI.Services;

/// <summary>应用锁的 PIN 派生与校验；设置表只保存盐和 PBKDF2 哈希。</summary>
public static class AppLockService
{
    private const int Iterations = 120_000;
    private const int KeyBytes = 32;

    public static string CreateSalt()
        => Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));

    public static string Hash(string pin, string saltBase64)
    {
        var salt = Convert.FromBase64String(saltBase64);
        var key = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(pin),
            salt,
            Iterations,
            HashAlgorithmName.SHA256,
            KeyBytes);
        return Convert.ToBase64String(key);
    }

    public static bool Verify(string pin, string saltBase64, string expectedHashBase64)
    {
        if (string.IsNullOrEmpty(pin)
            || string.IsNullOrEmpty(saltBase64)
            || string.IsNullOrEmpty(expectedHashBase64))
        {
            return false;
        }

        try
        {
            var actual = Convert.FromBase64String(Hash(pin, saltBase64));
            var expected = Convert.FromBase64String(expectedHashBase64);
            return actual.Length == expected.Length
                   && CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
