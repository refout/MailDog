using CommunityToolkit.Mvvm.ComponentModel;

namespace MailDog.UI.ViewModels;

/// <summary>通知声音下拉里的一个选项；Value 是写进设置的稳定值，Label 只用于界面。</summary>
public sealed record NotificationSoundOption(string Value, string Label);

/// <summary>设置页“按账户铃声”的一行。</summary>
public sealed partial class AccountSoundViewModel : ObservableObject
{
    private static readonly IReadOnlyList<NotificationSoundOption> SoundOptions =
    [
        new(string.Empty, "跟随全局"),
        new("none", "静音"),
        new("default", "默认"),
        new("mail", "邮件"),
        new("reminder", "提醒"),
        new("asterisk", "提示"),
        new("warning", "警告"),
        new("beep", "蜂鸣"),
        new("question", "询问"),
        new("start", "启动"),
    ];

    private readonly Action<AccountSoundViewModel> _changed;

    public AccountSoundViewModel(
        string accountId,
        string displayName,
        string sound,
        Action<AccountSoundViewModel> changed)
    {
        AccountId = accountId;
        DisplayName = displayName;
        _changed = changed;
        _selectedOption = SoundOptions.FirstOrDefault(
            option => string.Equals(option.Value, sound, StringComparison.Ordinal))
            ?? SoundOptions[0];
    }

    public string AccountId { get; }

    public string DisplayName { get; }

    public IReadOnlyList<NotificationSoundOption> Options => SoundOptions;

    [ObservableProperty]
    private NotificationSoundOption? _selectedOption;

    public string Sound => SelectedOption?.Value ?? string.Empty;

    partial void OnSelectedOptionChanged(NotificationSoundOption? value)
    {
        OnPropertyChanged(nameof(Sound));
        _changed(this);
    }
}
