namespace MailDog.Cli.Tests;

public sealed class CliAuditTests
{
    [Fact]
    public void FormatLineKeepsOnlyCommandAndOutcome()
    {
        var line = CliAudit.FormatLine(
            new DateTimeOffset(2026, 11, 25, 14, 30, 5, TimeSpan.FromHours(8)),
            "0.3.2",
            "list",
            0,
            TimeSpan.FromMilliseconds(123));

        Assert.Equal("2026-11-25 14:30:05 +08:00\t0.3.2\tlist\texit=0\t123ms", line);
        Assert.DoesNotContain("--folder", line);
        Assert.DoesNotContain("@", line);
    }

    [Fact]
    public void FormatLineMarksUnknownCommand()
    {
        var line = CliAudit.FormatLine(
            DateTimeOffset.MinValue,
            "0.3.2",
            null,
            5,
            TimeSpan.FromMilliseconds(7));

        Assert.Contains("\t(未知)\texit=5\t", line);
    }

    [Fact]
    public void FormatLineDropsControlCharactersFromCommand()
    {
        var line = CliAudit.FormatLine(
            DateTimeOffset.MinValue,
            "0.3.2",
            "list\n2026-01-01 fake",
            0,
            TimeSpan.FromMilliseconds(1));

        Assert.Contains("\tlist2026-01-01fake\texit=0\t", line);
        Assert.DoesNotContain("2026-01-01 fake", line);
        Assert.DoesNotContain("\n", line);
    }
}
