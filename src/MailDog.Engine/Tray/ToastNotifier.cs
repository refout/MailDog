using System.Security;
using System.Text;
using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;

namespace MailDog.Engine.Tray;

/// <summary>
/// 走 Windows 正式通知（ToastGeneric）。托盘气泡只是"临时弹窗"：不进通知中心、
/// 头部也没有应用图标；Toast 才会被存进通知中心，且头部图标取自应用标识的 IconUri。
/// </summary>
internal static class ToastNotifier
{
    private const string MailSound = "ms-winsoundevent:Notification.Mail";
    private const string NotificationGroup = "mail";

    /// <summary>发一条邮件通知；失败返回 false，由调用方退回托盘气泡。</summary>
    public static bool TryShow(
        string title,
        string body,
        string? preview,
        string? attribution,
        string? avatarPath,
        bool playSound,
        string? tag,
        string? activationUri,
        string? sound = null,
        IReadOnlyList<NotificationAction>? actions = null,
        string? appId = null)
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        try
        {
            var xml = BuildPayload(title, body, preview, attribution, avatarPath, playSound, activationUri, sound, actions);

            var document = new XmlDocument();
            document.LoadXml(xml);

            var toast = new ToastNotification(document)
            {
                Group = NotificationGroup
            };

            if (!string.IsNullOrWhiteSpace(tag))
            {
                toast.Tag = TruncateTag(tag);
            }

            ToastNotificationManager
                .CreateToastNotifier(appId ?? AppIdentity.AppUserModelId)
                .Show(toast);

            return true;
        }
        catch
        {
            // 通知被策略禁用、AUMID 尚未登记到通知系统等情况都会抛异常，交回气泡。
            return false;
        }
    }

    private static string BuildPayload(
        string title,
        string body,
        string? preview,
        string? attribution,
        string? avatarPath,
        bool playSound,
        string? activationUri,
        string? sound,
        IReadOnlyList<NotificationAction>? actions)
    {
        var builder = new StringBuilder(640);

        // 用 protocol 激活：点击弹窗或通知中心里的历史，都会按 maildog: 协议把地址交回本程序。
        if (!string.IsNullOrWhiteSpace(activationUri))
        {
            builder.Append("<toast activationType=\"protocol\" launch=\"")
                .Append(Escape(activationUri))
                .Append("\" duration=\"short\">");
        }
        else
        {
            builder.Append("<toast activationType=\"foreground\" duration=\"short\">");
        }

        builder.Append("<visual><binding template=\"ToastGeneric\">");
        builder.Append("<text>").Append(Escape(title)).Append("</text>");
        builder.Append("<text>").Append(Escape(body)).Append("</text>");

        if (!string.IsNullOrWhiteSpace(preview))
        {
            builder.Append("<text>").Append(Escape(preview)).Append("</text>");
        }

        if (!string.IsNullOrWhiteSpace(attribution))
        {
            builder.Append("<text placement=\"attribution\">")
                .Append(Escape(attribution))
                .Append("</text>");
        }

        if (!string.IsNullOrWhiteSpace(avatarPath) && File.Exists(avatarPath))
        {
            builder.Append("<image placement=\"appLogoOverride\" hint-crop=\"circle\" src=\"")
                .Append(Escape(ToFileUri(avatarPath)))
                .Append("\" alt=\"发件人头像\"/>");
        }

        builder.Append("</binding></visual>");

        if (actions is { Count: > 0 })
        {
            builder.Append("<actions>");
            foreach (var action in actions)
            {
                builder.Append("<action content=\"")
                    .Append(Escape(action.Label))
                    .Append("\" arguments=\"")
                    .Append(Escape(action.Uri))
                    .Append("\" activationType=\"protocol\"/>");
            }

            builder.Append("</actions>");
        }

        if (playSound && !string.Equals(sound, "none", StringComparison.OrdinalIgnoreCase))
        {
            builder.Append("<audio src=\"").Append(ResolveSoundEvent(sound)).Append("\" loop=\"false\"/>");
        }
        else
        {
            builder.Append("<audio silent=\"true\"/>");
        }

        builder.Append("</toast>");
        return builder.ToString();
    }

    private static string ResolveSoundEvent(string? sound)
        => sound switch
        {
            "default" => "ms-winsoundevent:Notification.Default",
            "reminder" => "ms-winsoundevent:Notification.Reminder",
            "asterisk" => "ms-winsoundevent:Notification.IM",
            "warning" => "ms-winsoundevent:Notification.Reminder",
            "beep" => "ms-winsoundevent:Notification.Default",
            "question" => "ms-winsoundevent:Notification.Reminder",
            "start" => "ms-winsoundevent:Notification.Default",
            _ => MailSound
        };

    private static string ToFileUri(string path)
        => new Uri(path).AbsoluteUri;

    /// <summary>Tag 上限 64 个字符，且只允许邮件地址与服务端标签用到的字符。</summary>
    private static string TruncateTag(string tag)
    {
        var builder = new StringBuilder(64);

        foreach (var character in tag)
        {
            if (builder.Length == 64)
            {
                break;
            }

            builder.Append(char.IsLetterOrDigit(character) || character is '.' or '_' or '-' or '@' or '+' or ':'
                ? character
                : '_');
        }

        return builder.Length == 0 ? "mail" : builder.ToString();
    }

    private static string Escape(string value)
        => SecurityElement.Escape(value) ?? string.Empty;
}
