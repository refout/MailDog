using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Net.Http;
using System.Net.Mail;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FluentIcons.Common;
using MailDog.Contracts.Dto;
using MailDog.Contracts.Ipc;
using MailDog.Contracts.Messages;
using MailDog.Contracts.Platform;
using MailDog.Html;
using MailDog.UI.Services;
using MailDog.UI.Views;

namespace MailDog.UI.ViewModels;

public sealed partial class MainWindowViewModel
{
    [RelayCommand]
    private async Task AddRuleAsync()
    {
        var pattern = NewRulePattern.Trim();

        if (pattern.Length == 0)
        {
            RuleHint = "请输入要匹配的关键词";
            return;
        }

        if (!NewRuleMarkRead && !NewRuleFlag && !NewRuleMute && !NewRuleMoveToJunk && NewRuleTargetFolder is null)
        {
            RuleHint = "至少选择一个动作";
            return;
        }

        if (NewRuleTargetFolder is not null && SelectedRuleAccount?.Id is null)
        {
            RuleHint = "移动到文件夹的规则需要先选择具体账号";
            return;
        }

        var rule = new RuleDto
        {
            AccountId = SelectedRuleAccount?.Id,
            Field = NewRuleFieldIndex switch
            {
                1 => "subject",
                2 => "any",
                _ => "from"
            },
            Pattern = pattern,
            MarkRead = NewRuleMarkRead,
            Flag = NewRuleFlag,
            Mute = NewRuleMute,
            MoveToJunk = NewRuleMoveToJunk,
            TargetFolderId = NewRuleTargetFolder?.Id,
            Enabled = true
        };

        try
        {
            await _engine.RequestAsync<OkResponse>(
                IpcMessageTypes.SaveRule,
                new SaveRuleRequest { Rule = rule });

            NewRulePattern = string.Empty;
            NewRuleMarkRead = true;
            NewRuleFlag = false;
            NewRuleMute = false;
            NewRuleMoveToJunk = false;
            NewRuleTargetFolder = null;
            RuleHint = "规则已保存，新的来信会立即生效";
            await LoadRulesAsync();
        }
        catch (Exception exception)
        {
            RuleHint = $"保存规则失败：{exception.Message}";
        }
    }

    private async Task SaveRuleAsync(RuleViewModel item)
    {
        try
        {
            await _engine.RequestAsync<OkResponse>(
                IpcMessageTypes.SaveRule,
                new SaveRuleRequest { Rule = item.ToDto() });
        }
        catch (Exception exception)
        {
            RuleHint = $"更新规则失败：{exception.Message}";
        }
    }

    [RelayCommand]
    private async Task PreviewRuleAsync(RuleViewModel? rule)
    {
        if (rule is null)
        {
            return;
        }

        try
        {
            var response = await _engine.RequestAsync<RuleMatchPreviewResponse>(
                IpcMessageTypes.PreviewRuleMatches,
                new PreviewRuleMatchesRequest { RuleId = rule.RuleId });

            var preview = response.Preview;
            if (preview.Matched == 0)
            {
                RuleHint = "这条规则在当前邮件里没有命中。";
                return;
            }

            var samples = preview.Samples
                .Select((sample, index) => $"{index + 1}. {sample.Date.ToLocalTime():MM-dd} {sample.Subject}")
                .ToArray();
            RuleHint = $"命中 {preview.Matched} 封（最多预览 {preview.ScanLimit} 封）"
                + (preview.TargetFolderName is null ? string.Empty : $"，目标：{preview.TargetFolderName}")
                + "：" + string.Join("；", samples);
        }
        catch (Exception exception)
        {
            RuleHint = $"预览失败：{exception.Message}";
        }
    }

    [RelayCommand]
    private async Task ApplyRuleHistoryAsync(RuleViewModel? rule)
    {
        if (rule is null)
        {
            return;
        }

        try
        {
            var response = await _engine.RequestAsync<RuleHistoryApplyResponse>(
                IpcMessageTypes.ApplyRuleHistory,
                new ApplyRuleHistoryRequest { RuleId = rule.RuleId, Limit = 200 },
                TimeSpan.FromSeconds(30));
            RuleHint = response.Result.Message;
            await LoadRulesAsync();
        }
        catch (Exception exception)
        {
            RuleHint = $"回溯失败：{exception.Message}";
        }
    }

