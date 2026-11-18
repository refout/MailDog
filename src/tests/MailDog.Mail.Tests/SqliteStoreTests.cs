using MailDog.Contracts.Dto;
using MailDog.Mail.Storage;

namespace MailDog.Mail.Tests;

[Collection("Sqlite")]
public sealed class SqliteStoreTests : IDisposable
{
    private readonly string _directory;
    private readonly string _databasePath;
    private readonly SqliteStore _store;

    public SqliteStoreTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "maildog-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        _databasePath = Path.Combine(_directory, "test.db");
        _store = new SqliteStore(_databasePath);
    }

    [Fact]
    public void SettingsRoundTrip()
    {
        var settings = new AppSettingsDto
        {
            Theme = "dark",
            Material = "acrylic",
            BackgroundColor = "#26282B",
            BackgroundOpacity = 72,
            NotificationsEnabled = false,
            NotificationSoundEnabled = false,
            NotificationSound = "mail",
            SyncIntervalMinutes = 15,
            CacheDays = 30,
            MarkReadOnOpen = false,
            ShowRemoteImages = true,
            RunAtStartup = true,
            RenderMode = "plain"
        };

        _store.SaveSettings(settings);
        var loaded = _store.GetSettings();

        Assert.Equal("dark", loaded.Theme);
        Assert.Equal("acrylic", loaded.Material);
        Assert.Equal("#26282B", loaded.BackgroundColor);
        Assert.Equal(72, loaded.BackgroundOpacity);
        Assert.False(loaded.NotificationsEnabled);
        Assert.False(loaded.NotificationSoundEnabled);
        Assert.Equal("mail", loaded.NotificationSound);
        Assert.Equal(15, loaded.SyncIntervalMinutes);
        Assert.Equal(30, loaded.CacheDays);
        Assert.False(loaded.MarkReadOnOpen);
        Assert.True(loaded.ShowRemoteImages);
        Assert.True(loaded.RunAtStartup);
        Assert.Equal("plain", loaded.RenderMode);
    }

    [Fact]
    public void MessagesPagingFlagsAndBody()
    {
        var account = CreateAccount();
        _store.UpsertAccount(account);

        _store.UpsertFolders(account.Id,
        [
            new FolderRecord(0, account.Id, "INBOX", "收件箱", FolderRole.Inbox, 7, 0, 0, 0)
        ]);

        var folder = Assert.Single(_store.GetFolders(account.Id));
        var now = DateTimeOffset.UtcNow;

        var records = Enumerable.Range(1, 60)
            .Select(index => new MessageRecord(
                0,
                account.Id,
                folder.Id,
                index,
                $"message-{index}",
                $"主题 {index}",
                "Alice",
                "alice@example.com",
                "me@example.com",
                string.Empty,
                now.AddMinutes(index),
                1024,
                MailFlags.None,
                index % 2 == 0,
                string.Empty,
                BodyStates.NotFetched))
            .ToArray();

        var result = _store.UpsertMessages(account.Id, folder.Id, records);
        Assert.Equal(60, result.Count);
        Assert.NotNull(result.Latest);

        var firstPage = _store.QueryMessages(folder.Id, null, null, 50);
        Assert.Equal(50, firstPage.Items.Length);
        Assert.True(firstPage.HasMore);

        var secondPage = _store.QueryMessages(folder.Id, firstPage.NextDate, firstPage.NextId, 50);
        Assert.Equal(10, secondPage.Items.Length);
        Assert.False(secondPage.HasMore);

        var message = firstPage.Items[0];
        _store.UpdateSeen(message.Id, true);
        _store.UpdateFlagged(message.Id, true);

        var updated = Assert.Single(_store.QueryMessages(folder.Id, null, null, 1).Items);
        Assert.True((updated.Flags & MailFlags.Seen) != 0);
        Assert.True((updated.Flags & MailFlags.Flagged) != 0);
        Assert.Equal(59, _store.GetFolders(account.Id)[0].UnreadCount);

        _store.SaveBody(
            message.Id,
            "<p>Hello</p>",
            "Hello world",
            truncated: false,
            [new AttachmentRecord("a.txt", "text/plain", 12, null, false)]);

        var body = _store.GetBody(message.Id);
        Assert.NotNull(body);
        Assert.Equal("<p>Hello</p>", body.Html);
        Assert.Equal("Hello world", body.PlainText);
        Assert.Single(body.Attachments);
        Assert.Equal("Hello world", _store.GetMessage(message.Id)!.Preview);
    }

    [Fact]
    public void UpdateFlagManyUpdatesAllMessagesAndUnreadCount()
    {
        var account = CreateAccount();
        _store.UpsertAccount(account);

        _store.UpsertFolders(account.Id,
        [
            new FolderRecord(0, account.Id, "INBOX", "收件箱", FolderRole.Inbox, 7, 0, 0, 0)
        ]);

        var folder = Assert.Single(_store.GetFolders(account.Id));
        var now = DateTimeOffset.UtcNow;

        _store.UpsertMessages(account.Id, folder.Id,
        [
            new MessageRecord(0, account.Id, folder.Id, 1, "m1", "一", "A", "a@example.com",
                string.Empty, string.Empty, now, 10, MailFlags.None, false, string.Empty, BodyStates.NotFetched),
            new MessageRecord(0, account.Id, folder.Id, 2, "m2", "二", "B", "b@example.com",
                string.Empty, string.Empty, now.AddMinutes(1), 10, MailFlags.None, false, string.Empty, BodyStates.NotFetched),
            new MessageRecord(0, account.Id, folder.Id, 3, "m3", "三", "C", "c@example.com",
                string.Empty, string.Empty, now.AddMinutes(2), 10, MailFlags.None, false, string.Empty, BodyStates.NotFetched)
        ]);

        var ids = _store.QueryMessages(folder.Id, null, null, 10).Items.Select(item => item.Id).ToArray();
        Assert.Equal(3, ids.Length);
        Assert.Equal(3, _store.GetFolders(account.Id)[0].UnreadCount);

        _store.UpdateFlagMany(ids, MailFlags.Seen, true);

        var afterRead = _store.QueryMessages(folder.Id, null, null, 10).Items;
        Assert.All(afterRead, item => Assert.True((item.Flags & MailFlags.Seen) != 0));
        Assert.Equal(0, _store.GetFolders(account.Id)[0].UnreadCount);

        _store.UpdateFlagMany(ids, MailFlags.Flagged, true);
        Assert.All(
            _store.QueryMessages(folder.Id, null, null, 10).Items,
            item => Assert.True((item.Flags & MailFlags.Flagged) != 0));

        _store.UpdateFlagMany(ids, MailFlags.Seen, false);
        Assert.Equal(3, _store.GetFolders(account.Id)[0].UnreadCount);
    }

    [Fact]
    public void UpsertMessagesDropsLegacyInvalidUidRows()
    {
        var account = CreateAccount();
        _store.UpsertAccount(account);

        _store.UpsertFolders(account.Id,
        [
            new FolderRecord(0, account.Id, "INBOX", "收件箱", FolderRole.Inbox, 7, 0, 0, 0)
        ]);

        var folder = Assert.Single(_store.GetFolders(account.Id));
        var now = DateTimeOffset.UtcNow;

        _store.UpsertMessages(account.Id, folder.Id,
        [
            new MessageRecord(0, account.Id, folder.Id, 0, null, "旧数据", string.Empty, string.Empty,
                string.Empty, string.Empty, now, 10, MailFlags.None, false, string.Empty, BodyStates.NotFetched)
        ]);

        Assert.Single(_store.QueryMessages(folder.Id, null, null, 10).Items);

        _store.UpsertMessages(account.Id, folder.Id,
        [
            new MessageRecord(0, account.Id, folder.Id, 42, null, "新数据", string.Empty, string.Empty,
                string.Empty, string.Empty, now, 10, MailFlags.None, false, string.Empty, BodyStates.NotFetched)
        ]);

        var message = Assert.Single(_store.QueryMessages(folder.Id, null, null, 10).Items);
        Assert.Equal("新数据", message.Subject);
    }

    [Fact]
    public void SecretStoreRoundTripsOnWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var secrets = new SecretStore(_store);
        secrets.Save("test:secret", "imap_password", "auth-code");

        var value = secrets.TryLoad("test:secret", out var kind);
        Assert.Equal("auth-code", value);
        Assert.Equal("imap_password", kind);

        secrets.Delete("test:secret");
        Assert.Null(secrets.TryLoad("test:secret", out _));
    }

    [Fact]
    public void FolderUpsertKeepsUidValidity()
    {
        var account = CreateAccount();
        _store.UpsertAccount(account);

        _store.UpsertFolders(account.Id,
        [
            new FolderRecord(0, account.Id, "INBOX", "收件箱", FolderRole.Inbox, 9, 40, 3, 10)
        ]);

        _store.UpsertFolders(account.Id,
        [
            new FolderRecord(0, account.Id, "INBOX", "Inbox", FolderRole.Inbox, 11, 0, 5, 20)
        ]);

        var folder = Assert.Single(_store.GetFolders(account.Id));
        Assert.Equal(9, folder.UidValidity);
        Assert.Equal(5, folder.UnreadCount);
        Assert.Equal(20, folder.TotalCount);
        Assert.Equal("Inbox", folder.DisplayName);
    }

    [Fact]
    public void OutboxRoundTripAttemptAndRecovery()
    {
        var account = CreateAccount();
        _store.UpsertAccount(account);

        var now = DateTimeOffset.UtcNow;
        var record = new OutboxRecord(
            0,
            account.Id,
            "to@example.com",
            "cc@example.com",
            string.Empty,
            "主题",
            "正文",
            "<original@example.com>",
            "<outbox@example.com>",
            OutboxStatuses.Pending,
            0,
            now,
            null,
            now,
            null,
            [],
            IsMarkdown: true);

        var id = _store.InsertOutbox(record,
        [
            new OutboxAttachmentRecord(0, "a.txt", "C:\\tmp\\a.txt", "text/plain", 10)
        ]);

        var loaded = Assert.IsType<OutboxRecord>(_store.GetOutbox(id));
        Assert.Equal("to@example.com", loaded.ToAddresses);
        Assert.Equal("<outbox@example.com>", loaded.MessageId);
        Assert.True(loaded.IsMarkdown);
        Assert.Single(loaded.Attachments);

        var due = _store.GetDueOutbox(now.AddSeconds(1), 5);
        Assert.Single(due);

        Assert.Equal(1, _store.BeginOutboxAttempt(id, now.AddMinutes(1)));
        Assert.Equal(OutboxStatuses.Sending, _store.GetOutbox(id)!.Status);

        Assert.Equal(1, _store.RecoverInterruptedOutbox());
        Assert.Equal(OutboxStatuses.Pending, _store.GetOutbox(id)!.Status);

        _store.CompleteOutbox(id, now.AddMinutes(2));
        Assert.Equal(OutboxStatuses.Sent, _store.GetOutbox(id)!.Status);
        Assert.NotNull(_store.GetOutbox(id)!.SentUtc);

        Assert.Empty(_store.GetDueOutbox(now.AddMinutes(10), 5));
    }

    [Fact]
    public void AccountServerRoundTrip()
    {
        var account = CreateAccount();
        _store.UpsertAccount(account);

        _store.UpsertAccountServer(new AccountServerRecord(
            account.Id,
            "imap.example.com",
            143,
            true,
            "smtp.example.com",
            587,
            true));

        var server = Assert.IsType<AccountServerRecord>(_store.GetAccountServer(account.Id));
        Assert.Equal("imap.example.com", server.ImapHost);
        Assert.Equal(143, server.ImapPort);
        Assert.True(server.ImapStartTls);
        Assert.Equal("smtp.example.com", server.SmtpHost);
        Assert.Equal(587, server.SmtpPort);
        Assert.True(server.SmtpStartTls);
    }

    [Fact]
    public void PruneMessagesKeepsFlaggedAndRecentMail()
    {
        var account = CreateAccount();
        _store.UpsertAccount(account);
        _store.UpsertFolders(account.Id,
        [
            new FolderRecord(0, account.Id, "INBOX", "收件箱", FolderRole.Inbox, 0, 0, 0, 0)
        ]);

        var folder = Assert.Single(_store.GetFolders(account.Id));
        var now = DateTimeOffset.UtcNow;
        var old = now.AddDays(-120);

        _store.UpsertMessages(account.Id, folder.Id,
        [
            new MessageRecord(0, account.Id, folder.Id, 1, "old", "旧邮件", "A", "a@example.com",
                "me@example.com", string.Empty, old, 1, MailFlags.None, false, string.Empty, BodyStates.NotFetched),
            new MessageRecord(0, account.Id, folder.Id, 2, "flagged", "已加旗标", "A", "a@example.com",
                "me@example.com", string.Empty, old, 1, MailFlags.Flagged, false, string.Empty, BodyStates.NotFetched),
            new MessageRecord(0, account.Id, folder.Id, 3, "new", "新邮件", "A", "a@example.com",
                "me@example.com", string.Empty, now, 1, MailFlags.None, false, string.Empty, BodyStates.NotFetched)
        ]);

        Assert.Equal(1, _store.PruneMessages(90));

        var remaining = _store.QueryMessages(folder.Id, null, null, 10).Items;
        Assert.Equal(2, remaining.Length);
        Assert.DoesNotContain(remaining, message => message.Subject == "旧邮件");
        Assert.Contains(remaining, message => message.Subject == "已加旗标");
        Assert.Contains(remaining, message => message.Subject == "新邮件");
    }

    [Fact]
    public void PendingFlagsSupersedeAndRetry()
    {
        var now = DateTimeOffset.UtcNow;

        _store.QueuePendingFlag(42, "Seen", true, now);
        _store.QueuePendingFlag(42, "Seen", false, now);

        var first = Assert.Single(_store.GetDuePendingFlags(now.AddSeconds(1), 10));
        Assert.Equal("Seen", first.Flag);
        Assert.False(first.Value);
        Assert.Equal(0, first.Attempts);

        _store.FailPendingFlag(first.Id, now.AddMinutes(5), "网络错误");

        Assert.Empty(_store.GetDuePendingFlags(now.AddMinutes(1), 10));

        var retry = Assert.Single(_store.GetDuePendingFlags(now.AddMinutes(6), 10));
        Assert.Equal(1, retry.Attempts);
        Assert.Equal("网络错误", retry.LastError);

        _store.CompletePendingFlag(retry.Id);
        Assert.Empty(_store.GetDuePendingFlags(now.AddMinutes(10), 10));
    }

    private static AccountRecord CreateAccount()
        => new(
            "account-1",
            MailProvider.QQ,
            "me@qq.com",
            "我",
            "password",
            null,
            DateTimeOffset.UtcNow,
            null,
            AccountStatus.Ok,
            null);

    public void Dispose()
    {
        _store.Dispose();

        foreach (var file in Directory.EnumerateFiles(_directory))
        {
            try
            {
                File.Delete(file);
            }
            catch
            {
                // 测试清理失败忽略。
            }
        }

        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch
        {
            // 测试清理失败忽略。
        }
    }
}
