using System.Text.Json;
using MailDog.Contracts.Dto;

namespace MailDog.Cli.Tests;

public sealed class ShowJsonTests
{
    [Fact]
    public void HtmlOnlyBodyReturnsRawHtmlAndNullText()
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

        var json = Commands.BuildBodyJson(body, null, body.Html);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        Assert.Equal(JsonValueKind.Null, root.GetProperty("text").ValueKind);
        Assert.Equal("<p>Hello <b>world</b></p>", root.GetProperty("html").GetString());
        Assert.False(root.TryGetProperty("textSource", out _));
    }

    [Fact]
    public void BothBodiesAreReturnedWithoutConversion()
    {
        var body = new MailBodyDto
        {
            MessageId = 43,
            Subject = "Both bodies",
            Html = "<p>Hello <b>HTML</b></p>",
            PlainText = "Hello plain"
        };

        var json = Commands.BuildBodyJson(body, body.PlainText, body.Html);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        Assert.Equal("Hello plain", root.GetProperty("text").GetString());
        Assert.Equal("<p>Hello <b>HTML</b></p>", root.GetProperty("html").GetString());
    }

    [Fact]
    public void MissingBodiesAreNull()
    {
        var body = new MailBodyDto
        {
            MessageId = 44,
            Subject = "Empty"
        };

        var json = Commands.BuildBodyJson(body, null, null);
        using var document = JsonDocument.Parse(json);

        Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("text").ValueKind);
        Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("html").ValueKind);
    }
}
