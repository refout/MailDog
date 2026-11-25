namespace MailDog.Engine.Tray;

/// <summary>引擎侧托盘与通知的跨平台接口。Windows 走 Win32 托盘 + Toast，Linux 走通知守护进程。</summary>
public interface IAppTray : IDisposable
{
    void Run(bool hideConsole);

    void UpdateTooltip(string tooltip);

    void UpdateUnreadCount(int unreadCount);

    void ShowNotification(
        string title,
        string message,
        bool playSound = true,
        string? sound = null,
        string? senderName = null,
        string? senderAddress = null,
        string? preview = null,
        string? activationUri = null,
        string? tag = null);

    void Quit();
}
