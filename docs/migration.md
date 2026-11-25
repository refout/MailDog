# MailDog 迁移与互操作指引

MailDog 支持 `.eml` 单封邮件和 `.mbox` 归档的导入，也能把当前账户已同步的邮件导出为标准 mbox。入口在“设置 → 本地文件 → 迁移与互操作”。

## 从其他邮件客户端迁入 MailDog

### Thunderbird → MailDog

1. 在 Thunderbird 中选中要迁移的邮件或整个文件夹。
2. 右键选择“保存为”，或使用扩展把文件夹导出为 `.mbox`。
3. 在 MailDog 中选择要接收邮件的账户，打开“设置 → 本地文件 → 迁移与互操作”。
4. 点击“导入 mbox / eml”，一次可多选若干 `.mbox`、`.mbx` 或 `.eml` 文件。
5. MailDog 会把每封邮件追加到所选账户的收件箱，随后由下一轮同步建立本地索引。

### Outlook → MailDog

Outlook 桌面版没有直接的 mbox 导入入口，推荐用 Thunderbird 作为中转：

1. 在 Outlook 中把邮件拖到本地文件夹，或使用“文件 → 打开和导出 → 导入/导出”导出为 `.pst`。
2. 在 Thunderbird 中导入 Outlook 的 PST（或直接把 `.eml` 邮件拖入 Thunderbird 本地文件夹）。
3. 从 Thunderbird 导出为 mbox，再按上面的步骤导入 MailDog。

如果手上已经是 `.eml` 文件，可跳过中转直接导入；`.msg` 不是 MailDog 支持的格式，需要先转成 `.eml`。

### 导入注意事项

- 导入依赖邮箱服务器支持 IMAP `APPEND`。部分只读账号或受限企业邮箱会拒绝追加。
- 导入不会去重；同一文件重复导入会产生重复邮件。
- 导入过程中单封失败会跳过并计入提示，不会中断整批。
- 导入后不会立刻出现在本地列表，等同步周期拉取新 UID 后可见。

## 从 MailDog 迁出

1. 在“设置 → 本地文件 → 迁移与互操作”点击“导出当前账户 mbox”。
2. 选择保存位置，MailDog 会把当前账户所有已同步邮件的原始 MIME 写成一个 mbox。
3. 导出完成后会显示邮件数量与文件大小；导出只覆盖本地已同步的邮件。

导出的 mbox 可以直接被 Thunderbird 导入。Outlook 不能原生读取 mbox，推荐先导入 Thunderbird，再从 Thunderbird 迁移到 Outlook。

## 与备份的区别

mbox 只包含邮件正文与附件，不包含账户配置、联系人、标签、规则、草稿状态或 MailDog 本地数据库。需要完整迁移 MailDog 自身状态时，使用“备份与恢复”生成 `.zip` / `.mdog` 备份。
