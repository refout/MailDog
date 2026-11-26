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

## 真实账户矩阵（2026-11-26，HOME）

测试使用 `%LOCALAPPDATA%\MailDog\maildog.db` 的隔离备份，通过 `MailDog.Engine.exe --db <临时库>` 启动当前构建；用户正在使用的数据库没有被修改。共 3 个已配置账户：QQ 1 个、Outlook OAuth2 2 个。

| 账户类型 | 登录/文件夹 | 收信/正文 | 发信 | 草稿 | 附件 | 搜索 | 定时/撤回 | 退订/垃圾规则 | 备注 |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| QQ 邮箱 | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | 🟡 | 密码登录；中文附件名往返正常 |
| Outlook / Microsoft 365 | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | 🟡 | 2 个 OAuth 账户；已发送副本重复已修复并复测 |
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
8. 退订/垃圾规则：`List-Unsubscribe` 解析、一键退订标记、规则 `MoveToJunk` 往返已有单元测试；未在真实邮件上用 UI 点击退订或“标记垃圾并拦截发件人”。

本轮真实账户发现并修复：

- **Outlook 已发送副本重复**：Outlook SMTP 会自动把邮件写入服务器“已发送”，客户端又 APPEND 一份，导致同一封邮件出现两条（Message-ID 不同，一条为 `@outlook.com`，一条为 `@home`）。修复为 `SmtpSendService.ShouldUploadSentCopy` 对 Outlook 跳过 APPEND，QQ/自定义仍保留；修复后两个 Outlook 账户各复测一封，均只有一条已发送副本。回归测试 `OutlookServerSavesItsOwnSentCopy` 覆盖 QQ/自定义/Outlook 三种策略。
- **标签 IPC 响应类型映射缺失**：UI 请求 `getMessageTags`、`getTags`、`saveTag` 时，`UiConnection.GetResponseType` 回退为 `ok`，日志出现 `Expected OkResponse, got MessageTagsResponse/TagsResponse`。已补 `GetTags→Tags`、`SaveTag→Tags`、`GetMessageTags→MessageTags` 映射。

仍未完成：

- Gmail、自定义 IMAP/SMTP 无真实账户。
- 真实邮件上的退订/垃圾规则 UI 操作（解析与规则往返已有单元测试）。
- Linux x64/arm64 桌面托盘、WebKitGTK、通知点击未验收。
- 安装包层面的覆盖安装/卸载未执行；Inno Setup 6.7.3 已安装，0.3.0 与 0.3.3 的 setup.exe 均已编译成功，但默认 AppId/安装目录会与机器上已有的 0.3.1 安装冲突，未在本机覆盖安装。数据库级升级回滚已通过。


## 升级回滚测试

自动化已覆盖 `src/build/verify-upgrade-rollback.ps1`：

1. 下载 0.3.3 便携包，由旧版创建 schema v10 数据库。
2. 用当前构建的 Engine 打开同一个库，升级到 v15，并生成 `rollback.pre-v10.bak`。
3. 旧版 0.3.3 直接打开 v15 数据库并正常退出。
4. 用快照替换数据库后再启动 0.3.3，确认旧 schema 可恢复。

结果：2026-11-26 在 HOME 实机通过。Inno Setup 6.7.3 已安装，0.3.0 与 0.3.3 的 setup.exe 编译成功；因本机已有 0.3.1 安装且 AppId/默认目录相同，覆盖安装/卸载仍应在干净机器或独立用户下执行。

当前 schema 版本为 15。启动时若检测到旧 schema，会先用 `VACUUM INTO` 生成 `maildog.db.pre-v<旧版本>.bak` 快照；快照不覆盖已有同名文件，可通过临时替换数据库文件回滚。

## 待办与阻塞

- Gmail、自定义 IMAP/SMTP 没有真实账户，真实账户矩阵只覆盖 QQ 1 个和 Outlook 2 个。
- 安装包覆盖安装/卸载需要干净 Windows 用户或机器；Inno 编译链已验证，setup.exe 已生成。
- Linux x64/arm64 桌面上的托盘、WebKitGTK 正文与通知仍需设备验收。
