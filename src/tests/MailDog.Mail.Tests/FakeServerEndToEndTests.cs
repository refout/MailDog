using MailDog.Contracts.Dto;
using MailDog.Contracts.Messages;
using MailDog.Mail.Accounts;
using MailDog.Mail.Auth;
using MailDog.Mail.Imap;
using MailDog.Mail.Smtp;
using MailDog.Mail.Storage;

namespace MailDog.Mail.Tests;

[Collection("Sqlite")]
public sealed class FakeServerEndToEndTests : IDisposable
{
    private readonly string _directory;
    private readonly SqliteStore _store;
    private readonly AccountService _accounts;

    public FakeServerEndToEndTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "maildog-e2e", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        _store = new SqliteStore(Path.Combine(_directory, "test.db"));
        _accounts = new AccountService(_store, new SecretStore(_store));
    }

    [Theory]
    [InlineData(MailProvider.QQ, true)]
    [InlineData(MailProvider.Custom, true)]
    [InlineData(MailProvider.Outlook, false)]
    public void OutlookServerSavesItsOwnSentCopy(MailProvider provider, bool expected)
    {
        // Outlook SMTP 自动保存“已发送”，客户端再 APPEND 会产生重复副本。
        Assert.Equal(expected, SmtpSendService.ShouldUploadSentCopy(provider));
    }

    [Fact]
    public async Task SmtpSendReachesFakeServer()
    {
        await using var server = new FakeSmtpServer();
        var account = AddAccount(server.Port, 1);
        _store.SaveSettings(new AppSettingsDto { SendDelaySeconds = 0 });

        var sender = new SmtpSendService(
            _store,
            _accounts,
            new NullAccessTokenProvider(),
            _directory,
            certificateValidator: (_, _, _, _) => true);

        var response = await sender.EnqueueAndSendAsync(
            new SendMailRequest
            {
                AccountId = account.Id,
                To = "receiver@example.com",
                Subject = "Fake SMTP subject",
                BodyText = "hello over fake smtp"
            },
            CancellationToken.None);

        Assert.True(response.Sent);
        var received = await server.Received.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Contains("Subject: Fake SMTP subject", received, StringComparison.Ordinal);
        Assert.Contains("hello over fake smtp", received, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ImapSyncReadsFakeInbox()
    {
        await using var server = new FakeImapServer();
        var account = AddAccount(server.Port, server.Port);

        await using var sync = new ImapSyncService(
            _store,
            _accounts,
            new NullAccessTokenProvider(),
            (_, _, _, _) => true);

        await sync.SyncAccountAsync(account.Id, CancellationToken.None);

        var folders = _store.GetFolders(account.Id);
        var inbox = Assert.Single(folders, folder => folder.Role == FolderRole.Inbox);
        var page = _store.QueryMessages(inbox.Id, null, null, 50);

        Assert.Equal(2, page.Items.Length);
        Assert.Contains(page.Items, message => message.Subject == "Fake one");
        Assert.Contains(page.Items, message => message.Subject == "Fake two");
        Assert.Contains(page.Items, message => message.FromAddress == "alice@example.com");
    }

    private AccountRecord AddAccount(int smtpPort, int imapPort)
    {
        var added = _accounts.AddPasswordAccount(
            MailProvider.Custom,
            $"fake-{Guid.NewGuid():N}@example.com",
            "test-secret",
            "Fake",
            new ProviderConfig("127.0.0.1", imapPort, false, "127.0.0.1", smtpPort, false));

        return _accounts.GetRecord(added.Id)!;
    }

    public void Dispose()
    {
        _store.Dispose();
        try
        {
            Directory.Delete(_directory, true);
        }
        catch
        {
            // 临时目录清理失败不影响测试结论。
        }
    }
}
