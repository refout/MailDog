using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MailDog.UI.Services;

namespace MailDog.UI.ViewModels;

/// <summary>设置「工具」页的 CLI 调用记录查看器。</summary>
public sealed partial class CliCallsViewModel : ObservableObject
{
    public ObservableCollection<CliCallRecord> Calls { get; } = [];

    [ObservableProperty]
    private CliCallRecord? _selectedCall;

    public string EmptyMessage
        => "还没有 CLI 调用记录。命令行运行过一次后，这里会显示摘要；带 --json 的调用还能看到当时的 JSON 输出。";

    public bool HasCalls => Calls.Count > 0;

    public string FilePathDisplay => CliCallLog.DisplayPath;

    public string SelectedMeta
        => SelectedCall is null
            ? string.Empty
            : $"{SelectedCall.Command}  exit={SelectedCall.ExitCode}  {SelectedCall.DurationMs}ms"
                + (SelectedCall.JsonRequested
                    ? SelectedCall.JsonTruncated ? "  ·  JSON 已截断" : "  ·  JSON 输出"
                    : "  ·  只有摘要");

    public string SelectedJson
        => SelectedCall?.JsonDisplay ?? "选择左侧一条记录查看详情。";

    public CliCallsViewModel()
    {
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

    partial void OnSelectedCallChanged(CliCallRecord? value)
    {
        OnPropertyChanged(nameof(SelectedMeta));
        OnPropertyChanged(nameof(SelectedJson));
    }
}
