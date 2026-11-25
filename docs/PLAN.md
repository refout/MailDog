# MailDog 技术方案

> 状态：v1.1 写操作、发件箱、搜索、附件预览、备份恢复与账户偏好完成（Stage 0–8）；Stage 9 的代码签名、自动更新与崩溃上报仍待证书/发布链路/上报端点
> 日期：2026-11-18
> 适用范围：MailDog v1（Windows 优先）
> 关联文档：[UI-DESIGN.md](./UI-DESIGN.md)、[SPIKES.md](./SPIKES.md)

---

## 0. 决策摘要

| 维度 | 决策 |
|---|---|
| 产品名 | **MailDog** |
| 形态 | 双进程：`MailDog.Engine.exe`（后台常驻）+ `MailDog.UI.exe`（按需启动，关闭即退） |
| 目标框架 | `net11.0`（SDK 11.0.100，已安装） |
| UI 框架 | Avalonia 12.1.6 + `Avalonia.Themes.Fluent` |
| WebView | `Avalonia.Controls.WebView` 12.1.2（`NativeWebView`，Windows 走 WebView2） |
| 邮件协议 | MailKit 5.0.0（IMAP/SMTP），不引用 `MimeKit.Cryptography` |
| 本地存储 | SQLite（`Microsoft.Data.Sqlite` 11.0.0，ADO.NET，不用 EF Core） |
| 认证 | QQ 邮箱授权码 + SASL PLAIN；Outlook OAuth2 Authorization Code + PKCE（自研回环） |
| IPC | 命名管道（Windows Named Pipe / Linux Unix Domain Socket）+ 4 字节长度前缀 + 源生成 JSON |
| 进程模型 | Engine 持有 Win32 托盘；Linux 用 Avalonia TrayIcon（无 StatusNotifier 宿主时静默常驻），`shutdown` IPC / `maildog quit` 收尾；UI 关闭即 `Shutdown()`，Engine 继续驻留 |
| 发布 | Native AOT 单文件 × 2，零 .NET 运行时依赖 |
| 仓库 | 本地 `C:\Users\hale\IdeaProjects\MailDog`；远端 `https://github.com/refout/MailDog`（Public） |
| 平台范围 | v1 交付 Windows x64/arm64；v0.3.0 增加 Linux x64（WebKitGTK HTML/纯文本回退、可选托盘、协议激活）；macOS 与 Linux arm64 仍未定 |

### 0.1 对原始架构稿的技术修正

1. **Engine 不能使用 Avalonia 的 `WebAuthenticationBroker`。**
   官方签名是 `AuthenticateAsync(TopLevel topLevel, WebAuthenticatorOptions options)`，必须传入一个窗口级 `TopLevel`。Engine 不加载任何 UI 框架，因此拿不到 `TopLevel`。结论：OAuth2 PKCE 在 Engine 内**自研回环实现**，不依赖 Avalonia 的 OAuth 组件；`AuthorizationCodePkceSession` 也不进入 Engine。

2. **回环监听用 `TcpListener`，不用 `HttpListener`。**
   在 Windows 上 `HttpListener` 走 HTTP.sys，非管理员账户绑定自定义前缀可能触发 URL ACL/Access Denied；而 `TcpListener` 可以直接绑定 `IPAddress.Loopback` 和端口 `0`，由操作系统分配空闲端口，无 ACL 问题、无端口探测竞态。代价是需要一个约 150 行的最小 HTTP/1.1 请求解析器，这属于可控成本。
   > 原方案强调 `response_mode=query` 以免 `#fragment` 无法读取，这一点仍然成立，必须显式设置。

3. **Linux 的 WebView 回退顺序需按官方文档修正。**
   `NativeWebView` 在 Linux 上**优先 WPE WebKit，未安装时自动回退 WebKitGTK**；`NativeWebDialog` 始终使用 WebKitGTK。因此在 Linux 上不存在“WPE 缺失就必须换 `NativeWebDialog`”的硬分叉。v0.3.0 的 UI 通过 `HtmlRendering` 探测 `libwebkit2gtk-4.1.so.0` / `libwebkit2gtk-4.0.so.37` / `libWPEWebKit-2.0.so.1`，可用时渲染 HTML，缺库时正文自动退回纯文本。

4. **.NET 11 SDK 已就位。**
   本机已安装 11.0.100；`global.json` 锁定 `11.0.100`（`rollForward=latestFeature`）。Stage 0 的所有探针均在该 SDK 上完成。

5. **Native AOT 不会打包第三方原生库。**
   `IncludeNativeLibrariesForSelfExtract=true` 在 AOT 下不生效：`e_sqlite3.dll`、`libSkiaSharp.dll`、`libHarfBuzzSharp.dll` 会留在 exe 旁边。解决方式是把它们作为嵌入资源，启动时解压到 `%LOCALAPPDATA%\MailDog\native\`，再用 `NativeLibrary.SetDllImportResolver` 解析。详见 [SPIKES.md](./SPIKES.md) 3.3。

6. **ANGLE 进入单文件，用于云母/亚克力材质。**
   软件渲染下 Avalonia 的 `ActualTransparencyLevel` 恒为 `None`，材质无法生效。现在把 `av_libglesv2.dll` 作为嵌入资源解压并注册解析器，`RenderingMode = [AngleEgl, Software]`：优先 ANGLE，失败自动回退。WebView2 仍使用自身的 GPU 进程。详见 [SPIKES.md](./SPIKES.md) 3.4。

7. **UI 必须有应用清单，WebView2 必须有可写的用户数据目录。**
   AOT 产物缺少 `supportedOS` 声明时，`NativeControlHost` 无法创建子窗口；未设置 `UserDataFolder` 时会以 `E_ACCESSDENIED` 失败。两者都已在 Stage 0 修复并写入模板。

---

## 1. 产品范围

### 1.1 v1 范围内

- 多账户：Outlook（Microsoft 个人/工作账户，OAuth2 PKCE）+ QQ 邮箱（授权码）。
- 只读邮件体验：账户、文件夹、邮件列表、邮件正文、未读/旗标状态；支持全选与批量标记。
- 本地索引：SQLite 缓存邮件头、文件夹与正文；离线可读已缓存内容。
- 新邮件提醒：60s 轮询 + Win32 气泡通知（IMAP IDLE 列为 v1.1 优化）。
- HTML 正文渲染：净化 + CSP + 默认拦截远程图片 + 失败降级纯文本。
- 托盘常驻：打开 UI、立即同步、账户状态、退出。
- 主题：Light / Dark / 跟随系统；Fluent Design。
- 发布：两个 Native AOT 单文件，Windows x64（arm64 同流程）。

### 1.2 v1 范围外（后续里程碑）

- 撰写/回复/转发/发件（SMTP 发信与 Outbox）。
- 附件下载、内嵌图片（`cid:`）解析、附件预览。
- 全文搜索（FTS5 可先埋点，v1 只搜主题/发件人/预览）。
- 规则、标签、日程、通讯录。
- 多窗口、拖拽、打印。
- macOS / Linux。
- 自动更新、安装包签名分发。

> 取舍理由：先把“认证 → 同步 → 索引 → IPC → 渲染 → 生命周期”这条最长的链路打通，写操作与附件在数据模型和协议上预留，不进入 v1 验收。

---

## 2. 总体架构

```
┌─────────────────────────────────────────────────────────────────────┐
│ MailDog.Engine.exe  (Native AOT, WinExe, 无 UI 框架)                │
│                                                                     │
│  ┌──────────────┐  ┌──────────────┐  ┌───────────────────────────┐  │
│  │ Account/Auth │  │ Sync Engine  │  │ Win32 Host                │  │
│  │ QQ PLAIN     │  │ MailKit IMAP │  │ Shell_NotifyIcon + Menu   │  │
│  │ OAuth2 PKCE  │  │ IDLE / Poll  │  │ Message-only window       │  │
│  │ Token Store  │  │ SQLite Index │  │ Single-instance mutex     │  │
│  └──────┬───────┘  └──────┬───────┘  └───────────┬───────────────┘  │
│         └─────────────────┴──────────────────────┘                  │
│                           │                                         │
│                  ┌────────▼─────────┐                               │
│                  │ IPC Server       │  Named Pipe, length-prefixed  │
│                  │ Request Router   │  source-generated JSON        │
│                  └────────┬─────────┘                               │
└───────────────────────────┼─────────────────────────────────────────┘
                            │ \\.\pipe\MailDog.Engine.IPC.v1
