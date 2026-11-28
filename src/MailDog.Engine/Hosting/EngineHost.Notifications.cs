using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Sockets;
using MailDog.Contracts.Dto;
using MailDog.Contracts.Ipc;
using MailDog.Contracts.Messages;
using MailDog.Contracts.Platform;
using MailDog.Engine.Ipc;
using MailDog.Engine.Logging;
using MailDog.Engine.Mime;
using MailDog.Engine.Tray;
using MailDog.Mail.Accounts;
using MailDog.Mail.Auth;
using MailDog.Mail.Imap;
using MailDog.Mail.Smtp;
using MailDog.Mail.Storage;
using MailKit.Net.Imap;
using MailKit.Security;

namespace MailDog.Engine.Hosting;

public sealed partial class EngineHost
{
    private void QueueNotification(MailSummaryDto latest, string accountId, long folderId, AppSettingsDto settings, int count)
    {
        if (settings.NotificationInboxOnly && _store.GetFolder(folderId)?.Role != FolderRole.Inbox)
        {
            // 订阅文件夹、自定义文件夹的新邮件只入库，不打扰。
            return;
        }

        if (NotificationPolicy.IsQuietTime(settings, DateTimeOffset.Now))
        {
            _log.Info($"免打扰时段，跳过通知：{latest.Subject}");
            return;
        }

        lock (_notificationGate)
        {
            _pendingNotifications.Add(new NewMailBatchItem(latest, accountId, folderId, Math.Max(count, 1)));

            if (_notificationBatchStart == DateTimeOffset.MinValue)
            {
                _notificationBatchStart = DateTimeOffset.Now;
            }

            _notificationTimer ??= new Timer(_ => FlushNotifications(), null, Timeout.Infinite, Timeout.Infinite);

            // 攒够窗口就立刻弹，否则再等一小会儿，让同批邮件合并成一条。
            var waited = DateTimeOffset.Now - _notificationBatchStart;
            var delay = waited >= NotificationBatchWindow ? 0 : NotificationBatchDelayMs;
            _notificationTimer.Change(delay, Timeout.Infinite);
        }
    }

    private void FlushNotifications()
    {
        List<NewMailBatchItem> batch;

        lock (_notificationGate)
        {
            if (_pendingNotifications.Count == 0)
            {
                return;
            }

            batch = [.. _pendingNotifications];
            _pendingNotifications.Clear();
            _notificationBatchStart = DateTimeOffset.MinValue;
        }

        var settings = _store.GetSettings();

        if (!settings.NotificationsEnabled)
        {
            // 攒的过程中用户把通知关掉了。
            return;
        }

        if (NotificationPolicy.IsPrivacyMode())
        {
            var first = batch[0];
            ShowNewMailNotification(first.Summary, first.AccountId, settings, privacy: true);
            return;
        }

        if (settings.NotificationMerge && batch.Count > 1)
        {
            var total = batch.Sum(item => item.Count);
            ShowMergedNotification(batch, total, settings);
            return;
        }

        foreach (var item in batch)
        {
            ShowNewMailNotification(item.Summary, item.AccountId, settings, privacy: false);
        }
    }

