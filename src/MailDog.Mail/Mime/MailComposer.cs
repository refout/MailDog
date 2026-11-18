using System.Net;
using MailDog.Html;
using MailDog.Mail.Storage;
using MimeKit;

namespace MailDog.Mail.Mime;

public sealed record MailAttachment(string FileName, string FilePath, string MimeType);

/// <summary>把撰写内容组装成 MimeMessage；发信与草稿共用。</summary>
public static class MailComposer
{
    public static MimeMessage Build(
        AccountRecord account,
        string to,
        string cc,
        string bcc,
        string subject,
        string bodyText,
        string? inReplyTo,
        string messageId,
        IReadOnlyList<MailAttachment> attachments,
        bool isMarkdown = false,
        bool requireRecipients = true)
    {
        var message = new MimeMessage
        {
            Subject = subject,
            Date = DateTimeOffset.Now,
            MessageId = messageId
        };

        message.From.Add(new MailboxAddress(account.DisplayName, account.Email));
        AddAddresses(message.To, ParseAddresses(to, "收件人"));
        AddAddresses(message.Cc, ParseAddresses(cc, "抄送"));
        AddAddresses(message.Bcc, ParseAddresses(bcc, "密送"));

        if (requireRecipients && message.To.Count == 0)
        {
            throw new ArgumentException("请填写至少一个收件人。", nameof(to));
        }

        if (!string.IsNullOrWhiteSpace(inReplyTo))
        {
            message.InReplyTo = inReplyTo.Trim();
            message.References.Add(inReplyTo.Trim());
        }

        if (isMarkdown)
        {
            // 草稿重新打开时据此恢复 Markdown 编辑模式。
            message.Headers.Add("X-MailDog-Markdown", "1");
        }

        var builder = new BodyBuilder
        {
            TextBody = bodyText ?? string.Empty,
            HtmlBody = isMarkdown
                ? MarkdownRenderer.ToHtml(bodyText)
                : ToHtml(bodyText ?? string.Empty)
        };

        foreach (var attachment in attachments)
        {
            if (!File.Exists(attachment.FilePath))
            {
                throw new FileNotFoundException($"附件不存在：{attachment.FileName}", attachment.FilePath);
            }

            // 读取到内存后再挂到 MimePart，避免发送期间文件句柄被占用。
            var bytes = File.ReadAllBytes(attachment.FilePath);
            var stream = new MemoryStream(bytes, writable: false);
            builder.Attachments.Add(
                attachment.FileName,
                stream,
                ContentType.Parse(attachment.MimeType));
        }

        message.Body = builder.ToMessageBody();
        return message;
    }

    public static MailAttachment ToAttachment(OutboxAttachmentRecord attachment)
        => new(attachment.FileName, attachment.StoredPath, attachment.MimeType);

    public static MailAttachment ToAttachment(string filePath)
        => new(
            Path.GetFileName(filePath),
            filePath,
            MimeTypes.GetMimeType(filePath));

    public static InternetAddressList ParseAddresses(string value, string fieldName)
    {
        var normalized = (value ?? string.Empty).Replace(';', ',');
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return new InternetAddressList();
        }

        if (!InternetAddressList.TryParse(normalized, out var addresses) || addresses.Count == 0)
        {
            throw new ArgumentException($"{fieldName}地址格式不正确：{value}", fieldName);
        }

        foreach (var item in addresses)
        {
            if (item is not MailboxAddress mailbox
                || string.IsNullOrWhiteSpace(mailbox.Address)
                || !mailbox.Address.Contains('@'))
            {
                throw new ArgumentException($"{fieldName}地址格式不正确：{value}", fieldName);
            }
        }

        return addresses;
    }

    private static string ToHtml(string text)
        => "<div style=\"font-family:'Segoe UI','Microsoft YaHei',sans-serif;font-size:14px;"
           + "line-height:1.6\">"
           + WebUtility.HtmlEncode(text).Replace("\r\n", "\n").Replace("\n", "<br/>")
           + "</div>";

    private static void AddAddresses(InternetAddressList target, InternetAddressList source)
    {
        foreach (var address in source)
        {
            target.Add(address);
        }
    }
}
