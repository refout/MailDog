using System.Runtime.CompilerServices;
using System.Text;
using MailDog.Contracts.Dto;
using MailDog.Mail.Storage;
using MailKit;
using MimeKit;

namespace MailDog.Mail.Mime;

internal static class CharsetBootstrap
{
    // 在读取任何邮件之前注册代码页，否则 GB2312/GBK 等中文邮件会按 Latin-1 解码成乱码。
#pragma warning disable CA2255 // MailDog.Mail 只由 Engine 与测试加载，需要随程序集初始化注册代码页。
    [ModuleInitializer]
#pragma warning restore CA2255
    internal static void Initialize()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }
}

public static class MimeConverter
{
    private const int MaxBodyChars = 2 * 1024 * 1024;

    public static MessageRecord ToRecord(IMessageSummary summary, string accountId, long folderId)
    {
        var envelope = summary.Envelope;
        var from = envelope?.From.Mailboxes.FirstOrDefault();
        var to = envelope?.To.Mailboxes.Select(mailbox => mailbox.Address) ?? [];
        var cc = envelope?.Cc.Mailboxes.Select(mailbox => mailbox.Address) ?? [];

        return new MessageRecord(
            Id: 0,
            AccountId: accountId,
            FolderId: folderId,
            Uid: summary.UniqueId.Id,
            MessageId: envelope?.MessageId,
            Subject: envelope?.Subject ?? string.Empty,
            FromName: from?.Name ?? string.Empty,
            FromAddress: from?.Address ?? string.Empty,
            ToAddresses: string.Join("; ", to),
            CcAddresses: string.Join("; ", cc),
            Date: summary.Date == default ? DateTimeOffset.UtcNow : summary.Date,
            Size: (int)Math.Min(summary.Size ?? 0, int.MaxValue),
            Flags: ToFlags(summary.Flags ?? MessageFlags.None),
            HasAttachments: summary.Attachments?.Any() == true,
            Preview: string.Empty,
            BodyState: BodyStates.NotFetched,
            ThreadKey: BuildThreadKey(
                envelope?.MessageId,
                summary.Headers?["In-Reply-To"],
                summary.Headers?["References"]));
    }

    /// <summary>会话根取 References 的第一个 Message-ID，退化为 In-Reply-To 或自身 Message-ID。</summary>
    private static string? BuildThreadKey(string? messageId, string? inReplyTo, string? references)
    {
        var root = FirstMessageId(references)
                   ?? FirstMessageId(inReplyTo)
                   ?? NormalizeMessageId(messageId);
        return root;
    }

    private static string? FirstMessageId(string? header)
    {
        if (string.IsNullOrWhiteSpace(header))
        {
            return null;
        }

        var start = header.IndexOf('<');
        if (start >= 0)
        {
            var end = header.IndexOf('>', start + 1);
            if (end > start)
            {
                return NormalizeMessageId(header[(start + 1)..end]);
            }
        }

        var tokenEnd = header.IndexOfAny([' ', '\t', '\r', '\n']);
        return NormalizeMessageId(tokenEnd < 0 ? header : header[..tokenEnd]);
    }

    private static string? NormalizeMessageId(string? value)
    {
        var text = value?.Trim().Trim('<', '>').Trim();
        return string.IsNullOrEmpty(text) ? null : text.ToLowerInvariant();
    }

    public static (string? Html, string? PlainText, bool Truncated, AttachmentRecord[] Attachments) ExtractBody(MimeMessage message)
    {
        var html = message.HtmlBody;
        var plain = message.TextBody;
        var truncated = false;

        if (html is { Length: > MaxBodyChars })
        {
            html = html[..MaxBodyChars];
            truncated = true;
        }

        if (plain is { Length: > MaxBodyChars })
        {
            plain = plain[..MaxBodyChars];
            truncated = true;
        }

        var attachments = GetAttachmentParts(message)
            .Select(part => new AttachmentRecord(
                FileName: part.FileName ?? "attachment",
                MimeType: part.ContentType.MimeType,
                Size: part.Content?.Stream is { CanSeek: true } stream ? stream.Length : 0,
                ContentId: part.ContentId,
                IsInline: IsInline(part)))
            .ToArray();

        return (html, plain, truncated, attachments);
    }

