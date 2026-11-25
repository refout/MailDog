using MailDog.Mail.Imap;

namespace MailDog.Mail.Tests;

public sealed class UnsubscribeTests
{
    [Fact]
    public void ParsesHttpAndMailtoTargets()
    {
        var info = ImapSyncService.ParseUnsubscribe(
            "<https://example.com/unsub?id=42>, <mailto:leave@example.com?subject=unsubscribe>",
            "List-Unsubscribe=One-Click");

        Assert.True(info.Available);
        Assert.Equal("https://example.com/unsub?id=42", info.Url);
        Assert.Equal("mailto:leave@example.com?subject=unsubscribe", info.MailTo);
        Assert.True(info.OneClickPost);
        Assert.Null(info.Error);
    }

    [Fact]
    public void MailtoOnlyIsAvailableButNotOneClick()
    {
        var info = ImapSyncService.ParseUnsubscribe("<mailto:leave@example.com>", null);

        Assert.True(info.Available);
        Assert.Null(info.Url);
        Assert.Equal("mailto:leave@example.com", info.MailTo);
        Assert.False(info.OneClickPost);
    }

    [Fact]
    public void MissingOrUnsupportedHeadersAreReported()
    {
        var empty = ImapSyncService.ParseUnsubscribe(null, null);
        Assert.False(empty.Available);
        Assert.NotNull(empty.Error);

        var unsupported = ImapSyncService.ParseUnsubscribe("<ftp://example.com/unsub>", null);
        Assert.False(unsupported.Available);
        Assert.Null(unsupported.Url);
    }
}
