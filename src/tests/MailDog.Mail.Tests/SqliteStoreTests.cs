using MailDog.Contracts.Dto;
using MailDog.Mail.Storage;

namespace MailDog.Mail.Tests;

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
            SyncIntervalMinutes = 15,
            CacheDays = 30,
            MarkReadOnOpen = false,
            ShowRemoteImages = true,
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
        Assert.Equal(15, loaded.SyncIntervalMinutes);
        Assert.Equal(30, loaded.CacheDays);
        Assert.False(loaded.MarkReadOnOpen);
        Assert.True(loaded.ShowRemoteImages);
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
