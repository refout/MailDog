using System.Text.Json;

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

    [Fact]
    public void FormatRecordKeepsJsonOutputAsString()
    {
        var record = CliAudit.FormatRecord(
            new DateTimeOffset(2026, 11, 25, 14, 30, 5, TimeSpan.FromHours(8)),
            "0.3.4",
            "show",
            0,
            TimeSpan.FromMilliseconds(123),
            "{\"id\":2}\n{\"id\":3}",
            true,
            false);

        using var document = JsonDocument.Parse(record);
        var root = document.RootElement;

        Assert.Equal("2026-11-25T06:30:05Z", root.GetProperty("time").GetString());
        Assert.Equal("0.3.4", root.GetProperty("version").GetString());
        Assert.Equal("show", root.GetProperty("command").GetString());
        Assert.Equal(0, root.GetProperty("exit").GetInt32());
        Assert.Equal(123, root.GetProperty("ms").GetInt64());
        Assert.True(root.GetProperty("jsonRequested").GetBoolean());
        Assert.False(root.GetProperty("jsonTruncated").GetBoolean());
        Assert.Equal("{\"id\":2}\n{\"id\":3}", root.GetProperty("json").GetString());
        Assert.DoesNotContain("\n", record);
    }

    [Fact]
    public void FormatRecordRedactsBodiesWhenPolicyAsks()
    {
        var record = CliAudit.FormatRecord(
            DateTimeOffset.MinValue,
            "0.3.5",
            "show",
            0,
            TimeSpan.FromMilliseconds(5),
            "{\"id\":2,\"text\":\"客户报价 100 万\",\"html\":\"<p>secret</p>\"}",
            true,
            false,
            redactBody: true);

        using var document = JsonDocument.Parse(record);
        var root = document.RootElement;

        Assert.True(root.GetProperty("jsonRedacted").GetBoolean());
        Assert.DoesNotContain("客户报价", record);
        Assert.DoesNotContain("secret", record);
        Assert.Contains("正文已脱敏", root.GetProperty("json").GetString());
    }

    [Fact]
    public void FormatRecordWritesNullWhenJsonWasNotRequested()
    {
        var record = CliAudit.FormatRecord(
            DateTimeOffset.MinValue,
            "0.3.4",
            "list",
            0,
            TimeSpan.Zero,
            null,
            false,
            false);

        using var document = JsonDocument.Parse(record);

        Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("json").ValueKind);
        Assert.False(document.RootElement.GetProperty("jsonRequested").GetBoolean());
    }
}
