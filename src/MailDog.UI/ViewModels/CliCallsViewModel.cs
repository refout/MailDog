using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MailDog.Contracts.Dto;
using MailDog.Contracts.Platform;
using MailDog.UI.Services;

namespace MailDog.UI.ViewModels;

/// <summary>设置「工具」页的 CLI 调用记录查看器与 Agent 权限。</summary>
public sealed partial class CliCallsViewModel : ObservableObject
{
    private CliPolicyDto _policy = new();

    public ObservableCollection<CliCallRecord> Calls { get; } = [];

    [ObservableProperty]
    private CliCallRecord? _selectedCall;

    [ObservableProperty]
    private string _policyStatus = string.Empty;

    public string EmptyMessage
        => "还没有 CLI 调用记录。命令行运行过一次后，这里会显示摘要；带 --json 的调用还会保留原始输出，默认只显示可读摘要。";

    public bool HasCalls => Calls.Count > 0;

    public string FilePathDisplay => CliCallLog.DisplayPath;

    public string PolicyPathDisplay => CliPolicyStore.Path;

    public bool PolicyReadOnly
    {
        get => _policy.ReadOnly;
        set
        {
            if (_policy.ReadOnly == value)
            {
                return;
            }

            _policy.ReadOnly = value;
            OnPropertyChanged();
            PolicyStatus = "有未保存的改动。";
        }
    }

    public bool PolicyRedactBody
    {
        get => _policy.RedactBody;
        set
        {
            if (_policy.RedactBody == value)
            {
                return;
            }

            _policy.RedactBody = value;
            OnPropertyChanged();
            PolicyStatus = "有未保存的改动。";
        }
    }

    public string PolicyRetentionDaysText
    {
        get => _policy.AuditRetentionDays.ToString();
        set
        {
            if (!int.TryParse(value, out var days) || days is < 1 or > 3650)
            {
                return;
            }

            if (_policy.AuditRetentionDays == days)
            {
                return;
            }

            _policy.AuditRetentionDays = days;
            OnPropertyChanged();
            PolicyStatus = "有未保存的改动。";
        }
    }

    public bool PolicyAllowDraft
    {
        get => IsAllowed("draft");
        set => SetAllowed("draft", value);
    }

    public bool PolicyAllowSend
    {
        get => IsAllowed("send");
        set => SetAllowed("send", value);
    }

    public bool PolicyAllowMark
    {
        get => IsAllowed("mark");
        set => SetAllowed("mark", value);
    }

    public bool PolicyAllowMove
    {
        get => IsAllowed("move");
        set => SetAllowed("move", value);
    }

    public bool PolicyAllowCancel
    {
        get => IsAllowed("cancel");
        set => SetAllowed("cancel", value);
    }

    public bool PolicyAllowApprove
    {
        get => IsAllowed("approve");
        set => SetAllowed("approve", value);
    }

    public string SelectedMeta
        => SelectedCall is null
            ? string.Empty
            : $"{SelectedCall.Command}  exit={SelectedCall.ExitCode}  {SelectedCall.DurationMs}ms"
                + (SelectedCall.JsonRequested
                    ? SelectedCall.JsonTruncated ? "  ·  原始记录已截断" : "  ·  原始记录"
                    : "  ·  只有摘要")
                + (SelectedCall.JsonRedacted ? "  ·  正文已脱敏" : string.Empty);

    public string SelectedDetail
        => SelectedCall is null
            ? "选择左侧一条记录查看详情。"
            : !string.IsNullOrWhiteSpace(SelectedCall.Json)
                ? CliCallDetailFormatter.Format(SelectedCall.Json)
                : SelectedCall.JsonRequested
                    ? "这次调用带了 --json，但命令没有输出可解析的 JSON。"
                    : "这次调用没有带 --json，只保留了调用摘要。";

    public string SelectedJson
        => SelectedCall?.JsonDisplay ?? "选择左侧一条记录查看详情。";

    public CliCallsViewModel()
    {
        LoadPolicy();
        Refresh();
    }

    [RelayCommand]
    private void Refresh()
    {
        var records = CliCallLog.ReadRecent();

        Calls.Clear();

        foreach (var record in records)
        {
            Calls.Add(record);
        }

        SelectedCall = Calls.FirstOrDefault();
        OnPropertyChanged(nameof(HasCalls));
    }

    [RelayCommand]
    private void SavePolicy()
    {
        CliPolicyStore.Save(_policy);
        PolicyStatus = $"已保存到 {CliPolicyStore.Path}。新启动的命令行会立即按这份权限执行。";
    }

    [RelayCommand]
    private void ReloadPolicy()
    {
        LoadPolicy();
        PolicyStatus = "已重新读取命令行的权限设置。";
    }

    private void LoadPolicy()
    {
        _policy = CliPolicyStore.Load();
        _policy.AllowedCommands ??= ["draft", "send", "mark", "move", "cancel", "approve"];
        OnPropertyChanged(nameof(PolicyReadOnly));
        OnPropertyChanged(nameof(PolicyRedactBody));
        OnPropertyChanged(nameof(PolicyRetentionDaysText));
        OnPropertyChanged(nameof(PolicyAllowDraft));
        OnPropertyChanged(nameof(PolicyAllowSend));
        OnPropertyChanged(nameof(PolicyAllowMark));
        OnPropertyChanged(nameof(PolicyAllowMove));
        OnPropertyChanged(nameof(PolicyAllowCancel));
        OnPropertyChanged(nameof(PolicyAllowApprove));
    }

    private bool IsAllowed(string command)
        => _policy.AllowedCommands.Contains(command, StringComparer.OrdinalIgnoreCase);

    private void SetAllowed(string command, bool value)
    {
        if (value)
        {
            if (!_policy.AllowedCommands.Contains(command, StringComparer.OrdinalIgnoreCase))
            {
                _policy.AllowedCommands = [.. _policy.AllowedCommands, command];
            }
        }
        else
        {
            _policy.AllowedCommands = _policy.AllowedCommands
                .Where(item => !string.Equals(item, command, StringComparison.OrdinalIgnoreCase))
                .ToArray();
        }

        OnPropertyChanged(command switch
        {
            "draft" => nameof(PolicyAllowDraft),
            "send" => nameof(PolicyAllowSend),
            "mark" => nameof(PolicyAllowMark),
            "move" => nameof(PolicyAllowMove),
            "cancel" => nameof(PolicyAllowCancel),
            "approve" => nameof(PolicyAllowApprove),
            _ => nameof(PolicyAllowDraft)
        });
        PolicyStatus = "有未保存的改动。";
    }

    partial void OnSelectedCallChanged(CliCallRecord? value)
    {
        OnPropertyChanged(nameof(SelectedMeta));
        OnPropertyChanged(nameof(SelectedDetail));
        OnPropertyChanged(nameof(SelectedJson));
    }
}
