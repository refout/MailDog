using MailDog.Mail.Smtp;

namespace MailDog.Mail.Tests;

public sealed class SendGuardTests
{
    [Fact]
    public void Clean_when_no_mention_and_no_attachments()
    {
        var result = SendGuard.Inspect("周会纪要", "今天讨论了三件事。", null, []);

        Assert.False(result.NeedsConfirmation);
        Assert.Empty(result.Messages);
    }

    [Theory]
    [InlineData("见附件")]
    [InlineData("材料在附件里")]
    [InlineData("Please see attached")]
    [InlineData("Attached is the report")]
    [InlineData("详见附件说明")]
    public void Warns_when_body_mentions_attachment_without_files(string body)
    {
        var result = SendGuard.Inspect("报告", body, null, []);

        Assert.True(result.NeedsConfirmation);
        Assert.Contains(result.Messages, message => message.Contains("提到了附件"));
    }

    [Fact]
    public void Warns_when_html_body_mentions_attachment()
    {
        var result = SendGuard.Inspect(
            "报告",
            string.Empty,
            "<p>详细数据<b>见附件</b>。</p>",
            []);

        Assert.True(result.NeedsConfirmation);
    }

    [Fact]
    public void Quiet_when_mention_but_attachment_present()
    {
        var path = CreateTempFile("a.txt", 10);

        try
        {
            var result = SendGuard.Inspect("报告", "见附件", null, [path]);

            Assert.False(result.NeedsConfirmation);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void MentionsAttachment_ignores_html_tags_themselves()
    {
        Assert.False(SendGuard.MentionsAttachment("<p>hello</p>"));
        Assert.True(SendGuard.MentionsAttachment("<p>见附件</p>"));
        Assert.False(SendGuard.MentionsAttachment("   "));
        Assert.False(SendGuard.MentionsAttachment(null));
    }

    private static string CreateTempFile(string name, int bytes)
    {
        var path = Path.Combine(Path.GetTempPath(), "maildog-tests", Guid.NewGuid().ToString("N"), name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[bytes]);
        return path;
    }
}
