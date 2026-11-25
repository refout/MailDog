using System.Text;
using System.Text.Json;
using MailDog.Contracts.Dto;
using MailDog.Contracts.Platform;
using MailDog.Contracts.Serialization;

namespace MailDog.Cli;

/// <summary>把待确认的发送/草稿请求落到本地队列；approve 成功后才删除。</summary>
internal static class CliPending
{
    public static string DirectoryPath
        => Environment.GetEnvironmentVariable("MAILDOG_PENDING_DIR")
           ?? Path.Combine(ProductPaths.DataDirectory, "pending");

    public static CliPendingDto Create(string kind, string summary, string requestJson)
    {
        System.IO.Directory.CreateDirectory(DirectoryPath);

        var now = DateTimeOffset.UtcNow;
        var id = $"{now:yyyyMMddHHmmss}-{Guid.NewGuid():N}"[..23];
        var item = new CliPendingDto
        {
            Id = id,
            Kind = kind,
            CreatedUtc = now,
            Summary = summary,
            RequestJson = requestJson
        };

        Write(item);
        return item;
    }

    public static IReadOnlyList<CliPendingDto> List()
    {
        try
        {
            if (!System.IO.Directory.Exists(DirectoryPath))
            {
                return [];
            }

            var items = new List<CliPendingDto>();
            foreach (var path in System.IO.Directory.EnumerateFiles(DirectoryPath, "*.json"))
            {
                var item = ReadFile(path);
                if (item is not null)
                {
                    items.Add(item);
                }
            }

            return items.OrderByDescending(item => item.CreatedUtc).ToArray();
        }
        catch
        {
            return [];
        }
    }

    public static CliPendingDto? Read(string id)
    {
        if (!IsSafeId(id))
        {
            return null;
        }

        return ReadFile(Path.Combine(DirectoryPath, id + ".json"));
    }

    public static bool Delete(string id)
    {
        if (!IsSafeId(id))
        {
            return false;
        }

        try
        {
            var path = Path.Combine(DirectoryPath, id + ".json");
            if (!File.Exists(path))
            {
                return false;
            }

            File.Delete(path);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static CliPendingDto? ReadFile(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            var json = File.ReadAllText(path, Encoding.UTF8);
            return JsonSerializer.Deserialize(json, IpcJsonContext.Default.CliPendingDto);
        }
        catch
        {
            return null;
        }
    }

    private static void Write(CliPendingDto item)
    {
        var path = Path.Combine(DirectoryPath, item.Id + ".json");
        var json = JsonSerializer.Serialize(item, IpcJsonContext.Default.CliPendingDto);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, json, new UTF8Encoding(false));
        File.Move(temporary, path, true);
    }

    private static bool IsSafeId(string id)
        => !string.IsNullOrWhiteSpace(id)
           && id.Length <= 40
           && id.All(character => char.IsAsciiLetterOrDigit(character) || character == '-');
}