┌───────────────────────────▼─────────────────────────────────────────┐
│ MailDog.UI.exe  (Native AOT)                                        │
│ Avalonia 12 + FluentTheme + NativeWebView + MailDog.Html            │
│ 按需启动；主窗口关闭 → Shutdown() → 进程退出                        │
└─────────────────────────────────────────────────────────────────────┘
```

### 2.1 进程职责

| 职责 | Engine | UI |
|---|---|---|
| 托盘图标与菜单 | ✅ 唯一持有者 | ❌ |
| IMAP / SMTP | ✅ | ❌（UI 不引用 MailKit） |
| SQLite | ✅ 唯一读写者 | ❌（通过 IPC） |
| OAuth2 浏览器流程 | ✅ | ❌ |
| Token 持久化 | ✅ | ❌ |
| HTML 净化与渲染 | ❌ | ✅ |
| Avalonia / WebView2 | ❌ | ✅ |
| 新邮件通知 | ✅ 气泡 | 可选页内提示 |
| 单实例 | ✅ 命名互斥体 | 由 Engine 拉起，通常只有一个 |

### 2.2 依赖方向

```
Contracts ← Html ← Mail ← Engine
Contracts ← UI
```

- `MailDog.Contracts`：零第三方依赖，只有 DTO、枚举、`JsonSerializerContext`、IPC 编解码器接口。
- `MailDog.Html`：只依赖 Contracts（实际上只需 BCL），纯字符串处理，不依赖 AngleSharp。
- `MailDog.Mail`：依赖 Contracts + MailKit + Microsoft.Data.Sqlite。
- `MailDog.Engine`：依赖 Mail + Contracts；只使用 BCL + Win32 P/Invoke。
- `MailDog.UI`：依赖 Contracts + Html + Avalonia + Avalonia.Controls.WebView + FluentIcons。

**硬约束**：`MailDog.UI` 的依赖树中不得出现 `MailKit`/`MimeKit`；`MailDog.Engine` 的依赖树中不得出现 `Avalonia*`。CI 增加一条依赖断言脚本，防止误引用。

### 2.3 为什么坚持双进程

- Engine 常驻只加载 MailKit + SQLite + Win32，内存与启动成本可控；UI 关闭后 Avalonia、Skia、WebView2/Chromium 子进程全部释放。
- Native AOT 下，UI 框架与邮件协议栈的裁剪/源生成互不干扰，问题定位边界清晰。
- 认证与令牌只存在于 Engine 进程；UI 即使被替换，也拿不到 refresh token。
- 代价是 IPC 协议与生命周期管理，需要严格测试；这是本方案的主要复杂度来源。

---

## 3. 技术栈与库选型

### 3.1 版本核验（2026-11-13，来自 nuget.org 与本机 SDK）

| 包 | 版本 | 用途 | AOT / 备注 |
|---|---|---|---|
| .NET SDK | 11.0.100 | 编译与 AOT | 已安装，Stage 0 验证通过 |
| `Avalonia` | 12.1.6 | UI 框架 | 官方支持 Native AOT |
| `Avalonia.Desktop` | 12.1.6 | 桌面窗口/平台后端 | — |
| `Avalonia.Themes.Fluent` | 12.1.6 | Fluent 主题 | 资源编译进程序集 |
| `Avalonia.Controls.WebView` | 12.1.2 | `NativeWebView` / `NativeWebDialog` | AOT 已验证；需 `app.manifest` 与显式 `UserDataFolder` |
| `FluentIcons.Avalonia` | 2.1.345 | 微软 Fluent UI System Icons 图标控件 | 依赖 Avalonia 12.0.0，net10.0 目标；AOT 渲染已验证 |
| `CommunityToolkit.Mvvm` | 8.4.2 | `[ObservableProperty]` / `[RelayCommand]` 源生成 | 源生成器，无反射绑定 |
| `MailKit` | 5.0.0 | IMAP/SMTP/MIME | 依赖 `MimeKit.Core` 5.0.0；AOT 已验证 |
| `Microsoft.Data.Sqlite` | 11.0.0 | SQLite ADO.NET | 自带 `SQLitePCLRaw.bundle_e_sqlite3`；`e_sqlite3` 需嵌入解压 |
| `SkiaSharp` / `SkiaSharp.NativeAssets.Win32` | 3.119.4 | Avalonia 渲染 | `libSkiaSharp.dll` 需嵌入解压 |
| `HarfBuzzSharp` / `HarfBuzzSharp.NativeAssets.Win32` | 8.3.1.3 | 文本整形 | `libHarfBuzzSharp.dll` 需嵌入解压 |
| `Avalonia.Angle.Windows.Natives` | 2.1.27548.20260419 | ANGLE | 嵌入解压，启用云母/亚克力材质 |
| `Avalonia.Controls.ColorPicker` | 12.1.6 | 设置页取色器 | `ColorView`，AOT 兼容 |
| `System.Security.Cryptography.ProtectedData` | 11.0.x | Windows DPAPI 令牌加密 | Windows 专用；跨平台后续替换 |
| xUnit + `Microsoft.NET.Test.Sdk` | 脚手架时锁定 | 单元测试 | 测试项目不 AOT |

> 版本策略：使用中央包管理（`Directory.Packages.props`），所有版本一处锁定；升级必须通过 AOT 门禁与冒烟测试。

### 3.2 明确不引入的库及原因

| 库 | 不用的原因 |
|---|---|
| `Microsoft.Identity.Client`（MSAL） | 原生互操作与 AOT 风险源；本方案用标准 PKCE 替代 |
| `MimeKit.Cryptography` | S/MIME 路径是 MailKit AOT 兼容性的历史障碍；v1 不需要 |
| `HtmlSanitizer` / `AngleSharp` | AngleSharp 反射与 DI 较重，AOT 兼容性不可控；净化器必须是可审计的自有代码 |
| `FluentAvaloniaUI` | 第三方 Fluent 控件库，Avalonia 12 兼容节奏不可控；直接用官方 `FluentTheme` 组合标准控件 |
| `Newtonsoft.Json` | 反射序列化，AOT 不可用；统一 `System.Text.Json` 源生成 |
| `Serilog` / `NLog` | 配置与反射较多；自写 ~120 行滚动文件日志即可 |
| `EntityFrameworkCore` | 裁剪与迁移复杂度高；直接 ADO.NET 更可控 |
| `System.CommandLine` | CLI 用 2.0.15 稳定版，保留原命令、开关、输出与退出码；Engine 参数极少，仍手写解析 |
| `IdentityModel.OidcClient` | 额外依赖；PKCE 代码量小且完全可控 |
| `System.Drawing.Common` | Windows-only 且 AOT 不友好；图标全部走 Fluent 图标几何/ICO 资源 |

### 3.3 图标纪律

- UI 内所有图标来自 **微软 Fluent UI System Icons**（MIT），通过 `FluentIcons.Avalonia` 使用，禁止手绘/临时拼 Path。
- 应用与托盘图标从官方 `Mail` 图标导出多尺寸 `.ico`（16/20/24/32/48/256），生成脚本与源 SVG 一起入库，保证可追溯。
- 图标尺寸固定 16 / 20 / 24；未读使用 Filled 变体，已读使用 Regular 变体。

---

## 4. 解决方案结构

```
MailDog/
├── .editorconfig
├── .gitignore
├── README.md
├── docs/
│   ├── PLAN.md
│   ├── SPIKES.md
│   └── UI-DESIGN.md
├── src/                          ← 代码根，所有代码与构建配置
│   ├── MailDog.slnx
│   ├── global.json
│   ├── Directory.Build.props
│   ├── Directory.Packages.props
│   ├── MailDog.Contracts/
│   │   ├── Ipc/                 Envelope、错误码、消息接口
│   │   ├── Messages/            Request/Response/Event DTO
│   │   ├── Dto/                 Account/Folder/MailSummary/MailBody
│   │   ├── Serialization/       IpcJsonContext、IpcCodecRegistry
│   │   └── MailDog.Contracts.csproj
│   ├── MailDog.Html/
│   │   ├── HtmlSanitizer.cs
│   │   ├── HtmlTokenizer.cs
│   │   ├── UrlPolicy.cs
│   │   ├── DocumentBuilder.cs   CSP / viewport / 基础 CSS
│   │   └── MailDog.Html.csproj
│   ├── MailDog.Mail/
│   │   ├── Accounts/            Provider、凭据、账户服务
│   │   ├── Auth/                OAuth2PkceFlow、LoopbackHttpServer、TokenStore
│   │   ├── Imap/                ImapSession、SyncService、FolderMapper
│   │   ├── Storage/             SqliteStore、Migrations
│   │   ├── Mime/                MimeMessage → DTO
│   │   └── MailDog.Mail.csproj
│   ├── MailDog.Engine/
│   │   ├── Program.cs
│   │   ├── Hosting/             CompositionRoot、SingleInstance、Shutdown
│   │   ├── Ipc/                 IpcServer、RequestRouter、UiConnection
│   │   ├── Tray/                Win32Tray、NativeMethods、Menu
│   │   ├── Logging/             FileLogger
│   │   └── MailDog.Engine.csproj
│   ├── MailDog.UI/
│   │   ├── Program.cs
│   │   ├── App.axaml / App.axaml.cs
│   │   ├── Services/            EngineClient、UiSettings、RenderCoordinator
│   │   ├── ViewModels/
│   │   ├── Views/
│   │   ├── Controls/
│   │   ├── Themes/              Tokens、Controls、ListStyles
│   │   ├── Assets/
│   │   └── MailDog.UI.csproj
│   ├── tests/
│   │   ├── MailDog.Contracts.Tests/
│   │   ├── MailDog.Html.Tests/
│   │   └── MailDog.Mail.Tests/  （可选集成测试，默认 Skip）
│   └── build/
│       ├── publish.ps1
│       ├── verify-aot.ps1
│       └── verify-dependencies.ps1
└── .github/workflows/ci.yml
```

> `global.json` 位于 `src/`，因此直接在仓库根执行 `dotnet` 不会应用 SDK 锁定。构建脚本会先切换到 `src/`；手动执行时也应先 `cd src`。

### 4.1 公共构建属性

`src/Directory.Build.props`：

```xml
<Project>
  <PropertyGroup>
    <LangVersion>latest</LangVersion>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
    <InvariantGlobalization>false</InvariantGlobalization>
    <AvaloniaUseCompiledBindingsByDefault>true</AvaloniaUseCompiledBindingsByDefault>
  </PropertyGroup>
</Project>
```

AOT 项目额外启用：

```xml
<PropertyGroup>
  <PublishAot>true</PublishAot>
  <IsAotCompatible>true</IsAotCompatible>
  <EnableTrimAnalyzer>true</EnableTrimAnalyzer>
  <TrimmerSingleWarn>false</TrimmerSingleWarn>
  <WarningsAsErrors>$(WarningsAsErrors);IL2026;IL2057;IL2060;IL2070;IL2072;IL2075;IL2090;IL2091;IL3050;IL3051;IL3053</WarningsAsErrors>
</PropertyGroup>
```

> `InvariantGlobalization=false` 会让 ICU 随包发布，体积增加但中文日期/排序正常。体积预算在 Stage 0 实测后写回本文档。

### 4.2 中央包管理

`src/Directory.Packages.props`：

```xml
<Project>
  <PropertyGroup>
    <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
  </PropertyGroup>
  <ItemGroup>
    <PackageVersion Include="Avalonia" Version="12.1.6" />
    <PackageVersion Include="Avalonia.Desktop" Version="12.1.6" />
    <PackageVersion Include="Avalonia.Themes.Fluent" Version="12.1.6" />
    <PackageVersion Include="Avalonia.Controls.WebView" Version="12.1.2" />
    <PackageVersion Include="FluentIcons.Avalonia" Version="2.1.345" />
    <PackageVersion Include="CommunityToolkit.Mvvm" Version="8.4.2" />
    <PackageVersion Include="MailKit" Version="5.0.0" />
    <PackageVersion Include="Microsoft.Data.Sqlite" Version="11.0.0" />
    <PackageVersion Include="System.Security.Cryptography.ProtectedData" Version="11.0.0" />
  </ItemGroup>
