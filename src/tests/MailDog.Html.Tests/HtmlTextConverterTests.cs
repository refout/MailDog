namespace MailDog.Html.Tests;

public sealed class HtmlTextConverterTests
{
    [Fact]
    public void ConvertsBlocksToLines()
    {
        var text = HtmlTextConverter.ToPlainText("<p>你好&nbsp;世界</p><div>第二行</div>");

        Assert.Equal("你好 世界\n第二行", text);
    }

    [Fact]
    public void SkipsStyleContent()
    {
        var text = HtmlTextConverter.ToPlainText("<style>.a{color:red}</style><p>正文</p>");

        Assert.Equal("正文", text);
    }
}
