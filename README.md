# MailDog

双进程、全 Native AOT、零 MSAL 的桌面邮件客户端。

- **Engine**：MailKit（IMAP）+ SQLite 索引 + Win32 托盘 + OAuth2 PKCE + 命名管道服务
- **UI**：Avalonia 12 + Fluent Design + NativeWebView，按需启动、关闭即退

> 当前状态：**v1 只读客户端已完成**。QQ / Outlook 账户、文件夹与邮件列表、HTML 正文渲染、HTML 净化、托盘常驻、通知、单文件 AOT 与 CI 全部落地。

## 已实现

- 多账户：QQ 邮箱授权码 + Outlook OAuth2 Authorization Code + PKCE。
- 邮件同步：文件夹、最近邮件、未读/旗标、周期增量同步（60s）。
- 本地索引：SQLite 缓存账户、文件夹、邮件头与正文，离线可读已缓存内容。
- HTML 正文：白名单净化、CSP、默认拦截远程图片、渲染失败自动降级纯文本。
- 桌面体验：Fluent 三栏布局、暗色/亮色主题、搜索、筛选、附件元数据。
- 托盘：打开 UI、立即同步、退出；UI 关闭后 Engine 与托盘继续驻留。
- 发布：两个 Native AOT 单文件，无 .NET 运行时依赖；GitHub Actions 已通过。

## 文档

- [技术方案](docs/PLAN.md)：架构、库选型、IPC 协议、存储、认证、AOT 纪律、里程碑、风险
- [界面设计](docs/UI-DESIGN.md)：Fluent 设计令牌、三栏布局、组件规格、交互与可访问性
- [探针结论](docs/SPIKES.md)：AOT、原生库嵌入、WebView2 与工具链实测记录

## 仓库结构

```
MailDog/
├── docs/               方案、界面设计、探针结论
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

## 构建与发布

```powershell
# 构建与测试（global.json 在 src 下）
cd src
dotnet build MailDog.slnx -c Release
dotnet test MailDog.slnx -c Debug --no-restore

# 发布两个 AOT 单文件（从仓库根执行）
pwsh -File src/build/publish.ps1 -Runtime win-x64
```

## 运行

```powershell
# 发布包：两个 exe 在同一个 zip 中，解压后双击 MailDog.UI.exe
dist/win-x64/MailDog-win-x64.zip

# 方式一：先启动后台，托盘常驻，需要时打开 UI
dist/win-x64/MailDog/MailDog.Engine.exe

# 方式二：直接启动 UI（若 Engine 未运行会自动拉起）
dist/win-x64/MailDog/MailDog.UI.exe
```

- QQ 邮箱：在 QQ 邮箱网页端开启 IMAP/SMTP，生成**授权码**（不是登录密码）。
- Outlook：在“设置 → Outlook”填写 Entra 公共客户端 `client_id`，再通过浏览器登录。重定向 URI 需注册 `http://localhost` 与 `http://127.0.0.1`。
- 数据库与日志：`%LOCALAPPDATA%\MailDog\`。

## 已实测产物

```
dist/win-x64/
├── MailDog/
│   ├── MailDog.Engine.exe   12.42 MiB
│   └── MailDog.UI.exe       44.20 MiB
└── MailDog-win-x64.zip      24.09 MiB
```

- 两个 exe 均为自包含单文件，无需 .NET 运行时；UI 需与 Engine 位于同一目录，才能自动拉起后台。
- `MailDog-win-x64.zip` 是完整发布包，解压后双击 `MailDog.UI.exe` 即可。
- SQLite / Skia / HarfBuzz / ANGLE 原生库嵌入 exe，首次启动解压到 `%LOCALAPPDATA%\MailDog\native\`。
- UI 优先使用 ANGLE 渲染以启用云母/亚克力材质与透明窗口；加载失败时自动回退 Skia 软件渲染。WebView2 仍使用自身 GPU 进程。
- 关闭 UI 后 `MailDog.UI` 与新增的 `msedgewebview2` 进程全部退出，Engine 托盘继续驻留。
- 托盘图标由微软 Fluent UI System Icons 的 `Mail` 图标导出。

## 路线图

| 阶段 | 内容 | 状态 |
|---|---|---|
| 0 | 工具链与 AOT 探针 | ✅ 已完成 |
| 1 | Contracts + 源生成序列化 | ✅ 已完成 |
| 2 | Mail + Engine（QQ 只读） | ✅ 已完成 |
| 3 | OAuth2 PKCE（Outlook） | ✅ 已完成 |
| 4 | UI 最小闭环 | ✅ 已完成 |
| 5 | NativeWebView + 净化 + 降级 | ✅ 已完成 |
| 6 | 托盘与进程生命周期闭环 | ✅ 已完成 |
| 7 | 撰写/回复/发信与附件下载 | v1.1 |
| 8 | 代码签名、安装包、自动更新 | v1.1 |

## 已知限制

- 未实现撰写、回复、转发与 SMTP 发信；附件仅显示元数据，不提供下载。
- 新邮件使用 60s 轮询，尚未启用 IMAP IDLE。
- Outlook OAuth 与 QQ 真实账户需要在具备凭据的环境中完成手工验收。
- Engine 仍为控制台子系统进程，启动托盘后会隐藏控制台窗口；后续可改为 `WinExe`。

## 许可

待定（见 [开放问题](docs/PLAN.md#18-开放问题)）。