</Project>
```

### 4.3 global.json

位置：`src/global.json`。所有 `dotnet` 命令应在 `src/` 下执行，或使用会切换工作目录的 `src/build/*.ps1`。

```json
{
  "sdk": {
    "version": "11.0.100",
    "rollForward": "latestFeature",
    "allowPrerelease": false
  }
}
```

---

## 5. Contracts 与 IPC

### 5.1 传输格式

- 命名管道：`\\.\pipe\MailDog.Engine.IPC.v1`
- 服务端：`NamedPipeServerStream`，`PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly`，最多 1 个 UI 连接（UI 单实例）。
- 帧格式：`4 字节小端无符号长度` + `UTF-8 JSON`。长度下限 2，上限 16 MiB；超限立即断开并记录。
- 读取使用 `PipeReader.ReadAtLeastAsync(4)` + `ReadAtLeastAsync(length)`，避免粘包/半包。
- JSON 选项：camelCase、枚举字符串、忽略 null、禁止注释与尾逗号。

### 5.2 信封

```csharp
public sealed class IpcEnvelope
{
    public required string Type { get; init; }      // 消息类型名，如 "getMessages"
    public long RequestId { get; init; }            // 请求/响应关联；事件为 0
    public JsonElement Body { get; init; }          // 具体载荷
}
```

约定：

- UI → Engine 为 Request；Engine → UI 的应答使用同一 `RequestId`，`Type` 为对应 Response。
- Engine → UI 的主动消息为 Event，`RequestId = 0`。
- 错误统一为 `Type = "error"`，`Body` 为 `{ code, message, detail? }`。
- 协议版本只在 `hello` / `helloAck` 中交换；不兼容时 Engine 返回 `protocol_mismatch` 并关闭连接。

### 5.3 消息清单

**握手与生命周期**

| Type | 方向 | Body |
|---|---|---|
| `hello` | UI→E | `{ protocolVersion, clientVersion }` |
| `helloAck` | E→UI | `{ protocolVersion, engineVersion, accounts }` |
| `shutdown` | UI→E | `{}`（仅开发/测试使用，正式版本不暴露） |

**查询**

| Type | 方向 | Body |
|---|---|---|
| `getAccounts` | UI→E | `{}` |
| `accounts` | E→UI | `{ items: AccountDto[] }` |
| `getFolders` | UI→E | `{ accountId }` |
| `folders` | E→UI | `{ accountId, items: FolderDto[] }` |
| `getMessages` | UI→E | `{ folderId, beforeDateUtc?, beforeId?, limit }` |
| `messagesPage` | E→UI | `{ items: MailSummaryDto[], hasMore, nextDateUtc?, nextId? }` |
| `getMessageBody` | UI→E | `{ messageId }` |
| `messageBody` | E→UI | `{ messageId, subject, html?, plainText?, truncated, attachments }` |

**状态变更**

| Type | 方向 | Body |
|---|---|---|
| `markRead` | UI→E | `{ messageId, isRead }` |
| `setFlag` | UI→E | `{ messageId, flag, value }` |
| `syncAccount` | UI→E | `{ accountId? }`（空为全部） |
| `ok` | E→UI | `{}` 或 `{ affected }` |

**账户与认证**

| Type | 方向 | Body |
|---|---|---|
| `addPasswordAccount` | UI→E | `{ provider, email, secret, displayName? }` |
| `startOAuth` | UI→E | `{ provider, loginHint? }` |
| `cancelOAuth` | UI→E | `{}` |
| `removeAccount` | UI→E | `{ accountId }` |

**事件**

| Type | 方向 | Body |
|---|---|---|
| `newMail` | E→UI | `{ accountId, folderId, count, latest: MailSummaryDto }` |
| `syncState` | E→UI | `{ accountId, folderId?, state, progress? }` |
| `authState` | E→UI | `{ accountId, state, message? }`（`connected` / `expired` / `required` / `failed`） |
| `showMainWindow` | E→UI | `{ messageId? }`（托盘点击/通知点击） |
| `accountChanged` | E→UI | `{ account: AccountDto }` |

### 5.4 类型注册表

不用 `[JsonPolymorphic]`，避免多态序列化在源生成下的边界问题。采用显式注册表：

```csharp
public interface IIpcMessage { }

public static class IpcCodecRegistry
{
    private static readonly Dictionary<string, IIpcCodec> Map = new(StringComparer.Ordinal);

    public static void Register<T>(string type, JsonTypeInfo<T> info) where T : IIpcMessage
        => Map[type] = new IpcCodec<T>(info);

    public static IIpcCodec Get(string type) => Map[type];
}
```

- 每个消息类型在 `IpcCodecRegistry.CreateDefault()` 中显式注册一次。
- 序列化只走 `JsonTypeInfo<T>`，不调用 `JsonSerializer.Serialize(object)`。
- `IpcJsonContext` 上为每个根类型标注 `[JsonSerializable(typeof(T))]`。

### 5.5 反射测试（非 AOT）

`MailDog.Contracts.Tests` 做三件事：

1. 枚举 `MailDog.Contracts` 程序集所有 `public` 且实现 `IIpcMessage` 的类型。
2. 断言每个类型都出现在 `IpcJsonContext.Default` 的元数据中（`GetTypeInfo(type) is not null`）。
3. 断言每个类型都在 `IpcCodecRegistry` 中有唯一 Type 名，且 Type 名与类型名一一对应（允许显式映射表）。

另外做往返测试：对每个类型构造样例，序列化→反序列化→再序列化，断言字节一致。

### 5.6 连接与超时

- UI 启动：先尝试连接；失败则 `Process.Start` 同目录 `MailDog.Engine.exe`，再以 200ms 间隔重试，总超时 10s。
- 普通请求 30s；正文请求 60s；OAuth `startOAuth` 立即返回，进度通过 `authState` 事件推送。
- UI 断开：该连接的所有未完成请求取消；Engine 不退出。
- Engine 退出：先停止接收新连接，等待 2s，再取消后台任务并关闭托盘。

---

## 6. 存储层

### 6.1 路径与初始化

- 数据目录：`%LOCALAPPDATA%\MailDog\`
- 主库：`maildog.db`；日志：`logs\`；临时渲染文件（仅必要时）：`cache\`
- 连接串：`Data Source=...;Mode=ReadWriteCreate;Cache=Shared;Pooling=True`
- 启动时执行：

```sql
PRAGMA journal_mode = WAL;
PRAGMA synchronous = NORMAL;
PRAGMA foreign_keys = ON;
PRAGMA busy_timeout = 5000;
PRAGMA temp_store = MEMORY;
```

- 迁移：`PRAGMA user_version` + 内嵌 SQL 脚本，事务内执行；禁止 EF Core 迁移。
- 写入串行化：Engine 内一个 `SemaphoreSlim(1,1)` 保护写事务；读取走连接池。

### 6.2 表结构（v1）

```sql
CREATE TABLE accounts (
    id            TEXT PRIMARY KEY,
    provider      TEXT NOT NULL,          -- 'outlook' | 'qq'
    email         TEXT NOT NULL UNIQUE,
    display_name  TEXT NOT NULL,
    auth_kind     TEXT NOT NULL,          -- 'oauth2' | 'password'
    secret_ref    TEXT,                   -- secrets 表引用
    created_utc   TEXT NOT NULL,
    last_sync_utc TEXT,
    status        TEXT NOT NULL DEFAULT 'ok' -- 'ok' | 'auth_required' | 'error'
);

CREATE TABLE folders (
    id            INTEGER PRIMARY KEY AUTOINCREMENT,
    account_id    TEXT NOT NULL REFERENCES accounts(id) ON DELETE CASCADE,
    full_name     TEXT NOT NULL,
    display_name  TEXT NOT NULL,
    role          TEXT NOT NULL,          -- inbox/sent/drafts/trash/junk/archive/other
    uid_validity  INTEGER NOT NULL DEFAULT 0,
    last_seen_uid INTEGER NOT NULL DEFAULT 0,
    unread_count  INTEGER NOT NULL DEFAULT 0,
    total_count   INTEGER NOT NULL DEFAULT 0,
    UNIQUE(account_id, full_name)
);

CREATE TABLE messages (
    id              INTEGER PRIMARY KEY AUTOINCREMENT,
    account_id      TEXT NOT NULL REFERENCES accounts(id) ON DELETE CASCADE,
    folder_id       INTEGER NOT NULL REFERENCES folders(id) ON DELETE CASCADE,
    uid             INTEGER NOT NULL,
    message_id      TEXT,
    thread_key      TEXT,                 -- 会话聚合键（References/In-Reply-To 首条或规范化主题哈希）
    subject         TEXT NOT NULL DEFAULT '',
    from_name       TEXT NOT NULL DEFAULT '',
    from_addr       TEXT NOT NULL DEFAULT '',
    to_addrs        TEXT NOT NULL DEFAULT '',
    cc_addrs        TEXT NOT NULL DEFAULT '',
    date_utc        TEXT NOT NULL,
    size            INTEGER NOT NULL DEFAULT 0,
    flags           INTEGER NOT NULL DEFAULT 0,
    has_attachments INTEGER NOT NULL DEFAULT 0,
    preview         TEXT NOT NULL DEFAULT '',
    body_state      INTEGER NOT NULL DEFAULT 0, -- 0 未取 1 已缓存 2 过大跳过 3 失败
    UNIQUE(folder_id, uid)
);

CREATE INDEX ix_messages_folder_date ON messages(folder_id, date_utc DESC, id DESC);
CREATE INDEX ix_messages_account_date ON messages(account_id, date_utc DESC, id DESC);
CREATE INDEX ix_messages_message_id ON messages(message_id);
CREATE INDEX ix_messages_thread_key ON messages(thread_key);

CREATE TABLE bodies (
    message_row_id    INTEGER PRIMARY KEY REFERENCES messages(id) ON DELETE CASCADE,
    html_sanitized    TEXT,
    text_plain        TEXT,
    sanitizer_version INTEGER NOT NULL,
    fetched_utc       TEXT NOT NULL
);

CREATE TABLE attachments (
    id             INTEGER PRIMARY KEY AUTOINCREMENT,
    message_row_id INTEGER NOT NULL REFERENCES messages(id) ON DELETE CASCADE,
    file_name      TEXT NOT NULL,
    mime_type      TEXT NOT NULL,
    size           INTEGER NOT NULL DEFAULT 0,
    content_id     TEXT,
    is_inline      INTEGER NOT NULL DEFAULT 0
);

CREATE TABLE secrets (
    ref           TEXT PRIMARY KEY,
    kind          TEXT NOT NULL,          -- 'oauth_refresh' | 'imap_password'
    protected_blob BLOB NOT NULL,         -- DPAPI 密文
    updated_utc   TEXT NOT NULL
);

CREATE TABLE settings (
    key   TEXT PRIMARY KEY,
    value TEXT NOT NULL
);
```

### 6.3 数据保留

- 默认缓存正文 90 天；超过后删除 `bodies` 行，`messages.body_state` 置 0，按需重新拉取。
- 已删除文件夹的邮件随 `ON DELETE CASCADE` 清理。
- “清空本地缓存”只清 `bodies`、`attachments`、`messages`，保留账户与设置。
- `VACUUM` 仅在用户手动清理后执行，避免同步期间抖动。

---

## 7. 邮件同步

### 7.1 连接策略

- 每账户一条 `ImapClient` 会话；`ImapClient` 非线程安全，用 `SemaphoreSlim(1,1)` 串行化所有操作。
- 连接参数：`SecureSocketOptions.SslOnConnect`（993）；超时 30s；失败指数退避 2s → 5s → 15s → 60s → 300s（带 ±20% 抖动）。
- 认证：
  - QQ：`SaslMechanismPlain`（用户名=完整邮箱，密码=授权码）。
  - Outlook：`SaslMechanismOAuth2`（用户名=邮箱，密码=access token）。
- 断线重连后重新 `SELECT` 文件夹并校验 `UIDVALIDITY`。

### 7.2 文件夹映射

- `GetFoldersAsync()` 后按 `FolderAttributes` 映射 `Inbox/Sent/Drafts/Trash/Junk/Archive`。
- QQ 的“已发送/草稿箱/已删除/垃圾邮件”按属性识别；识别失败时按名称兜底。
- 只同步有 `Inbox` 角色与用户展开的文件夹；默认同步 INBOX + 最近使用的 3 个文件夹，其余按需。

### 7.3 首次同步

1. 拉取文件夹列表并 upsert。
2. 对 INBOX 执行 `UID FETCH` 最近 200 封的 `ENVELOPE / FLAGS / INTERNALDATE / RFC822.SIZE / BODYSTRUCTURE`。
3. 写入 `messages`，计算 `preview`（主题或正文首 160 字符，纯文本化）。
4. 更新 `last_seen_uid = max(uid)`、`unread_count`、`total_count`。
5. 列表首屏返回 50 条，剩余由滚动到底部自动触发本地查询；本地不足且服务器还有更早邮件时，Engine 再向 IMAP 拉取一页（200 条）后返回。

### 7.4 增量同步

- `UID FETCH (last_seen_uid + 1):* (ENVELOPE FLAGS INTERNALDATE RFC822.SIZE BODYSTRUCTURE)`
- 对已存在 UID 只更新 flags；对新增 UID 插入并生成 `newMail` 事件。
- 用户标记已读/旗标：先写本地，再 `STORE +FLAGS/-FLAGS`；网络失败进入重试队列（内存 + 启动时补偿）；批量操作按账户/文件夹合并为一次 STORE。
- `UIDVALIDITY` 变化：删除该文件夹本地邮件，重新首次同步，并记录日志。

### 7.5 IDLE 与轮询

> v1 只读实现：周期轮询兜底（默认 60s）；v1.1 已加入 IDLE 实时收信。

- 支持 IDLE 的服务器：每个账户维持一条独立监听连接，进入 IDLE，服务器超时前（9 分钟）主动退出并重入；`CountChanged` 触发该账户立即增量同步。服务器不支持 IDLE 时自动回退到周期轮询。
- 同步失败按 30s / 60s / 120s / 240s / 600s 退避重试；认证失败改为 30 分钟后再试，避免频繁撞验证。
- QQ 若 IDLE 不稳定：退避重连；轮询始终作为兜底，有变化才拉取。
- 应用从睡眠恢复：托盘窗口接收 `WM_POWERBROADCAST`（`PBT_APMRESUMEAUTOMATIC` / `PBT_APMRESUMESUSPEND`），重建全部 IDLE 连接并立即同步。托盘窗口改为隐藏的顶层窗口，才能收到广播消息（`TaskbarCreated`、电源事件）。

### 7.6 正文获取

- 使用 `ImapFolder.GetStreamAsync(uid, "BODY.PEEK[]", ct)`，再用 `MimeMessage.LoadAsync` 解析，避免设置 `\Seen`。
- 优先 `MimeMessage.HtmlBody`；没有 HTML 时用 `TextBody`。
- 正文 > 10 MiB 或解析异常：返回 `truncated=true` + 提示，不缓存。
- 缓存写入 `bodies`，`sanitizer_version` 记录净化器版本；净化器升级后缓存失效，重新净化。
- 附件只返回元数据（文件名、MIME、大小、`Content-ID`、是否内嵌），不下载内容。

### 7.7 错误分类

| 场景 | 处理 |
|---|---|
| 认证失败（QQ 授权码错误/Outlook token 失效） | 账户状态置 `auth_required`，发 `authState`，停止该账户重试 |
| 网络不可达 | 退避重试，账户状态 `offline`，UI 顶部横幅提示 |
| 服务器限流/连接数超限 | 延长退避，降低并发（同账户永远串行） |
| 协议异常 | 重建连接；连续 3 次失败才标记 `error` |
| 单封邮件解析失败 | 该封 `body_state=3`，不影响其他邮件 |

---

## 8. 认证

### 8.1 QQ 邮箱

- 用户在 QQ 邮箱网页端开启 IMAP/SMTP 并生成“授权码”。
- 授权码不是 QQ 密码；UI 明确提示“请输入授权码，不是登录密码”。
- Engine 用 `SaslMechanismPlain` 连接 `imap.qq.com:993` 验证成功后保存。
- 授权码用 DPAPI 加密存 `secrets`，`kind='imap_password'`。

### 8.2 Outlook OAuth2 PKCE（Engine 内自研）

**Entra ID 应用注册**

| 配置项 | 值 |
|---|---|
| 平台 | 移动和桌面应用程序（public client） |
| 重定向 URI | `http://localhost` 与 `http://127.0.0.1`（两者都注册，端口忽略） |
| 允许公共客户端流 | 是 |
| 客户端密钥 | 不需要 |
| 客户端 ID | 内置在 `OutlookAuthService.DefaultClientId`；可用环境变量 `MAILDOG_ENTRA_CLIENT_ID` 覆盖 |
| 租户 | `common`（同时支持个人与工作账户） |

**授权请求参数**

```
https://login.microsoftonline.com/common/oauth2/v2.0/authorize
  ?client_id={client_id}
  &response_type=code
  &redirect_uri=http://127.0.0.1:{port}/callback
  &response_mode=query
  &scope=offline_access openid profile
         https://outlook.office.com/IMAP.AccessAsUser.All
         https://outlook.office.com/SMTP.Send
  &code_challenge={S256(verifier)}
  &code_challenge_method=S256
  &state={state}
  &prompt=select_account
```

> `response_mode=query` 必须显式指定。默认可能返回 `fragment`，回环服务器读不到 `#` 之后的内容，`code` 会为空。

**回环服务器（TcpListener）**

1. `var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();`
2. 端口取 `((IPEndPoint)listener.LocalEndpoint).Port`，无竞态。
3. 启动接受循环，设置 5 分钟总超时。
4. 只接受 `GET /callback?...`；其他路径返回 404 并继续等待。
5. 解析查询串（`Uri.UnescapeDataString` + 严格 key/value 解析），拒绝重复参数。
6. 常量时间比较 `state`；`state` 不匹配返回 400 并继续等待。
7. 若 `error` 存在，记录 `error`/`error_description`，返回错误页，流程失败。
8. 收到合法 `code` 后返回静态成功页（`no-store`、CSP `default-src 'none'; style-src 'unsafe-inline'`），随后停止监听。
9. `Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })` 打开默认浏览器。

**PKCE 细节**

- `verifier`：32 字节随机数 → Base64Url（无填充，长度 43）。
- `challenge`：SHA-256(verifier) → Base64Url。
- `state`：16 字节随机数 → Base64Url。
- 令牌请求：

```
POST https://login.microsoftonline.com/common/oauth2/v2.0/token
Content-Type: application/x-www-form-urlencoded

client_id=...&grant_type=authorization_code&code=...
&redirect_uri=http://127.0.0.1:{port}/callback&code_verifier=...
```

- 刷新：

```
grant_type=refresh_token&refresh_token=...&client_id=...&scope=...
```

- 刷新提前量 5 分钟；刷新返回 `invalid_grant` 时账户置 `auth_required`，通知 UI 重新登录。

**令牌存储**

- access token 仅内存持有；refresh token 持久化。
- DPAPI：`ProtectedData.Protect(utf8, entropy, DataProtectionScope.CurrentUser)`，entropy 为固定应用字节串的 SHA-256。
- 密文存 `secrets.protected_blob`，`ref` 写入 `accounts.secret_ref`。
- 解密失败（换用户/系统迁移）：删除密文，账户置 `auth_required`，提示重新登录。
- 日志中永不打印 token、授权码、`code_verifier`；异常消息做脱敏。

### 8.3 OAuth 时序

```
UI                Engine                         Browser              Entra
│ startOAuth       │                               │                   │
├─────────────────>│ 生成 verifier/state           │                   │
│                  │ TcpListener :0                │                   │
│                  ├── Process.Start(authorize) ──>│                   │
│  authState=       │                               ├── 用户登录/MFA ─>│
│  browser_opened  │                               │<── 302 callback ──┤
│<─────────────────┤<── GET /callback?code&state ──┤                   │
│                  │ 校验 state / PKCE             │                   │
│                  │ POST /token ─────────────────────────────────────>│
│                  │<──────────────── access+refresh ──────────────────│
│  authState=       │ DPAPI 保存 refresh            │                   │
│  connected        │                               │                   │
│<─────────────────┤                               │                   │
```

### 8.4 首次登录的 UI 协作

- Engine 收到 `startOAuth` 后立即返回 `ok`，随后发 `authState { state: 'browser_opened' }`。
- UI 显示模态进度面板：“已在浏览器中打开登录页面，完成登录后此窗口会自动更新”，提供“取消”。
- 成功：`authState { state: 'connected', account }`，关闭面板并刷新账户列表。
- 超时/失败：展示错误码与可操作建议（重试、检查系统时间、检查代理）。
- UI 未打开时 Engine 独立完成流程，token 落库；下次 UI 启动直接可见。

---

## 9. 安全模型

### 9.1 威胁与对策

| 威胁 | 对策 |
|---|---|
| 邮件 HTML 注入脚本/事件处理器 | 白名单净化、剥离 `script/on*/javascript:`、CSP、默认禁用 JS |
| 远程图片跟踪像素 | 默认 `img-src 'none'`；用户点击“显示图片”后仅允许 https |
| CSS 覆盖 UI / 固定定位钓鱼层 | 清洗 `<style>` 与 `style` 属性：只保留安全声明，剥离 `url()`、`@import`、`expression()`、`javascript:`、`behavior` 等；CSP 禁止脚本与外部资源 |
| 恶意链接诱导 | `NavigationStarting` 取消站内导航，交系统浏览器打开，且仅允许 http/https/mailto |
| 本地进程伪造 IPC 客户端 | `PipeOptions.CurrentUserOnly`；握手校验；可选校验客户端 exe 路径 |
| 回环回调被本机恶意进程抢先 | PKCE + `state` 常量时间校验 + 单次有效监听 + 5 分钟超时 |
| refresh token 被读取 | DPAPI CurrentUser 加密；仅 Engine 可解密；日志脱敏 |
| 日志泄露邮件内容 | 默认只记录元数据（UID/大小/耗时）；`--verbose` 才记录主题，且不入正文 |
| 附件路径穿越（后续） | 文件名净化、固定下载目录、拒绝 `..` 与绝对路径 |
| 数据库被其他用户读取 | 数据目录位于 `%LOCALAPPDATA%`，继承用户 ACL；敏感字段 DPAPI |

### 9.2 渲染安全边界

- 邮件 HTML 是**不可信输入**；净化是安全底线，渲染保真度可以让步。
- 净化后为空、导航失败、WebView 初始化失败 → 自动降级纯文本。
- 不允许邮件内容触发下载、弹窗、新窗口、外部协议。
- `mailto:` 链接交系统默认邮件客户端；`http/https` 交系统浏览器；其余协议一律丢弃。
- WebView 与 IPC 都只处理字符串/JSON，不执行邮件中的任何代码。

### 9.3 令牌与凭据生命周期

- 账户删除：删除 `accounts` 行、`secrets` 行、相关 `messages/folders/bodies`，并尽力撤销 refresh token（调用 Entra `logout` 端点；失败不阻塞删除）。
- 退出应用：清空内存中的 access token。
- 系统休眠/恢复：恢复后先校验 token 过期时间，再决定刷新。

---

## 10. 渲染管线

### 10.1 管线

```
IMAP BODY.PEEK[] → MimeMessage → rawHtml/plainText
        │
        ▼
