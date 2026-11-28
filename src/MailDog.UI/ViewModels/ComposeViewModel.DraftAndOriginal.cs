using System.Collections.ObjectModel;
using System.Net;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MailDog.Contracts.Dto;
using MailDog.Contracts.Ipc;
using MailDog.Contracts.Messages;
using MailDog.Html;
using MailDog.UI.Services;

namespace MailDog.UI.ViewModels;

public sealed partial class ComposeViewModel
{
    private void RemoveAttachment(ComposeAttachmentViewModel attachment)
    {
        Attachments.Remove(attachment);
        SendWarning = null;
        _sendChecksConfirmed = false;
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(HasAttachments));
        OnPropertyChanged(nameof(AttachmentSummaryText));
    }

    private void ApplyOriginal(ComposeMode mode, MailBodyDto original)
    {
        var subject = original.Subject ?? string.Empty;
        var quoteHtml = BuildQuoteHtml(original);
        var quoteText = HtmlTextConverter.ToPlainText(quoteHtml);

        switch (mode)
        {
            case ComposeMode.Reply:
                SeedRecipients(
                    original.FromAddress,
                    ToRecipients,
                    nameof(HasToRecipients),
                    nameof(ToRecipientsValue));
                SubjectText = PrefixSubject(subject, "Re:");
                BodyText = quoteText;
                BodyHtml = quoteHtml;
                break;

            case ComposeMode.ReplyAll:
                SeedRecipients(
                    original.FromAddress,
                    ToRecipients,
                    nameof(HasToRecipients),
                    nameof(ToRecipientsValue));
                SeedRecipients(
                    BuildReplyAllCc(original),
                    CcRecipients,
                    nameof(HasCcRecipients),
                    nameof(CcRecipientsValue));
                SubjectText = PrefixSubject(subject, "Re:");
                BodyText = quoteText;
                BodyHtml = quoteHtml;
                break;

            case ComposeMode.Forward:
                SubjectText = PrefixSubject(subject, "Fwd:");
                BodyText = quoteText;
                BodyHtml = quoteHtml;
                break;
        }
    }

    /// <summary>
    /// 把 mailto 带进来的纯文本正文放在签名上方，同时生成富文本编辑器使用的 HTML。
    /// 只改 BodyText 的话编辑器仍显示旧 BodyHtml，正文会看起来没被填上。
    /// </summary>
    public void SetExternalBody(string text)
    {
        var normalized = text.Replace("\r\n", "\n").Replace('\r', '\n').Trim();

        if (normalized.Length == 0)
        {
            return;
        }

        var html = string.Join(
            string.Empty,
            normalized.Split('\n').Select(line => string.IsNullOrWhiteSpace(line)
                ? "<p><br/></p>"
                : "<p>" + WebUtility.HtmlEncode(line) + "</p>"));

        BodyText = normalized + "\n\n" + BodyText.TrimStart('\n');
        BodyHtml = html + (BodyHtml ?? string.Empty);
    }

    private void ApplySignature()
    {
        if (string.IsNullOrWhiteSpace(_signature))
        {
            _signaturePrefixHtml = null;
            _signaturePrefixText = null;
            return;
        }

        var signature = _signature.Replace("\r\n", "\n").Replace('\r', '\n').TrimEnd();
        var signatureHtml = string.Join(
            string.Empty,
            signature.Split('\n').Select(line => string.IsNullOrWhiteSpace(line)
                ? "<p><br/></p>"
                : "<p>" + WebUtility.HtmlEncode(line) + "</p>"));

        // 第一行留空给用户直接输入，签名放在正文下方、引用上方。
        var prefixHtml = "<p>&nbsp;</p>" + signatureHtml;
        var prefixText = "\n\n" + signature + "\n\n";
        BodyHtml = prefixHtml + BodyHtml;
        BodyText = prefixText + BodyText;
        _signaturePrefixHtml = prefixHtml;
        _signaturePrefixText = prefixText;

        if (Mode == ComposeMode.New)
        {
            _signatureOnlyHtml = BodyHtml;
            _signatureOnlyText = BodyText;
        }
    }

    private bool IsSignatureOnly
        => _signatureOnlyHtml is not null
           && string.Equals(BodyHtml, _signatureOnlyHtml, StringComparison.Ordinal)
           && string.Equals(BodyText, _signatureOnlyText, StringComparison.Ordinal);

    private string BuildReplyAllCc(MailBodyDto original)
    {
        var self = SelectedAccount?.Email ?? string.Empty;
        var recipients = original.ToAddresses
            .Concat(original.CcAddresses)
            .Where(address => !string.IsNullOrWhiteSpace(address))
            .Where(address => !string.Equals(address, self, StringComparison.OrdinalIgnoreCase))
            .Where(address => !string.Equals(address, original.FromAddress, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase);

        return string.Join("; ", recipients);
    }

    private static string PrefixSubject(string subject, string prefix)
        => subject.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? subject
            : $"{prefix} {subject}";

    private static string BuildQuoteHtml(MailBodyDto original)
    {
        var safe = new HtmlSanitizer().Sanitize(original.Html, allowRemoteImages: false).Html;
        var quoteContent = string.IsNullOrWhiteSpace(safe)
            ? "<p>" + WebUtility.HtmlEncode(BuildQuotePlain(original)).Replace("\n", "<br/>") + "</p>"
            : safe;
        var header = WebUtility.HtmlEncode(BuildQuoteHeader(original)).Replace("\n", "<br/>");

        return "<p><br/></p><blockquote><p>"
               + header
               + "</p>"
               + quoteContent
               + "</blockquote>";
    }

    private static string BuildQuotePlain(MailBodyDto original)
    {
        var plain = original.PlainText;
        if (string.IsNullOrWhiteSpace(plain) && !string.IsNullOrWhiteSpace(original.Html))
        {
            plain = HtmlTextConverter.ToPlainText(original.Html);
        }

        return plain ?? string.Empty;
    }

    private static string BuildQuoteHeader(MailBodyDto original)
    {
        var from = string.IsNullOrWhiteSpace(original.FromName)
            ? original.FromAddress
            : $"{original.FromName} <{original.FromAddress}>";

        var header = $"发件人: {from}\n时间: {original.Date.LocalDateTime:yyyy-MM-dd HH:mm}";

        if (original.ToAddresses.Length > 0)
        {
            header += $"\n收件人: {string.Join("; ", original.ToAddresses)}";
        }

        if (original.CcAddresses.Length > 0)
        {
            header += $"\n抄送: {string.Join("; ", original.CcAddresses)}";
        }

        return header;
    }
}
