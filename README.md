# MailDog

双进程、全 Native AOT、零 MSAL 的桌面邮件客户端。

- **Engine**：MailKit（IMAP/SMTP）+ SQLite 索引 + Win32 托盘 + OAuth2 PKCE + 命名管道服务
- **UI**：Avalonia 12 + Fluent Design + NativeWebView，按需启动、关闭即退

> 当前状态：方案阶段。代码尚未开始，先评审架构与界面设计。

## 文档

- [技术方案](docs/PLAN.md)：架构、库选型、IPC 协议、存储、认证、AOT 纪律、里程碑、风险
- [界面设计](docs/UI-DESIGN.md)：Fluent 设计令牌、三栏布局、组件规格、交互与可访问性

## 核心决策

| 维度 | 决策 |
|---|---|
| 目标框架 | .NET 11 / `net11.0` |
| UI | Avalonia 12.1.6 + `Avalonia.Themes.Fluent` |
| WebView | `Avalonia.Controls.WebView` 12.1.2（Windows 使用 WebView2） |
| 邮件 | MailKit 5.0.0，不引用 `MimeKit.Cryptography` |
| 存储 | SQLite（`Microsoft.Data.Sqlite`），ADO.NET |
| 认证 | QQ 授权码；Outlook OAuth2 Authorization Code + PKCE（Engine 内自研回环） |
| IPC | 命名管道 + 4 字节长度前缀 + 源生成 JSON |
| 发布 | Native AOT 单文件 × 2，无 .NET 运行时依赖 |
| 平台 | v1 Windows 优先；macOS/Linux 预留抽象 |

## 前置条件

- .NET SDK 11（当前开发机仅有 10.0.403，Stage 0 安装）
- Windows 10 1809+ / Windows 11
- Windows 10 需要 [WebView2 Runtime](https://developer.microsoft.com/microsoft-edge/webview2/)（Windows 11 已内置）

## 计划产物

```
dist/win-x64/MailDog/
├── MailDog.Engine.exe
└── MailDog.UI.exe
```

## 路线图

| 阶段 | 内容 |
|---|---|
| 0 | 工具链与 AOT 探针 |
| 1 | Contracts + 源生成序列化 |
| 2 | Mail + Engine（QQ 只读） |
| 3 | OAuth2 PKCE（Outlook） |
| 4 | UI 最小闭环 |
| 5 | NativeWebView + 净化 + 降级 |
| 6 | 托盘与进程生命周期闭环 |

详见 [技术方案](docs/PLAN.md#16-里程碑)。

## 许可

待定（见 [开放问题](docs/PLAN.md#18-开放问题)）。
