using Microsoft.Win32;

namespace MailDog.Engine.Hosting;

/// <summary>把 UI 写入当前用户的 Run 键，Engine 与 UI 同目录分发。</summary>
internal static class StartupRegistration
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "MailDog";

    public static void Apply(bool enabled)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true)
                ?? Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);

            if (key is null)
            {
                return;
            }

            if (!enabled)
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
                return;
            }

            var uiPath = Environment.GetEnvironmentVariable("MAILDOG_UI_PATH");

            if (string.IsNullOrWhiteSpace(uiPath))
            {
                uiPath = Path.Combine(AppContext.BaseDirectory, "MailDog.UI.exe");
            }

            if (!File.Exists(uiPath))
            {
                return;
            }

            key.SetValue(ValueName, $"\"{uiPath}\"", RegistryValueKind.String);
        }
        catch
        {
            // 注册表被策略限制时不影响主流程。
        }
    }
}
