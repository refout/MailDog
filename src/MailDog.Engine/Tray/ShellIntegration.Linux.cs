namespace MailDog.Engine.Tray;

/// <summary>Linux 版本暂不注册 maildog:// 协议；通知点击回落由桌面决定。</summary>
internal static class ShellIntegration
{
    public const string UriPrefix = "maildog://";

    public static void EnsureProtocolRegistered()
    {
    }
}
