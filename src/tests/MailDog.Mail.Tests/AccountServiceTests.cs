using MailDog.Contracts.Dto;
using MailDog.Mail.Accounts;
using MailDog.Mail.Storage;

namespace MailDog.Mail.Tests;

[Collection("Sqlite")]
public sealed class AccountServiceTests : IDisposable
{
    private readonly string _directory;
    private readonly SqliteStore _store;
    private readonly AccountService _accounts;

    public AccountServiceTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "maildog-accounts", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        _store = new SqliteStore(Path.Combine(_directory, "test.db"));
        _accounts = new AccountService(_store, new SecretStore(_store));
    }

    [Fact]
    public void UpdatePasswordAccountReplacesSecretAndDisplayName()
    {
        var added = _accounts.AddPasswordAccount(
            MailProvider.QQ,
            "sender@qq.com",
            "old-secret",
            "旧名字");

        var updated = _accounts.UpdatePasswordAccount(
            added.Id,
            "新名字",
            "new-secret",
            null);

        Assert.Equal("新名字", updated.DisplayName);
        Assert.Equal("sender@qq.com", updated.Email);
        Assert.Null(updated.Server);
        Assert.Equal("new-secret", _accounts.GetPassword(_accounts.GetRecord(added.Id)!));
    }

    [Fact]
    public void UpdatePasswordAccountPersistsCustomServerAndBlankSecret()
    {
        var added = _accounts.AddPasswordAccount(
            MailProvider.Custom,
            "sender@example.com",
            "secret",
            "自定义",
            new ProviderConfig("imap.old.example", 993, false, "smtp.old.example", 465, false));

        var updated = _accounts.UpdatePasswordAccount(
            added.Id,
            "自定义邮箱",
            null,
            new ProviderConfig("imap.new.example", 143, true, "smtp.new.example", 587, true));

        Assert.Equal("自定义邮箱", updated.DisplayName);
        Assert.NotNull(updated.Server);
        Assert.Equal("imap.new.example", updated.Server.ImapHost);
        Assert.Equal(143, updated.Server.ImapPort);
        Assert.True(updated.Server.ImapStartTls);
        Assert.Equal("smtp.new.example", updated.Server.SmtpHost);
        Assert.Equal(587, updated.Server.SmtpPort);
        Assert.True(updated.Server.SmtpStartTls);
        Assert.Equal("secret", _accounts.GetPassword(_accounts.GetRecord(added.Id)!));
    }

    [Fact]
    public void UpdateOAuthDisplayNameKeepsProviderAndEmail()
    {
        var added = _accounts.AddOAuthAccount(
            MailProvider.Outlook,
            "user@outlook.com",
            "旧名字",
            "refresh-ref");

        var updated = _accounts.UpdateDisplayName(added.Id, "新名字");

        Assert.Equal("新名字", updated.DisplayName);
        Assert.Equal("user@outlook.com", updated.Email);
        Assert.Equal(MailProvider.Outlook, updated.Provider);
    }

    [Fact]
    public void RemoveAccountDeletesCachedDataAndOutboxAttachments()
    {
        var added = _accounts.AddPasswordAccount(
            MailProvider.QQ,
            "remove@qq.com",
            "secret",
            "待删除");

        _store.UpsertFolders(added.Id,
        [
            new FolderRecord(0, added.Id, "INBOX", "收件箱", FolderRole.Inbox, 1, 0, 0, 0)
        ]);

        var folder = Assert.Single(_store.GetFolders(added.Id));
        var now = DateTimeOffset.UtcNow;

        _store.UpsertMessages(added.Id, folder.Id,
        [
            new MessageRecord(
                0,
                added.Id,
                folder.Id,
                1,
                "message-1",
                "主题",
                "Alice",
                "alice@example.com",
                "me@example.com",
                string.Empty,
                now,
                1024,
                MailFlags.None,
                false,
                string.Empty,
                BodyStates.NotFetched)
        ]);

        var messageId = Assert.Single(_store.QueryMessages(folder.Id, null, null, 10).Items).Id;
        _store.QueuePendingFlag(messageId, "Seen", true, now);

        var attachmentDirectory = Path.Combine(_directory, "outbox", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(attachmentDirectory);
        var attachmentPath = Path.Combine(attachmentDirectory, "a.txt");
        File.WriteAllText(attachmentPath, "attachment");

        var outboxId = _store.InsertOutbox(
            new OutboxRecord(
                0,
                added.Id,
                "to@example.com",
                string.Empty,
                string.Empty,
                "主题",
                "正文",
                null,
                "<outbox@example.com>",
                OutboxStatuses.Pending,
                0,
                now,
                null,
                now,
                null,
                [],
                false),
            [
                new OutboxAttachmentRecord(0, "a.txt", attachmentPath, "text/plain", 10)
            ]);

        _accounts.RemoveAccount(added.Id);

        Assert.Empty(_accounts.GetAccounts());
        Assert.Empty(_store.GetFolders(added.Id));
        Assert.Null(_store.GetOutbox(outboxId));
        Assert.Empty(_store.GetDuePendingFlags(now.AddDays(1), 10));
        Assert.False(File.Exists(attachmentPath));
        Assert.False(Directory.Exists(attachmentDirectory));
    }

    public void Dispose()
    {
        _store.Dispose();
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
