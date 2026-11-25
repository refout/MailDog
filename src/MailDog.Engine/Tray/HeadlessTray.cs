namespace MailDog.Engine.Tray;

/// <summary>
/// Linux 没有 Win32 消息循环，也没有托盘窗口。引擎以无窗口进程常驻，
/// 通知交给桌面通知守护进程（notify-send），托盘入口暂时不做。
/// </summary>
internal sealed class HeadlessTray : IAppTray
{
    private readonly ManualResetEventSlim _exit = new(false);
    private readonly Action _notificationClicked;
    private readonly Action<string> _activated;
    private readonly Action _exitProcess;

    public HeadlessTray(
        Action openUi,
        Action openAbout,
        Action notificationClicked,
        Action<string> activated,
        Action syncAll,
        Action resumed,
        Action exit,
        Func<bool> isDarkTheme)
    {
        _notificationClicked = notificationClicked;
        _activated = activated;
        _exitProcess = exit;
    }

    public void Run(bool hideConsole)
    {
        _exit.Wait();
    }

    public void UpdateTooltip(string tooltip)
    {
        // Linux 版本没有托盘图标，保留接口给后续 StatusNotifierItem。
    }

    public void UpdateUnreadCount(int unreadCount)
    {
        // 同上；未读数由界面展示。
    }

    public void ShowNotification(
        string title,
        string message,
        bool playSound = true,
        string? sound = null,
        string? senderName = null,
        string? senderAddress = null,
        string? preview = null,
        string? activationUri = null,
        string? tag = null)
    {
        var body = string.IsNullOrWhiteSpace(preview) ? message : $"{message}\n{preview}";
        LinuxNotifier.Show(
            title,
            body,
            activationUri,
            uri => _activated(uri),
            () => _notificationClicked());
    }

    public void Quit()
    {
        _exitProcess();
        _exit.Set();
    }

    public void Dispose()
    {
        _exit.Set();
        _exit.Dispose();
    }
}