MailDog.Html.HtmlSanitizer.Sanitize(rawHtml)
        │  词法扫描 → 白名单过滤 → URL 策略 → 属性过滤
        ▼
DocumentBuilder.Build(sanitized)
        │  注入 <meta CSP>、viewport、基础排版 CSS、配色
        ▼
NativeWebView.NavigateToString(document)
        │
        ├─ NavigationCompleted 成功 → 显示
        └─ 失败 / 空内容 → 纯文本降级（SelectableTextBlock）
```

### 10.2 净化规则（v1）

**允许的标签**

`p, div, span, br, hr, blockquote, pre, code, b, strong, i, em, u, s, sub, sup,
h1..h6, ul, ol, li, table, thead, tbody, tfoot, tr, td, th, caption, colgroup, col, a, img,
font, center, big, small, strike, dl, dt, dd, article, section, header, footer, address`

**一律移除的标签（含内容）**

`script, iframe, frame, frameset, object, embed, applet, form, input, button,
select, textarea, link, base, meta, title, svg, math, video, audio, canvas, template`

**允许的属性**

`href, title, alt, width, height, colspan, rowspan, align, valign, src, border,
cellpadding, cellspacing, span, bgcolor, color, face, size, style, class, id`

- 不接受任何 `on*`、`data-*`。
- `style` 与 `<style>` 清洗：`@import` 整条移除；`url(...)` / `expression(...)` 替换为 `none`；剥离 `javascript:`、`vbscript:`、反斜杠与尖括号，保持其余规则结构完整。
- `class` / `id` 仅接受字母、数字、`-`、`_`、空格。
- `bgcolor` / `color` 仅接受 `#rgb`/`#rrggbb`/`#rrggbbaa` 或颜色名；`face` 仅接受字体名；`size` 仅接受 1–7。
- `src` 仅在“允许图片”模式下接受 `https:`；`http:` 升级为 `https:` 或丢弃。
- `href` 仅接受 `http:`, `https:`, `mailto:`；拒绝 `javascript:`, `data:`, `vbscript:`, `file:`, 协议相对 `//`，以及含控制字符/反斜杠的变体。

**文本与实体**

- 保留文本节点，统一换行；连续空白折叠（`pre` 内除外）。
- 只解码安全实体；未知实体按字面输出。
- 移除 NUL 与控制字符（保留 `\t\n\r`）。

**裁剪与限额**

- 输入上限 2 MiB；超过则截断并标记 `truncated`。
- 输出上限 2 MiB；超限继续裁剪尾部并插入提示。
- 嵌套深度上限 64；超过时丢弃更深节点（防栈/性能攻击）。

### 10.3 CSP 与文档模板

```html
<meta http-equiv="Content-Security-Policy"
      content="default-src 'none';
               img-src https: data:;
               style-src 'unsafe-inline';
               script-src 'none';
               object-src 'none';
               frame-src 'none';
               connect-src 'none';
               form-action 'none';
               base-uri 'none'">
<meta name="referrer" content="no-referrer">
<meta name="viewport" content="width=device-width, initial-scale=1">
```

- 默认模式：`img-src 'none'`；用户点击“显示图片”后切换为 `img-src https: data:`。
- 基础 CSS 由应用注入（字体、行高、引用、代码块、暗色模式）作为兜底；保留邮件自带的安全内联样式与 `<style>` 以还原原始排版。
- 正文放在浅色“纸张”卡片中，卡片外使用阅读区背景色；不注入表格单元格边框/内边距，避免文字与背景同色以及“满屏方框”。
- 暗色模式：`prefers-color-scheme` + 应用传入的 `data-theme` 双保险。

### 10.4 WebView 生命周期

- 阅读窗格只创建一个 `NativeWebView` 实例；切换邮件时更新内容，不重复创建。
- `WebMessageReceived` 在初始化时订阅一次，窗口关闭时退订。
- `MainWindow.Closed` 与 `DetachedFromVisualTree` 时退订 `AdapterCreated` / `WebMessageReceived` / `NavigationCompleted` 等事件。
- **API 事实修正**：`Avalonia.Controls.WebView` 12.1.2 的 `NativeWebView` **没有公开 `Dispose()`**；原生适配器随视觉树卸载与窗口关闭释放，不要假设可以手动 Dispose。验证“关闭后无 `msedgewebview2` 残留”仍作为验收项。
- **必须显式设置 WebView2 用户数据目录**：在 `EnvironmentRequested` 中把 `WindowsWebView2EnvironmentRequestedEventArgs.UserDataFolder` 指到 `%LOCALAPPDATA%\MailDog\WebView2`，否则默认路径可能以 `E_ACCESSDENIED` 失败（Stage 0 实测）。
- **UI 必须声明受支持的 OS**：`app.manifest` 中加入 Windows 10/11 的 `supportedOS` GUID，否则 AOT 产物创建 `NativeControlHost` 子窗口时会抛 `InvalidOperationException`。
- 渲染协调器维护状态机：`Idle → Loading → Rendered | Failed → TextFallback`。
- 同一时刻只允许一个导航；切换邮件时取消上一个导航并递增代次号，忽略过期回调。

### 10.5 Windows 加固

- 通过 `TryGetPlatformHandle()` 拿到 `ICoreWebView2`，设置：
  - `IsScriptEnabled = false`
  - `AreDevToolsEnabled = false`
  - `AreDefaultContextMenusEnabled = false`
  - `AreBrowserAcceleratorKeysEnabled = false`
  - `IsStatusBarEnabled = false`
- COM 接口使用 `[GeneratedComInterface]`（源生成），避免 AOT 下的 `ComImport` 问题。
- 设置失败不阻塞渲染，记录警告并继续（净化 + CSP 仍是主防线）。

### 10.6 降级策略

| 触发条件 | 行为 |
|---|---|
| 净化结果为空 | 显示纯文本；仍为空则显示“此邮件没有可显示的正文” |
| `NavigationCompleted.IsSuccess == false` | 销毁当前内容，切换纯文本并显示提示条 |
| WebView 初始化失败/运行时缺失 | 整个会话降级纯文本；设置页提示安装 WebView2 Runtime |
| 正文超过 2 MiB | 截断 HTML + 顶部提示；提供“以纯文本查看全文” |

---

## 11. UI 架构

- `AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont()`（Inter 仅作拉丁字符兜底，中文走系统字体）。
- `Application.RequestedThemeVariant = ThemeVariant.Default`，用户可固定 Light/Dark。
- `FluentTheme` + `DensityStyle="Compact"`；品牌强调色通过资源覆盖。
- MVVM：`CommunityToolkit.Mvvm` 源生成；所有 XAML 使用 `x:DataType` + 编译绑定，禁止 `ReflectionBinding`。
- 组合根：`AppServices` 手写构造，不引入 DI 容器；服务包括 `EngineClient`、`RenderCoordinator`、`ThemeService`、`NotificationService`。
- 线程：所有 ViewModel 状态变更回到 `Dispatcher.UIThread`；`EngineClient` 的后台读取循环通过 `Dispatcher.Post` 回传。
- 视图结构：
  - `MainWindow`：三栏外壳 + 状态横幅，使用系统原生标题栏。
  - `FolderPane`：账户切换、收藏、文件夹树。
  - `MessageListView`：搜索、筛选、虚拟化列表、滚动到底部自动加载。
  - `ReaderPane`：命令栏、头部、WebView/纯文本、附件条。
  - `SettingsWindow`、`AddAccountDialog`。
