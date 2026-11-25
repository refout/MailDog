using System.Diagnostics;

namespace MailDog.Engine.Tray;

/// <summary>用桌面自带的 notify-send 发通知；没有该命令时静默降级，不影响收信。</summary>
internal static class LinuxNotifier
{
    private static int _missingWarned;

    public static void Show(string title, string message)
    {
        try
        {
            var startInfo = new ProcessStartInfo("notify-send")
            {
                UseShellExecute = false,
                CreateNoWindow = true
            };

            startInfo.ArgumentList.Add("--app-name=MailDog");
            startInfo.ArgumentList.Add("--expire-time=8000");
            startInfo.ArgumentList.Add(title);
            startInfo.ArgumentList.Add(message);

            using var process = Process.Start(startInfo);
        }
        catch (Exception exception)
        {
            if (Interlocked.Exchange(ref _missingWarned, 1) == 0)
            {
                Console.Error.WriteLine($"无法发送 Linux 桌面通知：{exception.Message}");
            }
        }
    }
}
