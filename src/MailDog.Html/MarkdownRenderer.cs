using System.Net;
using System.Text;

namespace MailDog.Html;

/// <summary>
/// 轻量 Markdown 转 HTML，仅覆盖邮件常用语法。所有原始 HTML 都会被转义，
/// 链接只允许 http/https/mailto，避免把不可信内容直接带进外发邮件。
/// </summary>
public static class MarkdownRenderer
{
    private const string WrapperStart =
        "<div style=\"font-family:'Segoe UI','Microsoft YaHei',sans-serif;font-size:14px;line-height:1.6\">";

    public static string ToHtml(string? markdown)
    {
        var normalized = (markdown ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n');
        var body = RenderBlocks(normalized.Split('\n'));
        return WrapperStart + body + "</div>";
    }

    private static string RenderBlocks(string[] lines)
    {
        var html = new StringBuilder();
        var index = 0;

        while (index < lines.Length)
        {
            var line = lines[index];

            if (string.IsNullOrWhiteSpace(line))
            {
                index++;
                continue;
            }

            if (line.StartsWith("```", StringComparison.Ordinal))
            {
                index = RenderCodeBlock(lines, index, html);
                continue;
            }

            if (TryHeading(line, out var level, out var heading))
            {
                html.Append("<h").Append(level).Append('>')
                    .Append(RenderInline(heading))
                    .Append("</h").Append(level).Append('>');
                index++;
                continue;
            }

            if (IsHorizontalRule(line))
            {
                html.Append("<hr/>");
                index++;
                continue;
            }

            if (line.StartsWith('>'))
            {
                index = RenderQuote(lines, index, html);
                continue;
            }

            if (TryListMarker(line, out _, out _))
            {
                index = RenderList(lines, index, html);
                continue;
            }

            if (TryRenderTable(lines, index, html, out var nextIndex))
            {
                index = nextIndex;
                continue;
            }

            index = RenderParagraph(lines, index, html);
        }

        return html.ToString();
    }

    private static int RenderCodeBlock(string[] lines, int index, StringBuilder html)
    {
        index++;
        var code = new StringBuilder();

        while (index < lines.Length && !lines[index].StartsWith("```", StringComparison.Ordinal))
        {
            code.Append(lines[index]).Append('\n');
            index++;
        }

        if (index < lines.Length)
        {
            index++;
        }

        html.Append("<pre style=\"background:#f6f6f6;padding:10px;border-radius:6px;overflow:auto\"><code>")
            .Append(WebUtility.HtmlEncode(code.ToString().TrimEnd('\n')))
            .Append("</code></pre>");
        return index;
    }

    private static int RenderQuote(string[] lines, int index, StringBuilder html)
    {
        var content = new StringBuilder();

        while (index < lines.Length && lines[index].StartsWith('>'))
        {
            var line = lines[index][1..];
            if (line.StartsWith(' '))
            {
                line = line[1..];
            }

            content.Append(line).Append('\n');
            index++;
        }

        html.Append("<blockquote style=\"margin:8px 0;padding:6px 12px;border-left:3px solid #c8c8c8;color:#555\">")
            .Append(RenderInline(content.ToString().TrimEnd('\n')))
            .Append("</blockquote>");
        return index;
    }

    private static int RenderList(string[] lines, int index, StringBuilder html)
    {
        var ordered = char.IsDigit(lines[index].TrimStart()[0]);
        html.Append(ordered ? "<ol>" : "<ul>");

        while (index < lines.Length && TryListMarker(lines[index], out _, out var item))
        {
            html.Append("<li>").Append(RenderInline(item)).Append("</li>");
            index++;
        }

        html.Append(ordered ? "</ol>" : "</ul>");
        return index;
    }

    private static bool TryRenderTable(string[] lines, int index, StringBuilder html, out int nextIndex)
    {
        nextIndex = index;
        if (index + 1 >= lines.Length || !lines[index].Contains('|'))
        {
            return false;
        }

        var header = SplitTableRow(lines[index]);
        var separator = SplitTableRow(lines[index + 1]);
        if (header.Length < 2 || separator.Length != header.Length || !IsTableSeparator(separator))
        {
            return false;
        }

        html.Append("<table style=\"border-collapse:collapse;width:100%;margin:8px 0\"><thead><tr>");
        for (var column = 0; column < header.Length; column++)
        {
            html.Append("<th style=\"border:1px solid #d0d0d0;padding:6px;text-align:")
                .Append(TableAlignment(separator[column]))
                .Append("\">")
                .Append(RenderInline(header[column]))
                .Append("</th>");
        }

        html.Append("</tr></thead><tbody>");
        var rowIndex = index + 2;
        while (rowIndex < lines.Length && !string.IsNullOrWhiteSpace(lines[rowIndex]) && lines[rowIndex].Contains('|'))
        {
            var cells = SplitTableRow(lines[rowIndex]);
            html.Append("<tr>");
            for (var column = 0; column < header.Length; column++)
            {
                html.Append("<td style=\"border:1px solid #d0d0d0;padding:6px;text-align:")
                    .Append(TableAlignment(separator[column]))
                    .Append("\">")
                    .Append(RenderInline(column < cells.Length ? cells[column] : string.Empty))
                    .Append("</td>");
            }

            html.Append("</tr>");
            rowIndex++;
        }

        html.Append("</tbody></table>");
        nextIndex = rowIndex;
        return true;
    }

    private static string[] SplitTableRow(string line)
    {
        var trimmed = line.Trim();
        if (trimmed.StartsWith('|'))
        {
            trimmed = trimmed[1..];
        }

        if (trimmed.EndsWith('|'))
        {
            trimmed = trimmed[..^1];
        }

        return trimmed.Split('|').Select(cell => cell.Trim()).ToArray();
    }

    private static bool IsTableSeparator(string[] cells)
    {
        return cells.All(cell =>
        {
            var value = cell.Trim().Trim(':');
            return value.Length >= 3 && value.All(character => character == '-');
        });
    }

    private static string TableAlignment(string separatorCell)
    {
        var value = separatorCell.Trim();
        if (value.StartsWith(':') && value.EndsWith(':'))
        {
            return "center";
        }

        if (value.EndsWith(':'))
        {
            return "right";
        }

        return "left";
    }

    private static int RenderParagraph(string[] lines, int index, StringBuilder html)    {
        var content = new StringBuilder();

        while (index < lines.Length && !string.IsNullOrWhiteSpace(lines[index]))
        {
            var line = lines[index];

            if (content.Length > 0
                && (line.StartsWith("```", StringComparison.Ordinal)
                    || TryHeading(line, out _, out _)
                    || line.StartsWith('>')
                    || TryListMarker(line, out _, out _)
                    || IsHorizontalRule(line)))
            {
                break;
            }

            if (content.Length > 0)
            {
                content.Append('\n');
            }

            content.Append(line);
            index++;
        }

        html.Append("<p>").Append(RenderInline(content.ToString())).Append("</p>");
        return index;
    }

    private static string RenderInline(string text)
    {
        var html = new StringBuilder();
        var index = 0;

        while (index < text.Length)
        {
            var current = text[index];

            if (current == '\\' && index + 1 < text.Length && IsEscapable(text[index + 1]))
            {
                html.Append(WebUtility.HtmlEncode(text[index + 1].ToString()));
                index += 2;
                continue;
            }

            if (current == '`')
            {
                var end = text.IndexOf('`', index + 1);
                if (end > index)
                {
                    html.Append("<code>")
                        .Append(WebUtility.HtmlEncode(text[(index + 1)..end]))
                        .Append("</code>");
                    index = end + 1;
                    continue;
                }
            }

            if (current == '~' && index + 1 < text.Length && text[index + 1] == '~')
            {
                var end = text.IndexOf("~~", index + 2, StringComparison.Ordinal);
                if (end > index)
                {
                    html.Append("<s>")
                        .Append(RenderInline(text[(index + 2)..end]))
                        .Append("</s>");
                    index = end + 2;
                    continue;
                }
            }

            if (current == '*' && index + 1 < text.Length && text[index + 1] == '*')
            {
                var end = text.IndexOf("**", index + 2, StringComparison.Ordinal);
                if (end > index)
                {
                    html.Append("<strong>")
                        .Append(RenderInline(text[(index + 2)..end]))
                        .Append("</strong>");
                    index = end + 2;
                    continue;
                }
            }

            if (current == '*')
            {
                var end = text.IndexOf('*', index + 1);
                if (end > index)
                {
                    html.Append("<em>")
                        .Append(RenderInline(text[(index + 1)..end]))
                        .Append("</em>");
                    index = end + 1;
                    continue;
                }
            }

            if (current == '!' && index + 1 < text.Length && text[index + 1] == '[')
            {
                var close = text.IndexOf(']', index + 2);
                if (close > index
                    && close + 1 < text.Length
                    && text[close + 1] == '(')
                {
                    var end = text.IndexOf(')', close + 2);
                    if (end > close)
                    {
                        AppendImage(html, text[(index + 2)..close], text[(close + 2)..end]);
                        index = end + 1;
                        continue;
                    }
                }
            }

            if (current == '[')
            {
                var close = text.IndexOf(']', index + 1);
                if (close > index
                    && close + 1 < text.Length
                    && text[close + 1] == '(')
                {
                    var end = text.IndexOf(')', close + 2);
                    if (end > close)
                    {
                        AppendLink(html, text[(index + 1)..close], text[(close + 2)..end]);
                        index = end + 1;
                        continue;
                    }
                }
            }

            if (current == '\n')
            {
                html.Append("<br/>");
                index++;
                continue;
            }

            html.Append(WebUtility.HtmlEncode(current.ToString()));
            index++;
        }

        return html.ToString();
    }

    private static bool IsEscapable(char value)
        => value is '\\' or '`' or '*' or '_' or '[' or ']' or '(' or ')' or '#'
            or '+' or '-' or '.' or '!' or '>' or '|' or '~' or '<' or '=' or ':';

    private static void AppendImage(StringBuilder html, string alt, string url)
    {
        var target = url.Trim();
        if (Uri.TryCreate(target, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
        {
            html.Append("<img src=\"").Append(WebUtility.HtmlEncode(target)).Append("\" alt=\"")
                .Append(WebUtility.HtmlEncode(alt))
                .Append("\" style=\"max-width:100%;height:auto\" />");
            return;
        }

        html.Append(WebUtility.HtmlEncode(alt))
            .Append(" (")
            .Append(WebUtility.HtmlEncode(target))
            .Append(')');
    }

    private static void AppendLink(StringBuilder html, string label, string url)    {
        var target = url.Trim();

        if (Uri.TryCreate(target, UriKind.Absolute, out var uri)
            && uri.Scheme is "http" or "https" or "mailto")
        {
            html.Append("<a href=\"").Append(WebUtility.HtmlEncode(target)).Append("\">")
                .Append(RenderInline(label))
                .Append("</a>");
            return;
        }

        html.Append(RenderInline(label))
            .Append(" (")
            .Append(WebUtility.HtmlEncode(target))
            .Append(')');
    }

    private static bool TryHeading(string line, out int level, out string text)
    {
        level = 0;
        text = string.Empty;
        var count = 0;

        while (count < line.Length && count < 6 && line[count] == '#')
        {
            count++;
        }

        if (count == 0 || count >= line.Length || line[count] != ' ')
        {
            return false;
        }

        level = count;
        text = line[(count + 1)..].Trim();
        return text.Length > 0;
    }

    private static bool TryListMarker(string line, out bool ordered, out string item)
    {
        ordered = false;
        item = string.Empty;
        var trimmed = line.TrimStart();

        if (trimmed.Length >= 2 && (trimmed[0] == '-' || trimmed[0] == '*' || trimmed[0] == '+') && trimmed[1] == ' ')
        {
            item = trimmed[2..].Trim();
            return item.Length > 0;
        }

        var digits = 0;
        while (digits < trimmed.Length && char.IsDigit(trimmed[digits]))
        {
            digits++;
        }

        if (digits > 0 && digits + 1 < trimmed.Length && trimmed[digits] == '.' && trimmed[digits + 1] == ' ')
        {
            ordered = true;
            item = trimmed[(digits + 2)..].Trim();
            return item.Length > 0;
        }

        return false;
    }

    private static bool IsHorizontalRule(string line)
    {
        var trimmed = line.Trim();
        if (trimmed.Length < 3)
        {
            return false;
        }

        return trimmed.All(character => character == '-' || character == '*' || character == '_');
    }
}
