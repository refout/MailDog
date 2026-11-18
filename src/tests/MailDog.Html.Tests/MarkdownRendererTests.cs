using MailDog.Html;

namespace MailDog.Html.Tests;

public sealed class MarkdownRendererTests
{
    [Fact]
    public void RendersHeadingsListsAndInlineFormatting()
    {
        var html = MarkdownRenderer.ToHtml("""
            # 标题

            **粗体** 与 *斜体*

            - 第一项
            - 第二项
            """);

        Assert.Contains("<h1>标题</h1>", html);
        Assert.Contains("<strong>粗体</strong>", html);
        Assert.Contains("<em>斜体</em>", html);
        Assert.Contains("<ul><li>第一项</li><li>第二项</li></ul>", html);
    }

    [Fact]
    public void RendersSafeLinksAndRejectsUnsafeSchemes()
    {
        var html = MarkdownRenderer.ToHtml("[官网](https://example.com) [危险](javascript:alert(1))");

        Assert.Contains("<a href=\"https://example.com\">官网</a>", html);
        Assert.DoesNotContain("href=\"javascript", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("危险", html);
    }

    [Fact]
    public void EscapesRawHtmlAndCodeBlocks()
    {
        var html = MarkdownRenderer.ToHtml("<script>alert(1)</script>\n\n```\nif (a < b) { }\n```");

        Assert.DoesNotContain("<script>", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("&lt;script&gt;", html);
        Assert.Contains("<pre", html);
        Assert.Contains("if (a &lt; b) { }", html);
    }

    [Fact]
    public void RendersQuotesAndOrderedLists()
    {
        var html = MarkdownRenderer.ToHtml("> 引用\n\n1. 甲\n2. 乙");

        Assert.Contains("<blockquote", html);
        Assert.Contains("引用", html);
        Assert.Contains("<ol><li>甲</li><li>乙</li></ol>", html);
    }

    [Fact]
    public void RendersTablesAndSafeImages()
    {
        var html = MarkdownRenderer.ToHtml("""
            | 项目 | 状态 |
            | :--- | ---: |
            | 搜索 | 完成 |
            | 附件 | 完成 |

            ![截图](https://example.com/a.png)
            ![危险](javascript:alert(1))
            """);

        Assert.Contains("<table", html);
        Assert.Contains("<th", html);
        Assert.Contains("项目", html);
        Assert.Contains("搜索", html);
        Assert.Contains("<img src=\"https://example.com/a.png\"", html);
        Assert.DoesNotContain("src=\"javascript", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("危险", html);
    }
}
