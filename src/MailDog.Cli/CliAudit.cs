using System.Text;
using MailDog.Contracts.Platform;

namespace MailDog.Cli;

/// <summary>
/// 本地调用记录：只记命令名、退出码和耗时，不落参数、输出、邮箱或正文。
/// 记录失败不会影响命令本身；用户可用 MAILDOG_NO_CLI_AUDIT=1 关闭。
/// </summary>
internal static class CliAudit
{
    private const long MaxFileBytes = 512 * 1024;

    public static void Record(string? command, int exitCode, TimeSpan elapsed)
    {
        if (IsDisabled())
        {
            return;
        }

        try
        {
            var directory = Path.Combine(ProductPaths.DataDirectory, "logs");
            Directory.CreateDirectory(directory);

            var path = Path.Combine(directory, "cli-usage.log");
            Rotate(path);

            var line = FormatLine(DateTimeOffset.Now, Program.Version, command, exitCode, elapsed)
                + Environment.NewLine;

            using var stream = new FileStream(
                path,
                FileMode.Append,
                FileAccess.Write,
                FileShare.ReadWrite);

            stream.Write(Encoding.UTF8.GetBytes(line));
        }
        catch
        {
            // 审计日志不能反过来影响 CLI 的退出码。
        }
    }

    internal static string FormatLine(
        DateTimeOffset timestamp,
        string version,
        string? command,
        int exitCode,
        TimeSpan elapsed)
    {
        return $"{timestamp:yyyy-MM-dd HH:mm:ss zzz}\t{version}\t{NormalizeCommand(command)}\texit={exitCode}\t{elapsed.TotalMilliseconds:0}ms";
    }

    private static string NormalizeCommand(string? command)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return "(未知)";
        }

        var builder = new StringBuilder(command.Length);

        foreach (var character in command)
        {
            if (builder.Length >= 32)
            {
                break;
            }

            if (char.IsAsciiLetterOrDigit(character) || character is '-' or '_')
            {
                builder.Append(character);
            }
        }

        return builder.Length == 0 ? "(未知)" : builder.ToString();
    }

    private static bool IsDisabled()
    {
        var value = Environment.GetEnvironmentVariable("MAILDOG_NO_CLI_AUDIT");
        return value is "1" or "true" or "TRUE";
    }

    private static void Rotate(string path)
    {
        if (!File.Exists(path))
        {
            return;
        }

        try
        {
            if (new FileInfo(path).Length >= MaxFileBytes)
            {
                File.Move(path, path + ".1", true);
            }
        }
        catch
        {
            // 并发写时由另一个进程先轮转也没关系；这里失败就继续追加。
        }
    }
}
