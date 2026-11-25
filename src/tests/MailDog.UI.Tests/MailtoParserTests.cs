using MailDog.UI.Services;

namespace MailDog.UI.Tests;

public sealed class MailtoParserTests
{
    [Fact]
    public void KeepsAddressBecauseUriAbsolutePathIsEmpty()
    {
        var fields = MailtoParser.Parse("mailto:test@example.com?subject=Hi&body=hello");

        Assert.NotNull(fields);
        Assert.Equal("test@example.com", fields["to"]);
        Assert.Equal("Hi", fields["subject"]);
        Assert.Equal("hello", fields["body"]);
    }

    [Fact]
    public void DecodesCcAndBccAndRepeatedTo()
    {
        var fields = MailtoParser.Parse("mailto:a@example.com?to=b%40example.com&cc=c%40example.com&bcc=d%40example.com");

        Assert.NotNull(fields);
        Assert.Equal("a@example.com,b@example.com", fields["to"]);
        Assert.Equal("c@example.com", fields["cc"]);
        Assert.Equal("d@example.com", fields["bcc"]);
    }

    [Fact]
    public void ConvertsPlusAndKeepsEncodedPlus()
    {
        var fields = MailtoParser.Parse("mailto:?subject=hello+world&body=a%2Bb");

        Assert.NotNull(fields);
        Assert.Equal("hello world", fields["subject"]);
        Assert.Equal("a+b", fields["body"]);
    }

    [Fact]
    public void RejectsOtherSchemes()
    {
        Assert.Null(MailtoParser.Parse("https://example.com"));
        Assert.Null(MailtoParser.Parse("maildog://open?messageId=1"));
    }
}
