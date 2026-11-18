using MailDog.Contracts.Dto;

namespace MailDog.UI.ViewModels;

/// <summary>本地发件箱中的一封待发送或失败邮件。</summary>
public sealed class OutboxItemViewModel
{
    public OutboxItemViewModel(OutboxItemDto item)
    {
        Id = item.Id;
        AccountId = item.AccountId;
        ToAddresses = item.ToAddresses;
        Subject = string.IsNullOrWhiteSpace(item.Subject) ? "(无主题)" : item.Subject;
        Status = item.Status;
        Attempts = item.Attempts;
        LastError = item.LastError;
        CreatedUtc = item.CreatedUtc;
        NextAttemptUtc = item.NextAttemptUtc;
        AttachmentCount = item.AttachmentCount;
    }

    public long Id { get; }

    public string AccountId { get; }

    public string ToAddresses { get; }

    public string Subject { get; }

    public string Status { get; }

    public int Attempts { get; }

    public string? LastError { get; }

    public DateTimeOffset CreatedUtc { get; }

    public DateTimeOffset NextAttemptUtc { get; }

    public int AttachmentCount { get; }

    public bool IsFailed => Status == "failed";

    public bool IsSending => Status == "sending";

    public bool CanRetry => !IsSending;

    public bool CanDelete => !IsSending;

    public bool HasError => !string.IsNullOrWhiteSpace(LastError);

    public string StatusText => Status switch
    {
        "failed" => "发送失败",
        "sending" => "正在发送",
        _ => "等待重试"
    };

    public string AttemptText => Attempts <= 0 ? "尚未尝试" : $"已尝试 {Attempts} 次";

    public string NextAttemptText => IsSending
        ? string.Empty
        : $"下次重试 {NextAttemptUtc.ToLocalTime():M月d日 HH:mm}";

    public string DetailText => HasError
        ? $"{AttemptText} · {LastError}"
        : $"{AttemptText} · {NextAttemptText}";

    public string AttachmentText => AttachmentCount > 0 ? $"{AttachmentCount} 个附件" : string.Empty;

    public string CreatedText => CreatedUtc.ToLocalTime().ToString("M月d日 HH:mm");
}
