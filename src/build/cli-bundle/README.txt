MailDog 命令行工具（maildog）
================================

这个压缩包只在用户主动下载时提供，安装包不会把 CLI 展开到安装目录。

包含：
- Windows：MailDog.Cli.exe、maildog.cmd
- Linux：MailDog.Cli、maildog
- maildog-cli/SKILL.md：给支持 Agent Skill 的助手使用

使用：
1. 解压到一个固定目录，例如 Windows 的 %LOCALAPPDATA%\MailDog\cli，
   或 Linux 的 ~/.local/share/MailDog/cli。
2. 把该目录加入 PATH（Windows 可在“系统属性 → 环境变量”里改用户 Path；
   Linux 可把目录加入 ~/.local/bin 或自己的 PATH）。
3. 新开终端，运行：
   maildog schema --json
   maildog accounts --json

说明：
- 不会自动修改 PATH，也不会写系统目录。
- CLI 通过本机 Engine 的命名管道工作；Engine 没开着时会自动拉起。
- 写命令默认只打印预览，加 --confirm 才真正执行。
- maildog-cli 是给助手用的 Skill，不是 CLI 的运行依赖；不用助手可忽略该目录。
