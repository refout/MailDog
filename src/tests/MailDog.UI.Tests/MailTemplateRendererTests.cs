using MailDog.UI.Services;

namespace MailDog.UI.Tests;

public sealed class MailTemplateRendererTests
{
    private static readonly MailTemplateContext Context = new(
        "2026-11-25",
        "09:30",
        "张三",
        "zhangsan@example.com",
        "季度汇报");

    [Fact]
    public void RenderReplacesKnownVariablesAndKeepsUnknownOnes()
    {
        var rendered = MailTemplateRenderer.Render(
            "{{date}} {{TIME}} {{account}} <{{email}}> 主题：{{subject}}，未知：{{nickname}}",
            Context);

        Assert.Equal(
            "2026-11-25 09:30 张三 <zhangsan@example.com> 主题：季度汇报，未知：{{nickname}}",
            rendered);
    }

    [Fact]
    public void ToHtmlEncodesTextAndKeepsBlankLines()
    {
        var html = MailTemplateRenderer.ToHtml("第一行 <script>\n\n第三行");

        Assert.Contains("&lt;script&gt;", html);
        Assert.Contains("<p>第一行 &lt;script&gt;</p>", html);
        Assert.Contains("<p><br/></p>", html);
        Assert.Contains("<p>第三行</p>", html);
    }

    [Fact]
    public void RenderSubjectTrimsWhitespace()
    {
        var template = new MailDog.Contracts.Dto.MailTemplateDto
        {
            Subject = "  关于 {{subject}}  "
        };

        Assert.Equal("关于 季度汇报", MailTemplateRenderer.RenderSubject(template, Context));
    }

    [Fact]
    public void CreateContextUsesProvidedTime()
    {
        var context = MailTemplateRenderer.CreateContext(
            "张三",
            "zhangsan@example.com",
            "主题",
            new DateTimeOffset(2026, 11, 25, 9, 30, 0, TimeSpan.FromHours(8)));

        Assert.Equal("2026-11-25", context.Date);
        Assert.Equal("09:30", context.Time);
        Assert.Equal("张三", context.AccountName);
    }
}
