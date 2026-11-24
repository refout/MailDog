# 第三方开源组件清单

MailDog 的界面、邮件与存储能力建立在以下开源项目之上。各组件按自身许可协议使用，
本项目不改变其许可条款。

## 界面与渲染

| 组件 | 版本 | 用途 | 许可 |
| --- | --- | --- | --- |
| Avalonia | 12.1.6 | 跨平台桌面 UI 框架 | MIT |
| Avalonia.Controls.ColorPicker | 12.1.6 | 颜色选择控件 | MIT |
| Avalonia.Desktop | 12.1.6 | Windows 后端 | MIT |
| Avalonia.Themes.Fluent | 12.1.6 | Fluent 主题 | MIT |
| Avalonia.Controls.WebView | 12.1.2 | HTML 邮件渲染 | MIT |
| Avalonia.Angle.Windows.Natives | 2.1.27548.20260419 | 图形后端原生库（ANGLE） | BSD-3-Clause |
| SkiaSharp | 3.119.4 | 2D 图形绘制 | MIT |
| HarfBuzzSharp | 8.3.1.3 | 文本整形 | MIT |
| FluentIcons.Avalonia | 2.1.345 | 界面图标（Fluent UI System Icons） | MIT |
| AvaloniaRichEditor | 1.3.0 | 富文本写信编辑器 | 见该包内许可文件 |
| CommunityToolkit.Mvvm | 8.4.2 | MVVM、命令与消息 | MIT |

## 邮件与存储

| 组件 | 版本 | 用途 | 许可 |
| --- | --- | --- | --- |
| MailKit | 5.0.0 | IMAP / SMTP / MIME 协议栈 | MIT |
| MimeKit | 随 MailKit | MIME 解析与生成 | MIT |
| Microsoft.Data.Sqlite | 11.0.0 | 本地数据库访问 | MIT |
| SQLite | 3.53.4 | 嵌入式数据库引擎 | Public Domain |
| SQLitePCLRaw（bundle_e_sqlite3 等） | 随 Microsoft.Data.Sqlite | SQLite 原生绑定 | Apache-2.0 |
| HtmlAgilityPack | 1.12.4 | HTML 解析、清洗与降级为纯文本 | MIT |
| System.Security.Cryptography.ProtectedData | 11.0.0 | 账户凭据加密（DPAPI） | MIT |

## 构建与测试

| 组件 | 版本 | 用途 | 许可 |
| --- | --- | --- | --- |
| Microsoft.NET.Test.Sdk | 17.14.1 | 测试运行基础设施 | MIT |
| xunit | 2.9.3 | 单元测试框架 | Apache-2.0 |
| xunit.runner.visualstudio | 3.1.5 | 测试适配器 | Apache-2.0 |

## 运行时

| 组件 | 版本 | 用途 | 许可 |
| --- | --- | --- | --- |
| .NET（含 Native AOT 运行时） | 11.0 | 运行时与编译工具链 | MIT |

## 其他

- 安装包由 [Inno Setup](https://jrsoftware.org/isinfo.php) 生成，其自身许可与该安装包内附文件一致。
- Windows、.NET 及其徽标为 Microsoft Corporation 的商标或产品。
- 应用图标、品牌资产（`docs/brand/` 与 `web/assets/` 中的图形）由本项目维护，不在上述开源许可范围内。

如需在发行版中附带完整许可文本，请以上各项目的官方仓库为准；本文件仅作索引。
