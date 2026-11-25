namespace MailDog.Contracts.Dto;/// <summary>CLI 的 Agent 权限边界。文件由 UI 维护，CLI 在每次启动时读取。</summary>
public sealed class CliPolicyDto
{
    public static readonly string[] WriteCommands =
    [
        "draft", "send", "mark", "move", "cancel", "snooze", "approve"
    ];

    public bool ReadOnly { get; set; }

    public bool RedactBody { get; set; } = true;

    public int AuditRetentionDays { get; set; } = 30;

    public string[] AllowedCommands { get; set; } = WriteCommands.ToArray();

    public bool IsAllowed(string? command)
    {
        if (string.IsNullOrWhiteSpace(command) || !WriteCommands.Contains(command, StringComparer.OrdinalIgnoreCase))
        {
            return true;
        }

        if (ReadOnly)
        {
            return false;
        }

        return AllowedCommands?.Any(item => string.Equals(item, command, StringComparison.OrdinalIgnoreCase)) == true;
    }
}
