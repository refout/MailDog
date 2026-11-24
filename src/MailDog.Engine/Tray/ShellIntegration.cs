using Microsoft.Win32;

namespace MailDog.Engine.Tray;

/// <summary>
/// 注册 maildog: 协议。toast 用 protocol 方式激活（点击弹窗、或在通知中心点历史），
/// 系统就按这条命令把点击转回本程序，不需要再实现 COM 激活器。
/// </summary>
internal static class ShellIntegration
{
    public const string Protocol = "maildog";
    public const string UriPrefix = "maildog://";

    private const string ClassesKey = @"Software\Classes";
    private const string ActivateArgument = "--activate";

    public static void EnsureProtocolRegistered()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var executable = Environment.ProcessPath;

        if (string.IsNullOrWhiteSpace(executable) || !File.Exists(executable))
        {
            return;
        }

        try
        {
            using var key = Registry.CurrentUser.CreateSubKey($@"{ClassesKey}\{Protocol}");

            if (key is null)
            {
                return;
            }

            key.SetValue(string.Empty, "URL:MailDog 邮件通知协议", RegistryValueKind.String);
            key.SetValue("URL Protocol", string.Empty, RegistryValueKind.String);

            var iconPath = AppIdentity.EnsureIconFile();

            using (var icon = key.CreateSubKey("DefaultIcon"))
            {
                if (icon is not null && !string.IsNullOrWhiteSpace(iconPath))
                {
                    icon.SetValue(string.Empty, $"\"{iconPath}\",0", RegistryValueKind.String);
                }
            }

            using var command = key.CreateSubKey(@"shell\open\command");

            // 交给 Engine 而不是 UI：Engine 常驻，能接过点击再决定是广播给已打开的窗口还是拉起窗口。
            command?.SetValue(
                string.Empty,
                $"\"{executable}\" {ActivateArgument} \"%1\"",
                RegistryValueKind.String);
        }
        catch
        {
            // 注册表受限时点击通知不会有反应，但不影响其它功能。
        }
    }
}
