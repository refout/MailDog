namespace MailDog.Cli.Tests;

public sealed class ArgumentsTests
{
    [Fact]
    public void KeepsCommandAndPositionals()
    {
        var arguments = Arguments.Parse(["attachment", "12", "0", "--out", "/tmp"]);

        Assert.Equal("attachment", arguments.Command);
        Assert.Equal(["12", "0"], arguments.Positionals);
        Assert.Equal("/tmp", arguments.Value("--out"));
    }

    [Fact]
    public void SupportsEqualsSyntax()
    {
        var arguments = Arguments.Parse(["show", "9", "--max-chars=40", "--json"]);

        Assert.Equal("40", arguments.Value("--max-chars"));
        Assert.True(arguments.Json);
    }

    [Fact]
    public void ParsesHtmlSwitch()
    {
        var arguments = Arguments.Parse(["show", "9", "--html"]);

        Assert.True(arguments.Has("--html"));
        Assert.False(arguments.Has("--json"));
    }

    [Fact]
    public void SupportsOptionTerminator()
    {
        var arguments = Arguments.Parse(["search", "--no-start", "--", "--not-an-option"]);

        Assert.Equal(["--not-an-option"], arguments.Positionals);
        Assert.True(arguments.Has("--no-start"));
    }

    [Fact]
    public void LastSingleValueWins()
    {
        var arguments = Arguments.Parse(["list", "--account", "first", "--account", "second"]);

        Assert.Equal("second", arguments.Value("--account"));
        Assert.Equal(["first", "second"], arguments.Values("--account"));
    }

    [Fact]
    public void RepeatableValuesStayOrdered()
    {
        var arguments = Arguments.Parse(
            ["draft", "--to", "a@example.com", "--cc", "c@example.com", "--to", "b@example.com", "--attach", "x.pdf"]);

        Assert.Equal(["a@example.com", "b@example.com"], arguments.Values("--to"));
        Assert.Equal(["c@example.com"], arguments.Values("--cc"));
        Assert.Equal(["x.pdf"], arguments.Values("--attach"));
    }

    [Fact]
    public void IntegerErrorsStayChinese()
    {
        var exception = Assert.Throws<UsageException>(() => Arguments.Parse(["list", "--limit", "many"]).IntValue("--limit", 20));

        Assert.Contains("--limit", exception.Message);
        Assert.Contains("整数", exception.Message);
    }

    [Fact]
    public void RejectsUnknownCommand()
    {
        var exception = Assert.Throws<UsageException>(() => Arguments.Parse(["unknown"]));

        Assert.Contains("不认识的命令", exception.Message);
    }

    [Fact]
    public void RejectsUnknownFlag()
    {
        var exception = Assert.Throws<UsageException>(() => Arguments.Parse(["list", "--bogus"]));

        Assert.Contains("不认识的开关", exception.Message);
    }

    [Fact]
    public void RejectsMissingValue()
    {
        var exception = Assert.Throws<UsageException>(() => Arguments.Parse(["list", "--account"]));

        Assert.Contains("需要一个值", exception.Message);
    }

    [Fact]
    public void RejectsValueThatLooksLikeFlag()
    {
        var exception = Assert.Throws<UsageException>(() => Arguments.Parse(["list", "--account", "--json"]));

        Assert.Contains("需要一个值", exception.Message);
    }
}