    private async Task DeleteRuleAsync(RuleViewModel item)
    {
        try
        {
            await _engine.RequestAsync<OkResponse>(
                IpcMessageTypes.DeleteRule,
                new DeleteRuleRequest { Id = item.RuleId });

            Rules.Remove(item);
            NotifyRulesChanged();
            RuleHint = "规则已删除";
        }
        catch (Exception exception)
        {
            RuleHint = $"删除规则失败：{exception.Message}";
        }
    }

    /// <summary>规则集合变化后刷新空态与计数。</summary>
    private void NotifyRulesChanged()
    {
        OnPropertyChanged(nameof(HasRules));
        OnPropertyChanged(nameof(HasNoRules));
    }

    private async Task LoadRulesAsync()
    {
        if (Accounts.Count > 0)
        {
            var previous = SelectedRuleAccount?.Id;
            RuleAccounts.Clear();
            RuleAccounts.Add(new RuleAccountOption(null, "全部账号"));

            foreach (var account in Accounts)
            {
                RuleAccounts.Add(new RuleAccountOption(account.Id, account.Email));
            }

            SelectedRuleAccount = RuleAccounts.FirstOrDefault(o => o.Id == previous) ?? RuleAccounts[0];
        }

        RulesResponse response;

        try
        {
            response = await _engine.RequestAsync<RulesResponse>(
                IpcMessageTypes.GetRules,
                new GetRulesRequest());
        }
        catch (Exception exception)
        {
            RuleHint = $"读取规则失败：{exception.Message}";
            return;
        }

        Rules.Clear();

        foreach (var rule in response.Items)
        {
            var folderName = rule.TargetFolderId is { } folderId
                ? _foldersById.TryGetValue(folderId, out var folder) ? folder.Name : $"#{folderId}"
                : null;
            Rules.Add(new RuleViewModel(rule, SaveRuleAsync, DeleteRuleAsync, folderName));
        }

        NotifyRulesChanged();
    }

    public bool IsSoundDefault
    {
        get => NotificationSound == "default";
        set
        {
            if (value)
            {
                NotificationSound = "default";
            }
        }
    }

    public bool IsSoundMail
    {
        get => NotificationSound == "mail";
        set
        {
            if (value)
            {
                NotificationSound = "mail";
            }
        }
    }

    public bool IsSoundReminder
    {
        get => NotificationSound == "reminder";
        set
        {
            if (value)
            {
                NotificationSound = "reminder";
            }
        }
    }

    public bool IsSoundAsterisk
    {
        get => NotificationSound == "asterisk";
        set
        {
            if (value)
            {
                NotificationSound = "asterisk";
            }
        }
    }

    public bool IsSoundWarning
    {
        get => NotificationSound == "warning";
        set
        {
            if (value)
            {
                NotificationSound = "warning";
            }
        }
    }

    public bool IsSoundBeep
    {
        get => NotificationSound == "beep";
        set
        {
            if (value)
            {
                NotificationSound = "beep";
            }
        }
    }

    public bool IsSoundQuestion
    {
        get => NotificationSound == "question";
        set
        {
            if (value)
            {
                NotificationSound = "question";
            }
        }
    }

    public bool IsSoundStart
    {
        get => NotificationSound == "start";
        set
        {
            if (value)
            {
                NotificationSound = "start";
            }
        }
    }

    public bool IsQqProvider => SelectedProviderIndex == 1;

    public bool IsOutlookProvider => SelectedProviderIndex == 0;

    [ObservableProperty]
    private bool _isBatchBusy;

    [ObservableProperty]
    private bool _isSelectionMode;

    partial void OnIsSelectionModeChanged(bool value)
    {
        foreach (var message in _allMessages)
        {
            message.ShowSelection = value;

            if (!value)
            {
                message.IsSelected = false;
            }
        }

        NotifySelectionState();
    }

}
