using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MailDog.Contracts.Dto;

namespace MailDog.UI.ViewModels;

/// <summary>设置页里的一条来信规则；开关改动直接回写 Engine。</summary>
public sealed partial class RuleViewModel : ObservableObject
{
    private readonly Func<RuleViewModel, Task> _save;
    private readonly Func<RuleViewModel, Task> _delete;

    public RuleViewModel(RuleDto rule, Func<RuleViewModel, Task> save, Func<RuleViewModel, Task> delete)
    {
        RuleId = rule.Id;
        AccountId = rule.AccountId;
        Field = rule.Field;
        Pattern = rule.Pattern;
        MarkRead = rule.MarkRead;
        Flag = rule.Flag;
        Mute = rule.Mute;
        MoveToJunk = rule.MoveToJunk;
        Hits = rule.Hits;
        _enabled = rule.Enabled;
        _save = save;
        _delete = delete;
    }

    public long RuleId { get; }

    public string? AccountId { get; }

    public string Field { get; }

    public string Pattern { get; }

    public bool MarkRead { get; }

    public bool Flag { get; }

    public bool Mute { get; }

    public bool MoveToJunk { get; }

    public int Hits { get; }

    [ObservableProperty]
    private bool _enabled;

    public string Title => $"{FieldText}包含「{Pattern}」";

    public string FieldText => Field switch
    {
        "subject" => "主题",
        "any" => "主题或发件人",
        _ => "发件人"
    };

    public string ActionLabel
    {
        get
        {
            var actions = new List<string>();

            if (MarkRead)
            {
                actions.Add("标已读");
            }

            if (Flag)
            {
                actions.Add("加旗标");
            }

            if (Mute)
            {
                actions.Add("不通知");
            }

            if (MoveToJunk)
            {
                actions.Add("移入垃圾邮件");
            }

            return actions.Count == 0 ? "（没有动作）" : string.Join(" · ", actions);
        }
    }

    public string HitsLabel => Hits == 0 ? "还没命中过" : $"命中 {Hits} 次";

    public RuleDto ToDto()
        => new()
        {
            Id = RuleId,
            AccountId = AccountId,
            Field = Field,
            Pattern = Pattern,
            MarkRead = MarkRead,
            Flag = Flag,
            Mute = Mute,
            MoveToJunk = MoveToJunk,
            Enabled = Enabled,
            Hits = Hits
        };

    partial void OnEnabledChanged(bool value)
    {
        _ = _save(this);
    }

    [RelayCommand]
    private void Delete()
    {
        _ = _delete(this);
    }
}

/// <summary>规则里的账号下拉项；Id 为空表示对所有账号生效。</summary>
public sealed record RuleAccountOption(string? Id, string Label);
