using MailDog.Contracts.Platform;

namespace MailDog.Engine.Hosting;

/// <summary>Linux 用 XDG autostart 的 .desktop 文件实现“登录时启动”。</summary>
internal static class StartupRegistration
{
    private const string FileName = "maildog.desktop";

    public static void Apply(bool enabled)
    {
        try
        {
            var path = GetDesktopFilePath();

            if (!enabled)
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }

                return;
            }

            var uiPath = Path.Combine(AppContext.BaseDirectory, ProductPaths.UiExecutableName);

            if (!File.Exists(uiPath))
            {
                Console.Error.WriteLine($"开启开机启动失败：找不到 {uiPath}");
                return;
            }

            var directory = Path.GetDirectoryName(path);

            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var content = $"""
                [Desktop Entry]
                Type=Application
                Name=MailDog
                Comment=MailDog 邮件客户端
                Exec={DesktopQuote(uiPath)}
                Terminal=false
                StartupNotify=false
                X-GNOME-Autostart-enabled=true
                X-GNOME-Autostart-Delay=5

                """;

            File.WriteAllText(path, content);

            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(
                    path,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
            }
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"更新开机启动失败：{exception.Message}");
        }
    }

    private static string GetDesktopFilePath()
    {
        var configHome = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");

        if (string.IsNullOrWhiteSpace(configHome))
        {
            configHome = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".config");
        }

        return Path.Combine(configHome, "autostart", FileName);
    }

    private static string DesktopQuote(string value)
        => "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
}
