# 质量验收矩阵

日期：2026-11-25

## 自动化已覆盖

| 项目 | 证据 | 状态 |
| --- | --- | --- |
| 假 SMTP 发信 | `FakeServerEndToEndTests.SmtpSendReachesFakeServer` | ✅ 通过 |
| 假 IMAP 首屏同步 | `FakeServerEndToEndTests.ImapSyncReadsFakeInbox` | ✅ 通过 |
| Windows mailto / .eml / 默认邮件程序注册 | `src/build/verify-windows-integration.ps1` | ✅ 本机通过，CI 已接入 |
| Windows SendTo 快捷方式 | 同上 | ✅ 通过 |
| Windows JumpList 最近邮件 | 同上 | ✅ 通过 |
| 升级前数据库快照 | `SqliteStoreTests.UpgradeFromV14CreatesRestorableBackup` | ✅ 通过 |
| mbox 写入与回读 | `MboxFormatTests` | ✅ 通过 |

## 真实账户矩阵（待凭据）

| 账户类型 | 登录 | 收信 | 发信 | 草稿 | 附件 | 搜索 | 定时/撤回 | 退订/垃圾规则 | 备注 |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| QQ 邮箱 | 待测 | 待测 | 待测 | 待测 | 待测 | 待测 | 待测 | 待测 | 需要授权码 |
| Outlook / Microsoft 365 | 待测 | 待测 | 待测 | 待测 | 待测 | 待测 | 待测 | 待测 | 需要 OAuth 应用注册 |
| Gmail（可选） | 待测 | 待测 | 待测 | 待测 | 待测 | 待测 | 待测 | 待测 | 需要应用专用密码 |
| 自定义 IMAP/SMTP | 待测 | 待测 | 待测 | 待测 | 待测 | 待测 | 待测 | 待测 | 需要服务器地址和授权码 |

每个账户至少跑：

1. 添加账户、首次同步，确认文件夹角色（收件箱/已发送/草稿/垃圾箱）正确。
2. 发一封纯文本邮件，确认对方收到，且“已发送”出现副本。
3. 发一封带附件邮件，附件名中文/长文件名不乱码。
4. 保存草稿、重启后草稿恢复，再发送。
5. 搜索主题、发件人、正文各一次。
6. 定时发送选 2 分钟后，发件箱显示“计划发送”，到点发出。
7. 对推广邮件执行“退订”或“标记垃圾并拦截”，确认规则生效。
8. 关闭 UI 再重开，确认未读数和托盘/后台状态一致。

## 升级回滚测试（待安装包）

前置：准备上一版本 `MailDog-<旧版本>-win-x64-setup.exe` 与当前版本安装包；两版使用同一 Inno `AppId`。

1. 记录 `%LOCALAPPDATA%\MailDog\maildog.db` 的大小和修改时间；复制一份完整数据目录。
2. 安装当前版本，确认账户、邮件、草稿、规则、标签都在。
3. 用当前版本写信但不发送，确认发件箱有记录。
4. 直接覆盖安装旧版本，确认旧版能启动并读到同一份数据。
5. 若旧版打不开数据库（schema 版本高于旧版支持范围），记录为回滚阻断项；用备份恢复数据后再启动旧版。
6. 重新安装当前版本，确认旧版写入的邮件/标记没有丢失。

当前 schema 版本为 15。启动时若检测到旧 schema，会先用 `VACUUM INTO` 生成 `maildog.db.pre-v<旧版本>.bak` 快照；快照不覆盖已有同名文件，可通过临时替换数据库文件回滚。

## 待办与阻塞

- 真实 QQ / Outlook / Gmail 账户矩阵需要用户提供授权码或 OAuth 应用配置，当前无法替代执行。
- 升级回滚需要两个可安装的历史安装包；本机未安装 Inno Setup，`dist` 也没有 setup.exe。
- Linux x64/arm64 桌面上的托盘、WebKitGTK 正文与通知仍需设备验收。
