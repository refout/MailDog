using System.Diagnostics;
using System.Runtime.InteropServices;

namespace MailDog.Engine.Tray;

/// <summary>
/// 判断当前会话是否在锁屏/安全桌面上。锁屏时通知不该把主题和正文摘要摆在屏幕上，
/// 所以只留一句"收到新邮件"。
/// </summary>
internal static class SessionLock
{
    private const uint DesktopSwitchDesktop = 0x0100;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr OpenInputDesktop(uint flags, bool inherit, uint access);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool CloseDesktop(IntPtr desktop);

    /// <summary>
    /// 能打开输入桌面说明用户正对着自己的桌面；打不开通常是已锁屏。
    /// 取不到状态时按"未锁屏"处理，宁可多显示也不误吞通知。
    /// </summary>
    public static bool IsLocked()
    {
        if (OperatingSystem.IsLinux())
        {
            return IsLinuxLocked();
        }

        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        var desktop = OpenInputDesktop(0, false, DesktopSwitchDesktop);

        if (desktop == IntPtr.Zero)
        {
            return true;
        }

        CloseDesktop(desktop);
        return false;
    }

    /// <summary>
    /// Linux 没有统一的锁屏 API；依次问 GNOME、KDE 的 ScreenSaver 服务。
    /// 查询失败按“未锁屏”处理，和 Windows 一样宁可多显示也不误吞通知。
    /// </summary>
    private static bool IsLinuxLocked()
    {
        return QueryScreenSaver("org.gnome.ScreenSaver", "/org/gnome/ScreenSaver", "org.gnome.ScreenSaver.GetActive")
            ?? QueryScreenSaver("org.freedesktop.ScreenSaver", "/org/freedesktop/ScreenSaver", "org.freedesktop.ScreenSaver.GetActive")
            ?? QueryScreenSaver("org.freedesktop.ScreenSaver", "/ScreenSaver", "org.freedesktop.ScreenSaver.GetActive")
            ?? false;
    }

    private static bool? QueryScreenSaver(string service, string path, string method)
    {
        try
        {
            var startInfo = new ProcessStartInfo("gdbus")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            startInfo.ArgumentList.Add("call");
            startInfo.ArgumentList.Add("--session");
            startInfo.ArgumentList.Add("--dest");
            startInfo.ArgumentList.Add(service);
            startInfo.ArgumentList.Add("--object-path");
            startInfo.ArgumentList.Add(path);
            startInfo.ArgumentList.Add("--method");
            startInfo.ArgumentList.Add(method);

            using var process = Process.Start(startInfo);

            if (process is null || !process.WaitForExit(1500))
            {
                try
                {
                    process?.Kill(entireProcessTree: true);
                }
                catch
                {
                    // 进程已经退出。
                }

                return null;
            }

            var output = process.StandardOutput.ReadToEnd();

            if (output.Contains("(true", StringComparison.Ordinal))
            {
                return true;
            }

            if (output.Contains("(false", StringComparison.Ordinal))
            {
                return false;
            }

            return null;
        }
        catch
        {
            return null;
        }
    }
}
