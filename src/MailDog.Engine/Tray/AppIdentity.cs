using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace MailDog.Engine.Tray;

/// <summary>
/// 注册显式 AppUserModelID。不注册时 Windows 会按 exe 路径推断应用标识，
/// 通知标题栏的小图标就可能一直沿用首次登记时的旧图；显式注册后标识与图标都随本次构建刷新。
/// </summary>
internal static class AppIdentity
{
    public const string AppUserModelId = "MailDog.MailDog.App";

    private const string AppModelKey = @"Software\Classes\AppUserModelId";
    private const string IconResourceName = "maildog.ico";

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SetCurrentProcessExplicitAppUserModelID(string appUserModelId);

    /// <summary>只把当前进程归到这个应用标识（界面进程用，避免任务栏分组散开）。</summary>
    public static void ApplyToProcess()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            SetCurrentProcessExplicitAppUserModelID(AppUserModelId);
        }
        catch
        {
            // 失败时沿用系统推断的标识。
        }
    }

    /// <summary>写入 HKCU 的应用标识与图标，通知标题栏的小图标从这里取。</summary>
    public static void EnsureRegistered(string displayName)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        ApplyToProcess();

        var iconPath = EnsureIconFile();

        try
        {
            using var key = Registry.CurrentUser.CreateSubKey($@"{AppModelKey}\{AppUserModelId}");

            if (key is null)
            {
                return;
            }

            key.SetValue("DisplayName", displayName, RegistryValueKind.String);

            if (!string.IsNullOrWhiteSpace(iconPath) && File.Exists(iconPath))
            {
                // 指到 exe 时通知系统只认第一个图标资源，且常被缓存成旧图；
                // 落到一个固定的 .ico 文件路径，图标随构建更新就能立刻生效。
                key.SetValue("IconUri", iconPath, RegistryValueKind.String);
                key.SetValue("IconBackgroundColor", "0", RegistryValueKind.String);
            }

            key.SetValue("ShowInSettings", 1, RegistryValueKind.DWord);
        }
        catch
        {
            // 注册表被策略限制时退回系统默认图标。
        }
    }

    /// <summary>把内嵌的 ico 写到本地固定位置，供应用标识与通知系统读取。</summary>
    public static string? EnsureIconFile()
    {
        try
        {
            var directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "MailDog");

            Directory.CreateDirectory(directory);

            var path = Path.Combine(directory, "maildog.ico");

            using var resource = typeof(AppIdentity).Assembly.GetManifestResourceStream(IconResourceName);

            if (resource is null)
            {
                return File.Exists(path) ? path : null;
            }

            using var file = File.Create(path);
            resource.CopyTo(file);

            return path;
        }
        catch
        {
            return null;
        }
    }
}
