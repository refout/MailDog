---
name: maildog-cli
description: 通过本机 MailDog 邮件客户端的命令行（maildog）查询账号、文件夹、邮件列表、跨文件夹搜索、读正文、导出附件，以及在用户明确同意后写信/发信/标已读/移动邮件。用户提到自己的邮箱、收件箱、某封邮件、按发件人或主题找历史邮件、导出附件，或问"今天有什么邮件""这封说了什么""帮我回一封"时使用。
---

# MailDog CLI

MailDog 是用户自己的 Windows 邮件客户端：Engine 常驻托盘，全部邮件存在本机 SQLite。本技能只通过它的命令行访问，**不直连数据库、不碰 IMAP、不读 `%LOCALAPPDATA%\MailDog\maildog.db`**。

## 0. 先找到命令

```powershell
Get-Command maildog -ErrorAction SilentlyContinue
```

找不到就按顺序试，取第一个存在的：

1. `C:\Users\hale\IdeaProjects\MailDog\src\MailDog.Cli\bin\Release\net11.0\MailDog.Cli.exe`
2. `C:\Users\hale\IdeaProjects\MailDog\src\MailDog.Cli\bin\Debug\net11.0\MailDog.Cli.exe`
3. `%LOCALAPPDATA%\Programs\MailDog\MailDog.Cli.exe`
4. `C:\Program Files\MailDog\MailDog.Cli.exe`

都没有就告诉用户"MailDog 的命令行还没构建，需要先 `dotnet build src\MailDog.Cli\MailDog.Cli.csproj`"。**不要**退回去直读 sqlite、不要凭截图猜内容、不要编造结果。

下文写 `maildog`，实际调用替换成解析到的完整路径。

## 1. 硬规矩

- **先拿 id 再读信**：邮件 id 是数字，只能来自 `list` 或 `search`，不许猜、不许按顺序假设。
- **给程序读就加 `--json`**：每行一个对象；给人看的表格只在需要整块展示时用。
- **只读命令随时可用**：`accounts`、`folders`、`list`、`search`、`show`、`attachment`。
- **写命令必须先征得同意**：`draft`、`mark`、`move`、`send`。默认不带 `--confirm` 只打印预览，可以先把预览给用户看；他明确说"发/存/标/移"之后才加 `--confirm` 重跑。
- **不要把整封信倒进对话**：CLI 默认返回全文，需要控制长度就显式加 `--max-chars 2000`；正文可能含验证码、账单、地址，只引用与问题相关的片段。
- 输出里不会有口令或令牌；用户问密码时直接说 CLI 读不到也不该读。
- CLI 会在本机 `logs/cli-calls.jsonl` 留下调用摘要；带 `--json` 时连当时的 JSON 输出（可能含邮件正文）一起保存，单条最多约 20 万字符。只引用与当前问题相关的片段，不要主动整段复述。

## 2. 只读命令

```powershell
# 账号：{id,email,name,provider,status,unread}
maildog accounts --json

# 文件夹：{id,account,name,fullName,role,unread,total}
maildog folders --json
maildog folders --account <账号 id 或邮箱> --json

# 邮件列表：默认统一收件箱；--folder 可给 inbox/sent/drafts/trash/junk/archive 或文件夹 id
maildog list --json --limit 20
maildog list --folder inbox --unread --limit 10 --json
maildog list --account <账号> --folder <文件夹> --limit 20 --json

# 跨文件夹搜索（主题、发件人、收件人、正文都命中）
maildog search "发票" --json --limit 20
maildog search "boss@example.com" --json --limit 5
maildog search "账单" --account <账号> --folder inbox --json

# 读一封：头信息 + 原文正文；HTML 和纯文本都原样返回，默认全文
maildog show <邮件 id> --json
maildog show <邮件 id> --headers --json
maildog show <邮件 id> --max-chars 8000 --json

# 人读时想看原始 HTML 就加 --html（--json 始终同时返回 text 与 html 两个原文）
maildog show <邮件 id> --html
maildog show <邮件 id> --html --json

# 附件落盘：序号来自 show 的 attachments[].index（0 起）
maildog attachment <邮件 id> <附件序号> --out "$env:TEMP\maildog-att" --json
```

