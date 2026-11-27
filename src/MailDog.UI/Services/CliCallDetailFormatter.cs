using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MailDog.UI.Services;

/// <summary>把 CLI 的原始 JSON 输出压成可读摘要；原始 JSON 只在调试区展开。</summary>
public static class CliCallDetailFormatter
{
    private const int MaxListItems = 20;
    private const int MaxBodyPreview = 500;

    private static readonly Dictionary<string, string> Labels = new(StringComparer.OrdinalIgnoreCase)
    {
        ["id"] = "ID",
        ["subject"] = "主题",
        ["from"] = "发件人",
        ["fromAddress"] = "发件地址",
        ["to"] = "收件人",
        ["cc"] = "抄送",
        ["bcc"] = "密送",
        ["date"] = "时间",
        ["account"] = "账号",
        ["folder"] = "文件夹",
        ["fileName"] = "文件名",
        ["size"] = "大小",
        ["unread"] = "未读",
        ["flagged"] = "星标",
        ["hasAttachments"] = "带附件",
        ["preview"] = "预览",
        ["textChars"] = "正文字符数",
        ["dryRun"] = "发送预览",
        ["opened"] = "已打开",
        ["tool"] = "工具",
        ["version"] = "版本",
        ["transport"] = "传输",
        ["readOnly"] = "只读"
    };

    public static string Format(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return "这次调用没有保存输出。";
        }

        var values = ParseValues(json);
        if (values.Count == 0)
        {
            return "这次调用的输出不是可解析的 JSON；需要排查时请展开原始 JSON。";
        }

        if (values.Count == 1)
        {
            return FormatValue(values[0]);
        }

        if (values.All(value => value.ValueKind == JsonValueKind.Object && Has(value, "subject")))
        {
            return FormatMailList(values);
        }

        if (values.All(value => value.ValueKind != JsonValueKind.Object))
        {
            return FormatScalarList(values);
        }

        var builder = new StringBuilder($"共 {values.Count} 条记录\n");
        for (var index = 0; index < values.Count && index < MaxListItems; index++)
        {
            builder.AppendLine($"{index + 1}. {Compact(values[index])}");
        }

        if (values.Count > MaxListItems)
        {
            builder.AppendLine($"…仅显示前 {MaxListItems} 条");
        }

