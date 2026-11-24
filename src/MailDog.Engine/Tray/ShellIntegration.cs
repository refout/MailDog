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

        var expectedCommand = $"\"{executable}\" {ActivateArgument} \"%1\"";

        try
        {
            // 安装包放到别的目录、或者从构建目录直接跑起来时，注册表里可能还留着旧路径。
            // 命令与当前 exe 不一致就重写，否则点击通知会拉起一个不存在的程序。
            if (!IsRegisteredTo(executable))
            {
                using var command = Registry.CurrentUser.CreateSubKey($@"{ClassesKey}\{Protocol}\shell\open\command");
                command?.SetValue(string.Empty, expectedCommand, RegistryValueKind.String);
            }

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
        }
        catch
        {
            // 注册表受限时点击通知不会有反应，但不影响其它功能。
        }
    }

    /// <summary>注册表里登记的命令是否已经指向给定的可执行文件。</summary>
    public static bool IsRegisteredTo(string executable)
    {
        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(executable))
        {
            return false;
        }

        try
        {
            using var command = Registry.CurrentUser.OpenSubKey($@"{ClassesKey}\{Protocol}\shell\open\command");
            var value = command?.GetValue(string.Empty) as string;

            return !string.IsNullOrWhiteSpace(value)
                && value.Contains(executable, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>卸载或换版时清掉协议登记，避免留下指向旧程序的条目。</summary>
    public static void Unregister()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            Registry.CurrentUser.DeleteSubKeyTree($@"{ClassesKey}\{Protocol}", throwOnMissingSubKey: false);
        }
        catch
        {
            // 没有权限时交给安装包的注册表清理项。
        }
    }
}
