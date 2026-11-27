using System.Text.Json;
using MailDog.Contracts.Platform;

namespace MailDog.UI.Services;

/// <summary>CLI 的 cli-calls.jsonl 里的一行；JSON 输出按原样保存，最多 20 万字符。</summary>
public sealed class CliCallRecord
{
    public required DateTimeOffset Time { get; init; }

    public string Version { get; init; } = string.Empty;

    public string Command { get; init; } = "(未知)";

    public int ExitCode { get; init; }

    public long DurationMs { get; init; }

    public bool JsonRequested { get; init; }

    public bool JsonTruncated { get; init; }

    public bool JsonRedacted { get; init; }

    public string? Json { get; init; }

    public string DisplayTime => Time.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");

    public string Summary
        => $"{DisplayTime}  exit={ExitCode}  {DurationMs}ms"
            + (JsonRequested ? JsonTruncated ? "  原始记录已截断" : "  原始记录" : "  摘要");

    public string JsonDisplay
        => !string.IsNullOrEmpty(Json)
            ? Json
            : JsonRequested
                ? "（这次调用带了 --json，但命令没有输出可解析的 JSON。）"
                : "（这次调用没有带 --json，只保留了调用摘要。）";
}

/// <summary>读取 CLI 调用记录；坏行直接跳过，不阻塞界面。</summary>
public static class CliCallLog
{
    public const int DefaultMaxRecords = 100;

    public static string Path
        => System.IO.Path.Combine(ProductPaths.DataDirectory, "logs", "cli-calls.jsonl");

    public static string DisplayPath
        => System.IO.Path.Combine(ProductPaths.DisplayDataDirectory, "logs", "cli-calls.jsonl");

    public static IReadOnlyList<CliCallRecord> ReadRecent(int max = DefaultMaxRecords)
        => ReadRecent(Path, max);

    public static IReadOnlyList<CliCallRecord> ReadRecent(string path, int max)
    {
        if (max <= 0 || !File.Exists(path))
        {
            return [];
        }

        try
        {
            var lines = File.ReadAllLines(path);
            var records = new List<CliCallRecord>(Math.Min(max, lines.Length));

            for (var index = lines.Length - 1; index >= 0 && records.Count < max; index--)
            {
                var record = Parse(lines[index]);

                if (record is not null)
                {
                    records.Add(record);
                }
            }

            return records;
        }
        catch
        {
            return [];
        }
    }

    internal static CliCallRecord? Parse(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var timeText = GetString(root, "time");
            var time = DateTimeOffset.TryParse(timeText, out var parsed)
                ? parsed
                : DateTimeOffset.MinValue;

            return new CliCallRecord
            {
                Time = time,
                Version = GetString(root, "version") ?? string.Empty,
                Command = GetString(root, "command") ?? "(未知)",
                ExitCode = GetInt32(root, "exit"),
                DurationMs = GetInt64(root, "ms"),
                JsonRequested = GetBoolean(root, "jsonRequested"),
                JsonTruncated = GetBoolean(root, "jsonTruncated"),
                JsonRedacted = GetBoolean(root, "jsonRedacted"),
                Json = GetString(root, "json")
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? GetString(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Object or JsonValueKind.Array => value.GetRawText(),
            _ => null
        };
    }

    private static int GetInt32(JsonElement root, string name)
        => root.TryGetProperty(name, out var value) && value.TryGetInt32(out var number) ? number : 0;

    private static long GetInt64(JsonElement root, string name)
        => root.TryGetProperty(name, out var value) && value.TryGetInt64(out var number) ? number : 0;

    private static bool GetBoolean(JsonElement root, string name)
        => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;
}
