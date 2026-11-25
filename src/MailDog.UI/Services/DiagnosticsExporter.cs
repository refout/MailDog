using System.IO.Compression;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using MailDog.Contracts.Dto;
using MailDog.Contracts.Platform;

namespace MailDog.UI.Services;

/// <summary>诊断包只带版本、脱敏配置、账号状态和日志，不碰正文、口令与令牌。</summary>
public static class DiagnosticsExporter
{
    private static readonly Regex EmailPattern = new(
        "[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\\.[A-Za-z]{2,}",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex SecretPattern = new(
        "(?i)(password|passwd|pwd|token|secret|authorization|access_token|refresh_token)(\\s*[:=]\\s*)(\\S+)",
        RegexOptions.Compiled);

    private static readonly Regex BearerPattern = new(
        "(?i)(bearer\\s+)(\\S+)",
        RegexOptions.Compiled);

    public sealed record AccountSnapshot(string Id, string Email, string DisplayName, string StatusText);

    public static string Export(
        string path,
        string appVersion,
        AppSettingsDto settings,
        IReadOnlyList<AccountSnapshot> accounts,
        IReadOnlyList<AccountHealthDto> health,
        string? dataDirectory = null)
    {
        var directory = dataDirectory ?? ProductPaths.DataDirectory;
        var generated = DateTimeOffset.Now;

        using var archive = new ZipArchive(new FileStream(path, FileMode.Create, FileAccess.Write), ZipArchiveMode.Create);

        AddText(archive, "report.txt", BuildReport(appVersion, generated, directory, accounts, health));
        AddText(archive, "settings.json", BuildSettings(settings));

        var logDirectory = Path.Combine(directory, "logs");
        if (Directory.Exists(logDirectory))
        {
            foreach (var log in Directory.EnumerateFiles(logDirectory, "engine-*.log")
                         .OrderByDescending(File.GetLastWriteTimeUtc)
                         .Take(5))
            {
                AddText(archive, $"logs/{Path.GetFileName(log)}", Sanitize(File.ReadAllText(log)));
            }
        }

        var crashLog = Path.Combine(directory, CrashLog.FileName);
        if (File.Exists(crashLog))
        {
            AddText(archive, $"logs/{CrashLog.FileName}", Sanitize(File.ReadAllText(crashLog)));
        }

        return path;
    }

    private static string BuildReport(
        string appVersion,
        DateTimeOffset generated,
        string dataDirectory,
        IReadOnlyList<AccountSnapshot> accounts,
        IReadOnlyList<AccountHealthDto> health)
    {
        var builder = new StringBuilder();
        builder.AppendLine("MailDog 诊断包");
        builder.AppendLine($"生成时间：{generated:yyyy-MM-dd HH:mm:ss zzz}");
        builder.AppendLine($"应用版本：{appVersion}");
        builder.AppendLine($"平台：{ProductPaths.RuntimeIdentifier}");
        builder.AppendLine($"数据目录：{dataDirectory}");
        builder.AppendLine($"账户数量：{accounts.Count}");
        builder.AppendLine();
        builder.AppendLine("说明：邮箱地址、签名、口令与令牌已脱敏；不包含邮件正文和附件。");
        builder.AppendLine();

        var healthById = health.ToDictionary(item => item.AccountId, StringComparer.Ordinal);

        foreach (var account in accounts)
        {
            builder.AppendLine($"账户：{DisplayName(account)} <{MaskEmail(account.Email)}>");
            var item = healthById.GetValueOrDefault(account.Id);

            if (item is null)
            {
                builder.AppendLine($"  状态：{account.StatusText}（后台健康快照不可用）");
                continue;
            }

            builder.AppendLine($"  状态：{StatusText(item.Status)}");
            builder.AppendLine($"  上次同步：{(item.LastSyncUtc is { } sync ? sync.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") : "从未成功")}");
            builder.AppendLine($"  IDLE：{IdleText(item)}");
            builder.AppendLine($"  连续失败：{item.ConsecutiveFailures}");

            if (item.NextRetryUtc is { } retry)
            {
                builder.AppendLine($"  下次重试：{retry.ToLocalTime():yyyy-MM-dd HH:mm:ss}");
            }

            if (item.LastSyncError is { } syncError)
            {
                builder.AppendLine($"  同步错误（{item.LastSyncErrorKind ?? "other"}）：{Sanitize(syncError)}");
            }

            if (item.IdleError is { } idleError)
            {
                builder.AppendLine($"  IDLE 错误：{Sanitize(idleError)}");
            }

            builder.AppendLine();
        }

        return builder.ToString();
    }

    private static string BuildSettings(AppSettingsDto settings)
    {
        var node = new JsonObject
        {
            ["Theme"] = settings.Theme,
            ["Density"] = settings.Density,
            ["Material"] = settings.Material,
            ["BackgroundOpacity"] = settings.BackgroundOpacity,
            ["NotificationsEnabled"] = settings.NotificationsEnabled,
            ["NotificationSoundEnabled"] = settings.NotificationSoundEnabled,
            ["NotificationShowSubject"] = settings.NotificationShowSubject,
            ["NotificationShowPreview"] = settings.NotificationShowPreview,
            ["NotificationMerge"] = settings.NotificationMerge,
            ["NotificationInboxOnly"] = settings.NotificationInboxOnly,
            ["NotificationQuietEnabled"] = settings.NotificationQuietEnabled,
            ["NotificationQuietStart"] = settings.NotificationQuietStart,
            ["NotificationQuietEnd"] = settings.NotificationQuietEnd,
            ["SyncIntervalMinutes"] = settings.SyncIntervalMinutes,
            ["CacheDays"] = settings.CacheDays,
            ["MarkReadOnOpen"] = settings.MarkReadOnOpen,
            ["RunAtStartup"] = settings.RunAtStartup,
            ["ShowRemoteImages"] = settings.ShowRemoteImages,
            ["SaveSentCopy"] = settings.SaveSentCopy,
            ["SendDelaySeconds"] = settings.SendDelaySeconds,
            ["Signature"] = "[已隐藏]"
        };

        return node.ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        });
    }

    private static string Sanitize(string text)
    {
        var value = SecretPattern.Replace(text, "$1$2[已隐藏]");
        value = BearerPattern.Replace(value, "$1[已隐藏]");
        return EmailPattern.Replace(value, "***@***");
    }

    private static string MaskEmail(string email)
    {
        var at = email.IndexOf('@');

        if (at <= 0)
        {
            return "***";
        }

        return $"{email[..1]}***@{email[(at + 1)..]}";
    }

    private static string DisplayName(AccountSnapshot account)
        => string.IsNullOrWhiteSpace(account.DisplayName) ? "未命名账户" : account.DisplayName;

    private static string StatusText(AccountStatus status)
        => status switch
        {
            AccountStatus.AuthRequired => "需要重新登录",
            AccountStatus.Offline => "离线",
            AccountStatus.Error => "同步失败",
            _ => "正常"
        };

    private static string IdleText(AccountHealthDto health)
        => health.IdleConnected
            ? "已连接"
            : health.IdleSupported == false
                ? "服务器不支持"
                : "未连接";

    private static void AddText(ZipArchive archive, string name, string content)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.Optimal);

        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        writer.Write(content);
    }
}
