using MailDog.Contracts.Dto;

namespace MailDog.Mail.Storage;

/// <summary>
/// 本地来信规则的关键词匹配：包含判断、忽略大小写，故意不做正则，
/// 让规则写错时的后果只是"没匹配上"，而不是整个同步报错。
/// </summary>
public static class RuleMatcher
{
    public static bool Matches(RuleDto rule, string subject, string fromName, string fromAddress)
    {
        var pattern = rule.Pattern.Trim();

        if (pattern.Length == 0)
        {
            return false;
        }

        var from = $"{fromName} {fromAddress}";

        return rule.Field switch
        {
            "subject" => subject.Contains(pattern, StringComparison.OrdinalIgnoreCase),
            "any" => subject.Contains(pattern, StringComparison.OrdinalIgnoreCase)
                     || from.Contains(pattern, StringComparison.OrdinalIgnoreCase),
            _ => from.Contains(pattern, StringComparison.OrdinalIgnoreCase)
        };
    }
}
