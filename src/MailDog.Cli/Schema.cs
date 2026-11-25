using System.Text;
using MailDog.Contracts.Platform;

namespace MailDog.Cli;

/// <summary>
/// 自描述的机器版说明书：助手与脚本先跑 `maildog schema` 就能知道有什么命令、参数、输出与退出码，
/// 不用读文档，也不用靠猜。
/// </summary>
internal static class Schema
{
    public static string Json(bool readOnly = false)
    {
        var builder = new StringBuilder(4096);

        builder.Append("{\"tool\":\"maildog\",\"version\":").Append(Output.Str(Program.Version))
            .Append(",\"transport\":\"named-pipe:MailDog.Engine.IPC.v1\"")
            .Append(",\"readOnly\":").Append(Output.Bool(readOnly || CliPolicyStore.Load().ReadOnly))
            .Append(",\"globalFlags\":[")
            .Append(Flag("--json", "每行一条 JSON 输出", false))
            .Append(',')
            .Append(Flag("--no-start", "连不上 Engine 时不要自动拉起", false))
            .Append(',')
            .Append(Flag("--confirm", "写命令真正执行；不带时只打印预览", false))
            .Append(',')
            .Append(Flag("--read-only", "本次拒绝所有写命令，只允许读操作", false))
            .Append(',')
            .Append(Flag("--queue", "draft/send 不直接执行，改为放进本地待确认队列", false))
            .Append(',')
            .Append(Flag("--engine", "指定 MailDog.Engine 可执行文件的路径", true))
            .Append("],\"commands\":[");

        builder.Append(Command(
            "accounts",
            "账号清单：地址、显示名、状态、收件箱未读数",
            [],
            [],
            "account 对象逐行：{id,email,name,provider,status,unread}"));

        builder.Append(',');
        builder.Append(Command(
            "folders",
            "文件夹与未读数；不带 --account 时列出全部账号",
            [Flag("--account", "账号 id / 邮箱 / 显示名", true)],
            [],
            "folder 对象逐行：{id,account,name,fullName,role,unread,total}"));

        builder.Append(',');
        builder.Append(Command(
            "list",
            "邮件摘要；默认收件箱（不带 --account 时跨账号统一收件箱）",
            [
                Flag("--folder", "inbox/sent/drafts/trash/junk/archive、文件夹 id 或文件夹名（按名匹配需带 --account）", true),
                Flag("--account", "账号 id / 邮箱 / 显示名", true),
                Flag("--limit", "返回条数，1-500，默认 20", true)
            ],
            [Flag("--unread", "只留未读", false)],
            "summary 对象逐行：{id,account,folder,from,fromAddress,subject,date,unread,flagged,hasAttachments,size,preview}"));

        builder.Append(',');
        builder.Append(Command(
            "search",
            "跨文件夹搜索，结果按 --account / --folder 在本地收窄",
            [
                Flag("--folder", "inbox/sent/drafts/trash/junk/archive 或文件夹 id", true),
                Flag("--account", "账号 id / 邮箱 / 显示名", true),
                Flag("--limit", "返回条数，1-200，默认 30", true)
            ],
            [],
            "summary 对象逐行，字段同 list；第一个位置参数是关键词"));

        builder.Append(',');
        builder.Append(Command(
            "show",
            "邮件头信息与原始正文；纯文本和 HTML 都原样返回，默认全文",
            [Flag("--max-chars", "显式截断正文长度，0 或省略表示全文", true)],
            [
                Flag("--headers", "只看头信息，不输出正文", false),
                Flag("--body", "输出正文（默认行为，保留此开关便于脚本写明意图）", false),
                Flag("--html", "人读模式强制输出原始 HTML；--json 始终同时返回 text 与 html", false)
            ],
            "{\"id\",\"subject\",\"from\",\"fromAddress\",\"to\",\"cc\",\"date\",\"truncated\",\"fromCache\",\"text\",\"html\",\"attachments\":[{index,fileName,mimeType,size,inline}]}；text/html 均为原始内容，缺失时为 null；第一个位置参数是邮件 id"));

        builder.Append(',');
        builder.Append(Command(
            "attachment",
            "把指定附件存到本地",
            [Flag("--out", "保存目录，默认当前目录；重名自动加 (2)", true)],
            [],
            "{\"path\",\"fileName\",\"size\"}；位置参数依次是邮件 id 与附件序号（序号从 show 的附件清单取，0 起）"));

        builder.Append(',');
        builder.Append(Command(
            "draft",
            "存草稿；默认只预览，--confirm 才写入草稿箱",
            [
                Flag("--account", "发信账号 id / 邮箱 / 显示名", true),
                Flag("--to", "收件人，可重复或逗号分隔", true),
                Flag("--cc", "抄送，可重复", true),
                Flag("--bcc", "密送，可重复", true),
                Flag("--subject", "主题", true),
                Flag("--text", "正文纯文本，与 --body-file 二选一", true),
                Flag("--body-file", "从 UTF-8 文件读纯文本正文", true),
                Flag("--html-file", "HTML 正文文件，给出时按 HTML 发送", true),
                Flag("--attach", "附件路径，可重复", true)
            ],
            [Flag("--confirm", "真正写入", false), Flag("--queue", "放进待确认队列，之后用 approve 执行", false)],
            "{\"folderId\",\"folder\"} 或 {\"pendingId\",\"kind\":\"draft\",\"status\":\"queued\"}；不带 --confirm 且不带 --queue 时输出 {\"dryRun\":true,...}"));

        builder.Append(',');
        builder.Append(Command(
            "send",
            "发邮件；默认只预览，--confirm 才交给 SMTP",
            [
                Flag("--account", "发信账号 id / 邮箱 / 显示名", true),
                Flag("--to", "收件人，必填，可重复或逗号分隔", true),
                Flag("--cc", "抄送，可重复", true),
                Flag("--bcc", "密送，可重复", true),
                Flag("--subject", "主题", true),
                Flag("--text", "正文纯文本，与 --body-file 二选一", true),
                Flag("--body-file", "从 UTF-8 文件读纯文本正文", true),
                Flag("--html-file", "HTML 正文文件，给出时按 HTML 发送", true),
                Flag("--attach", "附件路径，可重复", true),
                Flag("--send-at", "计划发送时间，如 \"2026-11-26 08:30\"；至少 1 分钟后", true)
            ],
            [Flag("--confirm", "真正发送", false), Flag("--force-send", "发送前检查命中时仍然强发", false), Flag("--queue", "放进待确认队列，之后用 approve 执行", false)],
            "{\"outboxId\",\"sent\",\"queued\",\"status\",\"error\",\"delaySeconds\",\"scheduledAtUtc\"}；scheduledAtUtc 非空表示已排入计划发送；delaySeconds>0 表示还在撤销窗口内，可用 cancel 撤回；发送失败退出码 3；--queue 时输出 {\"pendingId\",\"kind\":\"send\",\"status\":\"queued\"}"));

        builder.Append(',');
        builder.Append(Command(
            "mark",
            "批量标已读；默认只预览，--confirm 才写入",
            [],
            [
                Flag("--unread", "反向：标未读", false),
                Flag("--confirm", "真正写入", false)
            ],
            "{\"affected\"}；位置参数是一个或多个邮件 id"));

        builder.Append(',');
        builder.Append(Command(
            "move",
            "把邮件移到指定文件夹；默认只预览，--confirm 才写入",
            [
                Flag("--account", "账号 id / 邮箱 / 显示名", true),
                Flag("--folder", "目标文件夹：role、id 或名字", true)
            ],
            [Flag("--confirm", "真正移动", false)],
            "{\"affected\",\"folder\"}；位置参数是一个或多个邮件 id"));

        builder.Append(',');
        builder.Append(Command(
            "cancel",
            "撤回延迟发送窗口内还没发出的邮件（id 来自 send 的 outboxId）",
            [],
            [],
            "{\"affected\"}；位置参数是一个或多个发件箱 id"));

        builder.Append(',');
        builder.Append(Command(
            "snooze",
            "把邮件稍后提醒：现在收起并标已读，到点后恢复未读；默认只预览，--confirm 才写入",
            [Flag("--until", "恢复时间，如 \"2026-11-26 08:30\"；至少 1 分钟后", true)],
            [Flag("--confirm", "真正写入", false)],
            "{\"affected\"}；位置参数是一个或多个邮件 id"));

        builder.Append(',');
        builder.Append(Command(
            "pending",
            "列出本地待确认队列（不连接 Engine，纯本地）",
            [],
            [],
            "pending 对象逐行：{pendingId,kind,created,summary}"));

        builder.Append(',');
        builder.Append(Command(
            "approve",
            "执行一条待确认的草稿或发送请求；成功后才从队列删除",
            [],
            [],
            "草稿输出 {\"folderId\",\"folder\"}；发送输出同 send；不存在时退出码 2；第一个位置参数是 pendingId"));

        builder.Append(',');
        builder.Append(Command(
            "deny",
            "拒绝并删除一条待确认请求（纯本地）",
            [],
            [],
            "{\"pendingId\",\"status\":\"denied\"}；不存在时退出码 2；第一个位置参数是 pendingId"));

        builder.Append(',');
        builder.Append(Command(
            "quit",
            "让后台 Engine 正常收尾并退出；Linux 无托盘时用它停止后台收信",
            [],
            [],
            "{\"stopped\":true}"));

        builder.Append(',');
        builder.Append(Command("schema", "输出本说明的 JSON", [], [], "本对象"))
            .Append(',')
            .Append(Command("help", "输出人读用法", [], [], "纯文本"));

        builder.Append("],\"exitCodes\":{")
            .Append("\"0\":\"成功\",")
            .Append("\"2\":\"没找到（账号 / 文件夹 / 邮件 / 附件）\",")
            .Append("\"3\":\"账号未登录、后台不可用或超时\",")
            .Append("\"4\":\"被权限或配置拒绝（含协议版本不匹配）\",")
            .Append("\"5\":\"参数写错\",")
            .Append("\"6\":\"发送前检查需要确认（附件缺失/过大）\"")
            .Append("},\"notes\":[")
            .Append(Output.Str("id 只能来自 list / search，不要猜。"))
            .Append(',')
            .Append(Output.Str("找不到 maildog 时先构建 src\\MailDog.Cli，不要退回直读 sqlite。"))
            .Append(',')
            .Append(Output.Str("写操作（draft / send / mark / move / cancel / snooze / approve）默认只预览，加 --confirm 才真正执行；draft/send 可改用 --queue 放进待确认队列。"))
            .Append(',')
            .Append(Output.Str("写命令还要通过 Agent 权限：默认全部允许；可在 MailDog UI 的「CLI 调用记录 → Agent 权限」里改成只读、白名单或关闭正文脱敏。"))
            .Append("]}");

        return builder.ToString();
    }

    private static string Command(string name, string description, string[] valueFlags, string[] booleanFlags, string output)
    {
        var builder = new StringBuilder(512);
        builder.Append("{\"name\":").Append(Output.Str(name))
            .Append(",\"description\":").Append(Output.Str(description))
            .Append(",\"flags\":[").Append(string.Join(',', valueFlags.Concat(booleanFlags)))
            .Append("],\"output\":").Append(Output.Str(output))
            .Append('}');

        return builder.ToString();
    }

    private static string Flag(string name, string description, bool requiresValue)
        => "{\"name\":" + Output.Str(name)
            + ",\"description\":" + Output.Str(description)
            + ",\"value\":" + Output.Bool(requiresValue)
            + "}";
}
