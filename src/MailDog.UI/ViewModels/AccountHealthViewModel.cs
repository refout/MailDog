using Avalonia.Media;
using MailDog.Contracts.Dto;

namespace MailDog.UI.ViewModels;

/// <summary>设置页“账号健康”一行：同步时间、IDLE 状态、最近错误与退避。</summary>
public sealed class AccountHealthViewModel
{
    public AccountHealthViewModel(AccountHealthDto health)
    {
        Email = health.Email;
        Title = string.IsNullOrWhiteSpace(health.DisplayName) ? health.Email : health.DisplayName;
        StatusText = health.Status switch
        {
            AccountStatus.AuthRequired => "需要重新登录",
            AccountStatus.Offline => "离线",
            AccountStatus.Error => "同步失败",
            _ => "正常"
        };
        StatusBrush = new SolidColorBrush(health.Status switch
        {
            AccountStatus.Ok => Color.Parse("#0F7B0F"),
            AccountStatus.AuthRequired => Color.Parse("#9D5D00"),
            _ => Color.Parse("#8A8A8A")
        });

        LastSyncText = health.LastSyncUtc is { } lastSync
            ? $"上次同步：{lastSync.ToLocalTime():MM-dd HH:mm}"
            : "上次同步：还没有成功同步过";

        IdleText = health.IdleConnected
            ? health.IdleSinceUtc is { } since
                ? $"IDLE：已连接（{FormatDuration(DateTimeOffset.UtcNow - since)}）"
                : "IDLE：已连接"
            : health.IdleSupported == false
                ? "IDLE：服务器不支持，使用定时同步"
                : "IDLE：未连接";

        var error = health.LastSyncError ?? health.IdleError;
        ErrorText = error is null
            ? string.Empty
            : $"最近错误（{ErrorKindText(health.LastSyncErrorKind)}）：{error}";
        HasError = error is not null;

        var retry = health.NextRetryUtc is { } next
            ? $"下次重试：{next.ToLocalTime():HH:mm:ss}"
            : string.Empty;
        FailuresText = health.ConsecutiveFailures > 0
            ? $"连续失败 {health.ConsecutiveFailures} 次{(retry.Length > 0 ? $"，{retry}" : string.Empty)}"
            : retry;
        HasFailures = FailuresText.Length > 0;
    }

    public string Title { get; }

    public string Email { get; }

    public string StatusText { get; }

    public IBrush StatusBrush { get; }

    public string LastSyncText { get; }

    public string IdleText { get; }

    public string ErrorText { get; }

    public bool HasError { get; }

    public string FailuresText { get; }

    public bool HasFailures { get; }

    private static string FormatDuration(TimeSpan duration)
    {
        if (duration < TimeSpan.FromMinutes(1))
        {
            return "刚刚";
        }

        if (duration < TimeSpan.FromHours(1))
        {
            return $"{duration.TotalMinutes:0} 分钟";
        }

        return $"{duration.TotalHours:0.#} 小时";
    }

    private static string ErrorKindText(string? kind)
        => kind switch
        {
            "authentication" => "认证",
            "network" => "网络",
            "timeout" => "超时",
            "protocol" => "协议",
            _ => "其他"
        };
}
