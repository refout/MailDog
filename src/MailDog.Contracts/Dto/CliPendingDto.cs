namespace MailDog.Contracts.Dto;

/// <summary>CLI 写操作的待确认项。payload 是 SendMailRequest / SaveDraftRequest 的 JSON。</summary>
public sealed class CliPendingDto
{
    public string Id { get; init; } = string.Empty;

    public string Kind { get; init; } = string.Empty;

    public DateTimeOffset CreatedUtc { get; init; } = DateTimeOffset.UtcNow;

    public string Summary { get; init; } = string.Empty;

    public string RequestJson { get; init; } = string.Empty;
}
