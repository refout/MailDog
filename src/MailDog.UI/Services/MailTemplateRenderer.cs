using System.Net;
using System.Text.RegularExpressions;
using MailDog.Contracts.Dto;

namespace MailDog.UI.Services;

public sealed record MailTemplateContext(
    string Date,
    string Time,
    string AccountName,
    string AccountEmail,
    string Subject);

/// <summary>邮件模板的变量替换与纯文本转富文本。未知变量原样保留，避免误删用户内容。</summary>
public static partial class MailTemplateRenderer
{
    public static MailTemplateContext CreateContext(
        string accountName,
        string accountEmail,
        string subject,
        DateTimeOffset? now = null)
    {
        var local = now ?? DateTimeOffset.Now;
        return new MailTemplateContext(
            local.ToString("yyyy-MM-dd"),
            local.ToString("HH:mm"),
            accountName ?? string.Empty,
            accountEmail ?? string.Empty,
            subject ?? string.Empty);
    }

    public static string Render(string template, MailTemplateContext context)
    {
        if (string.IsNullOrEmpty(template))
        {
            return string.Empty;
        }

        return VariableRegex().Replace(template, match =>
        {
            var name = match.Groups[1].Value.Trim();
            return name.ToLowerInvariant() switch
            {
                "date" => context.Date,
                "time" => context.Time,
                "account" => context.AccountName,
                "name" => context.AccountName,
                "email" => context.AccountEmail,
                "subject" => context.Subject,
                _ => match.Value
            };
        });
    }

    public static string ToHtml(string text)
    {
        var normalized = (text ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n').Trim();
        if (normalized.Length == 0)
        {
            return string.Empty;
        }

        return string.Join(
            string.Empty,
            normalized.Split('\n').Select(line => string.IsNullOrWhiteSpace(line)
                ? "<p><br/></p>"
                : "<p>" + WebUtility.HtmlEncode(line) + "</p>"));
    }

    public static string RenderBody(MailTemplateDto template, MailTemplateContext context)
        => ToHtml(Render(template.Body, context));

    public static string RenderSubject(MailTemplateDto template, MailTemplateContext context)
        => Render(template.Subject, context).Trim();

    [GeneratedRegex(@"\{\{\s*(\w+)\s*\}\}", RegexOptions.CultureInvariant)]
    private static partial Regex VariableRegex();
}