    /// <summary>按与 <see cref="ExtractBody"/> 相同的顺序返回指定附件，供下载使用。</summary>
    public static MimePart? FindAttachment(MimeMessage message, int index)
    {
        var parts = GetAttachmentParts(message).ToArray();
        return index >= 0 && index < parts.Length ? parts[index] : null;
    }

    private static IEnumerable<MimePart> GetAttachmentParts(MimeMessage message)
        => message.BodyParts
            .OfType<MimePart>()
            .Where(part => part.FileName is not null
                           || part.ContentDisposition?.IsAttachment == true
                           || IsInline(part));

    private static bool IsInline(MimePart part)
        => string.Equals(part.ContentDisposition?.Disposition, "inline", StringComparison.OrdinalIgnoreCase)
           || part.ContentId is not null;

    /// <summary>把正文中的 cid: 内嵌图片替换为 data URI，避免依赖 WebView 的 cid 协议。</summary>
    public static string? InlineCidImages(MimeMessage message, string? html)
    {
        if (string.IsNullOrEmpty(html) || !html.Contains("cid:", StringComparison.OrdinalIgnoreCase))
        {
            return html;
        }

        const long maxImageBytes = 5 * 1024 * 1024;
        const long maxTotalBytes = 10 * 1024 * 1024;
        long total = 0;

        foreach (var part in GetAttachmentParts(message))
        {
            if (part.ContentId is null
                || !string.Equals(part.ContentType.MediaType, "image", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var contentId = part.ContentId.Trim().Trim('<', '>');
            if (string.IsNullOrEmpty(contentId))
            {
                continue;
            }

            var marker = "cid:" + contentId;
            if (!html.Contains(marker, StringComparison.OrdinalIgnoreCase)
                && !html.Contains("cid:" + Uri.EscapeDataString(contentId), StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            using var buffer = new MemoryStream();
            part.Content?.DecodeTo(buffer);
            var length = buffer.Length;
            if (length <= 0 || length > maxImageBytes || total + length > maxTotalBytes)
            {
                continue;
            }

            total += length;
            var dataUri = $"data:{part.ContentType.MimeType};base64,{Convert.ToBase64String(buffer.ToArray())}";
            html = html.Replace(marker, dataUri, StringComparison.OrdinalIgnoreCase);
            html = html.Replace(
                "cid:" + Uri.EscapeDataString(contentId),
                dataUri,
                StringComparison.OrdinalIgnoreCase);
        }

        return html;
    }

    public static MailBodyDto ToDto(
        MessageRecord message,
        string? html,
        string? plainText,
        bool truncated,
        IReadOnlyList<AttachmentRecord> attachments,
        bool fromCache = false)
        => new()
        {
            MessageId = message.Id,
            Subject = message.Subject,
            Html = html,
            PlainText = plainText,
            Truncated = truncated,
            FromCache = fromCache,
            Attachments = attachments.Select(attachment => new AttachmentDto
            {
                FileName = attachment.FileName,
                MimeType = attachment.MimeType,
                Size = attachment.Size,
                ContentId = attachment.ContentId,
                IsInline = attachment.IsInline
            }).ToArray(),
            FromName = message.FromName,
            FromAddress = message.FromAddress,
            ToAddresses = MimeConverter.SplitAddresses(message.ToAddresses),
            CcAddresses = MimeConverter.SplitAddresses(message.CcAddresses),
            Date = message.Date,
            RfcMessageId = message.MessageId
        };

    public static string[] SplitAddresses(string value)
        => string.IsNullOrWhiteSpace(value)
            ? []
            : value.Split([';', ','], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

    private static MailFlags ToFlags(MessageFlags flags)
    {
        var result = MailFlags.None;

        if ((flags & MessageFlags.Seen) != 0)
        {
            result |= MailFlags.Seen;
        }

        if ((flags & MessageFlags.Flagged) != 0)
        {
            result |= MailFlags.Flagged;
        }

        if ((flags & MessageFlags.Answered) != 0)
        {
            result |= MailFlags.Answered;
        }

        if ((flags & MessageFlags.Deleted) != 0)
        {
            result |= MailFlags.Deleted;
        }

        if ((flags & MessageFlags.Draft) != 0)
        {
            result |= MailFlags.Draft;
        }

        return result;
    }
}