        return builder.ToString().TrimEnd();
    }

    private static List<JsonElement> ParseValues(string json)
    {
        var values = new List<JsonElement>();

        if (TryParse(json, out var root))
        {
            if (root.ValueKind == JsonValueKind.Array)
            {
                values.AddRange(root.EnumerateArray().Select(item => item.Clone()));
            }
            else
            {
                values.Add(root.Clone());
            }

            return values;
        }

        // CLI 的 list/search 等命令按行输出 JSON 对象（NDJSON），不是单个数组。
        foreach (var line in json.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (TryParse(line, out var item))
            {
                values.Add(item.Clone());
            }
        }

        return values;
    }

    private static bool TryParse(string text, out JsonElement value)
    {
        try
        {
            using var document = JsonDocument.Parse(text);
            value = document.RootElement.Clone();
            return true;
        }
        catch (JsonException)
        {
            value = default;
            return false;
        }
    }

    private static string FormatValue(JsonElement value)
    {
        return value.ValueKind switch
        {
            JsonValueKind.Object => FormatObject(value),
            JsonValueKind.Array => FormatScalarList(value.EnumerateArray().ToList()),
            JsonValueKind.String => value.GetString() ?? string.Empty,
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.True => "是",
            JsonValueKind.False => "否",
            JsonValueKind.Null => "无",
            _ => value.GetRawText()
        };
    }

    private static string FormatObject(JsonElement value)
    {
        if (Has(value, "subject") && Has(value, "dryRun"))
        {
            return FormatSend(value);
        }

        if (Has(value, "subject") && (Has(value, "text") || Has(value, "html") || Has(value, "attachments")))
        {
            return FormatMessage(value);
        }

        if (Has(value, "subject") && Has(value, "preview"))
        {
            return FormatMailLine(value);
        }

        if (Has(value, "tool") && Has(value, "commands"))
        {
            return FormatSchema(value);
        }

        return FormatGenericObject(value);
    }

    private static string FormatMessage(JsonElement value)
    {
        var builder = new StringBuilder();

        Append(builder, "ID", GetString(value, "id"));
        Append(builder, "主题", GetString(value, "subject"));
        Append(builder, "发件人", CombineSender(GetString(value, "from"), GetString(value, "fromAddress")));
        Append(builder, "收件人", FormatAddresses(value, "to"));
        Append(builder, "抄送", FormatAddresses(value, "cc"));
        Append(builder, "时间", FormatDate(GetString(value, "date")));

        if (GetBool(value, "truncated"))
        {
            Append(builder, "提示", "正文已截断");
        }

        if (GetBool(value, "fromCache"))
        {
            Append(builder, "来源", "本地缓存");
        }

        var attachments = GetArray(value, "attachments");
        if (attachments.Count > 0)
        {
            Append(builder, "附件", $"{attachments.Count} 个");
            foreach (var attachment in attachments.Take(8))
            {
                var fileName = GetString(attachment, "fileName") ?? "(未命名附件)";
                var size = FormatSize(GetLong(attachment, "size"));
                builder.AppendLine($"  - {fileName} · {size}");
            }

            if (attachments.Count > 8)
            {
                builder.AppendLine($"  …另有 {attachments.Count - 8} 个附件");
            }
        }

        var text = GetString(value, "text");
        var html = GetString(value, "html");
        if (!string.IsNullOrWhiteSpace(text))
        {
            Append(builder, "正文预览", Preview(text));
        }
        else if (!string.IsNullOrWhiteSpace(html))
        {
            Append(builder, "正文", $"HTML 正文（{html.Length:N0} 字符），展开原始 JSON 可查看");
        }

        return builder.ToString().TrimEnd();
    }

    private static string FormatSend(JsonElement value)
    {
        var builder = new StringBuilder();

        if (GetBool(value, "dryRun"))
        {
            builder.AppendLine("发送预览（未实际发送）");
        }

        Append(builder, "账号", GetString(value, "account"));
        Append(builder, "收件人", FormatAddresses(value, "to"));
        Append(builder, "抄送", FormatAddresses(value, "cc"));
        Append(builder, "密送", FormatAddresses(value, "bcc"));
        Append(builder, "主题", GetString(value, "subject"));

        var attachments = GetArray(value, "attachments");
        if (attachments.Count > 0)
        {
            Append(builder, "附件", $"{attachments.Count} 个");
            foreach (var attachment in attachments.Take(8))
            {
                var path = attachment.ValueKind == JsonValueKind.String ? attachment.GetString() : null;
                builder.AppendLine($"  - {Path.GetFileName(path ?? string.Empty)}");
            }
        }

        var textChars = GetLong(value, "textChars");
        if (textChars > 0)
        {
            Append(builder, "正文", $"{textChars:N0} 字符");
        }

        return builder.ToString().TrimEnd();
    }

    private static string FormatMailList(IReadOnlyList<JsonElement> values)
    {
        var builder = new StringBuilder($"共 {values.Count} 封邮件\n");

        for (var index = 0; index < values.Count && index < MaxListItems; index++)
        {
            builder.AppendLine($"{index + 1}. {FormatMailLine(values[index])}");
        }

        if (values.Count > MaxListItems)
        {
            builder.AppendLine($"…仅显示前 {MaxListItems} 封，完整内容请展开原始 JSON");
        }

        return builder.ToString().TrimEnd();
    }

    private static string FormatMailLine(JsonElement value)
    {
        var subject = Truncate(GetString(value, "subject") ?? "(无主题)", 80);
        var from = GetString(value, "from") ?? GetString(value, "fromAddress") ?? "(未知发件人)";
        var date = FormatDate(GetString(value, "date"));

        var flags = new List<string>();
        if (GetBool(value, "unread"))
        {
            flags.Add("未读");
        }

        if (GetBool(value, "flagged"))
        {
            flags.Add("星标");
        }

        if (GetBool(value, "hasAttachments"))
        {
            flags.Add("带附件");
        }

        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(date))
        {
            parts.Add(date);
        }

        parts.Add(from);
        parts.Add(subject);

        if (flags.Count > 0)
        {
            parts.Add(string.Join("、", flags));
        }

        return string.Join(" · ", parts);
    }

    private static string FormatScalarList(IReadOnlyList<JsonElement> values)
    {
        var builder = new StringBuilder($"共 {values.Count} 项\n");
        foreach (var value in values.Take(12))
        {
            builder.AppendLine($"- {FormatValue(value)}");
        }

        if (values.Count > 12)
        {
            builder.AppendLine($"…另有 {values.Count - 12} 项");
        }

        return builder.ToString().TrimEnd();
    }

    private static string FormatSchema(JsonElement value)
    {
        var builder = new StringBuilder();
        Append(builder, "工具", GetString(value, "tool"));
        Append(builder, "版本", GetString(value, "version"));
        Append(builder, "传输", GetString(value, "transport"));
        Append(builder, "只读", GetBool(value, "readOnly") ? "是" : "否");

        var commands = GetObject(value, "commands")
            .Select(property => property.Name)
            .ToArray();
        if (commands.Length > 0)
        {
            Append(builder, "命令", $"{commands.Length} 个");
            Append(builder, "命令列表", string.Join("、", commands.Take(16)));
            if (commands.Length > 16)
            {
                Append(builder, "更多命令", $"另有 {commands.Length - 16} 个");
            }
        }

        return builder.ToString().TrimEnd();
    }

    private static string FormatGenericObject(JsonElement value)
    {
        var builder = new StringBuilder();

        foreach (var property in value.EnumerateObject())
        {
            var propertyValue = property.Value;
            var label = Labels.GetValueOrDefault(property.Name, property.Name);

            if (propertyValue.ValueKind == JsonValueKind.Object)
            {
                var count = propertyValue.EnumerateObject().Count();
                Append(builder, label, $"共 {count} 个字段");
                continue;
            }

            if (propertyValue.ValueKind == JsonValueKind.Array)
            {
                var items = propertyValue.EnumerateArray().ToList();
                if (string.Equals(property.Name, "attachments", StringComparison.OrdinalIgnoreCase))
                {
                    Append(builder, label, $"{items.Count} 个");
                    foreach (var item in items.Take(8))
                    {
                        var fileName = GetString(item, "fileName");
                        if (!string.IsNullOrWhiteSpace(fileName))
                        {
                            builder.AppendLine($"  - {fileName}");
                        }
                    }

                    continue;
                }

                if (items.Count == 0)
                {
                    Append(builder, label, "无");
                }
                else if (items.All(item => item.ValueKind != JsonValueKind.Object && item.ValueKind != JsonValueKind.Array))
                {
                    Append(builder, label, string.Join("、", items.Take(6).Select(FormatValue))
                        + (items.Count > 6 ? $" 等 {items.Count} 项" : string.Empty));
                }
                else
                {
                    Append(builder, label, $"共 {items.Count} 项");
                }

                continue;
            }

            var text = FormatValue(propertyValue);
            if (string.Equals(property.Name, "html", StringComparison.OrdinalIgnoreCase) && text.Length > 200)
            {
                text = $"HTML 正文（{text.Length:N0} 字符）";
            }
            else
            {
                text = Truncate(text, 240);
            }

            Append(builder, label, text);
        }

        return builder.ToString().TrimEnd();
    }

    private static string Compact(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            return FormatValue(value);
        }

        if (Has(value, "subject"))
        {
            return FormatMailLine(value);
        }

        var pairs = value.EnumerateObject()
            .Take(4)
            .Select(property => $"{Labels.GetValueOrDefault(property.Name, property.Name)}={FormatValue(property.Value)}");
        return string.Join("  ", pairs);
    }

    private static void Append(StringBuilder builder, string label, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            builder.AppendLine($"{label}：{value}");
        }
    }

    private static string? FormatAddresses(JsonElement value, string name)
    {
        if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(name, out var property))
        {
            return null;
        }

        if (property.ValueKind == JsonValueKind.String)
        {
            return property.GetString();
        }

        if (property.ValueKind != JsonValueKind.Array)
        {
            return property.GetRawText();
        }

        var items = property.EnumerateArray().ToList();
        if (items.Count == 0)
        {
            return null;
        }

        var normalized = items
            .Select(item => item.ValueKind == JsonValueKind.String
                ? item.GetString()
                : GetString(item, "address") ?? GetString(item, "email") ?? GetString(item, "name") ?? item.GetRawText())
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .ToArray();

        if (normalized.Length == 0)
        {
            return null;
        }

        return string.Join("、", normalized.Take(8))
            + (normalized.Length > 8 ? $" 等 {normalized.Length} 项" : string.Empty);
    }

    private static string CombineSender(string? name, string? address)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return address ?? string.Empty;
        }

        if (string.IsNullOrWhiteSpace(address) || name.Contains(address, StringComparison.OrdinalIgnoreCase))
        {
            return name;
        }

        return $"{name} <{address}>";
    }

    private static string FormatDate(string? text)
    {
        if (DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var value))
        {
            return value.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
        }

        return text ?? string.Empty;
    }

    private static string Preview(string text)
    {
        var normalized = Regex.Replace(text, "\\s+", " ").Trim();
        return Truncate(normalized, MaxBodyPreview);
    }

    private static string Truncate(string text, int max)
    {
        return text.Length <= max ? text : text[..max] + "…";
    }

    private static string FormatSize(long bytes)
    {
        if (bytes < 1024)
        {
            return $"{bytes} B";
        }

        if (bytes < 1024 * 1024)
        {
            return $"{bytes / 1024d:0.#} KB";
        }

        return $"{bytes / (1024d * 1024d):0.#} MB";
    }

    private static string? GetString(JsonElement value, string name)
    {
        if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(name, out var property))
        {
            return null;
        }

        return property.ValueKind switch
        {
            JsonValueKind.String => property.GetString(),
            JsonValueKind.Null => null,
            _ => property.GetRawText()
        };
    }

    private static long GetLong(JsonElement value, string name)
    {
        if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(name, out var property))
        {
            return 0;
        }

        return property.ValueKind == JsonValueKind.Number && property.TryGetInt64(out var number) ? number : 0;
    }

    private static bool GetBool(JsonElement value, string name)
        => value.ValueKind == JsonValueKind.Object
            && value.TryGetProperty(name, out var property)
            && property.ValueKind == JsonValueKind.True;

    private static List<JsonElement> GetArray(JsonElement value, string name)
        => value.ValueKind == JsonValueKind.Object
            && value.TryGetProperty(name, out var property)
            && property.ValueKind == JsonValueKind.Array
            ? property.EnumerateArray().ToList()
            : [];

    private static IEnumerable<JsonProperty> GetObject(JsonElement value, string name)
        => value.ValueKind == JsonValueKind.Object
            && value.TryGetProperty(name, out var property)
            && property.ValueKind == JsonValueKind.Object
            ? property.EnumerateObject()
            : [];

    private static bool Has(JsonElement value, string name)
        => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out _);
}
