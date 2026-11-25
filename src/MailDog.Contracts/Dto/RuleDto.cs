namespace MailDog.Contracts.Dto;

/// <summary>
/// 一条本地来信规则：命中后只做本地与服务器都能接受的轻量动作，不做自动转发。
/// 匹配是"包含"（忽略大小写），够用且可解释，不做正则。
/// </summary>
public sealed class RuleDto
{
    public long Id { get; init; }

    /// <summary>限定到某个账号；空表示所有账号。</summary>
    public string? AccountId { get; init; }

    /// <summary>from（发件人地址或显示名）/ subject（主题）/ any（两者）。</summary>
    public string Field { get; init; } = "from";

    public string Pattern { get; init; } = string.Empty;

    public bool MarkRead { get; init; }

    public bool Flag { get; init; }

    /// <summary>命中后不弹通知，邮件照常进列表。</summary>
    public bool Mute { get; init; }

    /// <summary>命中后移动到账户的垃圾邮件文件夹；没有垃圾文件夹时只保留其他动作。</summary>
    public bool MoveToJunk { get; init; }

    public bool Enabled { get; init; } = true;

    /// <summary>累计命中次数，用来判断规则是不是写得太宽。</summary>
    public int Hits { get; init; }
}
