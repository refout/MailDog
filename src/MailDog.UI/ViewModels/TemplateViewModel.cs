using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MailDog.Contracts.Dto;

namespace MailDog.UI.ViewModels;

public sealed partial class MailTemplateViewModel : ObservableObject
{
    private readonly Action<MailTemplateViewModel> _edit;
    private readonly Action<MailTemplateViewModel> _delete;

    public MailTemplateViewModel(
        MailTemplateDto template,
        Action<MailTemplateViewModel> edit,
        Action<MailTemplateViewModel> delete)
    {
        Template = template;
        _edit = edit;
        _delete = delete;
    }

    public MailTemplateDto Template { get; }

    public string Id => Template.Id;

    public string Name => string.IsNullOrWhiteSpace(Template.Name) ? "未命名模板" : Template.Name;

    public string Subject => Template.Subject;

    public string Body => Template.Body;

    public bool IsQuickReply => Template.IsQuickReply;

    public string KindText => IsQuickReply ? "快速回复" : "普通模板";

    public string Preview => string.IsNullOrWhiteSpace(Body)
        ? "（正文为空）"
        : Body.Replace("\r\n", " ").Replace('\n', ' ') is { Length: > 48 } text
            ? text[..48] + "…"
            : Body.Replace("\r\n", " ").Replace('\n', ' ');

    [RelayCommand]
    private void Edit() => _edit(this);

    [RelayCommand]
    private void Delete() => _delete(this);
}

public sealed partial class QuickReplyTargetViewModel : ObservableObject
{
    private readonly Action<QuickReplyTargetViewModel> _reply;

    public QuickReplyTargetViewModel(MailTemplateDto template, Action<QuickReplyTargetViewModel> reply)
    {
        Template = template;
        _reply = reply;
    }

    public MailTemplateDto Template { get; }

    public long Id => long.TryParse(Template.Id, out var id) ? id : 0;

    public string Name => string.IsNullOrWhiteSpace(Template.Name) ? "快速回复" : Template.Name;
    [RelayCommand]
    private void Reply() => _reply(this);
}