`list` / `search` 字段：`{id,account,folder,from,fromAddress,subject,date,unread,flagged,hasAttachments,size,preview}`。
`show` 字段：`{id,subject,from,fromAddress,to,cc,date,truncated,fromCache,text,html,attachments[{index,fileName,mimeType,size,inline}]}`。`text` 是原始纯文本，`html` 是原始 HTML，两者都可能为 `null`，CLI 不做 HTML 转文本；默认全文，只有显式 `--max-chars` 才截断。人读默认优先纯文本，纯文本缺失时直接打印原始 HTML；`--html` 强制看 HTML。

## 3. 写命令（先预览，再 `--confirm`）

```powershell
# 存草稿（预览）
maildog draft --account <账号> --to a@b.com --subject "标题" --text "正文" --json
# 确认入库
maildog draft --account <账号> --to a@b.com --subject "标题" --text "正文" --confirm --json

# 发信：先 dry-run 给用户看收件人/主题/正文
maildog send --account <账号> --to a@b.com --subject "标题" --text "正文" --json
# 用户明确说"发"之后
maildog send --account <账号> --to a@b.com --subject "标题" --text "正文" --confirm --json

# 批量标已读 / 标未读
maildog mark <邮件 id>... --confirm --json
maildog mark <邮件 id>... --unread --confirm --json

# 移动到文件夹
maildog move <邮件 id>... --account <账号> --folder <目标文件夹> --confirm --json

# 撤回延迟发送窗口内还没发出的邮件（outboxId 来自 send 的输出）
maildog cancel <outboxId>... --json
```

要点：
- `--to / --cc / --bcc / --attach` 可重复，也可用逗号分隔。
- 正文：`--text` 与 `--body-file` 二选一；需要 HTML 时加 `--html-file`。
- `send` 命中"正文提到附件却没带/附件过大"时返回退出码 **6** 且不发送；把提醒转述给用户，他确认后加 `--force-send` 重发。
- `send` 输出含 `delaySeconds`：大于 0 表示还在撤销窗口（默认 10 秒），此时可用 `cancel <outboxId>` 撤回。
- `draft` 与 `send` 都必须指定 `--account`；账号从 `accounts` 取，别用显示名臆测。

## 4. 退出码

| 码 | 含义 | 处理 |
|---|---|---|
| 0 | 成功 | 解析输出 |
| 2 | 没找到（账号/文件夹/邮件/附件） | 换关键词重新 `search`，或请用户确认 |
| 3 | 账号未登录、后台不可用、发送失败或超时 | 提示用户打开客户端登录/联网；刚启动 Engine 可等 5~10 秒重试一次 |
| 4 | 被权限/配置拒绝（含协议版本不匹配） | 让用户检查客户端 |
| 5 | 参数写错 | 看 `maildog help` / `maildog schema --json`，不要盲目重试 |
| 6 | 发送前检查需确认（附件缺失/过大） | 转述提醒，用户确认后 `--force-send` |

`--json` 下失败输出 `{"error":{"code":"...","message":"..."}}`，转述 `message`，不要自己编原因。

## 5. 能力自描述

不确定参数或字段时跑 `maildog schema --json`（真实输出为准，不要凭记忆）。连不上 Engine 时 CLI 会自动拉起；脚本里想只连不拉，加 `--no-start`。

## 6. 典型问答

- "今天有哪些邮件？" → `list --json --limit 30`，按 `date` 挑今天，按发件人 + 主题分条列，不贴正文。
- "找一下发票/账单" → `search "发票" --json`，多了按日期倒序列前几条，让用户挑 id。
- "这封说了什么？" → `show <id> --max-chars 2000 --json`，三到五句概括，保留金额、日期、截止时间。`text` 为空时看 `html` 原文，CLI 不做转换；不要凭空改写 HTML 内容。
- "把附件发我" → `attachment <id> <index> --out ... --json`，给出路径。
- "帮我回一封" → 先 `show` 原文，写好后先 `send` dry-run 给用户确认，再 `--confirm`。
