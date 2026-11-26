# 质量验收矩阵

日期：2026-11-26（自动化基线 2026-11-26）

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

## 真实账户矩阵（2026-11-26，HOME）

测试使用 `%LOCALAPPDATA%\MailDog\maildog.db` 的隔离备份，通过 `MailDog.Engine.exe --db <临时库>` 启动当前构建；用户正在使用的数据库没有被修改。共 3 个已配置账户：QQ 1 个、Outlook OAuth2 2 个。

| 账户类型 | 登录/文件夹 | 收信/正文 | 发信 | 草稿 | 附件 | 搜索 | 定时/撤回 | 退订/垃圾规则 | 备注 |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| QQ 邮箱 | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | 🟡 | 密码登录；中文附件名往返正常 |
| Outlook / Microsoft 365 | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | 2 个 OAuth 账户；已发送副本重复已修复并复测；垃圾规则 UI 与自动拦截已实测 |
| Gmail（可选） | 未配置 | — | — | — | — | — | — | — | 无可用账户 |
| 自定义 IMAP/SMTP | 假服务器覆盖 | 假服务器覆盖 | 假服务器覆盖 | — | — | — | — | — | 无真实服务器配置 |

逐项结果：

1. 登录与文件夹角色：3 个账户均显示 `Ok`；收件箱、已发送、草稿、垃圾箱、已删除角色识别正确，QQ 的“其他文件夹/QQ邮件订阅”保留为 Other。
2. 收信与正文：QQ 直接读本地缓存；两个 Outlook 账户首次打开正文时按需从 IMAP 拉取（`fromCache=false`），HTML 与纯文本均返回。
3. 发信：3 个账户分别向自己发送纯文本邮件，均到达收件箱；已发送文件夹出现副本。
4. 附件：QQ 与 Outlook 各发送 `验收附件-中文文件名.txt` 后重新下载，文件名均未乱码，SHA-256 与源文件一致。
5. 草稿：QQ 与 Outlook 保存草稿后重启 Engine，草稿均仍在草稿箱；未在 UI 内完成“从草稿直接发送”。
6. 搜索：QQ 与 Outlook 均按主题、发件人、正文命中。
7. 定时/撤回：QQ 定时 3 分钟后发送、重启 Engine 后仍按点发出；另建一封 5 分钟后发送的邮件并 `cancel`，返回 `affected=1`。Outlook 定时 3 分钟发送，重启后也按点发出。
8. 退订/垃圾规则：`List-Unsubscribe` 解析、一键退订标记、规则 `MoveToJunk` 往返有单元测试；2026-11-26 在 Outlook 真实邮件上通过 UI 点击「标记垃圾邮件并拦截发件人」，邮件移入 Junk 并保存规则；随后同发件人新邮件自动进入 Junk。QQ 账户未单独做该 UI 操作；第三方退订未提交。

本轮真实账户发现并修复：

- **Outlook 已发送副本重复**：Outlook SMTP 会自动把邮件写入服务器“已发送”，客户端又 APPEND 一份，导致同一封邮件出现两条（Message-ID 不同，一条为 `@outlook.com`，一条为 `@home`）。修复为 `SmtpSendService.ShouldUploadSentCopy` 对 Outlook 跳过 APPEND，QQ/自定义仍保留；修复后两个 Outlook 账户各复测一封，均只有一条已发送副本。回归测试 `OutlookServerSavesItsOwnSentCopy` 覆盖 QQ/自定义/Outlook 三种策略。
- **标签 IPC 响应类型映射缺失**：UI 请求 `getMessageTags`、`getTags`、`saveTag` 时，`UiConnection.GetResponseType` 回退为 `ok`，日志出现 `Expected OkResponse, got MessageTagsResponse/TagsResponse`。已补 `GetTags→Tags`、`SaveTag→Tags`、`GetMessageTags→MessageTags` 映射。

仍未完成：

- Gmail、自定义 IMAP/SMTP 无真实账户。
- Linux x64/arm64 桌面托盘、WebKitGTK、通知点击未验收。
- 真实第三方退订提交；解析与规则往返已覆盖，未向订阅方发请求。

## Windows 安装包覆盖安装与卸载（2026-11-26，XIZXI）

在干净用户环境实测 Inno Setup 安装包：

1. 0.3.4 静默安装 → 退出码 0，文件版本 `0.3.4.0`，Engine probe 通过。
2. 0.3.4 卸载 → 退出码 0，安装目录、`HKCU\Software\Clients\Mail\MailDog`、`HKCU\Software\Classes\MailDog.Eml` 删除。
3. 0.3.3 安装 → 退出码 0，文件版本 `0.3.3.0`，probe 通过。
4. 0.3.4 覆盖安装 → 退出码 0，文件版本 `0.3.4.0`，probe 通过，数据库保留。
5. 最终卸载 → 退出码 0，安装目录与注册项消失，`%LOCALAPPDATA%\MailDog\maildog.db` 保留。

结论：覆盖升级、卸载清理与用户数据保留均通过。详见 `docs/smoke/2026-11-25-stage24.md`。

## 代码签名与崩溃上报

- 代码签名：`src/build/sign.ps1`，`package.ps1` 在 Windows 且配置 `MAILDOG_SIGN_PFX_BASE64` 时签名 Engine/UI/CLI；CI secrets 已接入。自签 PFX 走完整打包+安装包流程验证通过；受信任 CA 证书未提供，`Get-AuthenticodeSignature` 对自签根显示 `UnknownError` 属预期。详见 `docs/release-signing.md`。
- 崩溃上报：`CrashReportUploader` 与 `CrashLog`，默认只写本地 `crash.log`，配置 `MAILDOG_CRASH_ENDPOINT` 或 `crash-endpoint.txt` 后上报；只发送异常类型与堆栈，不发送异常消息。`CrashReportUploaderTests` 7 项通过；真实上报地址未提供。详见 `docs/crash-reporting.md`。


## 升级回滚测试

自动化已覆盖 `src/build/verify-upgrade-rollback.ps1`：

1. 下载 0.3.3 便携包，由旧版创建 schema v10 数据库。
2. 用当前构建的 Engine 打开同一个库，升级到 v15，并生成 `rollback.pre-v10.bak`。
3. 旧版 0.3.3 直接打开 v15 数据库并正常退出。
4. 用快照替换数据库后再启动 0.3.3，确认旧 schema 可恢复。

结果：2026-11-26 在 HOME 实机通过。安装包覆盖安装/卸载另在干净用户 XIZXI 上实测通过，见上一节。

当前 schema 版本为 15。启动时若检测到旧 schema，会先用 `VACUUM INTO` 生成 `maildog.db.pre-v<旧版本>.bak` 快照；快照不覆盖已有同名文件，可通过临时替换数据库文件回滚。

## 待办与阻塞

- Gmail、自定义 IMAP/SMTP 没有真实账户，真实账户矩阵只覆盖 QQ 1 个和 Outlook 2 个。
- `docs/qa-matrix.md` 中的真实账户结果来自隔离库；UI 垃圾规则测试临时换库后已恢复真实库，测试邮件已移入已删除。
- Linux x64/arm64 桌面上的托盘、WebKitGTK 正文与通知仍需设备验收。
