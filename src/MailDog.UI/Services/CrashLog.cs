using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using MailDog.Contracts.Platform;

namespace MailDog.UI.Services;

internal sealed record CrashReportPayload(
    [property: JsonPropertyName("app")] string App,
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("time")] string Time,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("exception")] string Exception,
    [property: JsonPropertyName("stack")] string? Stack);

[JsonSerializable(typeof(CrashReportPayload))]
internal partial class CrashReportJsonContext : JsonSerializerContext
{
}

/// <summary>界面没有控制台时把未处理异常落到数据目录；配置了端点时再尝试上报。</summary>
internal static class CrashLog
{
    public const string FileName = "crash.log";

    private static readonly HttpClient UploadClient = new()
    {
        Timeout = TimeSpan.FromSeconds(10)
    };

    private static readonly CrashReportUploader Uploader = new(
        UploadClient,
        Path.Combine(ProductPaths.DataDirectory, CrashReportUploader.PendingDirectoryName));

    public static void Install()
    {
        var endpoint = ResolveEndpoint();
        if (endpoint is not null)
        {
            _ = Uploader.FlushPendingAsync(endpoint);
        }

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception exception)
            {
                Write("未处理异常", exception);
            }
        };

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Write("未观察的任务异常", args.Exception);
        };
    }

    /// <summary>环境变量 MAILDOG_CRASH_ENDPOINT 优先，其次数据目录的 crash-endpoint.txt。</summary>
    private static Uri? ResolveEndpoint()
        => CrashReportUploader.ResolveEndpoint(
            Environment.GetEnvironmentVariable("MAILDOG_CRASH_ENDPOINT"),
            ProductPaths.DataDirectory);

    private static void Write(string title, Exception exception)
    {
        try
        {
            var directory = ProductPaths.DataDirectory;
            Directory.CreateDirectory(directory);

            var text = new StringBuilder()
                .AppendLine($"{DateTimeOffset.Now:O} {title}")
                .AppendLine(exception.ToString())
                .AppendLine()
                .ToString();

            File.AppendAllText(Path.Combine(directory, FileName), text);

            var endpoint = ResolveEndpoint();
            if (endpoint is not null)
            {
                _ = Uploader.CaptureAsync(endpoint, BuildPayload(title, exception));
            }
        }
        catch
        {
            // 记日志失败时不再抛出新异常。
        }
    }

    /// <summary>只上报异常类型和堆栈，不上报异常消息，避免把邮件地址或正文带出去。</summary>
    private static string BuildPayload(string title, Exception exception)
        => JsonSerializer.Serialize(
            new CrashReportPayload(
                "MailDog",
                typeof(CrashLog).Assembly.GetName().Version?.ToString() ?? "unknown",
                DateTimeOffset.UtcNow.ToString("O"),
                title,
                exception.GetType().FullName ?? "unknown",
                exception.StackTrace),
            CrashReportJsonContext.Default.CrashReportPayload);
}
