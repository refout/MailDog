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

## 升级回滚测试

自动化已覆盖 `src/build/verify-upgrade-rollback.ps1`：

1. 下载 0.3.3 便携包，由旧版创建 schema v10 数据库。
2. 用当前构建的 Engine 打开同一个库，升级到 v15，并生成 `rollback.pre-v10.bak`。
3. 旧版 0.3.3 直接打开 v15 数据库并正常退出。
4. 用快照替换数据库后再启动 0.3.3，确认旧 schema 可恢复。

结果：2026-11-26 在 HOME 实机通过。安装包层面的覆盖安装/卸载仍建议在发布前用真实 setup.exe 再跑一遍。

当前 schema 版本为 15。启动时若检测到旧 schema，会先用 `VACUUM INTO` 生成 `maildog.db.pre-v<旧版本>.bak` 快照；快照不覆盖已有同名文件，可通过临时替换数据库文件回滚。

## 待办与阻塞

- 真实 QQ / Outlook / Gmail 账户矩阵需要用户提供授权码或 OAuth 应用配置，当前无法替代执行。
- 安装包层面的升级/回滚需要真实 setup.exe 和 Inno Setup 环境；协议层数据库回滚已验证。
- Linux x64/arm64 桌面上的托盘、WebKitGTK 正文与通知仍需设备验收。
