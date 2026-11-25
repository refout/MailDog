using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace MailDog.Mail.Storage;

/// <summary>
/// Linux 桌面钥匙串的可选接入：有 secret-tool 且 Secret Service 可用时，把 AES 主密钥存进系统钥匙串；
/// 没有 DBus 会话、钥匙串锁定或命令缺失时返回 false，由调用方回退到数据目录里的 0600 密钥文件。
/// </summary>
internal static class LinuxKeyring
{
    private const string Attribute = "application";
    private const string Service = "MailDog";
    private const string DatabaseAttribute = "database";
    private const string PurposeAttribute = "purpose";
    private const string Purpose = "secret-store-key";

    public static bool TryLoad(string databasePath, out byte[] key)
    {
        key = [];

        try
        {
            using var process = Start(
                ["lookup", Attribute, Service, DatabaseAttribute, Scope(databasePath), PurposeAttribute, Purpose],
                redirectInput: false);

            if (process is null || !Wait(process) || process.ExitCode != 0)
            {
                return false;
            }

            var text = process.StandardOutput.ReadToEnd().Trim();
            var decoded = Convert.FromBase64String(text);

            if (decoded.Length != 32)
            {
                return false;
            }

            key = decoded;
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static bool TryStore(string databasePath, byte[] key)
    {
        try
        {
            // 同一组 attribute 重复 store 会留下多条记录，先清掉旧的再写。
            using (var clear = Start(
                       ["clear", Attribute, Service, DatabaseAttribute, Scope(databasePath), PurposeAttribute, Purpose],
                       redirectInput: false))
            {
                if (clear is not null)
                {
                    Wait(clear);
                }
            }

            using var process = Start(
                [
                    "store",
                    "--label=MailDog 凭据密钥",
                    Attribute, Service,
                    DatabaseAttribute, Scope(databasePath),
                    PurposeAttribute, Purpose
                ],
                redirectInput: true);

            if (process is null)
            {
                return false;
            }

            process.StandardInput.Write(Convert.ToBase64String(key));
            process.StandardInput.Close();

            return Wait(process) && process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>钥匙串条目按数据库路径隔离，避免同一用户多个 MailDog 库共用一把密钥。</summary>
    private static string Scope(string databasePath)
    {
        var normalized = Path.GetFullPath(databasePath);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized))).ToLowerInvariant();
    }

    private static Process? Start(string[] arguments, bool redirectInput)
    {
        var startInfo = new ProcessStartInfo("secret-tool")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = redirectInput,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        return Process.Start(startInfo);
    }

    private static bool Wait(Process process)
    {
        if (process.WaitForExit(3000))
        {
            return true;
        }

        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch
        {
            // 已经退出或没有权限，忽略。
        }

        return false;
    }
}
