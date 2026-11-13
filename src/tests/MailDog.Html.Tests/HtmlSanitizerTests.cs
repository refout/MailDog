namespace MailDog.Html.Tests;

public sealed class HtmlSanitizerTests
{
    private readonly HtmlSanitizer _sanitizer = new();
    private readonly HtmlDocumentBuilder _builder = new();

    [Fact]
    public void RemovesScriptAndDangerousElements()
    {
        var result = _sanitizer.Sanitize(
            "<p>Hello</p><script>alert(1)</script><iframe src=\"https://evil\">x</iframe>");

        Assert.Equal("<p>Hello</p>", result.Html);
    }

    [Fact]
    public void RemovesEventHandlersAndStyleAttributes()
    {
        var result = _sanitizer.Sanitize(
            "<p onclick=\"alert(1)\" style=\"color:red\" class=\"x\">Hi</p>");

        Assert.Equal("<p>Hi</p>", result.Html);
    }

    [Fact]
    public void RemovesJavascriptUrls()
    {
        var result = _sanitizer.Sanitize("<a href=\"javascript:alert(1)\">click</a>");

        Assert.Equal("<a>click</a>", result.Html);
    }

    [Fact]
    public void RemovesProtocolRelativeUrls()
    {
        var result = _sanitizer.Sanitize("<a href=\"//evil.example\">click</a>");

        Assert.Equal("<a>click</a>", result.Html);
    }

    [Fact]
    public void KeepsSafeLinks()
    {
        var result = _sanitizer.Sanitize(
            "<a href=\"https://example.com\" title=\"site\">web</a><a href=\"mailto:a@b.com\">mail</a>");

        Assert.Contains("href=\"https://example.com\"", result.Html);
        Assert.Contains("href=\"mailto:a@b.com\"", result.Html);
    }

    [Fact]
    public void BlocksRemoteImagesByDefault()
    {
        var result = _sanitizer.Sanitize("<p>x</p><img src=\"https://example.com/a.png\" alt=\"a\">");

        Assert.DoesNotContain("<img", result.Html);
        Assert.True(result.RemoteImagesBlocked);
    }

    [Fact]
    public void AllowsHttpsImagesOnlyWhenEnabled()
    {
        var result = _sanitizer.Sanitize(
            "<img src=\"https://example.com/a.png\"><img src=\"http://example.com/b.png\"><img src=\"data:image/png;base64,AAAA\">",
            allowRemoteImages: true);

        Assert.Contains("src=\"https://example.com/a.png\"", result.Html);
        Assert.DoesNotContain("b.png", result.Html);
        Assert.DoesNotContain("data:image", result.Html);
        Assert.False(result.RemoteImagesBlocked);
    }

    [Fact]
    public void BalancesUnclosedTags()
    {
        var result = _sanitizer.Sanitize("<div><p>Hello");

        Assert.Equal("<div><p>Hello</p></div>", result.Html);
    }

    [Fact]
    public void EscapesLoneAngleBracket()
    {
        var result = _sanitizer.Sanitize("1 < 2 and <script>");

        Assert.Contains("&lt;", result.Html);
        Assert.DoesNotContain("<script", result.Html);
    }

    [Fact]
    public void KeepsEntitiesAsText()
    {
        var result = _sanitizer.Sanitize("&lt;script&gt;alert(1)&lt;/script&gt;");

        Assert.Equal("&lt;script&gt;alert(1)&lt;/script&gt;", result.Html);
    }

    [Fact]
    public void LimitsNestingDepth()
    {
        var input = string.Concat(Enumerable.Repeat("<div>", 100)) + "x";
        var result = _sanitizer.Sanitize(input);

        Assert.True(CountOccurrences(result.Html, "<div>") <= 64);
        Assert.Equal(CountOccurrences(result.Html, "<div>"), CountOccurrences(result.Html, "</div>"));
    }

    [Fact]
    public void TruncatesOversizedInput()
    {
        var input = new string('a', 3 * 1024 * 1024);

        var result = _sanitizer.Sanitize(input);

        Assert.True(result.Truncated);
        Assert.True(result.Html.Length <= 2 * 1024 * 1024);
    }

    [Fact]
    public void KeepsTableStructure()
    {
        var result = _sanitizer.Sanitize(
            "<table><tr><td colspan=\"2\" align=\"center\">A</td></tr></table>");

        Assert.Contains("<table>", result.Html);
        Assert.Contains("colspan=\"2\"", result.Html);
        Assert.Contains("align=\"center\"", result.Html);
    }

    [Fact]
    public void BuildsDocumentWithCsp()
    {
        var document = _builder.Build("<p>Hi</p>", darkMode: false, allowRemoteImages: false);

        Assert.Contains("Content-Security-Policy", document);
        Assert.Contains("img-src 'none'", document);
        Assert.Contains("script-src 'none'", document);
        Assert.Contains("<p>Hi</p>", document);
        Assert.DoesNotContain("<script>", document);
    }

    [Fact]
    public void BuildsDocumentWithImagePolicy()
    {
        var document = _builder.Build("<img src=\"https://x/y.png\">", darkMode: true, allowRemoteImages: true);

        Assert.Contains("img-src https:", document);
        Assert.Contains("background: #272727", document);
    }

    private static int CountOccurrences(string input, string value)
    {
        var count = 0;
        var index = 0;

        while ((index = input.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
    }
}