- 可访问性与键盘映射详见 [UI-DESIGN.md](./UI-DESIGN.md)。

---

## 12. Engine 宿主

### 12.1 启动顺序

1. 解析参数：`--console`（附加父控制台）、`--verbose`、`--reset-cache`。
2. 单实例互斥体：`Local\MailDog.Engine.Singleton`；已存在则退出 0。
3. 初始化日志、SQLite、迁移。
4. 启动 IPC Server。
5. 启动托盘（Win32 消息循环在主线程）。
6. 启动账户恢复与同步调度。
7. 等待退出信号：托盘“退出”、`shutdown` 请求、系统注销。

### 12.2 Win32 托盘

- 隐藏的 message-only 窗口（`CreateWindowEx(HWND_MESSAGE)`），注册窗口类，`WndProc` 用 `delegate* unmanaged` + `GCHandle` 回传实例。
- `Shell_NotifyIcon(NIM_ADD, ...)`，图标来自嵌入的 `maildog.ico`。
- 菜单：`CreatePopupMenu` + `AppendMenuW`：
  - 打开 MailDog
  - 立即同步
  - 账户状态（子菜单，禁用态展示）
  - 退出
- 菜单主题通过 uxtheme `SetPreferredAppMode` + `FlushMenuThemes` 跟随应用明暗设置，而不是固定系统默认。
- 左键单击：启动 UI 或发 `showMainWindow`。
- 任务栏重建：`RegisterWindowMessageW("TaskbarCreated")` 后重新 `NIM_ADD`。
- 通知：`NIF_INFO` 气泡，`NIIF_USER` + `hBalloonIcon` 使用应用大图标（新 logo）；同一账户 30s 内最多 1 条；点击气泡打开 UI 并定位邮件（通过 `NIN_BALLOONUSERCLICK`）。
- 通知声音：默认 / 邮件 / 提醒 / 提示；非默认铃声用 `PlaySound` 播放系统别名并抑制气泡默认音。
- 退出时 `NIM_DELETE`、`DestroyWindow`、`PostQuitMessage`。

### 12.3 UI 进程管理

- 托盘点击：
  1. 尝试 IPC 连接；成功则发 `showMainWindow`。
  2. 失败则 `Process.Start(Path.Combine(AppContext.BaseDirectory, "MailDog.UI.exe"))`。
  3. UI 启动后自行握手并拉取数据。
- 防重复启动：UI 侧也用 `Local\MailDog.UI.Singleton` 互斥体；已存在则激活已有窗口（通过 Engine 转发 `showMainWindow`）。
- UI 关闭：`ShutdownMode.OnMainWindowClose` + `Shutdown()`；不隐藏窗口、不驻留托盘。
- Engine 退出前发 `shutdown` 事件，UI 收到后自行关闭（可选）。

### 12.4 日志

- `%LOCALAPPDATA%\MailDog\logs\engine-YYYYMMDD.log`，按天滚动，保留 7 天，单文件 10 MiB 上限。
- 行格式：`2026-11-13T22:56:01.123Z INFO  [sync] account=... folder=... elapsed=...`
- 脱敏：token、授权码、`code_verifier`、完整邮箱地址（日志中保留 `a***@domain`）。
- UI 日志单独 `ui-YYYYMMDD.log`，仅记录生命周期与渲染失败原因，不记录正文。

### 12.5 崩溃与退出

- 捕获 `AppDomain.UnhandledException`、`TaskScheduler.UnobservedTaskException`，写 `crash-*.log`。
- 退出流程：停止调度 → 取消所有 CTS → 等待后台任务（最多 5s）→ 关闭 SQLite → 删除托盘 → 释放互斥体。
- 不自动重启，避免崩溃循环；下次用户点击托盘再启动。

---

## 13. AOT 纪律

### 13.1 硬规则

1. **序列化**：只用 `System.Text.Json` 源生成；生产代码禁止 `JsonSerializer.Serialize(object, Type)`。
2. **反射**：生产路径禁止 `Type.GetType`、`Activator.CreateInstance`、`MakeGenericType`、`GetProperties`。
3. **P/Invoke**：统一 `[LibraryImport]`，`StringMarshalling.Utf16`；不用 `DllImport`（分析器会提示）。
4. **COM**：只用 `[GeneratedComInterface]`；不使用 `[ComImport]` 运行时封送。
5. **XAML**：全部编译绑定、`x:DataType`；禁止 `ReflectionBinding` 与运行时 `DataTemplate` 类型匹配。
6. **正则**：邮件内容处理不使用正则；固定配置串用 `[GeneratedRegex]`。
7. **动态代码**：禁止 `System.Reflection.Emit`、`Expression.Compile`、`dynamic`。
8. **UI 资源**：图标用 `FluentIcons` 预生成几何；禁止运行时 SVG/图片解码作为图标来源。
9. **裁剪警告**：`TrimmerSingleWarn=false`，任何 `IL2xxx/IL3xxx` 警告视为构建失败。

### 13.2 第三方 AOT 风险点

| 包 | 风险 | 验证方式 |
|---|---|---|
| `Avalonia.Controls.WebView` | 官方文档未明确写 AOT；WebView2Loader 原生库与单文件 | Stage 0 探针：AOT 发布最小窗口 + `NativeWebView`，检查 `IL*` 与运行时加载 |
| `FluentIcons.Avalonia` | 资源程序集裁剪 | 探针中渲染 5 个图标并截图确认 |
| `MailKit` | 个别 API 可能带 `RequiresUnreferencedCode` | Stage 2 AOT 发布 + 真实 QQ 连接 |
| `Microsoft.Data.Sqlite` | `SQLitePCLRaw` 原生库加载 | 探针中建表/读写/重启 |
| `CommunityToolkit.Mvvm` | 源生成器版本兼容 | Stage 4 AOT 发布 UI |

### 13.3 探针清单与结果（已完成）

| 探针 | 结果 | 关键结论 |
|---|---|---|
| `net11.0` 控制台 AOT | ✅ | 无 `IL*` 警告 |
| Avalonia 空窗口 AOT | ✅ | 需要 `app.manifest` |
| `NativeWebView` + WebView2 AOT | ✅ | 需显式 `UserDataFolder`；无公开 `Dispose()` |
| MailKit MIME + IMAP 类型 AOT | ✅ | 无 `IL*` 警告 |
| SQLite 建库/读写 AOT | ✅ | `e_sqlite3.dll` 需嵌入解压 |
| FluentIcons 渲染 AOT | ✅ | 正常 |
| 关闭 UI 后进程清理 | ✅ | 无新增 `msedgewebview2` 残留 |

实测：Engine 6.00 MiB / 203 ms；UI 35.54 MiB / 445 ms（首个窗口句柄可用）。完整记录见 [SPIKES.md](./SPIKES.md)。

---

## 14. 构建与发布

### 14.1 发布命令

**Windows 构建机前置**：Visual Studio Build Tools 2026（18.11.3 或更高）并勾选：

- `Microsoft.VisualStudio.Component.VC.Tools.x86.x64`（MSVC 编译器与链接器）
- `Microsoft.VisualStudio.Component.Windows11SDK.26100`（Windows SDK）

只安装 BuildTools 本体不会带 `link.exe` 与 SDK 头文件/库，Native AOT 会在链接阶段失败。

```powershell
# 推荐：脚本会切换到 src 执行，并组装 dist
pwsh -File src/build/publish.ps1 -Runtime win-x64 -Configuration Release

# 手动执行（必须先进入 src，确保 global.json 生效）
cd src
dotnet publish MailDog.Engine/MailDog.Engine.csproj `
  -c Release -r win-x64 `
  -p:PublishAot=true `
  -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:StripSymbols=true `
  -o ../artifacts/engine

dotnet publish MailDog.UI/MailDog.UI.csproj `
  -c Release -r win-x64 `
  -p:PublishAot=true `
  -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:StripSymbols=true `
  -o ../artifacts/ui
```

- `PublishReadyToRun` 与 AOT 互斥，不启用。
- **`IncludeNativeLibrariesForSelfExtract=true` 在 Native AOT 下不会打包第三方原生库**；`e_sqlite3`、`libSkiaSharp`、`libHarfBuzzSharp`、`av_libglesv2` 通过 `EmbeddedResource` + 启动解压 + `NativeLibrary.SetDllImportResolver` 解决。
- UI 优先 `Win32RenderingMode.AngleEgl`，失败回退 `Software`；材质不可用时自动回退不透明面板。WebView2 仍使用自身 GPU 进程。
- 产物组装到 `dist/win-x64/MailDog/`：两个 exe + `README.txt`。

### 14.2 体积与性能预算（最终实测）

| 指标 | 目标 | 实测 |
|---|---|---|
| Engine 单文件 | ≤ 35 MiB | **13.46 MiB** |
| UI 单文件 | ≤ 55 MiB | **44.80 MiB** |
| Engine 启动 | ≤ 1.5s | Stage 0 探针 203 ms；v1 含托盘常驻 |
| UI 首窗口 | ≤ 2.5s | Stage 0 探针 445 ms |
| Engine 空闲内存 | ≤ 120 MiB | 待长稳测试 |
| UI 关闭后残留进程 | 0 | ✅ 已验收（UI 与新增 WebView2 进程均退出） |

### 14.3 CI

GitHub Actions `windows-latest`：

1. `actions/setup-dotnet` 安装 .NET 11。
2. `dotnet restore` / `build` / `test`。
3. `dotnet publish` 两个 AOT 项目。
4. 扫描输出中的 `warning IL`，有则失败。
5. 上传 `dist/win-x64/MailDog/` 为 artifact。
6. 缓存 NuGet 与 AOT 中间产物（可选，注意缓存正确性）。

### 14.4 签名与分发（后续）

- 代码签名证书（OV/EV）解决 SmartScreen 与杀软误报。
- 安装包二选一：MSIX（需签名、适合商店）或 Inno Setup（简单、可控）。
- 自动更新不进入 v1；预留 `version.json` 检查点，后续实现。

---

## 15. 测试与质量门

### 15.1 单元测试

| 项目 | 覆盖 |
|---|---|
| `MailDog.Contracts.Tests` | 所有 IPC 类型注册齐全、JSON 往返、错误码映射、协议版本兼容 |
| `MailDog.Html.Tests` | XSS 语料、mXSS、编码绕过、CSS/URL 过滤、深度/大小限额、空输入、纯文本降级 |
| `MailDog.Mail.Tests` | PKCE RFC 7636 测试向量、state 校验、端口分配、token 刷新决策、DPAPI 往返（Windows）、SQLite 迁移 |

### 15.2 集成测试

- GreenMail（Docker）作为本地 IMAP/SMTP 服务器，覆盖：文件夹映射、首次同步、增量同步、UIDVALIDITY 变化、正文 peek 不置已读。
- 默认 `[Trait("Category","Integration")]`，CI 通过 service container 运行；本地无 Docker 时跳过。
- Outlook OAuth 无法自动化，使用真实账户手工验收。

### 15.3 冒烟测试（每阶段必做）

- 必须使用 `dotnet publish` 产物，不允许只验证 `dotnet run`。
- 检查项：启动、托盘、连接、列表、正文、关闭 UI 后进程消失、Engine 仍在。
- 记录到 `docs/smoke/`（后续阶段创建，按日期命名）。

### 15.4 质量门

- 构建：0 warning（含 `IL*`）。
- 测试：单元测试全绿。
- 依赖：`MailDog.UI` 依赖树无 `MailKit`；`MailDog.Engine` 依赖树无 `Avalonia*`。
- 安全：净化器语料全部通过；默认配置下邮件无法发起网络请求（除允许的图片）。
- 性能：达到 14.2 预算或记录偏差并评审。

---

## 16. 里程碑

> 每个阶段的“完成”定义：AOT 发布产物冒烟通过 + 文档更新 + 提交到 `main`。

> 实现状态：Stage 0–7 已完成（v1.1 写操作与发信）；Stage 8 的签名与自动更新待证书/发布链路。

### Stage 0：工具链与 AOT 探针（已完成）

**交付**

- 安装 .NET 11 SDK 11.0.100，`global.json` 锁定。
- 建立解决方案骨架、中央包管理、发布与依赖校验脚本、CI 工作流。
- 7 项探针全部通过，结论写入 [SPIKES.md](./SPIKES.md)。

