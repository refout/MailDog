using MailDog.UI.Services;

namespace MailDog.UI.Tests;

public sealed class CliCallLogTests
{
    [Fact]
    public void ReadsNewestFirstAndDecodesEscapedJson()
    {
        var path = Path.Combine(Path.GetTempPath(), $"maildog-cli-calls-{Guid.NewGuid():N}.jsonl");

        try
        {
            File.WriteAllLines(path,
            [
                "{\"time\":\"2026-11-25T01:00:00Z\",\"version\":\"0.3.4\",\"command\":\"list\",\"exit\":0,\"ms\":10,\"jsonRequested\":false,\"jsonTruncated\":false,\"json\":null}",
                "{\"time\":\"2026-11-25T02:00:00Z\",\"version\":\"0.3.4\",\"command\":\"show\",\"exit\":0,\"ms\":20,\"jsonRequested\":true,\"jsonTruncated\":false,\"json\":\"{\\\"id\\\":2}\\n{\\\"id\\\":3}\"}"
            ]);

            var records = CliCallLog.ReadRecent(path, 2);

            Assert.Equal(2, records.Count);
            Assert.Equal("show", records[0].Command);
            Assert.Equal("{\"id\":2}\n{\"id\":3}", records[0].Json);
            Assert.True(records[0].JsonRequested);
            Assert.Equal("list", records[1].Command);
            Assert.False(records[1].JsonRequested);
            Assert.Contains("摘要", records[1].Summary);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void SkipsMalformedLines()
    {
        var path = Path.Combine(Path.GetTempPath(), $"maildog-cli-calls-{Guid.NewGuid():N}.jsonl");

        try
        {
            File.WriteAllLines(path,
            [
                "not json",
                "{\"time\":\"2026-11-25T02:00:00Z\",\"version\":\"0.3.4\",\"command\":\"show\",\"exit\":0,\"ms\":20,\"jsonRequested\":true,\"jsonTruncated\":false,\"json\":\"ok\"}"
            ]);

            var records = CliCallLog.ReadRecent(path, 2);

            Assert.Single(records);
            Assert.Equal("show", records[0].Command);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
