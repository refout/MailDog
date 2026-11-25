namespace MailDog.Engine.Tray;

/// <summary>Linux 暂不注册 AppUserModelID；入口保留给通知实现复用。</summary>
internal static class AppIdentity
{
    public const string AppUserModelId = "MailDog.MailDog.App";

    public static void ApplyToProcess()
    {
    }

    public static void EnsureRegistered(string displayName)
    {
    }

    public static string? EnsureIconFile() => null;
}