    /// <summary>通知正文按设置拼：主题与正文摘要都可以单独关掉。</summary>
    private void ShowNewMailNotification(MailSummaryDto latest, string accountId, AppSettingsDto settings, bool privacy)
    {
        var subject = string.IsNullOrWhiteSpace(latest.Subject) ? "（无主题）" : Collapse(latest.Subject);
        var preview = Truncate(Collapse(latest.Preview), 90);
        var sender = string.IsNullOrWhiteSpace(latest.FromName) ? latest.FromAddress : latest.FromName;

        string title;
        string body;
        string? extraLine = null;

        if (privacy)
        {
            // 锁屏时把主题、摘要、发件人都收起来，只留一句"有新邮件"。
            title = "MailDog";
            body = "收到一封新邮件";
        }
        else if (!settings.NotificationShowSubject)
        {
            // 不显示主题时至少给一句能看懂的话，避免通知看起来是空的。
            title = string.IsNullOrWhiteSpace(sender) ? "新邮件" : sender;
            body = settings.NotificationShowPreview && preview.Length > 0 ? preview : "收到一封新邮件";
        }
        else
        {
            title = string.IsNullOrWhiteSpace(sender) ? "新邮件" : sender;
            body = subject;

            if (settings.NotificationShowPreview && preview.Length > 0)
            {
                extraLine = preview;
            }
        }

        var sound = ResolveNotificationSound(settings, accountId);
        var playSound = settings.NotificationSoundEnabled
            && !string.Equals(sound, "none", StringComparison.OrdinalIgnoreCase);

        _tray?.ShowNotification(
            title,
            body,
            playSound,
            sound,
            privacy ? null : latest.FromName,
            privacy ? null : latest.FromAddress,
            privacy ? null : extraLine,
            BuildActivationUri(latest.Id, accountId, latest.FolderId),
            actions: privacy ? null : BuildNotificationActions(latest, accountId));
    }

    /// <summary>同一批到达的多封邮件合并成一条，只显示数量与最新一封。</summary>
    private void ShowMergedNotification(IReadOnlyList<NewMailBatchItem> batch, int total, AppSettingsDto settings)
    {
        var newest = batch[^1];
        var subject = string.IsNullOrWhiteSpace(newest.Summary.Subject) ? "（无主题）" : Collapse(newest.Summary.Subject);
        var sender = string.IsNullOrWhiteSpace(newest.Summary.FromName)
            ? newest.Summary.FromAddress
            : newest.Summary.FromName;

        var title = $"收到 {total} 封新邮件";
        var body = settings.NotificationShowSubject
            ? $"{Collapse(sender)}：{subject}"
            : "打开 MailDog 查看收件箱";

        var sound = ResolveNotificationSound(settings, newest.AccountId);
        var playSound = settings.NotificationSoundEnabled
            && !string.Equals(sound, "none", StringComparison.OrdinalIgnoreCase);

        _tray?.ShowNotification(
            title,
            body,
            playSound,
            sound,
            null,
            null,
            null,
            BuildActivationUri(newest.Summary.Id, newest.AccountId, newest.Summary.FolderId),
            tag: "digest");
    }

    private static string BuildActivationUri(long messageId, string accountId, long folderId)
        => $"{ShellIntegration.UriPrefix}mail?m={messageId}&a={Uri.EscapeDataString(accountId)}&f={folderId}";

    private static string ResolveNotificationSound(AppSettingsDto settings, string accountId)
    {
        var perAccount = settings.AccountNotificationSounds
            .FirstOrDefault(item => string.Equals(item.AccountId, accountId, StringComparison.Ordinal))
            ?.Sound;

        return string.IsNullOrWhiteSpace(perAccount) ? settings.NotificationSound : perAccount;
    }

    private IReadOnlyList<NotificationAction>? BuildNotificationActions(
        MailSummaryDto latest,
        string accountId)
    {
        var actions = new List<NotificationAction>(2);

        if (!latest.Flags.HasFlag(MailFlags.Seen))
        {
            actions.Add(new NotificationAction(
                "read",
                "标记已读",
                BuildActionUri("read", latest.Id, accountId, latest.FolderId)));
        }

        var archive = _store.GetFolders(accountId).FirstOrDefault(folder =>
            folder.Role == FolderRole.Archive && folder.Id != latest.FolderId);
        if (archive is not null)
        {
            actions.Add(new NotificationAction(
                "archive",
                "归档",
                BuildActionUri("archive", latest.Id, accountId, latest.FolderId)));
        }

        return actions.Count == 0 ? null : actions;
    }

    private static string BuildActionUri(string action, long messageId, string accountId, long folderId)
        => $"{ShellIntegration.UriPrefix}action?do={action}&m={messageId}&a={Uri.EscapeDataString(accountId)}&f={folderId}";

    private static string Collapse(string value)
        => string.Join(' ', value.Split(['\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries)).Trim();

    private static string Truncate(string value, int length)
        => value.Length <= length ? value : value[..length].TrimEnd() + "…";

}
