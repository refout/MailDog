namespace MailDog.Engine.Tray;

/// <summary>按目标平台选择托盘实现。Windows 与 Linux 的实现文件分别只在各自目标框架中编译。</summary>
internal static class PlatformTray
{
    public static IAppTray Create(
        Action openUi,
        Action openAbout,
        Action notificationClicked,
        Action<string> activated,
        Action syncAll,
        Action resumed,
        Action exit,
        Func<bool> isDarkTheme)
    {
#if MAILDOG_WINDOWS
        return new Win32Tray(
            openUi,
            openAbout,
            notificationClicked,
            activated,
            syncAll,
            resumed,
            exit,
            isDarkTheme);
#else
        return new HeadlessTray(
            openUi,
            openAbout,
            notificationClicked,
            activated,
            syncAll,
            resumed,
            exit,
            isDarkTheme);
#endif
    }
}