**实测结果**

- `MailDog.Engine.exe` 6.00 MiB，203 ms；`MailDog.UI.exe` 35.54 MiB，445 ms。
- 两个 exe 均可在空目录中独立运行。
- `NativeWebView` AOT 渲染成功；关闭后无新增 WebView2 残留。
- MailKit / SQLite / FluentIcons 全部通过，无 `IL*` 警告。

### Stage 1：Contracts 与源生成序列化

**交付**

- 全部 IPC DTO、`IpcJsonContext`、`IpcCodecRegistry`。
- 反射覆盖测试、往返测试。
- 空控制台 AOT 发布。

**退出标准**

- 反射测试证明“所有 IPC 类型均已注册”。
- AOT 发布 0 警告；未知类型路径有明确异常测试。

### Stage 2：Mail + Engine（QQ 只读）

**交付**

- SQLite 迁移与仓储；MailKit 连接、文件夹、首次/增量同步。
- 命名管道服务端与请求路由。
- `--console` 调试模式。
- 用真实 QQ 授权码跑通列表与正文。

**退出标准**

- 拉取 INBOX 最近 200 封并落库；重启后不重复插入。
- 正文 peek 不改变未读状态。
- AOT 发布 0 警告；CPU 空闲 < 1%。

### Stage 3：OAuth2 PKCE（Outlook）

**交付**

- `LoopbackHttpServer`（TcpListener）、PKCE、token 交换与刷新、DPAPI 存储。
- Entra 应用注册说明（`docs/ENTRA-SETUP.md` 后续）。
- `SaslMechanismOAuth2` 连接 `outlook.office365.com:993`。

**退出标准**

- 浏览器登录 → 回环捕获 → token 落库 → IMAP 连接成功全链路。
- `state` 不匹配被拒绝；`response_mode=query` 生效；刷新路径可用。
- token 过期后自动刷新；撤销授权后账户进入 `auth_required`。
- AOT 发布 0 警告。

### Stage 4：UI 最小闭环（无 WebView）

**交付**

- Avalonia 三栏外壳、账户/文件夹/列表、纯文本正文。
- `EngineClient` 连接、重连、错误横幅。
- 关闭窗口即退出进程。

**退出标准**

- 通过 IPC 显示真实邮件列表与正文。
- AOT 发布 UI 0 警告。
- 关闭 UI 后 `MailDog.UI.exe` 消失，Engine 托盘仍在。

### Stage 5：NativeWebView + 净化 + 降级

**交付**

- `MailDog.Html` 净化器、文档构建器、CSP。
- `RenderCoordinator` 状态机、事件退订纪律、Windows 加固。
- 恶意邮件语料测试。

**退出标准**

- 真实 HTML 邮件渲染正常；脚本/远程图片被阻断。
- 导航失败自动降级纯文本。
- 反复打开/关闭 50 封邮件，UI 进程内存回落，无 WebView 泄漏。

### Stage 6：托盘与生命周期闭环

**交付**

- Win32 托盘、菜单、气泡通知、任务栏重建处理。
- 单实例、托盘拉起 UI、`showMainWindow`。
- 开机自启开关（注册表 Run）。**已完成**：设置页“启动”分区的“登录 Windows 时自动启动”，Engine 在设置保存与启动时同步 HKCU Run。

**退出标准**

- 任务管理器中后台只有 `MailDog.Engine.exe` 与托盘，无 Avalonia/WebView2 进程。
- 托盘点击可拉起/聚焦 UI；通知点击可定位邮件。
- 注销/重启后托盘恢复。

### Stage 7：写操作与发信（v1.1，已完成）

- 已读/旗标双向同步、重试队列。
- SMTP：QQ PLAIN / Outlook OAuth2；Outbox 表与重试。
- 撰写页、草稿、附件。撰写为主窗口内嵌页，不弹独立窗口；正文只保留富文本编辑器，旧 Markdown 草稿首次打开时转成 HTML。
- 自定义 IMAP/SMTP 服务器、cid 内嵌图、附件默认程序打开。

### Stage 8：分发（v1.1，部分完成）

- 压缩包、Inno Setup 安装包与 GitHub Release 流水线已完成。
- 仍待：代码签名证书（需 OV/EV 证书与签名密钥）、自动更新检查、崩溃报告（可选）。

### Stage 9：可选工作台（v1.2，已移除）

> 2026-11-22 已整体移除日历、联系人、待办、记事本四个模块及设置开关；以下保留为历史记录。MailDog 现在只保留邮件功能。

- 日历、联系人、待办、记事本四个模块，设置 → 功能可单独开关。
- 本地 `workspace.json` 持久化，记录可绑定邮箱账户；关闭模块只隐藏入口，不删数据。
- 待接入：Outlook Microsoft Graph 的日历/联系人/待办双向同步，需要扩展 OAuth scope 并处理首次重新授权。

### v1.2 邮件交互补齐（2026-11-22，已完成）

- 列表：行容器悬停浮出归档/切换已读/删除；容器级无障碍名称（未读前缀 + 发件人 + 主题）随已读状态刷新；紧凑档两行、标准档三行。
- 搜索：清除按钮；「当前文件夹 / 全部邮箱」范围切换（`SearchMessagesRequest.AllFolders`）；主题/发件人/预览命中词高亮。
- 会话：`messages.thread_key` migration v9 + 索引，`MimeConverter.BuildThreadKey` 取 References/In-Reply-To 或规范化主题；列表按线程折叠并在阅读页提供会话 Flyout。
- 写信：To/Cc/Bcc chip、联系人候选（`listContacts` / `contacts` IPC，聚合 `messages.from_addr`）、发送/草稿/判空统一使用 `*RecipientsValue`。
- 快捷键：`Ctrl+N` / `Ctrl+R` / `Ctrl+Shift+R` / `Ctrl+Shift+F` / `Delete` / `F5` / `Ctrl+A` / `Esc` / `Ctrl+F` / `Ctrl+,`。
- 验证：`dotnet build` 0 警告 0 错误；`dotnet test` 67 通过；1296×839 实机逐项验收，测试线程键已还原。

### v0.2.5（2026-11-25 发布）

关于页把「检查更新」变成有反馈的文字链接，检查完不必再猜有没有结果。

- ✅ 状态：版本号下方新增就地状态行，未检查时隐藏；点击后依次显示「正在检查更新…」「已是最新版本。」或「发现新版本 X。」，失败时显示原因。发现新版本时同一行出现「下载并安装」链接，便携版显示「去下载页」。
- ✅ 链接样式：`Button.link` 统一为无底色、无边框、强调色文字；「检查更新」「下载更新」「打开项目主页」都改用该样式并带下划线，替代原来的普通按钮。
- ✅ 验证：`dotnet build` 0 警告 0 错误；`dotnet test` 82 通过；UI Automation 实测关于页初始不显示状态，点击检查更新后显示「已是最新版本。」；窗口截图确认三个入口为文字链接。
- ✅ 发布：tag `v0.2.5`，Release <https://github.com/refout/MailDog/releases/tag/v0.2.5>；x64 便携包 32.6 MiB、arm64 便携包 31.2 MiB、x64 安装包 23.5 MiB，安装包 SHA-256 `b1849b52e755ede0445c72902e027034c931986904c917a03f52c0856fdd8a74`。
- ✅ 真机升级（2026-11-25）：本机从 0.2.4 以 `/SILENT /CLOSEAPPLICATIONS /LAUNCHAPP=1` 升级，安装器退出码 0，`FileVersion` 为 0.2.5.0；旧 UI 关闭后安装器拉起新 PID，窗口正常出现。

### v0.2.4（2026-11-24 发布）

关于页继续减法：整块移除「更新」区，只保留版本号后面的「检查更新」按钮。

- ✅ 版式：删除「更新」分区及其状态文本、「下载并安装」按钮和 GitHub Releases/SHA-256/便携版说明；版本号仍在品牌区只出现一次，第三方许可与支持区保持不变。
- ✅ 验证：`dotnet build` 0 警告 0 错误；`dotnet test` 82 通过；UI Automation 实测关于页只剩品牌区「检查更新」按钮，无「更新」分区。
- ✅ 发布：tag `v0.2.4`，Release <https://github.com/refout/MailDog/releases/tag/v0.2.4>；x64 便携包 32.6 MiB、arm64 便携包 31.2 MiB、x64 安装包 23.4 MiB，安装包 SHA-256 `ac3347a6206a09ec860ec9377d17a7d136d1ad43bd757d74917a1683419dfb8e`。
- ✅ 真机升级（2026-11-24）：本机从 0.2.3 以 `/SILENT /CLOSEAPPLICATIONS /LAUNCHAPP=1` 升级，安装器退出码 0，`FileVersion` 为 0.2.4.0；旧 UI 关闭后安装器拉起新 PID，窗口正常出现。

### v0.2.3（2026-11-24 发布）

设置页去重、主窗口三栏边距统一，并改用安装器显式拉起完成升级。

- ✅ 关于页：版本只在品牌区「版本 0.2.3」出现一次；更新状态不再重复当前版本；软件信息只保留适用平台、数据存储、网络通信；删除功能概览、后台运行行、品牌副标题，以及支持区里重复的许可入口。第三方许可仍默认折叠，检查更新仍在版本号后面。
- ✅ 设置页其他分区：删除外观、通知、阅读、同步、账户管理、本地文件、启动的复述型页面副标题；通知页去掉「系统通知」分组标题和铃声重复说明；同步页去掉「后台检查」分组标题。
- ✅ 主窗口三栏：文件夹栏、邮件列表、阅读区统一为 16px 上、左、右、下内边距；列表头、搜索框、批量栏、阅读头、正文与底栏同步对齐。
- ✅ 自动升级：`MailDog.iss` 的 `[Run]` 去掉 `postinstall`/`skipifsilent`，改为 `Check: ShouldLaunchApp` 读取 `/LAUNCHAPP=1`；`UpdateService` 静默安装时传入该参数，由安装器在装完后启动新版本。
- ✅ 验证：`dotnet build` 0 警告 0 错误；`dotnet test` 82 通过；UI Automation 实测关于页只出现一次版本号，主窗口三栏内容边界均距窗口/分栏 16px。
- ✅ 发布：tag `v0.2.3`，Release <https://github.com/refout/MailDog/releases/tag/v0.2.3>；x64 便携包 32.6 MiB、arm64 便携包 31.2 MiB、x64 安装包 23.4 MiB，安装包 SHA-256 `0561b2794305cb901127eb1557a000e20774b552f1169db780dc89ddf401758e`。
- ✅ 真机升级（2026-11-24）：本机从 0.2.2 以 `/SILENT /CLOSEAPPLICATIONS /LAUNCHAPP=1` 升级，安装器退出码 0，`FileVersion` 为 0.2.3.0；旧 UI 被关闭后，安装器在 7 秒内拉起新 PID，窗口正常出现。`[Run] Check: ShouldLaunchApp` 生效。

### v0.2.2（2026-11-24 发布）

尝试修复 v0.2.1 自动升级后不自动重新打开的问题，但实际未生效。

- 根因：安装脚本 `CloseApplications=yes` 会让 Restart Manager 关掉正在运行的 UI 与 Engine，但 `RestartApplications=no` 又禁止安装程序把它们拉回来；`[Run]` 还带 `skipifsilent`，静默安装不会启动应用。UI 被关闭后就没人执行 `UpdateService` 里安装完成后的 `Process.Start`。
- 尝试：`src/build/installer/MailDog.iss` 改为 `RestartApplications=yes`，希望安装程序在静默升级结束后重新启动被关闭的应用。
- ❌ 结果：从本机 0.2.1 实测升级到 0.2.2，下载、SHA-256 校验与静默安装均成功，`FileVersion` 为 0.2.2.0，但 UI 与 Engine 仍全部退出。仅改 `RestartApplications` 不能恢复应用，转 v0.2.3 改为安装器显式拉起。

### v0.2.1（2026-11-24 发布）

关于页重做，不涉及协议、数据库与引擎行为，版本号从 0.2.0 升到 0.2.1（提交 `fdd3092`）。

