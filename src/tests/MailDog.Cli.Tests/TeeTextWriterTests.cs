using System.Text;

namespace MailDog.Cli.Tests;

public sealed class TeeTextWriterTests
{
    [Fact]
    public void CapturesLinesAndKeepsCapping()
    {
        var inner = new StringWriter();
        var tee = new TeeTextWriter(inner, 20);

        tee.WriteLine("abcdef");
        tee.WriteLine("ghijkl");

        Assert.Contains("abcdef", inner.ToString());
        Assert.Contains("ghijkl", inner.ToString());
        Assert.Contains("abcdef", tee.Captured);
        Assert.Contains("ghijkl", tee.Captured);
        Assert.False(tee.Truncated);

        tee.WriteLine("more");

        Assert.Equal(20, tee.Captured.Length);
        Assert.True(tee.Truncated);
        Assert.Contains("more", inner.ToString());
    }

    [Fact]
    public void PreservesEncoding()
    {
        var inner = new StringWriter();
        var tee = new TeeTextWriter(inner, 100);

        Assert.Equal(inner.Encoding, tee.Encoding);
    }
}
