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
    public string BodyPlaceholder => "正文";

    public string BuildStartingHtml()
    {
        if (!string.IsNullOrWhiteSpace(BodyHtml))
        {
            return BodyHtml;
        }

        if (string.IsNullOrWhiteSpace(BodyText))
        {
            return "<p></p>";
        }

        return "<p>"
               + WebUtility.HtmlEncode(BodyText).Replace("\r\n", "\n").Replace("\n", "<br/>")
               + "</p>";
    }

    private void InsertTemplate(ComposeTemplateViewModel item)
    {
        var rendered = MailTemplateRenderer.Render(item.Template.Body, BuildTemplateContext());
        if (string.IsNullOrWhiteSpace(rendered))
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(SubjectText))
        {
            SubjectText = MailTemplateRenderer.RenderSubject(item.Template, BuildTemplateContext());
        }

        TemplateInsertRequested?.Invoke(MailTemplateRenderer.ToHtml(rendered));
        Status = $"已插入模板“{item.Name}”。";
    }

    private void ApplyTemplateBody(MailTemplateDto template, bool applySubject)
    {
        var context = BuildTemplateContext();
        if (applySubject)
        {
            var renderedSubject = MailTemplateRenderer.RenderSubject(template, context);
            if (!string.IsNullOrWhiteSpace(renderedSubject))
            {
                SubjectText = renderedSubject;
            }
        }

        var rendered = MailTemplateRenderer.Render(template.Body, context);
        if (string.IsNullOrWhiteSpace(rendered))
        {
            return;
        }

        var html = MailTemplateRenderer.ToHtml(rendered);
        BodyHtml = html + (BodyHtml ?? string.Empty);
        BodyText = rendered + "\n\n" + BodyText.TrimStart('\n');
    }

    private MailTemplateContext BuildTemplateContext()
        => MailTemplateRenderer.CreateContext(
            SelectedIdentity?.DisplayName ?? SelectedAccount?.DisplayName ?? string.Empty,
            SelectedIdentity?.Address ?? SelectedAccount?.Email ?? string.Empty,
            SubjectText);

    private string ResolveSignature(string? accountId, ComposeIdentity? identity)
    {
        if (identity is not null && !string.IsNullOrWhiteSpace(identity.Signature))
        {
            return identity.Signature.Trim();
        }

        var configured = _accountSignatures.FirstOrDefault(item =>
            !string.IsNullOrWhiteSpace(item.AccountId)
            && string.Equals(item.AccountId, accountId, StringComparison.Ordinal));

        return configured is not null
            ? configured.Signature.Trim()
            : _defaultSignature;
    }

    private void RefreshIdentities(ComposeAccount? account, string? preferredAddress)
    {
        _suppressIdentityChange = true;
        try
        {
            Identities.Clear();
            foreach (var identity in ComposeIdentityResolver.BuildForAccount(account, _identitySettings))
            {
                Identities.Add(identity);
            }

            SelectedIdentity = ComposeIdentityResolver.FindPreferred(Identities, preferredAddress);
            OnPropertyChanged(nameof(HasIdentities));
        }
        finally
        {
            _suppressIdentityChange = false;
        }
    }

    partial void OnSelectedAccountChanged(ComposeAccount? value)
    {
        if (_initializing)
        {
            return;
        }

        RefreshIdentities(value, null);
        ApplySignatureChange(ResolveSignature(value?.Id, SelectedIdentity));
    }

    partial void OnSelectedIdentityChanged(ComposeIdentity? value)
    {
        if (_initializing || _suppressIdentityChange)
        {
            return;
        }

        ApplySignatureChange(ResolveSignature(SelectedAccount?.Id, value));
    }

    private void ApplySignatureChange(string next)
    {
        if (string.Equals(next, _signature, StringComparison.Ordinal))
        {
            return;
        }

        _signature = next;
        if (Mode == ComposeMode.Draft)
        {
            return;
        }

        if (_signaturePrefixHtml is null)
        {
            // 旧账户没有签名时，正文里没有可替换的前缀，直接补上新账户签名。
            if (BodyHtml is not null || BodyText.Length > 0)
            {
                ApplySignature();
            }

            return;
        }

        if (BodyHtml is null || !BodyHtml.StartsWith(_signaturePrefixHtml, StringComparison.Ordinal))
        {
            return;
        }

        BodyHtml = BodyHtml[_signaturePrefixHtml.Length..];
        if (_signaturePrefixText is not null && BodyText.StartsWith(_signaturePrefixText, StringComparison.Ordinal))
        {
            BodyText = BodyText[_signaturePrefixText.Length..];
        }

        _signaturePrefixHtml = null;
        _signaturePrefixText = null;
        _signatureOnlyHtml = null;
        _signatureOnlyText = null;
        ApplySignature();
    }

}
