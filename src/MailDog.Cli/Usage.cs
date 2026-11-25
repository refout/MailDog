namespace MailDog.Cli;

/// <summary>人读的用法说明；机器读的版本在 <see cref="Schema"/>。</summary>
internal static class Usage
{
    public const string Text = """
        MailDog 命令行

        用法：
          maildog <命令> [参数] [开关]

        读：
          accounts                     账号清单（地址、状态、未读）
          folders [--account <邮箱>]   文件夹与未读数
          list [--folder inbox] [--account <邮箱>] [--limit 20] [--unread]
                                       邮件摘要，默认从收件箱开始
          search <关键词> [--folder inbox] [--account <邮箱>] [--limit 30]
                                       跨文件夹搜索，可按账号 / 文件夹收窄
          show <邮件 id> [--headers] [--body] [--html] [--max-chars 0]
                                       头信息与原始正文；HTML/纯文本原样返回
          attachment <邮件 id> <序号> [--out 目录]
                                       把附件存到本地并打印路径

        写（都默认只预览，加 --confirm 才真正执行）：
          draft --account <邮箱> --to <地址> [--cc] [--bcc] [--subject <主题>]
                [--text <正文> | --body-file <文件>] [--html-file <文件>] [--attach <文件>]...
                                       存进草稿箱
          send  参数同 draft，--to 必填           交给 SMTP；--send-at "2026-11-26 08:30" 计划发送；失败会留痕并回退出码 3
          mark <邮件 id>... [--unread]            批量标已读；--unread 反向标未读
          move <邮件 id>... --account <邮箱> --folder <目标>   移到指定文件夹
          snooze <邮件 id>... --until "2026-11-26 08:30"  稍后提醒，到点恢复未读
          cancel <发件箱 id>...                    撤回延迟发送或计划发送里还没发出的邮件
          pending                                 列出本地待确认队列（不连接 Engine）
          approve <待确认 id>                     执行队列里的草稿或发送请求
          deny <待确认 id>                        拒绝并删除队列项
          quit                                    让后台 Engine 正常退出（Linux 无托盘时用）

        待确认队列：
          draft / send 加 --queue 时不会直接执行，而是落到本地队列；
          由用户运行 approve 或 deny。队列在 %LOCALAPPDATA%\MailDog\pending。

        通用开关：
          --json        每行一条 JSON，便于管道与脚本
          --confirm     写命令真正执行；不带时只打印预览
          --queue       把 draft/send 放进待确认队列
          --read-only   本次拒绝所有写命令，只允许读
          --force-send  发送前检查没过（如正文提到附件却没带）时，确认后强发
          --no-start    连不上时不去拉起 Engine
          --engine <路径>  指定 MailDog.Engine（Windows 下为 .exe）

        退出码：
          0 成功   2 没找到   3 账号未登录或后台不可用、发送失败
          4 被权限或配置拒绝   5 参数写错   6 发送前检查需要确认

        说明：id 只从 list / search 里取，不要猜。命令全部走命名管道交给 Engine，
        命令行自己不碰数据库。--to / --cc / --bcc / --attach 可以重复写多次。
        Agent 权限在 UI 的「CLI 调用记录 → Agent 权限」里配置，默认允许全部写命令。
        """;
}
