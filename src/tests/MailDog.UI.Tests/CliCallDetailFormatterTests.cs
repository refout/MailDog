using MailDog.UI.Services;

namespace MailDog.UI.Tests;

public sealed class CliCallDetailFormatterTests
{
    [Fact]
    public void FormatsMessageWithoutRawJson()
    {
        var json = """
                   {"id":7,"subject":"季度复盘","from":"张三","fromAddress":"zhangsan@example.com","to":["zhangsan@example.com","lisi@example.com"],"cc":[],"date":"2026-11-26T08:30:00Z","text":"第一段\n第二段","attachments":[{"fileName":"复盘.pdf","size":2048}]}
                   """;

        var text = CliCallDetailFormatter.Format(json);

        Assert.Contains("主题：季度复盘", text);
        Assert.Contains("发件人：张三 <zhangsan@example.com>", text);
        Assert.Contains("收件人：zhangsan@example.com、lisi@example.com", text);
        Assert.DoesNotContain("抄送：[]", text);
        Assert.Contains("附件：1 个", text);
        Assert.Contains("复盘.pdf · 2 KB", text);
        Assert.Contains("正文预览：第一段 第二段", text);
        Assert.DoesNotContain("{\"id\"", text);
    }

    [Fact]
    public void FormatsJsonLinesAsMailList()
    {
        var json = """
                   {"subject":"第一封","from":"张三","date":"2026-11-26T08:30:00Z","unread":true}
                   {"subject":"第二封","from":"李四","date":"2026-11-26T09:30:00Z","hasAttachments":true}
                   """;

        var text = CliCallDetailFormatter.Format(json);

        Assert.Contains("共 2 封邮件", text);
        Assert.Contains("第一封", text);
        Assert.Contains("第二封", text);
        Assert.Contains("未读", text);
        Assert.Contains("带附件", text);
    }

    [Fact]
    public void FallsBackWhenJsonIsNotParseable()
    {
        var text = CliCallDetailFormatter.Format("not-json");

        Assert.Contains("不是可解析的 JSON", text);
        Assert.DoesNotContain("not-json", text);
    }
}