- ✅ 版式：页标题 + 品牌区（图标、版本、检查更新）；正文按「软件信息」「功能概览」「更新」「第三方许可」「支持」分区，区间用 1px 上边框分隔，整体 `MaxWidth=880` 左对齐。
- ✅ 文案：去掉口语化说明；网络通信明确「邮件收发仅连接已添加账户的 IMAP / SMTP 服务；检查更新时访问 GitHub Releases」；版权句统一为「商标或注册商标」。
- ✅ 第三方许可折叠：默认收起，标题右侧显示「展开 / 收起」；展开后列出 13 项主要运行时组件（含 Avalonia.Controls.WebView、ANGLE、ProtectedData），完整版本与间接依赖仍指向 `docs/THIRD-PARTY-NOTICES.md`。
- ✅ 更新入口：`检查更新` 移到品牌区「版本 0.2.1」后面；「更新」区保留状态文本、可用更新时的「下载并安装」按钮、更新来源与 SHA-256/便携版说明。初始状态为「尚未检查更新。」
- ✅ 验证：`dotnet build src\MailDog.slnx -c Debug` 0 警告 0 错误；`dotnet test` 82 通过（Html 30、Contracts 6、Mail 46）；UI Automation 实测折叠/展开与「检查更新」返回「已是最新版本（0.2.0）」。
- ✅ 发布：tag `v0.2.1`，Release <https://github.com/refout/MailDog/releases/tag/v0.2.1>；x64 便携包 32.6 MiB、arm64 便携包 31.2 MiB、x64 安装包 23.4 MiB，安装包 SHA-256 `c3d838d9a0697cc4782c9ef57e3e98ca88ee97ab9aba1c719a87c42e3b63b6c3`。
- ✅ 真机升级（2026-11-24）：本机安装版 0.2.0 发起更新，下载约 5 分钟、SHA-256 校验通过并静默安装成功，安装后 `FileVersion` 为 0.2.1.0；**但安装程序关闭旧窗口后没有重新启动 MailDog**，复现并定位到 `MailDog.iss` 的 `RestartApplications=no` 与 `[Run] skipifsilent`，转 v0.2.2 修复。

### v0.2.0（2026-11-24 完成并打 tag，代码见 `46b5407` 之后的提交链）

基线：v0.1.2 已发布（新图标、系统 toast 通知、`maildog:` 协议激活、通知内容开关、高透明背景下的承字面）。v0.2.0 的功能项与 CLI 集成测试、`maildog-cli` Skill 均已落地；只剩发布后的真机通知点击与自动升级验证（需要把 0.2.0 包装上旧的 0.1.2 环境）。

#### P0 通知链路收口（v0.1.2 遗留）

- ⏳ 真机点击验证：`activationType="protocol"` 在 ToastGeneric 上是否被鼠标点击认可，目前只验证到命令层（协议注册 → `--activate` → `WM_COPYDATA` → `Activate` → 广播/拉起 UI）。若失效，退回"开始菜单快捷方式 + COM 激活器"。
- ✅ 协议路径自校验（2026-11-24）：启动时比对 `HKCU\Software\Classes\maildog\shell\open\command` 里的 exe 与当前进程，不一致即重写；实测把命令改成 `C:\nowhere\...` 后重启 Engine，值已恢复为当前路径。
- ✅ 生命周期清理（2026-11-24）：安装包加 `[Registry]` 两项（`Software\Classes\maildog` 与 `Software\Classes\AppUserModelId\MailDog.MailDog.App`，`Flags: uninsdeletekey`），卸载/升级一并删除；AUMID 的 `IconUri` 每次 Engine 启动重写，跟随新安装目录。
- ✅ 通知节流与合并（2026-11-24）：`EngineHost` 里攒 1.5 秒再弹（单批最长等 4 秒），多封合成"收到 N 封新邮件"，Tag 固定 `digest` 让后来的汇总覆盖前一条；设置里有"多封合并"开关。
- ✅ 免打扰与隐私（2026-11-24）：免打扰时段（`HH:mm` 起止，可跨夜，默认 22:00–08:00，默认关）；锁屏（`OpenInputDesktop` 打不开输入桌面）时只显示"收到一封新邮件"，主题/摘要/发件人都不上屏；只通知收件箱（按 `folders.role` 判断，订阅与自定义文件夹只入库）。

#### P0 自动更新

- ✅ 应用内检查（2026-11-24）：`UpdateService` 读 `https://api.github.com/repos/refout/MailDog/releases/latest`，按数字段比较版本；关于页有「检查更新」按钮与状态文案，托盘菜单新增「检查更新」（界面没开就拉起，直接落到关于页并跑一次检查）。
- ✅ 静默升级（2026-11-24）：下载 `MailDog-<ver>-win-x64-setup.exe` 到 `%LOCALAPPDATA%\MailDog\updates\`，`/SILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS /SP-` 静默安装，装完从同目录拉起新 UI 再关闭当前进程；便携版（不在 `Programs\MailDog` / `Program Files\MailDog` 下）不自动安装，只显示「去下载页」。
- ✅ 降级与失败（2026-11-24）：检查失败只显示一句原因，不弹窗、不重试；下载边写边算 SHA-256，与 GitHub asset 的 `digest` 比对，不一致即删除；安装失败保留原版本并提示手动下载（Inno 静默失败不改目标目录，原文件仍在）。
- ⏳ 真机升级验证（2026-11-24 修正口径）：0.1.2 里**没有**更新入口，所以"用 0.1.2 点下载并安装"这条原计划做不到。现已把 0.2.0 安装版覆盖装到本机（SHA-256 与 Release 一致），关于页「检查更新」实测返回"已是最新版本（0.2.0）"，说明 GitHub 查询与版本比较正常；**静默下载安装 + Restart Manager 收进程 + 新版自启** 这条链路要等 0.2.1 或 v0.3 发布后再点一次才能验证。
- ✅ 安装版实装（2026-11-24）：`%LOCALAPPDATA%\Programs\MailDog` 从 0.1.2 静默覆盖到 0.2.0，UI/Engine/Cli 三件套都在，`MailDog.Cli.exe` 随包分发。

#### P1 命令行 `MailDog.Cli`

**MCP 服务已取消**（2026-11-24 决定）。原方案要多一层协议、端点、会话与令牌，收益在本项目面前不明显：本地单用户、能力全部在 Engine 后面，用命令行同样能被任何能跑 shell 的助手调用，还能直接进脚本、计划任务与 CI。省下的是传输层、鉴权与客户端配置这三块最容易出问题的部分。

- 形态：新项目 `MailDog.Cli`（AOT 控制台 exe，与 Engine/UI 同目录分发，安装包可加入 PATH）。参数解析手写，不引入长期预发布状态的命令行库。
- 与 Engine 的关系：不直连数据库、不自己发信，全部走现有命名管道 IPC（`getAccounts` / `getFolders` / `getMessages` / `searchMessages` / `getMessageBody` / `readAttachment` / `saveDraft` / `sendMail` / `markRead` / `moveMessage`）；Engine 没运行就按 `EngineHost` 的路径探测先拉起来，避免出现第二个写库进程。
- 只读子命令（✅ 已实现，真机跑过）：
  - `maildog accounts` — 账号清单（地址、类型、状态、未读数）
  - `maildog folders [--account <id>]` — 文件夹与未读数
  - `maildog list [--folder inbox] [--account <id>] [--limit 20] [--unread]` — 邮件摘要
  - `maildog search <query> [--folder|--all] [--account] [--limit]` — 复用现有 FTS + LIKE 回退
  - `maildog show <id> [--body] [--headers]` — 头信息与纯文本正文（超长按 `--max-chars` 截断）
  - `maildog attachment <message-id> <attachment-id> [--out <dir>]` — 落盘并打印路径
- 写入子命令（✅ 已实现，默认 dry-run，加 `--confirm` 才执行）：`draft`（写草稿）、`mark`（已读/旗标）、`move`、`send`。`send` 必须带 `--confirm` 才真正发送，缺省等于 `--dry-run`（打印收件人、主题、正文预览后退出）。
- 机器可读的自描述：`maildog schema`（或 `maildog help --json`）输出所有子命令、参数、`--json` 结果结构与退出码，供助手自己发现能力——替代 MCP `tools/list` 当初承担的那点发现作用，也便于脚本与文档自动生成。
- 输出约定：默认人读表格；`--json` 输出稳定 JSON（列表为每行一条 JSON，便于管道）；禁止在输出里出现口令、令牌、DPAPI 明文。
- 退出码：`0` 成功、`2` 未找到、`3` 账号未登录或离线、`4` 被权限/配置拒绝、`5` 参数错误。
- 测试：✅ CI 已加轻量冒烟（`schema` 结构、`help` 退出 0、未知开关退出 5，不需要 Engine）；✅ 临时库种子集成测试（2026-11-24）：`src/build/testdata/cli-seed.db` 是一份 140 KB 的预置库（1 个种子账号、3 封邮件、1 封带正文），CI 用 `--db` 起一个真实 Engine 后跑 `accounts` / `list` / `search haystack --all` / `show 2 --body` / `send --dry-run`，脚本为 `src/build/ci-cli-seed.ps1`，跑完即删临时库；✅ HTML-only 种子集成测试（v0.3.4）：`cli-html-seed.db` 把第 2 封的 `text_plain` 置空、只留 HTML，`ci-cli-html.ps1` 验证默认转换、`--html`、`--json` 的 `textSource`/`html` 字段。写命令（`mark` / `move` / `draft`）仍只在真机验证过，未进 CI。
- HTML-only 正文（v0.3.4）：`show` 先取 `PlainText`，为空时用 `MailDog.Html.HtmlTextConverter.ToPlainText` 回退；默认 2000 字截断，`--html` 默认全文并输出原始 HTML；`--json` 增加 `textSource`，`--html` 时再增加 `html` 字段。传输层已有 `MailBodyDto.Html`，不需要改 Engine。
- 打包与分发（v0.3.0 调整，v0.3.1 修正入口）：`publish.ps1` 仍产出 `MailDog.Cli` 与 `maildog` 转发壳供构建/CI 使用，但 `package.ps1` / `package-linux.sh` 会把它们和 `maildog-cli` Skill、说明收进 `cli/MailDog-cli.zip`（Linux 为 `.tar.gz`），再从安装目录移除顶层 CLI。安装包只带这个压缩包；v0.3.1 把下载入口从关于页移到设置「工具」，保存时不再弹文件对话框，直接写到安装目录根下。不自动改 PATH，也不写系统目录。这样普通用户不会多一个 5 MB 的 exe 和一条卸载时不敢删的 PATH。v0.3.2 在工具页加「复制给智能体」，把安装目录、压缩包、解压后的 CLI/启动脚本/Skill 路径与写命令约束一次复制到剪贴板。v0.3.3 下载后直接解压到安装目录，成功即删除压缩包；失败保留压缩包并回退到复制手动解压命令。
- CLI 下载自动解压（v0.3.3）：`CliBundle.Extract` 支持 zip 与 tar.gz，`TarFile` 走 `GZipStream`，Linux 下补 `MailDog.Cli` / `maildog` 的执行位；解压目标固定为 `AppContext.BaseDirectory`，覆盖旧文件，不改 PATH。`DownloadCliAsync` 区分下载失败与解压失败，后者保留压缩包供重试。
- 更新包清理（v0.3.2）：安装器返回 0 后立刻删除 `%LOCALAPPDATA%\MailDog\updates` 里的 `*-setup.exe`；界面常被安装器的 `/CLOSEAPPLICATIONS` 先关掉，因此 `MainWindowViewModel.InitializeAsync` 在连接后台前再扫一遍。下载新包前也清一次旧包，只保留当前下载目标。
- CLI 调用记录（v0.3.2，v0.3.4 扩展）：`CliAudit` 继续写时间、CLI 版本、命令名、退出码和耗时到 `<数据目录>/logs/cli-usage.log`；v0.3.4 同时追加 `<数据目录>/logs/cli-calls.jsonl` 结构化记录，带 `--json` 时把标准输出截留到 20 万字符一并保存，超出标记 `jsonTruncated`。命令名过滤控制字符，不记录参数或令牌；JSON 内容可能含邮件正文，界面「工具 → 查看调用记录」按时间倒序展示最近 100 条，并提示隐私与截断。两个文件分别 512 KB / 2 MB 轮转为 `.1`，`MAILDOG_NO_CLI_AUDIT=1` 可关闭；记录失败不影响退出码。
- 配套 Agent Skill（✅ 2026-11-24 已产出）：`workspace/.user_skills/maildog-cli`，正文按 `maildog schema --json` 的真实输出重写：命令名、参数（`--folder` 不是 `--role`，正文用 `--text` / `--body-file`，移动用 `--account` + `--folder`）、`--json` 字段、退出码 0/2/3/4/5/6 与 `--force-send`、`cancel <outboxId>` 全部对齐实现；只读命令在种子库上逐条跑过，写命令在临时库验证了 `mark`，`draft` / `move` 受种子账号无授权码限制只验证到退出码 3。硬约束已写进 SKILL.md：
  - 找不到 `maildog` 时先构建 `src\MailDog.Cli`，**禁止**退回去直读 sqlite 或凭截图臆测。
  - id 只能来自 `list` / `search`，不猜 id。
  - `send` 先 dry-run 给用户看，确认后才加 `--confirm`；`draft` / `mark` / `move` 同样要用户明确同意；命中发送前检查时退出码 6，确认后 `--force-send`。
  - 随包副本：`src/build/cli-bundle/maildog-cli/SKILL.md`，和 CLI 一起压进 `cli/MailDog-cli.zip` / `.tar.gz`；不依赖开发机的 `.user_skills` 路径，正文里的命令解析改成先查 PATH。
- ✅ **解析层迁移到 `System.CommandLine`**（2026-11-25 完成）：`Arguments.cs` 用 `RootCommand` / `Option<T>` / `ParseResult` 承载词法与开关识别，`Program.cs`、`Schema.cs`、`Output.cs`、`Commands.cs` 未改；命令名、开关、`--json` 输出、`schema` JSON 与退出码 0/2/3/4/5/6 保持不变。`Arguments` 仍保留原外观，先按白名单扫一遍，未知开关/缺值/未知命令继续给中文提示；`--name=value`、`--` 终止符、可重复 `--to`/`--attach`、单值开关重复后写覆盖前写均有测试（`MailDog.Cli.Tests`，10 条）。
  - 现状依据：`System.CommandLine` 已有正式版（2.0.x 到 2.0.15，3.0.0 已发布），不再是当年长期 beta 的状态；AOT/裁剪场景可用，仍是微软官方维护。微软那篇中文教程用的是 beta 期 API（`CommandLineBuilder` / `UseDefaults`），迁移时以官方当前文档的 `RootCommand` / `Option<T>` / `ParseResult` 为准，不要照教程抄。
  - 实际只改 `Arguments.cs`；`Program.cs`、`Schema.cs`、`Output.cs`、`Commands.cs` 未动。**对外契约不变**：命令名、开关名、`--json` 输出结构、退出码（0/2/3/4/5）、`maildog schema` 的 JSON 全部保持原样。
  - 已验收：`schema` / `help` / 未知开关与未知命令退回 5 / `--name=value` / `--` 后接 `-` 开头的正文 / 可重复 `--to`、`--attach` / AOT 发布无 IL 警告 / 本机真实账号跑只读命令。
  - 迁移失败即回滚单条提交，手写版保留在 git 历史里可随时取回。

#### P1 邮件可靠性：本地规则、延迟发送、附件防漏

- ✅ **本地规则过滤器**（2026-11-24 完成）：按发件人（显示名 + 地址联合匹配）/ 主题 / 两者关键字做"包含"匹配（忽略大小写，不做正则），可限定到单个账号；命中后标已读、加旗标、不弹通知，可多选。规则在新邮件入库那一刻按 `NewMailEvent.NewMessageIds` 逐封应用，存储为 SQLite `rules` 表（`account_id`、`field`、`pattern`、`mark_read`、`flag`、`mute`、`enabled`、`hits`），命中计数用于判断规则是否写得太宽。设置页「同步」区提供规则列表（启用开关、命中次数、删除）与新增表单，走 `getRules` / `saveRule` / `deleteRule` 三个 IPC。标已读与加旗标会同步到服务器（失败只记日志，不影响来信入库）；**不做**"自动移到文件夹"，避免与拖拽/客户端排序冲突，也暂不对历史邮件回溯应用。匹配单测见 `RuleMatcherTests`（5 条），存储单测见 `SqliteStoreTests.RulesRoundTripAndHits`。
- ✅ **延迟发送 + 撤销窗口**（2026-11-24 完成）：`SendDelaySeconds` 设置（0/5/10/30，默认 10，设置页「延迟发送」）。入队时 `NextAttemptUtc = now + 延迟`，窗口内不碰 SMTP，直接返回 `queued + delaySeconds`；写信窗口显示倒计时与「撤回」，CLI 显示 `outboxId` 并提供 `maildog cancel <outboxId>`。发件箱轮询从 30 秒缩到 5 秒，保证延迟到期后及时投递。撤回走已有 `deleteOutbox`，只删 `pending`，已在发送中的不动。
- ✅ **附件防漏提醒**（2026-11-24 完成）：检查放在 Engine 的发送路径（`MailDog.Mail/Smtp/SendGuard.cs`），UI 与 CLI 共用同一套判断。正文（含 HTML 去标签后）出现"附件 / 见附件 / 附上 / attach / enclosed"等词但附件为空、单附件超 25 MiB、合计超 40 MiB 时，Engine 不发送、回 `status=confirm_required`；UI 弹出提醒条（「去加附件」/「仍然发送」），CLI 打印提醒并退出 `6`，确认后加 `--force-send` 重发。只提醒不硬拦，重发时带 `SkipSendChecks`。测试见 `SendGuardTests`（9 条）。

#### P2 功能候选（排在 v0.2.0 之后）

- 会议邀请入日程：解析 `.ics` 并一键加入日程。现有日历只是"按日期跳转"的弹层（`CalendarDayViewModel` / `CalendarMonthText`），要承接邀请需确认是否扩成轻量日程视图，还是先只提供"加入系统日历（生成 .ics 交默认程序）"。
- 来信可信提示：显示名与真实域名不一致、首次往来域名、链接真实目标预览。
- 未读可见性：任务栏徽标（`ITaskbarList3` 叠加图标）+ 托盘 tooltip 未读数。
- 搜索增强：结果高亮已在（`HighlightedTextBlock`），补范围限定（主题/发件人/正文）与相关性排序；**同时加入搜索语法** `from:` / `subject:` / `has:attachment` / `after:` / `before:`，解析器由 UI 与 CLI 共用一份，避免两处各写；trigram 短词回退 LIKE 的逻辑保持不变。
- 通知粒度：按账号开关、点击行为二选一（仅拉起窗口 / 拉起并标记已读）。
- 极低透明度兜底：透明度低于 55% 时给一句提示，或提供"内容区实/透"独立开关，避免用户以为显示坏了。

#### P2 工程卫生

- UI 崩溃留痕：顶层异常捕获写本地日志（现在只有 Engine 写 `logs/engine-*.log`）。
- CI 增加 Avalonia Headless 冒烟测试：切主题、开设置、模拟通知激活拉起窗口。
- 官网截图重拍：首页仍是 v0.1.1 的旧图标真机图，与 v0.1.2 新标不一致。
- 代码签名与依赖审计：OV/EV 证书接入，`NuGetAudit` 进 CI。

### v0.3 规划（2026-11-24 拟定）

- **Linux x64（已完成 0.3.0）**：Engine、UI、CLI 可在 Linux 构建运行；Engine 按宿主 OS 单目标编译，Windows 专用托盘/Toast 与 Linux 实现分离；数据目录走 XDG，登录启动写 `~/.config/autostart/maildog.desktop`；凭据优先存 `secret-tool` 的系统钥匙串，不可用时回退数据目录内 0600 密钥的 AES-GCM；SQLite 嵌入 `libe_sqlite3.so`；通知走 `notify-send`，0.8+ 支持 action 时点击跳回邮件；Avalonia `TrayIcon` 提供显示/退出菜单；写 `maildog-handler.desktop` 注册 `maildog://`，二次启动经容器管道交回已运行 Engine；`shutdown` IPC、CLI `quit` 与关于页“退出后台”兜底；WebKitGTK 可用时渲染 HTML，缺库回退纯文本；锁屏时通知隐藏内容；更新识别 `*-linux-x64.tar.gz`；发布 `MailDog-<版本>-linux-x64.tar.gz`，CI 增加 ubuntu-latest 任务。未做：Linux arm64。

