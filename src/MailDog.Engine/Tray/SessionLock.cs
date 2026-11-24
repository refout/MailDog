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
}
