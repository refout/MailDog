using MailDog.Contracts.Dto;

namespace MailDog.Engine.Tray;

/// <summary>
/// 通知的"什么时候弹"由这里决定：免打扰时段、只通知收件箱、锁屏降级。
/// 是否弹、弹什么内容分开判断，便于排查"为什么没弹"。
/// </summary>
internal static class NotificationPolicy
{
    /// <summary>命中免打扰时段则不弹窗，邮件照常入库。</summary>
    public static bool IsQuietTime(AppSettingsDto settings, DateTimeOffset now)
    {
        if (!settings.NotificationQuietEnabled)
        {
            return false;
        }

        var start = ParseClock(settings.NotificationQuietStart, 22 * 60);
        var end = ParseClock(settings.NotificationQuietEnd, 8 * 60);

        if (start == end)
        {
            return true;
        }

        var current = now.Hour * 60 + now.Minute;

        return start < end
            ? current >= start && current < end
            : current >= start || current < end;
    }

    /// <summary>锁屏时只保留"收到新邮件"这句，主题、摘要、发件人都收起。</summary>
    public static bool IsPrivacyMode() => SessionLock.IsLocked();

    private static int ParseClock(string? value, int fallbackMinutes)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallbackMinutes;
        }

        var parts = value.Trim().Split(':');

        if (parts.Length != 2
            || !int.TryParse(parts[0], out var hour)
            || !int.TryParse(parts[1], out var minute)
            || hour is < 0 or > 23
            || minute is < 0 or > 59)
        {
            return fallbackMinutes;
        }

        return hour * 60 + minute;
    }
}