- **Windows 集成（已完成 0.3.0 代码）**：注册 `mailto:` 协议、`.eml` ProgID 与 `OpenWithProgids`、`RegisteredApplications`/`Capabilities` 默认邮件程序候选、资源管理器“发送到”快捷方式、拖入 `.eml` 打开、`SHAddToRecentDocs` + 快捷方式维护最近邮件。卸载器清理上述 HKCU 键、SendTo 快捷方式与 JumpList 目录。Windows 交叉编译通过；实机注册表、SendTo 与 JumpList 仍需在 Windows 上验收。
- **诊断包导出（已完成 0.3.0）**：`report.txt` + `settings.json` + 最近 5 个 `engine-*.log` + `crash.log` 打成 zip；邮箱地址、签名、口令、令牌、Bearer 全部脱敏；关于页一键导出；UI 未处理异常与未观察 Task 异常写 `crash.log`。
- **账号健康面板（已完成 0.3.0）**：每个账号的上次同步成功时间、IDLE 连接状态、最近一次错误分类与重试时间；设置页“同步”区展示，进入页面自动刷新。

### 远期与不做（先记结论，避免反复讨论）

- **HTTP / SOCKS5 代理**：IMAP、SMTP、WebView2 三条通道都要接，还要处理代理认证与失败回退，做错很难排查。等真实报障再启动。
- **S/MIME 或 PGP**：受众窄、证书管理复杂，与"本地只读、轻量"的定位冲突，远期再议。
- **多语言界面**：现有文案全是口语化中文，抽 i18n 等于把所有文案重写一遍，收益不确定，暂不做。

---

## 17. 风险与缓解

| 风险 | 可能性 | 影响 | 缓解 |
|---|---|---|---|
| `Avalonia.Controls.WebView` AOT 不可用或需额外原生文件 | 中 | 高 | Stage 0 探针；必要时 `IncludeNativeLibrariesForSelfExtract`；最坏情况 UI 改用纯文本 + 外部浏览器查看 |
| WebView2 Runtime 在 Win10 缺失 | 中 | 中 | 启动检测，提示安装；自动降级纯文本 |
| Entra 对 IMAP OAuth 的政策变化 | 中 | 高 | Stage 3 尽早验证；监控 Microsoft 公告；预留 Graph 适配器接口（不实现） |
| QQ IMAP IDLE 不稳定/限流 | 高 | 中 | 轮询兜底、退避、单账户串行 |
| 净化器被绕过 | 中 | 高 | 白名单 + CSP + 禁用 JS + 语料库 + 默认禁图；安全评审门 |
| 大邮件导致内存峰值 | 中 | 中 | 2 MiB 截断、10 MiB 正文上限、流式读取 |
| AOT 警告来自第三方升级 | 中 | 中 | 版本锁定 + CI 门禁 + 探针先行 |
| 未签名 exe 触发 SmartScreen/杀软 | 高 | 中 | 代码签名；发布说明；SHA-256 校验 |
| 单文件 + DPAPI 的迁移/换机失败 | 低 | 中 | 解密失败即要求重新登录，不崩溃 |
| UI 与 Engine 版本不匹配 | 低 | 中 | 握手协议版本 + 安装包同版本分发 |

---

## 18. 开放问题

1. **Entra client id**：已内置公开客户端 ID（公共客户端 ID 不是机密），无需用户填写；如需替换可用环境变量 `MAILDOG_ENTRA_CLIENT_ID`。
2. **许可证**：Public 仓库采用 MIT / Apache-2.0 / 其他？
3. **代码签名**：是否已有证书？没有的话 v1 先以未签名 ZIP 分发？
4. **发信范围**：v1 是否必须包含撰写/回复？当前方案将其放入 v1.1。
5. **附件**：是否需要 v1 支持下载？当前只返回元数据。
6. **macOS/Linux**：Linux x64 已在 v0.3.0 落地；macOS 与 Linux arm64 的目标时间点仍未定。
7. **自动更新**：是否允许联网检查版本（涉及隐私与代理环境）。
8. **代理**：是否需要支持系统代理/自定义代理（MailKit 与 HttpClient 均需配置）。

---

## 19. 参考

- Avalonia WebView 文档：<https://docs.avaloniaui.net/docs/app-development/embedding-web-content>
- Avalonia `NativeWebView`：<https://docs.avaloniaui.net/controls/web/nativewebview/>
- Avalonia `WebAuthenticationBroker`：<https://docs.avaloniaui.net/controls/web/webauthenticationbroker>
- Avalonia Fluent 主题：<https://docs.avaloniaui.net/docs/basics/user-interface/styling/theme-variants>
- Microsoft Entra 授权码流（PKCE）：<https://learn.microsoft.com/entra/identity-platform/v2-oauth2-auth-code-flow>
- RFC 8252（OAuth 2.0 for Native Apps）：<https://www.rfc-editor.org/rfc/rfc8252>
- RFC 7636（PKCE）：<https://www.rfc-editor.org/rfc/rfc7636>
- MailKit 文档：<https://github.com/jstedfast/MailKit>
- MailKit AOT 兼容项目：<https://github.com/jstedfast/MailKit/tree/master/AotCompatibility>
- Microsoft.Data.Sqlite：<https://learn.microsoft.com/dotnet/standard/data/sqlite/>
- Fluent UI System Icons：<https://github.com/microsoft/fluentui-system-icons>
- Fluent 2 设计规范：<https://fluent2.microsoft.design/>
- .NET Native AOT：<https://learn.microsoft.com/dotnet/core/deploying/native-aot/>
