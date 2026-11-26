using System.Text;

namespace MailDog.Html;

/// <summary>把净化后的片段包装成完整文档，并注入 CSP、viewport 与基础排版样式。</summary>
public sealed class HtmlDocumentBuilder
{
    private const string LightCss = """
        :root { color-scheme: light; }
        *, *::before, *::after { box-sizing: border-box !important; }
        html, body { max-width: 100%; overflow-x: hidden; }
        body {
          margin: 0; padding: 16px;
          font-family: "Segoe UI", "Microsoft YaHei UI", "PingFang SC", "Noto Sans SC", sans-serif;
          font-size: 14px; line-height: 1.65;
          color: #1b1b1b; background: #ffffff;
          word-wrap: break-word; overflow-wrap: anywhere;
        }
        .maildog-mail {
          box-sizing: border-box; width: 100%;
          max-width: 900px; margin: 0 auto; padding: 24px;
          background: #ffffff; color: #1b1b1b;
          border-radius: 10px;
        }
        .maildog-mail a { color: #0f6cbd; }
        .maildog-mail pre { background: #f6f6f6; color: #1b1b1b; }
        a { color: #0f6cbd; text-decoration: none; }
        a:hover { text-decoration: underline; }
        img { max-width: 100% !important; height: auto !important; }
        table { width: 100% !important; max-width: 100% !important; table-layout: auto !important; }
        td, th { width: auto !important; max-width: 100% !important; }
        blockquote { margin: 12px 0; padding: 4px 16px; border-left: 3px solid #d0d0d0; color: #5c5c5c; }
        pre, code { font-family: "Cascadia Mono", Consolas, monospace; font-size: 13px; }
        pre { padding: 12px; background: #f6f6f6; border-radius: 6px; overflow-x: auto; }
        h1, h2, h3, h4, h5, h6 { line-height: 1.3; margin: 20px 0 8px; }
        h1 { font-size: 24px; } h2 { font-size: 20px; } h3 { font-size: 17px; }
        p { margin: 0 0 12px; }
        hr { border: 0; border-top: 1px solid #e0e0e0; margin: 20px 0; }
        ::-webkit-scrollbar { width: 10px; height: 10px; }
        ::-webkit-scrollbar-track { background: transparent; }
        ::-webkit-scrollbar-thumb {
          background: rgba(0, 0, 0, 0.26);
          border: 2px solid transparent;
          border-radius: 5px;
          background-clip: content-box;
        }
        ::-webkit-scrollbar-thumb:hover { background: rgba(0, 0, 0, 0.42); background-clip: content-box; }
        @media (max-width: 560px) {
          body { padding: 8px; }
          .maildog-mail { padding: 14px; border-radius: 6px; }
        }
        """;

    private const string DarkCss = """
        :root { color-scheme: dark; }
        *, *::before, *::after { box-sizing: border-box !important; }
        html, body { max-width: 100%; overflow-x: hidden; }
        body {
          margin: 0; padding: 16px;
          font-family: "Segoe UI", "Microsoft YaHei UI", "PingFang SC", "Noto Sans SC", sans-serif;
          font-size: 14px; line-height: 1.65;
          color: #e8e8e8; background: #272727;
          word-wrap: break-word; overflow-wrap: anywhere;
        }
        .maildog-mail {
          box-sizing: border-box; width: 100%;
          max-width: 900px; margin: 0 auto; padding: 24px;
          background: #ffffff; color: #1b1b1b;
          border-radius: 10px;
        }
        .maildog-mail a { color: #0f6cbd; }
        .maildog-mail pre { background: #f6f6f6; color: #1b1b1b; }
        a { color: #7cc0ff; text-decoration: none; }
        a:hover { text-decoration: underline; }
        img { max-width: 100% !important; height: auto !important; opacity: 0.92; }
        table { width: 100% !important; max-width: 100% !important; table-layout: auto !important; }
        td, th { width: auto !important; max-width: 100% !important; }
        blockquote { margin: 12px 0; padding: 4px 16px; border-left: 3px solid #4d4d4d; color: #c7c7c7; }
        pre, code { font-family: "Cascadia Mono", Consolas, monospace; font-size: 13px; }
        pre { padding: 12px; background: #1f1f1f; border-radius: 6px; overflow-x: auto; }
        h1, h2, h3, h4, h5, h6 { line-height: 1.3; margin: 20px 0 8px; }
        h1 { font-size: 24px; } h2 { font-size: 20px; } h3 { font-size: 17px; }
        p { margin: 0 0 12px; }
        hr { border: 0; border-top: 1px solid #3d3d3d; margin: 20px 0; }
        ::-webkit-scrollbar { width: 10px; height: 10px; }
        ::-webkit-scrollbar-track { background: transparent; }
        ::-webkit-scrollbar-thumb {
          background: rgba(255, 255, 255, 0.28);
          border: 2px solid transparent;
          border-radius: 5px;
          background-clip: content-box;
        }
        ::-webkit-scrollbar-thumb:hover { background: rgba(255, 255, 255, 0.46); background-clip: content-box; }
        @media (max-width: 560px) {
          body { padding: 8px; }
          .maildog-mail { padding: 14px; border-radius: 6px; }
        }
        """;

    public string Build(
        string sanitizedHtml,
        bool darkMode,
        bool allowRemoteImages,
        string? bodyBackground = null,
        string? extraCss = null)
    {
        var imageSource = allowRemoteImages ? "https: data:" : "data:";
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
            .Append("<style>").Append(css).Append(extraCss ?? string.Empty).Append("</style>")
            .Append("</head><body");

        if (!string.IsNullOrWhiteSpace(bodyBackground))
        {
            // 让正文背景与应用阅读区一致，避免 WebView 出现一块突兀的白色/黑色区域。
            builder.Append(" style=\"background:").Append(bodyBackground).Append('"');
        }

        builder.Append('>')
            .Append("<div class=\"maildog-mail\">")
            .Append(sanitizedHtml)
            .Append("</div></body></html>");

        return builder.ToString();
    }
}
