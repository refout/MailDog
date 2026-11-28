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
}
