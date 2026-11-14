using MailDog.Mail.Mime;
using MimeKit;

namespace MailDog.Mail.Tests;

public sealed class MimeCharsetTests
{
    [Fact]
    public void DecodesGb2312BodyAndSubject()
    {
        // 触发 MailDog.Mail 的模块初始化，注册 GB2312/GBK 代码页。
        _ = MimeConverter.ExtractBody(new MimeMessage());

        const string raw = """
            From: sender@example.com
            To: receiver@example.com
            Subject: =?gb2312?B?1tDOxNb3zOI=?=
            MIME-Version: 1.0
            Content-Type: text/plain; charset=gb2312
            Content-Transfer-Encoding: base64

            ytW1vcC019Sy4srUtcTW0M7E08q8/g==
            """;

        using var stream = new MemoryStream(System.Text.Encoding.ASCII.GetBytes(raw));
        var message = MimeMessage.Load(stream);
        var body = MimeConverter.ExtractBody(message);

        Assert.Equal("中文主题", message.Subject);
        Assert.Equal("收到来自测试的中文邮件", body.PlainText);
    }
}
