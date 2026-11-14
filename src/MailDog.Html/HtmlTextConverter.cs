using System.Net;
using System.Text;

namespace MailDog.Html;

/// <summary>把邮件 HTML 转成可读纯文本，用于“纯文本”渲染模式。</summary>
public static class HtmlTextConverter
{
    private static readonly HashSet<string> BreakTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "br", "p", "div", "tr", "li", "h1", "h2", "h3", "h4", "h5", "h6",
        "blockquote", "pre", "table", "ul", "ol", "hr",
        "section", "article", "header", "footer", "address", "caption"
    };

    private static readonly HashSet<string> CellTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "td", "th"
    };

    public static string ToPlainText(string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(html.Length);
        var index = 0;

        while (index < html.Length)
        {
            var tagStart = html.IndexOf('<', index);
            if (tagStart < 0)
            {
                builder.Append(html, index, html.Length - index);
                break;
            }

            if (tagStart > index)
            {
                builder.Append(html, index, tagStart - index);
            }

            var tagEnd = html.IndexOf('>', tagStart + 1);
            if (tagEnd < 0)
            {
                break;
            }

            var name = ReadTagName(html.AsSpan(tagStart + 1, tagEnd - tagStart - 1));

            if (name is "style" or "script" or "head")
            {
                index = SkipElement(html, tagEnd + 1, name);
                continue;
            }

            if (BreakTags.Contains(name) || CellTags.Contains(name))
            {
                builder.Append('\n');
            }

            index = tagEnd + 1;
        }

        return Normalize(WebUtility.HtmlDecode(builder.ToString()));
    }

    private static string ReadTagName(ReadOnlySpan<char> tag)
    {
        tag = tag.Trim();
        if (tag.Length > 0 && tag[0] == '/')
        {
            tag = tag[1..].TrimStart();
        }

        var end = 0;
        while (end < tag.Length && (char.IsLetterOrDigit(tag[end]) || tag[end] == '-'))
        {
            end++;
        }

        return tag[..end].ToString().ToLowerInvariant();
    }

    private static int SkipElement(string html, int contentStart, string name)
    {
        var closing = html.IndexOf("</" + name, contentStart, StringComparison.OrdinalIgnoreCase);
        if (closing < 0)
        {
            return html.Length;
        }

        var end = html.IndexOf('>', closing + name.Length + 2);
        return end < 0 ? html.Length : end + 1;
    }

    private static string Normalize(string text)
    {
        text = text.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Replace('\u00A0', ' ');

        var result = new StringBuilder(text.Length);
        var pendingBreaks = 0;

        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0)
            {
                pendingBreaks = Math.Min(pendingBreaks + 1, 2);
                continue;
            }

            if (result.Length > 0)
            {
                result.Append('\n', Math.Max(1, pendingBreaks));
            }

            pendingBreaks = 0;
            result.Append(line);
        }

        return result.ToString();
    }
}
