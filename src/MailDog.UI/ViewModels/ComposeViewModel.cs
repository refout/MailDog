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

public enum ComposeMode
{
    New,
    Reply,
    ReplyAll,
    Forward,
    Draft
}

public sealed class ComposeAccount
{
    public ComposeAccount(string id, string email, string displayName)
    {
        Id = id;
        Email = email;
        DisplayName = displayName;
    }

    public string Id { get; }

    public string Email { get; }

    public string DisplayName { get; }

    public string Label => string.IsNullOrWhiteSpace(DisplayName)
        || string.Equals(DisplayName.Trim(), Email, StringComparison.OrdinalIgnoreCase)
            ? Email
            : $"{DisplayName} <{Email}>";
}

/// <summary>撰写窗口里可选的一个发信地址；账户主地址永远是第一项。</summary>
public sealed class ComposeIdentity
{
    public ComposeIdentity(
        string id,
        string accountId,
        string address,
        string displayName,
        string signature,
        bool isAccountDefault)
    {
        Id = id;
        AccountId = accountId;
        Address = address;
        DisplayName = displayName;
        Signature = signature;
        IsAccountDefault = isAccountDefault;
    }

    public string Id { get; }

    public string AccountId { get; }

    public string Address { get; }

    public string DisplayName { get; }

    /// <summary>仅别名专属签名；空字符串表示回退账户签名或全局签名。</summary>
    public string Signature { get; }

    public bool IsAccountDefault { get; }

    public string Label => string.IsNullOrWhiteSpace(DisplayName)
        || string.Equals(DisplayName.Trim(), Address, StringComparison.OrdinalIgnoreCase)
            ? Address
            : $"{DisplayName} <{Address}>";
}

