using System.Text;
using MailDog.Mail.Mime;
using MimeKit;

namespace MailDog.Mail.Tests;

public sealed class MboxFormatTests
{
    [Fact]
    public void NormalizesCrlfAndEscapesFromLines()
    {
        var input = "From: alice@example.com\r\nSubject: test\r\n\r\nFrom the desk\r\nsecond line\r\n";

        var result = MboxFormat.NormalizeMessage(input);

        Assert.DoesNotContain("\r", result);
        Assert.StartsWith("From: alice@example.com\n", result);
        Assert.Contains("\n>From the desk\n", result);
        Assert.DoesNotContain("\nFrom the desk\n", result);
    }

    [Fact]
    public void RecordsCanBeParsedBackAsMboxMessages()
    {
        var first = BuildMessage("first@example.com", "First");
        var second = BuildMessage("second@example.com", "Second");
        var mbox = MboxFormat.CreateRecord(Serialize(first), "first@example.com", DateTimeOffset.UtcNow)
            + MboxFormat.CreateRecord(Serialize(second), "second@example.com", DateTimeOffset.UtcNow);

        var parser = new MimeParser(new MemoryStream(Encoding.UTF8.GetBytes(mbox)), MimeFormat.Mbox);
        var subjects = new List<string>();
        while (!parser.IsEndOfStream)
        {
            subjects.Add(parser.ParseMessage().Subject ?? string.Empty);
        }

        Assert.Equal(["First", "Second"], subjects);
    }

    private static MimeMessage BuildMessage(string from, string subject)
        => new()
        {
            From = { new MailboxAddress("Sender", from) },
            To = { new MailboxAddress("Receiver", "receiver@example.com") },
            Subject = subject,
            Date = DateTimeOffset.UtcNow,
            Body = new TextPart("plain") { Text = $"Body of {subject}\nFrom this line" }
        };

    private static byte[] Serialize(MimeMessage message)
    {
        using var stream = new MemoryStream();
        message.WriteTo(stream);
        return stream.ToArray();
    }
}
