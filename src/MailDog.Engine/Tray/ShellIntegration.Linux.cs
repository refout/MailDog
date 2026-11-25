using System.Diagnostics;

namespace MailDog.Engine.Tray;

/// <summary>
/// Linux 用用户级 .desktop 文件登记 maildog://。
/// 托盘通知的点击由进程内回调处理，协议注册用于外部链接和通知中心重放。
/// </summary>
internal static class ShellIntegration
{
    public const string UriPrefix = "maildog://";

    private const string Scheme = "x-scheme-handler/maildog";
    private const string DesktopFileName = "maildog-handler.desktop";

    public static void EnsureProtocolRegistered()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var executable = Environment.ProcessPath;

        if (string.IsNullOrWhiteSpace(executable) || !File.Exists(executable))
        {
            return;
        }

        var applications = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "applications");
        Directory.CreateDirectory(applications);

        var desktopPath = Path.Combine(applications, DesktopFileName);
        var command = executable.Contains(' ')
            ? $"\"{executable}\" --activate %u"
            : $"{executable} --activate %u";

        var content = $"""
            [Desktop Entry]
            Type=Application
            Name=MailDog 通知链接
            Comment=打开 MailDog 并定位到邮件
            Exec={command}
            Terminal=false
            NoDisplay=true
            Categories=Network;Email;
            MimeType={Scheme};
            """;

        try
        {
            if (!File.Exists(desktopPath) || File.ReadAllText(desktopPath) != content)
            {
                File.WriteAllText(desktopPath, content);
            }

            TryRun("update-desktop-database", applications);
            TryRun("xdg-mime", "default", DesktopFileName, Scheme);
        }
        catch
        {
            // 桌面数据库不可写时不影响收信；只是外部链接不会拉起应用。
        }
    }

    private static void TryRun(string fileName, params string[] arguments)
    {
        try
        {
            var startInfo = new ProcessStartInfo(fileName)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            foreach (var argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            using var process = Process.Start(startInfo);

            if (process is null)
            {
                return;
            }

            if (!process.WaitForExit(2000))
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // 对应工具没装时忽略。
        }
    }
}