public static class ComposeIdentityResolver
{
    public static IReadOnlyList<ComposeIdentity> BuildForAccount(
        ComposeAccount? account,
        IReadOnlyList<AccountIdentityDto>? identities)
    {
        if (account is null)
        {
            return [];
        }

        var result = new List<ComposeIdentity>
        {
            new(
                $"account:{account.Id}",
                account.Id,
                account.Email,
                account.DisplayName,
                string.Empty,
                true)
        };

        foreach (var identity in identities ?? [])
        {
            if (string.IsNullOrWhiteSpace(identity.AccountId)
                || !string.Equals(identity.AccountId, account.Id, StringComparison.Ordinal)
                || string.IsNullOrWhiteSpace(identity.Address))
            {
                continue;
            }

            var address = identity.Address.Trim();
            if (address.Length == 0
                || string.Equals(address, account.Email, StringComparison.OrdinalIgnoreCase)
                || result.Any(item => string.Equals(item.Address, address, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            result.Add(new ComposeIdentity(
                string.IsNullOrWhiteSpace(identity.Id)
                    ? $"identity:{account.Id}:{address}"
                    : identity.Id,
                account.Id,
                address,
                identity.DisplayName?.Trim() ?? string.Empty,
                identity.Signature ?? string.Empty,
                false));
        }

        return result;
    }

    public static ComposeIdentity? FindPreferred(
        IReadOnlyList<ComposeIdentity> identities,
        string? address)
    {
        if (!string.IsNullOrWhiteSpace(address))
        {
            var match = identities.FirstOrDefault(item =>
                string.Equals(item.Address, address.Trim(), StringComparison.OrdinalIgnoreCase));
            if (match is not null)
            {
                return match;
            }
        }

        return identities.FirstOrDefault();
    }
}

public sealed partial class RecipientChipViewModel : ObservableObject
{
    private readonly Action<RecipientChipViewModel> _remove;

    public RecipientChipViewModel(string label, string address, Action<RecipientChipViewModel> remove)
    {
        Label = label;
        Address = address;
        _remove = remove;
    }

    public string Label { get; }

    public string Address { get; }

    public string RemoveAutomationName => $"移除收件人 {Label}";

    [RelayCommand]
    private void Remove() => _remove(this);
}

public sealed partial class RecipientSuggestionViewModel : ObservableObject
{
    private readonly Action<RecipientSuggestionViewModel> _select;

    public RecipientSuggestionViewModel(
        string name,
        string address,
        Action<RecipientSuggestionViewModel> select)
    {
        Name = name;
        Address = address;
        _select = select;
    }

    public string Name { get; }

    public string Address { get; }

    public string DisplayName => string.IsNullOrWhiteSpace(Name)
                                || string.Equals(Name, Address, StringComparison.OrdinalIgnoreCase)
        ? Address
        : Name;

    public string Label => DisplayName == Address ? Address : $"{Name} <{Address}>";

    public string SelectAutomationName => $"选择联系人 {DisplayName}";

    [RelayCommand]
    private void Select() => _select(this);
}

public sealed partial class ComposeAttachmentViewModel : ObservableObject
{    private readonly Action<ComposeAttachmentViewModel> _remove;

    public ComposeAttachmentViewModel(string path, Action<ComposeAttachmentViewModel> remove)
    {
        Path = path;
        FileName = System.IO.Path.GetFileName(path);
        SizeBytes = new FileInfo(path).Length;
        SizeText = FormatSize(SizeBytes);
        _remove = remove;
    }

    public string Path { get; }

    public string FileName { get; }

    public long SizeBytes { get; }

    public string SizeText { get; }

    [RelayCommand]
    private void Remove() => _remove(this);

    private static string FormatSize(long bytes)
    {
        if (bytes < 1024)
        {
            return $"{bytes} B";
        }

        if (bytes < 1024 * 1024)
        {
            return $"{bytes / 1024.0:0.#} KB";
        }

        return $"{bytes / (1024.0 * 1024.0):0.#} MB";
    }
}

public sealed partial class ComposeTemplateViewModel : ObservableObject
{
    private readonly Action<ComposeTemplateViewModel> _insert;

    public ComposeTemplateViewModel(MailTemplateDto template, Action<ComposeTemplateViewModel> insert)
    {
        Template = template;
        _insert = insert;
    }

    public MailTemplateDto Template { get; }

    public string Name => string.IsNullOrWhiteSpace(Template.Name) ? "未命名模板" : Template.Name;

    [RelayCommand]
    private void Insert() => _insert(this);
}

public sealed partial class ComposeViewModel : ObservableObject
{
    private const long MaxAttachmentBytes = 25 * 1024 * 1024;

    private readonly EngineClient _engine;
    private readonly MailBodyDto? _original;
    private readonly IReadOnlyList<AccountSignatureDto> _accountSignatures;
    private readonly IReadOnlyList<AccountIdentityDto> _identitySettings;
    private readonly IReadOnlyList<ContactSuggestionDto> _contacts;
    private string _signature;
    private readonly string _defaultSignature;
    private long? _draftMessageId;
    private bool _sent;
    private bool _draftSaved;
    private bool _updatingRecipientText;
    private string? _signatureOnlyHtml;
    private string? _signatureOnlyText;
    private string? _signaturePrefixHtml;
    private string? _signaturePrefixText;
    private bool _initializing = true;
    private bool _suppressIdentityChange;

    public ComposeViewModel(
        EngineClient engine,
        IReadOnlyList<ComposeAccount> accounts,
        ComposeMode mode,
        MailBodyDto? original,
        DraftContentDto? draft = null,
        string? draftAccountId = null,
        string? signature = null,
        IReadOnlyList<AccountSignatureDto>? accountSignatures = null,
        IReadOnlyList<MailTemplateDto>? templates = null,
        MailTemplateDto? initialTemplate = null,
        IReadOnlyList<ContactSuggestionDto>? contacts = null,
        IReadOnlyList<AccountIdentityDto>? identities = null)
    {
        _engine = engine;
        _original = original;
        _accountSignatures = accountSignatures ?? [];
        _identitySettings = identities ?? [];
        _signature = signature?.Trim() ?? string.Empty;
        _defaultSignature = _signature;
        _contacts = contacts ?? [];
        Mode = mode;
        Accounts = new ObservableCollection<ComposeAccount>(accounts);
        SelectedAccount = Accounts.FirstOrDefault(account =>
            (draftAccountId is not null && string.Equals(account.Id, draftAccountId, StringComparison.Ordinal))
            || (original is not null && string.Equals(account.Email, original.FromAddress, StringComparison.OrdinalIgnoreCase))
            || (draftAccountId is null && original is null))
            ?? Accounts.FirstOrDefault();
        RefreshIdentities(SelectedAccount, draft?.FromAddress);
        _signature = ResolveSignature(SelectedAccount?.Id, SelectedIdentity);

        Templates = new ObservableCollection<ComposeTemplateViewModel>(
            (templates ?? [])
            .Where(template => !string.IsNullOrWhiteSpace(template.Name))
            .Select(template => new ComposeTemplateViewModel(template, InsertTemplate)));

        WindowTitle = draft is not null
            ? "继续写邮件"
            : mode switch
            {
                ComposeMode.Reply => "回复邮件",
                ComposeMode.ReplyAll => "回复全部",
                ComposeMode.Forward => "转发邮件",
                _ => "写邮件"
            };

        if (draft is not null)
        {
            _draftMessageId = draft.MessageId;
            SeedRecipients(draft.To, ToRecipients, nameof(HasToRecipients), nameof(ToRecipientsValue));
            SeedRecipients(draft.Cc, CcRecipients, nameof(HasCcRecipients), nameof(CcRecipientsValue));
            SeedRecipients(draft.Bcc, BccRecipients, nameof(HasBccRecipients), nameof(BccRecipientsValue));
            SubjectText = draft.Subject;
            if (draft.IsMarkdown)
            {
                // 兼容旧 Markdown 草稿：首次打开时转成 HTML，后续只走富文本。
                BodyHtml = MarkdownRenderer.ToHtml(draft.BodyText);
                BodyText = HtmlTextConverter.ToPlainText(BodyHtml);
            }
            else
            {
                BodyText = draft.BodyText;
                BodyHtml = draft.BodyHtml;
            }

            if (draft.AttachmentNames.Length > 0)
            {
                Status = $"原草稿包含 {draft.AttachmentNames.Length} 个附件，请重新添加后再发送。";
            }
        }
        else if (original is not null && mode != ComposeMode.New)
        {
            ApplyOriginal(mode, original);
        }

        if (initialTemplate is not null)
        {
            ApplyTemplateBody(initialTemplate, applySubject: string.IsNullOrWhiteSpace(SubjectText));
        }

        if (draft is null)
        {
            ApplySignature();
        }

        _initializing = false;
    }

    public event Action<string>? TemplateInsertRequested;

    public ObservableCollection<ComposeTemplateViewModel> Templates { get; }

    public bool HasTemplates => Templates.Count > 0;

    public ComposeMode Mode { get; }

    public string WindowTitle { get; }

    public ObservableCollection<ComposeAccount> Accounts { get; }

    public ObservableCollection<ComposeIdentity> Identities { get; } = [];

    public bool HasIdentities => Identities.Count > 0;

    [ObservableProperty]
    private ComposeAccount? _selectedAccount;

    [ObservableProperty]
    private ComposeIdentity? _selectedIdentity;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty))]
    private string _toText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty))]
    private string _ccText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty))]
    private string _bccText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty))]
    private string _subjectText = string.Empty;

    public ObservableCollection<RecipientChipViewModel> ToRecipients { get; } = [];

    public ObservableCollection<RecipientChipViewModel> CcRecipients { get; } = [];

    public ObservableCollection<RecipientChipViewModel> BccRecipients { get; } = [];

    public ObservableCollection<RecipientSuggestionViewModel> ToSuggestions { get; } = [];

    public ObservableCollection<RecipientSuggestionViewModel> CcSuggestions { get; } = [];

    public ObservableCollection<RecipientSuggestionViewModel> BccSuggestions { get; } = [];

    public bool HasToRecipients => ToRecipients.Count > 0;

    public bool HasCcRecipients => CcRecipients.Count > 0;

    public bool HasBccRecipients => BccRecipients.Count > 0;

    public bool HasToSuggestions => ToSuggestions.Count > 0;

    public bool HasCcSuggestions => CcSuggestions.Count > 0;

    public bool HasBccSuggestions => BccSuggestions.Count > 0;

    /// <summary>已完成的收件人 chip 加上输入框中未提交的有效地址。</summary>
    public string ToRecipientsValue => BuildRecipientValue(ToRecipients, ToText);

    public string CcRecipientsValue => BuildRecipientValue(CcRecipients, CcText);

    public string BccRecipientsValue => BuildRecipientValue(BccRecipients, BccText);

    [ObservableProperty]
    private string _bodyText = string.Empty;

    /// <summary>富文本正文的 HTML。</summary>
    [ObservableProperty]
    private string? _bodyHtml;

    partial void OnToTextChanged(string value)
    {
        if (_updatingRecipientText)
        {
            return;
        }

        var remaining = CommitCompletedRecipients(
            value,
            ToRecipients,
            nameof(HasToRecipients),
            nameof(ToRecipientsValue));
        if (remaining is not null)
        {
            SetRecipientText(remaining, next => ToText = next);
        }

        UpdateSuggestions(
            ToText,
            ToSuggestions,
            ToRecipients,
            nameof(HasToSuggestions),
            suggestion => SelectSuggestion(
                suggestion,
                ToRecipients,
                () => ToText,
                next => ToText = next,
                ToSuggestions,
                nameof(HasToRecipients),
                nameof(ToRecipientsValue),
                nameof(HasToSuggestions)));
        OnPropertyChanged(nameof(ToRecipientsValue));
        OnPropertyChanged(nameof(IsEmpty));
    }

    partial void OnCcTextChanged(string value)
    {
        if (_updatingRecipientText)
        {
            return;
        }

        var remaining = CommitCompletedRecipients(
            value,
            CcRecipients,
            nameof(HasCcRecipients),
            nameof(CcRecipientsValue));
        if (remaining is not null)
        {
            SetRecipientText(remaining, next => CcText = next);
        }

        UpdateSuggestions(
            CcText,
            CcSuggestions,
            CcRecipients,
            nameof(HasCcSuggestions),
            suggestion => SelectSuggestion(
                suggestion,
                CcRecipients,
                () => CcText,
                next => CcText = next,
                CcSuggestions,
                nameof(HasCcRecipients),
                nameof(CcRecipientsValue),
                nameof(HasCcSuggestions)));
        OnPropertyChanged(nameof(CcRecipientsValue));
        OnPropertyChanged(nameof(IsEmpty));
    }

    partial void OnBccTextChanged(string value)
    {
        if (_updatingRecipientText)
        {
            return;
        }

        var remaining = CommitCompletedRecipients(
            value,
            BccRecipients,
            nameof(HasBccRecipients),
            nameof(BccRecipientsValue));
        if (remaining is not null)
        {
            SetRecipientText(remaining, next => BccText = next);
        }

        UpdateSuggestions(
            BccText,
            BccSuggestions,
            BccRecipients,
            nameof(HasBccSuggestions),
            suggestion => SelectSuggestion(
                suggestion,
                BccRecipients,
                () => BccText,
                next => BccText = next,
                BccSuggestions,
                nameof(HasBccRecipients),
                nameof(BccRecipientsValue),
                nameof(HasBccSuggestions)));
        OnPropertyChanged(nameof(BccRecipientsValue));
        OnPropertyChanged(nameof(IsEmpty));
    }

    /// <summary>把已用分号/逗号结束的地址转成 chip，输入框只保留还没提交的文本。</summary>
    private string? CommitCompletedRecipients(
        string text,
        ObservableCollection<RecipientChipViewModel> target,
        string hasProperty,
        string valueProperty)
    {
        var lastSeparator = Math.Max(text.LastIndexOf(';'), text.LastIndexOf(','));
        if (lastSeparator < 0)
        {
            return null;
        }

        var pending = text[(lastSeparator + 1)..].TrimStart();
        var kept = new List<string>();
        var recognized = false;
        foreach (var token in SplitRecipientTokens(text[..(lastSeparator + 1)]))
        {
            if (ParseRecipientToken(token) is { } recipient)
            {
                recognized = true;
                AddRecipient(target, recipient.Label, recipient.Address, hasProperty, valueProperty);
            }
            else
            {
                kept.Add(token);
            }
        }

        if (!recognized)
        {
            return null;
        }

        if (pending.Length > 0)
        {
            kept.Add(pending);
        }

        return string.Join("; ", kept);
    }

    private void SelectSuggestion(
        RecipientSuggestionViewModel suggestion,
        ObservableCollection<RecipientChipViewModel> target,
        Func<string> getText,
        Action<string> setText,
        ObservableCollection<RecipientSuggestionViewModel> suggestions,
        string hasRecipientsProperty,
        string valueProperty,
        string hasSuggestionsProperty)
    {
        AddRecipient(
            target,
            suggestion.Name,
            suggestion.Address,
            hasRecipientsProperty,
            valueProperty);
        SetRecipientText(RemoveLastRecipientToken(getText()), setText);
        suggestions.Clear();
        OnPropertyChanged(hasSuggestionsProperty);
        OnPropertyChanged(valueProperty);
        OnPropertyChanged(nameof(IsEmpty));
    }

    private void AddRecipient(
        ObservableCollection<RecipientChipViewModel> target,
        string label,
        string address,
        string hasProperty,
        string valueProperty)
    {
        if (target.Any(item =>
                string.Equals(item.Address, address, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        var chip = new RecipientChipViewModel(
            label,
            address,
            item =>
            {
                target.Remove(item);
                NotifyRecipientState(hasProperty, valueProperty);
            });
        target.Add(chip);
        NotifyRecipientState(hasProperty, valueProperty);
    }

    private void NotifyRecipientState(string hasProperty, string valueProperty)
    {
        OnPropertyChanged(hasProperty);
        OnPropertyChanged(valueProperty);
        OnPropertyChanged(nameof(IsEmpty));
    }

    private void SeedRecipients(
        string text,
        ObservableCollection<RecipientChipViewModel> target,
        string hasProperty,
        string valueProperty)
    {
        foreach (var token in SplitRecipientTokens(text))
        {
            if (ParseRecipientToken(token) is { } recipient)
            {
                AddRecipient(target, recipient.Label, recipient.Address, hasProperty, valueProperty);
            }
        }
    }

    private void SetRecipientText(string value, Action<string> setText)
    {
        _updatingRecipientText = true;
        try
        {
            setText(value);
        }
        finally
        {
            _updatingRecipientText = false;
        }
    }

    private static string BuildRecipientValue(
        IEnumerable<RecipientChipViewModel> chips,
        string input)
    {
        var addresses = chips
            .Select(chip => chip.Address)
            .ToList();
        foreach (var token in SplitRecipientTokens(input))
        {
            if (ParseRecipientToken(token) is not { } recipient)
            {
                continue;
            }

            if (!addresses.Any(address =>
                    string.Equals(address, recipient.Address, StringComparison.OrdinalIgnoreCase)))
            {
                addresses.Add(recipient.Address);
            }
        }

        return string.Join("; ", addresses);
    }

    private void UpdateSuggestions(
        string text,
        ObservableCollection<RecipientSuggestionViewModel> target,
        ObservableCollection<RecipientChipViewModel> chips,
        string hasProperty,
        Action<RecipientSuggestionViewModel> select)
    {
        target.Clear();

        var token = LastRecipientToken(text).Trim();
        if (token.StartsWith('<'))
        {
            token = token[1..];
        }

        var query = token.Trim().Trim('<', '>', ' ');
        if (query.Length < 2)
        {
            OnPropertyChanged(hasProperty);
            return;
        }

        var existing = SplitRecipientTokens(text)
            .Select(ParseRecipientToken)
            .Where(recipient => recipient is not null)
            .Select(recipient => recipient!.Value.Address)
            .ToList();
        existing.AddRange(chips.Select(chip => chip.Address));
        var existingSet = existing.ToHashSet(StringComparer.OrdinalIgnoreCase);

        var matches = _contacts
            .Where(contact => !existingSet.Contains(contact.Address))
            .Where(contact => contact.Address.Contains(query, StringComparison.OrdinalIgnoreCase)
                              || contact.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(contact =>
                contact.Address.StartsWith(query, StringComparison.OrdinalIgnoreCase))
            .ThenBy(contact => contact.Name, StringComparer.CurrentCulture)
            .Take(5)
            .Select(contact => new RecipientSuggestionViewModel(
                contact.Name,
                contact.Address,
                select));

        foreach (var suggestion in matches)
        {
            target.Add(suggestion);
        }

        OnPropertyChanged(hasProperty);
    }

    private static string RemoveLastRecipientToken(string text)
    {
        var separator = Math.Max(text.LastIndexOf(';'), text.LastIndexOf(','));
        var prefix = separator >= 0 ? text[..(separator + 1)].TrimEnd() : string.Empty;
        return prefix.Length == 0 ? string.Empty : $"{prefix} ";
    }

    private static string LastRecipientToken(string text)
    {
        var separator = Math.Max(text.LastIndexOf(';'), text.LastIndexOf(','));
        return separator >= 0 ? text[(separator + 1)..] : text;
    }

    private static string[] SplitRecipientTokens(string text)
        => text.Split([';', ','], StringSplitOptions.RemoveEmptyEntries)
            .Select(token => token.Trim())
            .Where(token => token.Length > 0)
            .ToArray();

    private static RecipientToken? ParseRecipientToken(string token)
    {
        var value = token.Trim();
        if (value.Length == 0)
        {
            return null;
        }

        var open = value.LastIndexOf('<');
        var close = value.LastIndexOf('>');
        if (open >= 0 && close > open)
        {
            var address = value[(open + 1)..close].Trim();
            if (!address.Contains('@'))
            {
                return null;
            }

            var label = value[..open].Trim().Trim('"').Trim();
            return new RecipientToken(address, string.IsNullOrWhiteSpace(label) ? address : label);
        }

        return value.Contains('@') && !value.Contains(' ')
            ? new RecipientToken(value, value)
            : null;
    }

    private readonly record struct RecipientToken(string Address, string Label);

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

    /// <summary>富文本编辑器回传内容后同步纯文本回退。</summary>
    public void UpdateRichBody(string? html)
    {
        BodyHtml = html;
        BodyText = HtmlTextConverter.ToPlainText(html);
        OnPropertyChanged(nameof(IsEmpty));
    }

    public void DropRichBody()
    {
        BodyHtml = null;
    }

    public ObservableCollection<ComposeAttachmentViewModel> Attachments { get; } = [];

    public bool HasAttachments => Attachments.Count > 0;

    public string AttachmentSummaryText
    {
        get
        {
            if (Attachments.Count == 0)
            {
                return "未添加附件";
            }

            var total = Attachments.Sum(item => item.SizeBytes);
            return $"附件 {Attachments.Count} 个 · {FormatAttachmentSize(total)}";
        }
    }

    private static string FormatAttachmentSize(long bytes)
    {
        if (bytes < 1024)
        {
            return $"{bytes} B";
        }

        if (bytes < 1024 * 1024)
        {
            return $"{bytes / 1024.0:0.#} KB";
        }

        return $"{bytes / (1024.0 * 1024.0):0.#} MiB";
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSend))]
    [NotifyPropertyChangedFor(nameof(CanEdit))]
    private bool _isBusy;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSend))]
    [NotifyPropertyChangedFor(nameof(CanEdit))]
    private bool _isQueued;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string? _error;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatus))]
    private string? _status;

    /// <summary>发送前检查命中时的提示（附件缺失 / 体积过大），用户确认后才继续。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSendWarning))]
    private string? _sendWarning;

    private bool _sendChecksConfirmed;
    private Avalonia.Threading.DispatcherTimer? _undoTimer;
    private long _undoOutboxId;

    /// <summary>延迟发送的倒计时提示；窗口结束时清空。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUndo))]
    private string? _undoHint;

    public bool HasUndo => !string.IsNullOrEmpty(UndoHint);

    public bool HasSendWarning => !string.IsNullOrEmpty(SendWarning);

    public bool HasError => !string.IsNullOrEmpty(Error);

    public bool HasStatus => !string.IsNullOrEmpty(Status);

    public bool CanSend => !IsBusy && !IsQueued && Accounts.Count > 0;

    public bool CanEdit => !IsBusy && !IsQueued;

    public bool IsCompleted => _sent || _draftSaved;

    public bool CanCloseSilently => IsQueued || _sent || _draftSaved;

    public bool IsEmpty
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(ToRecipientsValue)
                || !string.IsNullOrWhiteSpace(CcRecipientsValue)
                || !string.IsNullOrWhiteSpace(BccRecipientsValue)
                || !string.IsNullOrWhiteSpace(SubjectText)
                || Attachments.Count > 0)
            {
                return false;
            }

            return (string.IsNullOrWhiteSpace(BodyText) && string.IsNullOrWhiteSpace(BodyHtml))
                   || IsSignatureOnly;
        }
    }

    /// <summary>发送或草稿保存成功、或用户主动放弃时请求关闭窗口。</summary>
    public event Action? CloseRequested;

    /// <summary>成功发送或保存草稿后通知主窗口刷新状态。</summary>
    public event Action<ComposeViewModel, string>? Completed;

    public void AddAttachment(string path)
    {
        Error = null;

        if (Attachments.Any(item => string.Equals(item.Path, path, StringComparison.OrdinalIgnoreCase)))
        {
            Error = "该附件已经添加。";
            return;
        }

        if (!File.Exists(path))
        {
            Error = "附件不存在。";
            return;
        }

        var total = Attachments.Sum(item => item.SizeBytes) + new FileInfo(path).Length;
        if (total > MaxAttachmentBytes)
        {
            Error = "附件总大小不能超过 25 MiB。";
            return;
        }

        Attachments.Add(new ComposeAttachmentViewModel(path, RemoveAttachment));
        SendWarning = null;
        _sendChecksConfirmed = false;
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(HasAttachments));
        OnPropertyChanged(nameof(AttachmentSummaryText));
    }

    public async Task<bool> SaveDraftAsync()
    {
        if (IsBusy || IsQueued)
        {
            return false;
        }

        if (SelectedAccount is null)
        {
            Error = "请选择发件账户。";
            return false;
        }

        if (IsEmpty)
        {
            _draftSaved = true;
            return true;
        }

        IsBusy = true;
        Error = null;
        Status = "正在保存草稿…";

        try
        {
            var response = await _engine.RequestAsync<DraftSavedResponse>(
                IpcMessageTypes.SaveDraft,
                BuildDraftRequest(),
                TimeSpan.FromSeconds(60));

            _draftSaved = true;
            Status = $"已保存到{response.FolderName}。";
            Completed?.Invoke(this, Status!);
            return true;
        }
        catch (Exception exception) when (exception is IpcException or InvalidOperationException or TimeoutException)
        {
            Error = exception.Message;
            Status = null;
            return false;
        }
        finally
        {
            IsBusy = false;
            OnPropertyChanged(nameof(IsEmpty));
        }
    }

    [RelayCommand]
    private async Task SendAsync()
    {
        if (!CanSend)
        {
            return;
        }

        if (SelectedAccount is null)
        {
            Error = "请选择发件账户。";
            return;
        }

        if (string.IsNullOrWhiteSpace(ToRecipientsValue))
        {
            Error = "请填写至少一个收件人。";
            return;
        }

        IsBusy = true;
        Error = null;
        Status = "正在发送…";

        try
        {
            var response = await _engine.RequestAsync<SendMailResponse>(
                IpcMessageTypes.SendMail,
                BuildSendRequest(_sendChecksConfirmed),
                TimeSpan.FromSeconds(75));

            if (response.Status == SendStatuses.ConfirmRequired)
            {
                SendWarning = response.Error ?? "发送前检查未通过。";
                Status = null;
                return;
            }

            if (response.Sent)
            {
                _sent = true;
                IsQueued = false;
                Status = "邮件已发送。";

                if (_draftMessageId is { } draftId)
                {
                    _ = DeleteOriginalDraftAsync(draftId);
                }

                Completed?.Invoke(this, Status!);
                CloseRequested?.Invoke();
                return;
            }

            if (response.Queued)
            {
                IsQueued = true;
                Error = response.Error;

                if (response.DelaySeconds > 0)
                {
                    Status = $"已放进发件箱，{response.DelaySeconds} 秒后发出。";
                    StartUndoWindow(response.DelaySeconds, response.OutboxId);
                }
                else
                {
                    Status = "已加入发件箱，将在后台自动重试。";
                }

                Completed?.Invoke(this, Status!);
                return;
            }

            Error = response.Error ?? "发送失败。";
            Status = null;
        }
        catch (Exception exception) when (exception is IpcException or InvalidOperationException or TimeoutException)
        {
            Error = exception.Message;
            Status = null;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>撤回还没发出去的那封：直接从发件箱里删掉。</summary>
    [RelayCommand]
    private async Task UndoQueuedSendAsync()
    {
        if (_undoOutboxId <= 0)
        {
            return;
        }

        var outboxId = _undoOutboxId;

        try
        {
            await _engine.RequestAsync<OkResponse>(
                IpcMessageTypes.DeleteOutbox,
                new DeleteOutboxRequest { OutboxId = outboxId },
                TimeSpan.FromSeconds(20));

            IsQueued = false;
            Status = "已撤回，邮件没有发出。";
            Error = null;
        }
        catch (Exception exception) when (exception is IpcException or InvalidOperationException or TimeoutException)
        {
            Error = exception.Message;
        }
        finally
        {
            EndUndoWindow();
        }
    }

    private void StartUndoWindow(int seconds, long outboxId)
    {
        EndUndoWindow();

        _undoOutboxId = outboxId;
        var left = Math.Max(seconds, 1);
        UndoHint = $"还剩 {left} 秒可以撤回";

        _undoTimer = new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _undoTimer.Tick += (_, _) =>
        {
            left--;

            if (left <= 0)
            {
                EndUndoWindow();
                Status = "已交给后台发送。";
                return;
            }

            UndoHint = $"还剩 {left} 秒可以撤回";
        };
        _undoTimer.Start();
    }

    private void EndUndoWindow()
    {
        _undoTimer?.Stop();
        _undoTimer = null;
        _undoOutboxId = 0;
        UndoHint = null;
    }

    /// <summary>用户看过提醒后仍然要发：跳过发送前检查，整封重新提交。</summary>
    [RelayCommand]
    private async Task SendAnywayAsync()
    {
        _sendChecksConfirmed = true;
        SendWarning = null;
        await SendAsync();
    }

    [RelayCommand]
    private void DismissSendWarning()
    {
        SendWarning = null;
        _sendChecksConfirmed = false;
    }

    [RelayCommand]
    private async Task SaveDraft()
    {
        if (await SaveDraftAsync())
        {
            CloseRequested?.Invoke();
        }
    }

    [RelayCommand]
    private async Task DiscardAsync()
    {
        if (!CanCloseSilently && !IsEmpty && !await SaveDraftAsync())
        {
            return;
        }

        _draftSaved = true;
        CloseRequested?.Invoke();
    }

    private SendMailRequest BuildSendRequest(bool skipChecks = false)
        => new()
        {
            AccountId = SelectedAccount!.Id,
            FromAddress = SelectedIdentity?.Address ?? SelectedAccount.Email,
            FromName = SelectedIdentity?.DisplayName ?? SelectedAccount.DisplayName,
            To = ToRecipientsValue.Trim(),
            Cc = CcRecipientsValue.Trim(),
            Bcc = BccRecipientsValue.Trim(),
            Subject = SubjectText.Trim(),
            BodyText = BodyText,
            BodyHtml = BodyHtml,
            InReplyTo = Mode is ComposeMode.Reply or ComposeMode.ReplyAll ? _original?.RfcMessageId : null,
            AttachmentPaths = Attachments.Select(item => item.Path).ToArray(),
            SkipSendChecks = skipChecks
        };

    private SaveDraftRequest BuildDraftRequest()
        => new()
        {
            AccountId = SelectedAccount!.Id,
            FromAddress = SelectedIdentity?.Address ?? SelectedAccount.Email,
            FromName = SelectedIdentity?.DisplayName ?? SelectedAccount.DisplayName,
            To = ToRecipientsValue.Trim(),
            Cc = CcRecipientsValue.Trim(),
            Bcc = BccRecipientsValue.Trim(),
            Subject = SubjectText.Trim(),
            BodyText = BodyText,
            BodyHtml = BodyHtml,
            ReplaceDraftMessageId = _draftMessageId,
            AttachmentPaths = Attachments.Select(item => item.Path).ToArray()
        };

    private async Task DeleteOriginalDraftAsync(long draftMessageId)
    {
        try
        {
            await _engine.RequestAsync<OkResponse>(
                IpcMessageTypes.DeleteMessage,
                new DeleteMessageRequest { MessageId = draftMessageId, Permanent = true },
                TimeSpan.FromSeconds(20));
        }
        catch (Exception)
        {
            // 邮件已经发出；旧草稿删除失败不应影响发送结果，下次同步会保留可见草稿。
        }
    }

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
