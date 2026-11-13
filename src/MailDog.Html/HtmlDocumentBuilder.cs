using System.Text;

namespace MailDog.Html;

/// <summary>把净化后的片段包装成完整文档，并注入 CSP、viewport 与基础排版样式。</summary>
public sealed class HtmlDocumentBuilder
{
    private const string LightCss = """
        :root { color-scheme: light; }
        body {
          margin: 0; padding: 24px;
          font-family: "Segoe UI", "Microsoft YaHei UI", "PingFang SC", "Noto Sans SC", sans-serif;
          font-size: 14px; line-height: 1.65;
          color: #1b1b1b; background: #ffffff;
          word-wrap: break-word; overflow-wrap: anywhere;
        }
        a { color: #0f6cbd; text-decoration: none; }
        a:hover { text-decoration: underline; }
        img { max-width: 100%; height: auto; }
        table { max-width: 100%; border-collapse: collapse; }
        td, th { padding: 6px 8px; border: 1px solid #e0e0e0; vertical-align: top; }
        blockquote { margin: 12px 0; padding: 4px 16px; border-left: 3px solid #d0d0d0; color: #5c5c5c; }
        pre, code { font-family: "Cascadia Mono", Consolas, monospace; font-size: 13px; }
        pre { padding: 12px; background: #f6f6f6; border-radius: 6px; overflow-x: auto; }
        h1, h2, h3, h4, h5, h6 { line-height: 1.3; margin: 20px 0 8px; }
        h1 { font-size: 24px; } h2 { font-size: 20px; } h3 { font-size: 17px; }
        p { margin: 0 0 12px; }
        hr { border: 0; border-top: 1px solid #e0e0e0; margin: 20px 0; }
        """;

    private const string DarkCss = """
        :root { color-scheme: dark; }
        body {
          margin: 0; padding: 24px;
          font-family: "Segoe UI", "Microsoft YaHei UI", "PingFang SC", "Noto Sans SC", sans-serif;
          font-size: 14px; line-height: 1.65;
          color: #ffffff; background: #272727;
          word-wrap: break-word; overflow-wrap: anywhere;
        }
        a { color: #479ef5; text-decoration: none; }
        a:hover { text-decoration: underline; }
        img { max-width: 100%; height: auto; }
        table { max-width: 100%; border-collapse: collapse; }
        td, th { padding: 6px 8px; border: 1px solid #3d3d3d; vertical-align: top; }
        blockquote { margin: 12px 0; padding: 4px 16px; border-left: 3px solid #4d4d4d; color: #c7c7c7; }
        pre, code { font-family: "Cascadia Mono", Consolas, monospace; font-size: 13px; }
        pre { padding: 12px; background: #1f1f1f; border-radius: 6px; overflow-x: auto; }
        h1, h2, h3, h4, h5, h6 { line-height: 1.3; margin: 20px 0 8px; }
        h1 { font-size: 24px; } h2 { font-size: 20px; } h3 { font-size: 17px; }
        p { margin: 0 0 12px; }
        hr { border: 0; border-top: 1px solid #3d3d3d; margin: 20px 0; }
        """;

    public string Build(string sanitizedHtml, bool darkMode, bool allowRemoteImages)
    {
        var imageSource = allowRemoteImages ? "https:" : "'none'";
        var css = darkMode ? DarkCss : LightCss;

        var builder = new StringBuilder(sanitizedHtml.Length + css.Length + 1024);
        builder.Append("<!doctype html><html><head><meta charset=\"utf-8\">")
            .Append("<meta http-equiv=\"Content-Security-Policy\" content=\"")
            .Append("default-src 'none'; ")
            .Append("img-src ").Append(imageSource).Append("; ")
            .Append("style-src 'unsafe-inline'; ")
            .Append("script-src 'none'; object-src 'none'; frame-src 'none'; ")
            .Append("connect-src 'none'; form-action 'none'; base-uri 'none'\">")
            .Append("<meta name=\"referrer\" content=\"no-referrer\">")
            .Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">")
            .Append("<style>").Append(css).Append("</style>")
            .Append("</head><body>")
            .Append(sanitizedHtml)
            .Append("</body></html>");

        return builder.ToString();
    }
}
