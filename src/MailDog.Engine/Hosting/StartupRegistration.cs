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

            var uiPath = ResolveUiPath();

            if (string.IsNullOrWhiteSpace(uiPath))
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

    private static string? ResolveUiPath()
    {
        var configured = Environment.GetEnvironmentVariable("MAILDOG_UI_PATH");

        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
        {
            return configured;
        }

        var coLocated = Path.Combine(AppContext.BaseDirectory, "MailDog.UI.exe");

        if (File.Exists(coLocated))
        {
            return coLocated;
        }

        // 开发时 UI 与 Engine 分属不同输出目录，向上查找同仓库的 UI 构建产物。
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        var configurations = AppContext.BaseDirectory.Contains(
            $"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}",
            StringComparison.OrdinalIgnoreCase)
            ? new[] { "Release", "Debug" }
            : new[] { "Debug", "Release" };

        for (var depth = 0; depth < 6 && directory is not null; depth++, directory = directory.Parent)
        {
            var uiRoot = Path.Combine(directory.FullName, "MailDog.UI", "bin");

            if (Directory.Exists(uiRoot))
            {
                foreach (var configuration in configurations)
                {
                    foreach (var runtime in new[] { "win-x64", "win-arm64" })
                    {
                        var published = Path.Combine(
                            uiRoot,
                            configuration,
                            "net11.0",
                            runtime,
                            "MailDog.UI.exe");

                        if (File.Exists(published))
                        {
                            return published;
                        }
                    }

                    var candidate = Path.Combine(
                        uiRoot,
                        configuration,
                        "net11.0",
                        "MailDog.UI.exe");

                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }
                }
            }

            foreach (var runtime in new[] { "win-x64", "win-arm64" })
            {
                var artifact = Path.Combine(
                    directory.FullName,
                    "artifacts",
                    runtime,
                    "ui",
                    "MailDog.UI.exe");

                if (File.Exists(artifact))
                {
                    return artifact;
                }
            }
        }

        return null;
    }
}
