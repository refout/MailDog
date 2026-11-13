# MailDog

双进程、全 Native AOT、零 MSAL 的桌面邮件客户端。

- **Engine**：MailKit（IMAP/SMTP）+ SQLite 索引 + Win32 托盘 + OAuth2 PKCE + 命名管道服务
- **UI**：Avalonia 12 + Fluent Design + NativeWebView，按需启动、关闭即退

> 当前状态：Stage 0 已完成。双进程 AOT 探针、WebView2 渲染、MailKit/SQLite 单文件验证全部通过；下一步进入 Contracts 与 IPC。

## 文档

- [技术方案](docs/PLAN.md)：架构、库选型、IPC 协议、存储、认证、AOT 纪律、里程碑、风险
- [界面设计](docs/UI-DESIGN.md)：Fluent 设计令牌、三栏布局、组件规格、交互与可访问性
- [Stage 0 探针结论](docs/SPIKES.md)：实测结果、原生库嵌入方案、WebView2 与清单修正

## 仓库结构

```
MailDog/
├── docs/               方案、界面设计、Stage 0 探针结论
├── src/                代码根：MailDog.slnx、global.json、项目、tests、build 脚本
├── .github/            CI 工作流
├── .editorconfig
└── README.md
```

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

- .NET SDK 11（已锁定 `11.0.100`，见 `src/global.json`）
- Visual Studio Build Tools 2026，勾选 `Microsoft.VisualStudio.Component.VC.Tools.x86.x64` 与 `Microsoft.VisualStudio.Component.Windows11SDK.26100`
- Windows 10 1809+ / Windows 11
- Windows 10 需要 [WebView2 Runtime](https://developer.microsoft.com/microsoft-edge/webview2/)（Windows 11 已内置）

## 构建

```powershell
# 构建（global.json 在 src 下）
cd src
dotnet build MailDog.slnx -c Release

# 发布两个 AOT 单文件（从仓库根执行）
pwsh -File src/build/publish.ps1 -Runtime win-x64
```

## 已实测产物

```
dist/win-x64/MailDog/
├── MailDog.Engine.exe    6.00 MiB
└── MailDog.UI.exe       35.54 MiB
```

- 两个 exe 均可在空目录中独立运行，无需 .NET 运行时。
- SQLite / Skia / HarfBuzz 原生库嵌入 exe，首次启动解压到 `%LOCALAPPDATA%\MailDog\native\`。
- UI 使用 Skia 软件渲染，不依赖 ANGLE；WebView2 仍使用自身 GPU 进程。
- 实测：Engine 203 ms；UI 首个窗口 445 ms。

## 路线图

| 阶段 | 内容 | 状态 |
|---|---|---|
| 0 | 工具链与 AOT 探针 | ✅ 已完成 |
| 1 | Contracts + 源生成序列化 | 下一步 |
| 2 | Mail + Engine（QQ 只读） | 待开始 |
| 3 | OAuth2 PKCE（Outlook） | 待开始 |
| 4 | UI 最小闭环 | 待开始 |
| 5 | NativeWebView + 净化 + 降级 | 待开始 |
| 6 | 托盘与进程生命周期闭环 | 待开始 |

详见 [技术方案](docs/PLAN.md#16-里程碑)。

## 许可

待定（见 [开放问题](docs/PLAN.md#18-开放问题)）。
