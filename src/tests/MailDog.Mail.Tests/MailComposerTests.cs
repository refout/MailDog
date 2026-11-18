using MailDog.Contracts.Dto;
using MailDog.Mail.Mime;
using MailDog.Mail.Storage;
using MimeKit;

namespace MailDog.Mail.Tests;

public sealed class MailComposerTests : IDisposable
{
    private readonly string _directory;

    public MailComposerTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "maildog-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
    }

    [Fact]
    public void BuildsRecipientsThreadingAndAttachments()
    {
        var attachmentPath = Path.Combine(_directory, "报价单.txt");
        File.WriteAllText(attachmentPath, "hello");

        var account = new AccountRecord(
            "acc-1",
            MailProvider.QQ,
            "sender@example.com",
            "发件人",
            "password",
            "secret",
            DateTimeOffset.UtcNow,
            null,
            AccountStatus.Ok,
            null);

        using var message = MailComposer.Build(
            account,
            "a@example.com; b@example.com",
            "cc@example.com",
            "bcc@example.com",
            "测试主题",
            "第一行\n第二行",
            "<original@example.com>",
            "<new@example.com>",
            [MailComposer.ToAttachment(attachmentPath)]);

        Assert.Equal("测试主题", message.Subject);
        Assert.Equal(2, message.To.Count);
        Assert.Single(message.Cc);
        Assert.Single(message.Bcc);
        Assert.Equal("original@example.com", message.InReplyTo);
        Assert.Contains("第一行", message.TextBody);
        Assert.Contains("第一行", message.HtmlBody);
        var attachment = Assert.Single(message.Attachments);
        Assert.Equal("报价单.txt", ((MimePart)attachment).FileName);
    }

    [Fact]
    public void MarkdownBodyProducesFormattedHtml()
    {
        var account = new AccountRecord(
            "acc-1",
            MailProvider.QQ,
            "sender@example.com",
            "发件人",
            "password",
            null,
            DateTimeOffset.UtcNow,
            null,
            AccountStatus.Ok,
            null);

        using var message = MailComposer.Build(
            account,
            "to@example.com",
            string.Empty,
            string.Empty,
            "Markdown",
            "**粗体**\n\n- 一\n- 二",
            null,
            "<md@example.com>",
            [],
            isMarkdown: true);

        Assert.Contains("<strong>粗体</strong>", message.HtmlBody);
        Assert.Contains("<li>一</li>", message.HtmlBody);
        Assert.Contains("**粗体**", message.TextBody);
    }

    [Fact]
    public void DraftCanHaveNoRecipients()
    {
        var account = new AccountRecord(
            "acc-1",
            MailProvider.Outlook,
            "sender@example.com",
            "发件人",
            "oauth2",
            null,
            DateTimeOffset.UtcNow,
            null,
            AccountStatus.Ok,
            null);

        using var message = MailComposer.Build(
            account,
            string.Empty,
            string.Empty,
            string.Empty,
            "草稿",
            "未写完",
            null,
            "<draft@example.com>",
            [],
            requireRecipients: false);

        Assert.Empty(message.To);
        Assert.Equal("未写完", message.TextBody);
    }

    [Fact]
    public void RejectsInvalidRecipient()
    {
        var account = new AccountRecord(
            "acc-1",
            MailProvider.QQ,
            "sender@example.com",
            "发件人",
            "password",
            "secret",
            DateTimeOffset.UtcNow,
            null,
            AccountStatus.Ok,
            null);

        var exception = Assert.Throws<ArgumentException>(() => MailComposer.Build(
            account,
            "not-an-address",
            string.Empty,
            string.Empty,
            "主题",
            "正文",
            null,
            "<new@example.com>",
            []));

        Assert.Contains("收件人", exception.Message);
    }

    [Fact]
    public void InlinesCidImagesAsDataUris()
    {
        var builder = new BodyBuilder
        {
            HtmlBody = "<p><img src=\"cid:logo@example.com\"></p>"
        };

        var resource = builder.LinkedResources.Add(
            "logo.png",
            new byte[] { 1, 2, 3, 4 },
            new ContentType("image", "png"));
        resource.ContentId = "logo@example.com";

        using var message = new MimeMessage();
        message.Body = builder.ToMessageBody();

        var html = MimeConverter.InlineCidImages(message, message.HtmlBody);

        Assert.NotNull(html);
        Assert.Contains("data:image/png;base64,", html);
        Assert.DoesNotContain("cid:logo", html);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch
        {
            // 临时目录清理失败不影响测试结果。
        }
    }
}
