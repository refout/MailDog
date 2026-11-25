using System.Text;
using MailDog.Contracts.Platform;

namespace MailDog.Cli;

/// <summary>
/// 本地调用记录。cli-usage.log 保留轻量摘要；cli-calls.jsonl 保存结构化调用，
/// 带 --json 时把当时的 JSON 输出一并留下，方便在界面里回看。
/// 记录失败不会影响命令本身；用户可用 MAILDOG_NO_CLI_AUDIT=1 关闭。
/// </summary>
internal static class CliAudit
{
    private const long MaxUsageFileBytes = 512 * 1024;
    private const long MaxCallsFileBytes = 2 * 1024 * 1024;

    public static void Record(
        string? command,
        int exitCode,
        TimeSpan elapsed,
        string? jsonOutput = null,
        bool jsonRequested = false,
        bool jsonTruncated = false)
    {
        if (IsDisabled())
        {
            return;
        }

        try
        {
            var directory = Path.Combine(ProductPaths.DataDirectory, "logs");
            Directory.CreateDirectory(directory);

            var timestamp = DateTimeOffset.Now;

            var usagePath = Path.Combine(directory, "cli-usage.log");
            Rotate(usagePath, MaxUsageFileBytes);
            Append(
                usagePath,
                FormatLine(timestamp, Program.Version, command, exitCode, elapsed)
                + Environment.NewLine);

            var callsPath = Path.Combine(directory, "cli-calls.jsonl");
            Rotate(callsPath, MaxCallsFileBytes);
            Append(
                callsPath,
                FormatRecord(timestamp, Program.Version, command, exitCode, elapsed, jsonOutput, jsonRequested, jsonTruncated)
                + Environment.NewLine);
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

    internal static string FormatRecord(
        DateTimeOffset timestamp,
        string version,
        string? command,
        int exitCode,
        TimeSpan elapsed,
        string? jsonOutput,
        bool jsonRequested,
        bool jsonTruncated)
    {
        var normalized = NormalizeCommand(command);

        return new StringBuilder(256 + (jsonOutput?.Length ?? 0))
            .Append("{\"time\":").Append(Output.Str(Output.Iso(timestamp)))
            .Append(",\"version\":").Append(Output.Str(version))
            .Append(",\"command\":").Append(Output.Str(normalized))
            .Append(",\"exit\":").Append(Output.Num(exitCode))
            .Append(",\"ms\":").Append(Output.Num((long)elapsed.TotalMilliseconds))
            .Append(",\"jsonRequested\":").Append(Output.Bool(jsonRequested))
            .Append(",\"jsonTruncated\":").Append(Output.Bool(jsonTruncated))
            .Append(",\"json\":").Append(Output.Str(jsonOutput))
            .Append('}')
            .ToString();
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

    private static void Append(string path, string content)
    {
        using var stream = new FileStream(
            path,
            FileMode.Append,
            FileAccess.Write,
            FileShare.ReadWrite);

        stream.Write(Encoding.UTF8.GetBytes(content));
    }

    private static void Rotate(string path, long maxBytes)
    {
        if (!File.Exists(path))
        {
            return;
        }

        try
        {
            if (new FileInfo(path).Length >= maxBytes)
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
