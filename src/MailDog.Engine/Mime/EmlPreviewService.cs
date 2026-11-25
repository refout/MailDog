using MailDog.Contracts.Dto;
using MailDog.Html;
using MailDog.Mail.Mime;
using MimeKit;

namespace MailDog.Engine.Mime;

/// <summary>读取用户直接打开或拖入的 .eml，只解析头和正文用于只读预览。</summary>
internal static class EmlPreviewService
{
    private const long MaxFileBytes = 25L * 1024 * 1024;

    public static EmlPreviewDto Load(string path)
    {
        try
        {
            var file = new FileInfo(path);

            if (!file.Exists)
            {
                return Error(Path.GetFileName(path), "文件不存在。");
            }

            if (file.Length > MaxFileBytes)
            {
                return Error(file.Name, "文件超过 25 MB，未打开。");
            }

            using var stream = File.OpenRead(path);
            var message = MimeMessage.Load(stream);
            var body = MimeConverter.ExtractBody(message);
            var plain = body.PlainText;

            if (string.IsNullOrWhiteSpace(plain) && body.Html is { Length: > 0 } html)
            {
                plain = HtmlTextConverter.ToPlainText(html);
            }

            return new EmlPreviewDto
            {
                FileName = file.Name,
                Subject = message.Subject ?? "(无主题)",
                FromName = message.From.Mailboxes.FirstOrDefault()?.Name ?? string.Empty,
                FromAddress = message.From.Mailboxes.FirstOrDefault()?.Address ?? string.Empty,
                ToAddresses = string.Join(", ", message.To.Mailboxes.Select(Format)),
                CcAddresses = string.Join(", ", message.Cc.Mailboxes.Select(Format)),
                Date = message.Date,
                PlainText = plain ?? string.Empty,
                Truncated = body.Truncated,
                AttachmentNames = body.Attachments.Select(item => item.FileName).ToArray()
            };
        }
        catch (Exception exception) when (exception is IOException or FormatException or ArgumentException)
        {
            return Error(Path.GetFileName(path), $"无法解析这封邮件：{exception.Message}");
        }
    }

    private static string Format(MailboxAddress address)
        => !string.IsNullOrEmpty(address.Name) ? $"{address.Name} <{address.Address}>" : address.Address;

    private static EmlPreviewDto Error(string fileName, string message)
        => new() { FileName = fileName, Subject = fileName, Error = message };
}
