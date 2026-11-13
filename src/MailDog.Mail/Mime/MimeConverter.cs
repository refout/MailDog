using MailDog.Contracts.Dto;
using MailDog.Mail.Storage;
using MailKit;
using MimeKit;

namespace MailDog.Mail.Mime;

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
            BodyState: BodyStates.NotFetched);
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

        var attachments = message.BodyParts
            .OfType<MimePart>()
            .Where(part => part.FileName is not null
                           || part.ContentDisposition?.IsAttachment == true
                           || string.Equals(part.ContentDisposition?.Disposition, "inline", StringComparison.OrdinalIgnoreCase))
            .Select(part => new AttachmentRecord(
                FileName: part.FileName ?? "attachment",
                MimeType: part.ContentType.MimeType,
                Size: part.Content?.Stream is { CanSeek: true } stream ? stream.Length : 0,
                ContentId: part.ContentId,
                IsInline: string.Equals(part.ContentDisposition?.Disposition, "inline", StringComparison.OrdinalIgnoreCase) || part.ContentId is not null))
            .ToArray();

        return (html, plain, truncated, attachments);
    }

    public static MailBodyDto ToDto(
        long messageId,
        string subject,
        string? html,
        string? plainText,
        bool truncated,
        IReadOnlyList<AttachmentRecord> attachments)
        => new()
        {
            MessageId = messageId,
            Subject = subject,
            Html = html,
            PlainText = plainText,
            Truncated = truncated,
            Attachments = attachments.Select(attachment => new AttachmentDto
            {
                FileName = attachment.FileName,
                MimeType = attachment.MimeType,
                Size = attachment.Size,
                ContentId = attachment.ContentId,
                IsInline = attachment.IsInline
            }).ToArray()
        };

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
