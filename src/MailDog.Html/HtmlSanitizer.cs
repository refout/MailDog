using System.Net;
using System.Text;

namespace MailDog.Html;

public sealed record HtmlSanitizationResult(
    string Html,
    bool Truncated,
    bool RemoteImagesBlocked);

/// <summary>
/// 白名单式 HTML 净化器。输入是不可信邮件 HTML，输出只包含允许的标签与属性。
/// 不使用正则、不依赖第三方解析器，保证 AOT 与可审计性。
/// </summary>
public sealed class HtmlSanitizer
{
    private const int MaxInputChars = 2 * 1024 * 1024;
    private const int MaxOutputChars = 2 * 1024 * 1024;
    private const int MaxDepth = 64;

    private static readonly HashSet<string> AllowedTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "p", "div", "span", "br", "hr", "blockquote", "pre", "code",
        "b", "strong", "i", "em", "u", "s", "sub", "sup",
        "h1", "h2", "h3", "h4", "h5", "h6",
        "ul", "ol", "li",
        "table", "thead", "tbody", "tfoot", "tr", "td", "th",
        "a", "img"
    };

    private static readonly HashSet<string> VoidTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "br", "hr", "img"
    };

    private static readonly HashSet<string> DangerousTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "script", "style", "iframe", "frame", "frameset", "object", "embed", "applet",
        "form", "input", "button", "select", "textarea", "option",
        "link", "base", "meta", "svg", "math", "video", "audio", "canvas",
        "template", "noscript", "plaintext", "xmp", "listing", "marquee"
    };

    public HtmlSanitizationResult Sanitize(string? rawHtml, bool allowRemoteImages = false)
    {
        if (string.IsNullOrWhiteSpace(rawHtml))
        {
            return new HtmlSanitizationResult(string.Empty, false, false);
        }

        var truncated = false;
        var input = rawHtml;

        if (input.Length > MaxInputChars)
        {
            input = input[..MaxInputChars];
            truncated = true;
        }

        var state = new SanitizerState(allowRemoteImages, truncated);
        state.Run(input);

        return new HtmlSanitizationResult(state.Output, state.Truncated, state.RemoteImagesBlocked);
    }

    private sealed class SanitizerState
    {
        private readonly bool _allowRemoteImages;
        private readonly StringBuilder _output = new();
        private readonly List<string> _openTags = [];

        public SanitizerState(bool allowRemoteImages, bool truncated)
        {
            _allowRemoteImages = allowRemoteImages;
            Truncated = truncated;
        }

        public string Output => _output.ToString();

        public bool Truncated { get; private set; }

        public bool RemoteImagesBlocked { get; private set; }

        public void Run(string input)
        {
            var index = 0;

            while (index < input.Length && !Truncated)
            {
                if (input[index] == '<')
                {
                    index = ParseMarkup(input, index);
                    continue;
                }

                var next = input.IndexOf('<', index);
                if (next < 0)
                {
                    next = input.Length;
                }

                AppendText(input.AsSpan(index, next - index));
                index = next;
            }

            CloseOpenTags();
        }

        private int ParseMarkup(string input, int index)
        {
            if (StartsWith(input, index, "<!--"))
            {
                var end = input.IndexOf("-->", index + 4, StringComparison.Ordinal);
                return end < 0 ? input.Length : end + 3;
            }

            if (StartsWith(input, index, "<![CDATA["))
            {
                var end = input.IndexOf("]]>", index + 9, StringComparison.Ordinal);
                return end < 0 ? input.Length : end + 3;
            }

            if (index + 1 >= input.Length)
            {
                AppendRaw("&lt;");
                return index + 1;
            }

            var next = input[index + 1];

            if (next is '!' or '?')
            {
                var end = FindTagEnd(input, index + 2);
                return end < 0 ? input.Length : end + 1;
            }

            if (next == '/')
            {
                return ParseCloseTag(input, index);
            }

            if (!char.IsLetter(next))
            {
                AppendRaw("&lt;");
                return index + 1;
            }

            return ParseStartTag(input, index);
        }

        private int ParseCloseTag(string input, int index)
        {
            var nameStart = index + 2;
            var nameEnd = nameStart;

            while (nameEnd < input.Length && IsTagNameChar(input[nameEnd]))
            {
                nameEnd++;
            }

            var tagEnd = FindTagEnd(input, nameEnd);
            var next = tagEnd < 0 ? input.Length : tagEnd + 1;

            if (nameEnd == nameStart)
            {
                return next;
            }

            var name = input[nameStart..nameEnd].ToLowerInvariant();

            if (!AllowedTags.Contains(name) || VoidTags.Contains(name))
            {
                return next;
            }

            var openIndex = _openTags.FindLastIndex(tag => tag.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (openIndex < 0)
            {
                return next;
            }

            while (_openTags.Count > openIndex)
            {
                var tag = _openTags[^1];
                _openTags.RemoveAt(_openTags.Count - 1);
                AppendRaw("</" + tag + ">");
            }

            return next;
        }

        private int ParseStartTag(string input, int index)
        {
            var nameStart = index + 1;
            var nameEnd = nameStart;

            while (nameEnd < input.Length && IsTagNameChar(input[nameEnd]))
            {
                nameEnd++;
            }

            var name = input[nameStart..nameEnd].ToLowerInvariant();

            if (DangerousTags.Contains(name))
            {
                return SkipDangerousElement(input, nameEnd, name);
            }

            var tagEnd = FindTagEnd(input, nameEnd);
            if (tagEnd < 0)
            {
                return input.Length;
            }

            var attributes = ParseAttributes(input, nameEnd, tagEnd, name, out var selfClosing);
            var next = tagEnd + 1;

            if (!AllowedTags.Contains(name))
            {
                return next;
            }

            if (name == "img")
            {
                if (!_allowRemoteImages)
                {
                    RemoteImagesBlocked = true;
                    return next;
                }

                var source = attributes
                    .Where(attribute => attribute.Name == "src")
                    .Select(attribute => attribute.Value)
                    .FirstOrDefault();

                if (string.IsNullOrEmpty(source))
                {
                    return next;
                }
            }

            if (!VoidTags.Contains(name) && _openTags.Count >= MaxDepth)
            {
                return next;
            }

            var builder = new StringBuilder(64);
            builder.Append('<').Append(name);

            foreach (var (attributeName, attributeValue) in attributes)
            {
                builder.Append(' ')
                    .Append(attributeName)
                    .Append("=\"")
                    .Append(EscapeAttribute(attributeValue))
                    .Append('"');
            }

            if (VoidTags.Contains(name))
            {
                builder.Append(selfClosing ? " />" : ">");
                AppendRaw(builder.ToString());
                return next;
            }

            builder.Append('>');
            AppendRaw(builder.ToString());
            _openTags.Add(name);

            return next;
        }

        private List<(string Name, string Value)> ParseAttributes(
            string input,
            int start,
            int end,
            string tagName,
            out bool selfClosing)
        {
            var attributes = new List<(string Name, string Value)>();
            var index = start;
            selfClosing = false;

            while (index < end)
            {
                while (index < end && char.IsWhiteSpace(input[index]))
                {
                    index++;
                }

                if (index >= end)
                {
                    break;
                }

                if (input[index] == '>')
                {
                    break;
                }

                if (input[index] == '/')
                {
                    selfClosing = true;
                    index++;
                    continue;
                }

                var nameStart = index;
                while (index < end
                       && !char.IsWhiteSpace(input[index])
                       && input[index] is not '=' and not '>' and not '/')
                {
                    index++;
                }

                if (index == nameStart)
                {
                    index++;
                    continue;
                }

                var name = input[nameStart..index].ToLowerInvariant();

                while (index < end && char.IsWhiteSpace(input[index]))
                {
                    index++;
                }

                var value = string.Empty;
                if (index < end && input[index] == '=')
                {
                    index++;
                    while (index < end && char.IsWhiteSpace(input[index]))
                    {
                        index++;
                    }

                    if (index < end && input[index] is '"' or '\'')
                    {
                        var quote = input[index++];
                        var valueStart = index;
                        while (index < end && input[index] != quote)
                        {
                            index++;
                        }

                        value = input[valueStart..index];
                        if (index < end)
                        {
                            index++;
                        }
                    }
                    else
                    {
                        var valueStart = index;
                        while (index < end && !char.IsWhiteSpace(input[index]) && input[index] != '>')
                        {
                            index++;
                        }

                        value = input[valueStart..index];
                    }
                }

                if (!IsAllowedAttribute(tagName, name) || value.Length == 0)
                {
                    continue;
                }

                var normalized = NormalizeAttributeValue(tagName, name, value);
                if (normalized is not null)
                {
                    attributes.Add((name, normalized));
                }
            }

            return attributes;
        }

        private static bool IsAllowedAttribute(string tagName, string name)
        {
            if (name.StartsWith("on", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (name is "style" or "class" or "id" or "srcset" or "formaction" or "xmlns" or "xlink:href")
            {
                return false;
            }

            return tagName switch
            {
                "a" => name is "href" or "title",
                "img" => name is "src" or "alt" or "width" or "height" or "title",
                _ => name is "title" or "width" or "height" or "colspan" or "rowspan" or "align" or "valign"
            };
        }

        private static string? NormalizeAttributeValue(string tagName, string name, string value)
        {
            if (name is "href" or "src")
            {
                return NormalizeUrl(value, isImage: tagName == "img");
            }

            if (name is "width" or "height")
            {
                return IsSafeDimension(value) ? value.Trim() : null;
            }

            if (name is "colspan" or "rowspan")
            {
                return int.TryParse(value, out var number) && number is > 0 and <= 100 ? value : null;
            }

            if (name is "align" or "valign")
            {
                var lower = value.Trim().ToLowerInvariant();
                return lower is "left" or "right" or "center" or "justify" or "top" or "middle" or "bottom"
                    ? lower
                    : null;
            }

            return value;
        }

        private static bool IsSafeDimension(string value)
        {
            var trimmed = value.Trim();
            if (trimmed.EndsWith('%'))
            {
                trimmed = trimmed[..^1];
            }

            return int.TryParse(trimmed, out var number) && number is > 0 and <= 4096;
        }

        private static string? NormalizeUrl(string value, bool isImage)
        {
            var decoded = WebUtility.HtmlDecode(value);

            var builder = new StringBuilder(decoded.Length);
            foreach (var character in decoded)
            {
                if (character is '\r' or '\n' or '\t' || char.IsControl(character))
                {
                    continue;
                }

                builder.Append(character);
            }

            var cleaned = builder.ToString().Trim();
            if (cleaned.Length == 0)
            {
                return null;
            }

            if (cleaned.StartsWith("//", StringComparison.Ordinal))
            {
                return null;
            }

            var lower = cleaned.ToLowerInvariant();

            if (isImage)
            {
                return lower.StartsWith("https://", StringComparison.Ordinal) ? cleaned : null;
            }

            if (lower.StartsWith("http://", StringComparison.Ordinal)
                || lower.StartsWith("https://", StringComparison.Ordinal)
                || lower.StartsWith("mailto:", StringComparison.Ordinal))
            {
                return cleaned;
            }

            return cleaned.StartsWith('#') ? cleaned : null;
        }

        private int SkipDangerousElement(string input, int contentStart, string name)
        {
            var closingTag = "</" + name;
            var closeIndex = input.IndexOf(closingTag, contentStart, StringComparison.OrdinalIgnoreCase);
            if (closeIndex < 0)
            {
                return input.Length;
            }

            var tagEnd = FindTagEnd(input, closeIndex + closingTag.Length);
            return tagEnd < 0 ? input.Length : tagEnd + 1;
        }

        private static int FindTagEnd(string input, int start)
        {
            var quote = '\0';

            for (var index = start; index < input.Length; index++)
            {
                var character = input[index];

                if (quote != '\0')
                {
                    if (character == quote)
                    {
                        quote = '\0';
                    }

                    continue;
                }

                if (character is '"' or '\'')
                {
                    quote = character;
                    continue;
                }

                if (character == '>')
                {
                    return index;
                }
            }

            return -1;
        }

        private void AppendText(ReadOnlySpan<char> text)
        {
            foreach (var character in text)
            {
                if (character == '\0' || (char.IsControl(character) && character is not '\t' and not '\n' and not '\r'))
                {
                    continue;
                }

                if (_output.Length >= MaxOutputChars)
                {
                    Truncated = true;
                    return;
                }

                _output.Append(character);
            }
        }

        private void AppendRaw(string text)
        {
            if (text.Length == 0)
            {
                return;
            }

            if (_output.Length + text.Length > MaxOutputChars)
            {
                var remaining = MaxOutputChars - _output.Length;
                if (remaining > 0)
                {
                    _output.Append(text.AsSpan(0, remaining));
                }

                Truncated = true;
                return;
            }

            _output.Append(text);
        }

        private void CloseOpenTags()
        {
            while (_openTags.Count > 0)
            {
                var tag = _openTags[^1];
                _openTags.RemoveAt(_openTags.Count - 1);
                AppendRaw("</" + tag + ">");
            }
        }

        private static string EscapeAttribute(string value)
            => value.Replace("&", "&amp;", StringComparison.Ordinal)
                .Replace("\"", "&quot;", StringComparison.Ordinal)
                .Replace("<", "&lt;", StringComparison.Ordinal)
                .Replace(">", "&gt;", StringComparison.Ordinal);

        private static bool StartsWith(string input, int index, string value)
            => index + value.Length <= input.Length
               && input.AsSpan(index, value.Length).Equals(value, StringComparison.Ordinal);

        private static bool IsTagNameChar(char character)
            => char.IsLetterOrDigit(character) || character is '-' or '_' or ':';
    }
}
