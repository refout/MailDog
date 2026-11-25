namespace MailDog.UI.Services;

/// <summary>
/// 按 RFC 6068 解析 mailto 链接。.NET 的 Uri 会把地址拆进 Host/UserInfo，
/// AbsolutePath 反而是空的，所以这里直接处理 mailto: 后面的原文。
/// </summary>
public static class MailtoParser
{
    public static Dictionary<string, string>? Parse(string uri)
    {
        var raw = uri.Trim();

        if (!raw.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        raw = raw["mailto:".Length..];

        var queryStart = raw.IndexOf('?');
        var address = queryStart >= 0 ? raw[..queryStart] : raw;
        var to = Uri.UnescapeDataString(address.Trim());
        values["to"] = to;

        var query = queryStart >= 0 ? raw[queryStart..] : string.Empty;

        foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split('=', 2);

            if (parts.Length != 2)
            {
                continue;
            }

            var key = Uri.UnescapeDataString(parts[0]).ToLowerInvariant();
            var value = Uri.UnescapeDataString(parts[1].Replace("+", " ", StringComparison.Ordinal));
            if (key is "to" or "cc" or "bcc" or "subject" or "body")
            {
                var previous = values.GetValueOrDefault(key);
                values[key] = previous is { Length: > 0 } ? $"{previous},{value}" : value;
            }
        }

        return values;
    }
}
