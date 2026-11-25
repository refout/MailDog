using MailDog.UI.ViewModels;

namespace MailDog.UI.Tests;

public sealed class AccountSoundViewModelTests
{
    [Fact]
    public void SelectedSoundRaisesCallbacksAndCanInheritAgain()
    {
        AccountSoundViewModel? changed = null;
        var setting = new AccountSoundViewModel("account-1", "工作邮箱", "reminder", item => changed = item);

        Assert.Equal("reminder", setting.Sound);
        Assert.Null(changed);

        setting.SelectedOption = setting.Options.Single(option => option.Value == "none");
        Assert.Equal("none", setting.Sound);
        Assert.Same(setting, changed);

        setting.SelectedOption = setting.Options[0];
        Assert.Equal(string.Empty, setting.Sound);
        Assert.Same(setting, changed);
    }

    [Fact]
    public void UnknownSoundFallsBackToInherit()
    {
        var setting = new AccountSoundViewModel("account-2", "个人邮箱", "unknown", _ => { });

        Assert.Equal(string.Empty, setting.Sound);
        Assert.Equal("跟随全局", setting.SelectedOption?.Label);

        setting.SelectedOption = null;
        Assert.Equal(string.Empty, setting.Sound);
    }
}
