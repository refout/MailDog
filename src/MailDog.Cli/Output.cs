using System.Globalization;
using System.Text;
using MailDog.Contracts.Dto;

namespace MailDog.Cli;

/// <summary>
/// 人读文本与 JSON 都从这里出。JSON 手写，避免为了几个字段把反射式序列化拖进 AOT。
/// </summary>
internal static class Output
{
    public static string Escape(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(value.Length);

        foreach (var character in value)
        {
            switch (character)
            {
                case '"':
                    builder.Append("\\\"");
                    break;
                case '\\':
                    builder.Append("\\\\");
                    break;
                case '\n':
                    builder.Append("\\n");
                    break;
                case '\r':
                    builder.Append("\\r");
                    break;
                case '\t':
                    builder.Append("\\t");
                    break;
                default:
                    if (character < 0x20)
                    {
                        builder.Append("\\u").Append(((int)character).ToString("x4", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        builder.Append(character);
                    }

                    break;
            }
        }

        return builder.ToString();
    }

    public static string Str(string? value)
        => value is null ? "null" : $"\"{Escape(value)}\"";

    public static string Num(long value) => value.ToString(CultureInfo.InvariantCulture);

    public static string Bool(bool value) => value ? "true" : "false";

    public static string Iso(DateTimeOffset value) => value.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);

    public static string LocalTime(DateTimeOffset value)
        => value.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    public static bool IsUnread(MailFlags flags) => (flags & MailFlags.Seen) == 0;

    public static string Truncate(string value, int max)
    {
        if (max <= 0 || value.Length <= max)
        {
            return value;
        }

        return value[..max].TrimEnd() + "…";
    }

    public static string SummaryJson(MailSummaryDto message)
        => "{\"id\":" + Num(message.Id)
            + ",\"account\":" + Str(message.AccountId)
            + ",\"folder\":" + Num(message.FolderId)
            + ",\"from\":" + Str(string.IsNullOrWhiteSpace(message.FromName) ? message.FromAddress : message.FromName)
            + ",\"fromAddress\":" + Str(message.FromAddress)
            + ",\"subject\":" + Str(message.Subject)
            + ",\"date\":" + Str(Iso(message.Date))
            + ",\"unread\":" + Bool(IsUnread(message.Flags))
            + ",\"flagged\":" + Bool((message.Flags & MailFlags.Flagged) != 0)
            + ",\"hasAttachments\":" + Bool(message.HasAttachments)
            + ",\"size\":" + Num(message.Size)
            + ",\"preview\":" + Str(Truncate(message.Preview, 120))
            + "}";

    public static string AccountJson(AccountDto account, int unread)
        => "{\"id\":" + Str(account.Id)
            + ",\"email\":" + Str(account.Email)
            + ",\"name\":" + Str(account.DisplayName)
            + ",\"provider\":" + Str(account.Provider.ToString())
            + ",\"status\":" + Str(account.Status.ToString())
            + ",\"unread\":" + Num(unread)
            + "}";

    public static string FolderJson(FolderDto folder)
        => "{\"id\":" + Num(folder.Id)
            + ",\"account\":" + Str(folder.AccountId)
            + ",\"name\":" + Str(folder.Name)
            + ",\"fullName\":" + Str(folder.FullName)
            + ",\"role\":" + Str(folder.Role.ToString())
            + ",\"unread\":" + Num(folder.UnreadCount)
            + ",\"total\":" + Num(folder.TotalCount)
            + "}";

    public static string AttachmentJson(AttachmentDto attachment, int index)
        => "{\"index\":" + Num(index)
            + ",\"fileName\":" + Str(attachment.FileName)
            + ",\"mimeType\":" + Str(attachment.MimeType)
            + ",\"size\":" + Num(attachment.Size)
            + ",\"inline\":" + Bool(attachment.IsInline)
            + "}";

    /// <summary>人读一行：时间、发件人、主题；未读前面加个 *，有附件追一个 [附件]。</summary>
    public static string SummaryLine(MailSummaryDto message)
    {
        var flag = IsUnread(message.Flags) ? "* " : "  ";
        var sender = string.IsNullOrWhiteSpace(message.FromName) ? message.FromAddress : message.FromName;
        var subject = string.IsNullOrWhiteSpace(message.Subject) ? "（无主题）" : message.Subject;
        var attachment = message.HasAttachments ? " [附件]" : string.Empty;

        return $"{flag}{message.Id,8}  {LocalTime(message.Date)}  {sender}  {subject}{attachment}";
    }
}
