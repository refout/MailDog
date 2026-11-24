using System.Text.RegularExpressions;

namespace MailDog.Mail.Smtp;

/// <summary>发送前的提醒结果：命中时由调用方（UI / 命令行）先让用户确认，再带 SkipSendChecks 重发。</summary>
public sealed record SendGuardResult(bool NeedsConfirmation, IReadOnlyList<string> Messages)
{
    public string Summary => string.Join("\n", Messages);

    public static SendGuardResult Clean { get; } = new(false, []);
}

/// <summary>
/// 发送前检查：正文提到附件但一个都没带，或附件体积超过常见 SMTP 上限。
/// 只提醒、不拦截真实发送——用户确认后清空检查继续发出去。
/// </summary>
public static partial class SendGuard
{
    /// <summary>单个附件超过这个体积基本会被对方服务器拒收，建议改用网盘链接。</summary>
    public const long SingleAttachmentLimitBytes = 25L * 1024 * 1024;

    /// <summary>所有附件加起来超过这个体积，很多邮箱会直接退信。</summary>
    public const long TotalAttachmentLimitBytes = 40L * 1024 * 1024;

    [GeneratedRegex("<[^>]+>", RegexOptions.CultureInvariant)]
    private static partial Regex HtmlTag();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex WhiteSpace();

    private static readonly string[] AttachmentMentions =
    [
        "附件",
        "见附件",
        "随信附上",
        "附上",
        "attach",
        "attached",
        "attachment",
        "enclosed",
        "enclosure"
    ];

    public static SendGuardResult Inspect(
        string subject,
        string? bodyText,
        string? bodyHtml,
        IReadOnlyList<string> attachmentPaths)
    {
        var messages = new List<string>();

        if (attachmentPaths.Count == 0
            && (MentionsAttachment(subject) || MentionsAttachment(bodyText) || MentionsAttachment(bodyHtml)))
        {
            messages.Add("正文里提到了附件，但没有添加任何附件。");
        }

        if (attachmentPaths.Count > 0)
        {
            long total = 0;
            string? oversized = null;

            foreach (var path in attachmentPaths)
            {
                var info = new FileInfo(path);

                if (!info.Exists)
                {
                    continue;
                }

                total += info.Length;

                if (oversized is null && info.Length > SingleAttachmentLimitBytes)
                {
                    oversized = info.Name;
                }
            }

            if (oversized is not null)
            {
                messages.Add($"「{oversized}」超过 25 MiB，对方服务器很可能拒收，建议改用网盘链接。");
            }

            if (total > TotalAttachmentLimitBytes)
            {
                messages.Add($"附件合计 {total / (1024.0 * 1024.0):0.#} MiB，超过 40 MiB，很多邮箱会退信。");
            }
        }

        return messages.Count == 0 ? SendGuardResult.Clean : new SendGuardResult(true, messages);
    }

    public static bool MentionsAttachment(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var plain = HtmlTag().Replace(text, " ");
        plain = WhiteSpace().Replace(plain, " ").Trim();

        return AttachmentMentions.Any(word => plain.Contains(word, StringComparison.OrdinalIgnoreCase));
    }
}
