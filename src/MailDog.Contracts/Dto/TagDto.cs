namespace MailDog.Contracts.Dto;

public sealed class TagDto
{
    public long Id { get; init; }

    public string Name { get; init; } = string.Empty;

    /// <summary>#RRGGBB 颜色。</summary>
    public string Color { get; init; } = "#0F7B0F";
}

public sealed class MessageTagAssignmentDto
{
    public long MessageId { get; init; }

    public long[] TagIds { get; init; } = [];
}
