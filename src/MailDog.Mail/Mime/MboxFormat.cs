using System.Text;

namespace MailDog.Mail.Mime;

/// <summary>mbox 的 From_ 分隔行与正文转义规则。</summary>
public static class MboxFormat
{
    /// <summary>把单个 RFC 5322 消息规范成 mbox 正文：统一 LF，并转义正文行首的 From 。</summary>
    public static string NormalizeMessage(string mimeText)
    {
        var normalized = mimeText.Replace("\r\n", "\n", StringComparison.Ordinal);
        if (normalized.StartsWith("From ", StringComparison.Ordinal))
        {
            normalized = ">From " + normalized[5..];
        }

        return normalized.Replace("\nFrom ", "\n>From ", StringComparison.Ordinal);
    }

    /// <summary>写 mbox 分隔行；调用方负责随后写正文和空行。</summary>
    public static string CreateSeparator(string senderAddress, DateTimeOffset date)
        => $"From {senderAddress} {date:ddd MMM dd HH:mm:ss yyyy}\n";

    /// <summary>从原始 MIME 字节构造一条完整 mbox 记录。</summary>
    public static string CreateRecord(byte[] mimeBytes, string senderAddress, DateTimeOffset date)
    {
        var body = Encoding.UTF8.GetString(mimeBytes);
        return CreateSeparator(senderAddress, date) + NormalizeMessage(body) + "\n\n";
    }
}
