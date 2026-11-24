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
          show <邮件 id> [--headers] [--body] [--max-chars 2000]
                                       头信息与纯文本正文
          attachment <邮件 id> <序号> [--out 目录]
                                       把附件存到本地并打印路径

        写（都默认只预览，加 --confirm 才真正执行）：
          draft --account <邮箱> --to <地址> [--cc] [--bcc] [--subject <主题>]
                [--text <正文> | --body-file <文件>] [--html-file <文件>] [--attach <文件>]...
                                       存进草稿箱
          send  参数同 draft，--to 必填           交给 SMTP；发送前会查附件；失败会留痕并回退出码 3
          mark <邮件 id>... [--unread]            批量标已读；--unread 反向标未读
          move <邮件 id>... --account <邮箱> --folder <目标>   移到指定文件夹
          cancel <发件箱 id>...                    撤回延迟发送窗口里还没发出的邮件

        通用开关：
          --json        每行一条 JSON，便于管道与脚本
          --confirm     写命令真正执行；不带时只打印预览
          --force-send  发送前检查没过（如正文提到附件却没带）时，确认后强发
          --no-start    连不上时不去拉起 Engine
          --engine <路径>  指定 MailDog.Engine.exe

        退出码：
          0 成功   2 没找到   3 账号未登录或后台不可用、发送失败
          4 被权限或配置拒绝   5 参数写错

        说明：id 只从 list / search 里取，不要猜。命令全部走命名管道交给 Engine，
        命令行自己不碰数据库。--to / --cc / --bcc / --attach 可以重复写多次。
        """;
}
