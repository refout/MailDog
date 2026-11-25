using System.Text.Json;
using MailDog.Contracts.Dto;

namespace MailDog.Cli.Tests;

public sealed class ShowJsonTests
{
    [Fact]
    public void HtmlOnlyBodyFallsBackToConvertedText()
    {
        var body = new MailBodyDto
        {
            Html = "<p>Hello <b>world</b></p>",
            PlainText = null
        };

        var (text, source) = Commands.ResolveBodyText(body);

        Assert.Equal("html", source);
        Assert.Contains("Hello", text);
        Assert.Contains("world", text);
        Assert.DoesNotContain("<b>", text);
    }

    [Fact]
    public void PlainTextWinsWhenBothExist()
    {
        var body = new MailBodyDto
        {
            Html = "<p>HTML</p>",
            PlainText = "plain"
        };

        var (text, source) = Commands.ResolveBodyText(body);

        Assert.Equal("plain", source);
        Assert.Equal("plain", text);
    }

    [Fact]
    public void EmptyBodyReportsNone()
    {
        var body = new MailBodyDto
        {
            Html = "   ",
            PlainText = null
        };

        var (text, source) = Commands.ResolveBodyText(body);

        Assert.Equal("none", source);
        Assert.Equal(string.Empty, text);
    }

    [Fact]
    public void HtmlOnlyBodyAddsConvertedTextAndRawHtml()
    {
        var body = new MailBodyDto
        {
            MessageId = 42,
            Subject = "HTML only",
            FromName = "Sender",
            FromAddress = "sender@example.com",
            ToAddresses = ["seed@example.com"],
            Date = new DateTimeOffset(2026, 11, 25, 9, 0, 0, TimeSpan.FromHours(8)),
            Html = "<p>Hello <b>world</b></p>",
            PlainText = null
        };

        var json = Commands.BuildBodyJson(body, "Hello world", body.Html, "html");
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        Assert.Equal("Hello world", root.GetProperty("text").GetString());
        Assert.Equal("html", root.GetProperty("textSource").GetString());
        Assert.Equal("<p>Hello <b>world</b></p>", root.GetProperty("html").GetString());
    }

    [Fact]
    public void PlainTextBodyDoesNotExposeHtmlByDefault()
    {
        var body = new MailBodyDto
        {
            MessageId = 43,
            Subject = "Plain text",
            Html = "<p>Hello</p>",
            PlainText = "Hello"
        };

        var json = Commands.BuildBodyJson(body, "Hello", null, "plain");
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        Assert.Equal("Hello", root.GetProperty("text").GetString());
        Assert.Equal("plain", root.GetProperty("textSource").GetString());
        Assert.False(root.TryGetProperty("html", out _));
    }
}
