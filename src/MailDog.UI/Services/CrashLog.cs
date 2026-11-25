using System.Text;
using MailDog.Contracts.Platform;

namespace MailDog.UI.Services;

/// <summary>界面没有控制台时把未处理异常落到数据目录，诊断包会带走。</summary>
internal static class CrashLog
{
    public const string FileName = "crash.log";

    public static void Install()
    {
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
        }
        catch
        {
            // 记日志失败时不再抛出新异常。
        }
    }
}
