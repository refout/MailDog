# MailDog

Windows 上把 QQ 邮箱和 Outlook 收进同一个窗口的桌面邮件客户端。看过的信存在本机，断网也能翻；关掉窗口，后台照样收信。

[下载](https://github.com/refout/MailDog/releases/latest) · [官网](https://refout.github.io/MailDog/) · [更新日志](https://refout.github.io/MailDog/changelog.html)

![MailDog 收件箱](web/assets/shots/inbox-light.png)

> 截图取自 v0.1.1 的实机运行窗口，里面的邮件是演示数据，不是谁的收件箱。

<details>
<summary>暗色主题和设置页的样子</summary>

![MailDog 收件箱（暗色）](web/assets/shots/inbox-dark.png)

![MailDog 设置页](web/assets/shots/settings-light.png)

</details>

## 现在能做什么

当前发布版 **v0.1.1**，只读：能加邮箱、能收信、能看信。

- **两个邮箱，一个窗口**：QQ 邮箱用授权码登录，Outlook 点一下用浏览器登录，不用填密码。两个邮箱的信在同一份列表里，收件箱、已发送、草稿、存档按邮箱分开列，哪个邮箱有几封没读，名字旁边直接标着。
- **新邮件马上提醒**：信到了就弹桌面通知，点一下跳到那封；连接断了会自己重连，笔记本合盖再打开会补一次同步。
- **看过的信存在本机**：邮件头、正文都存进本地的 SQLite，断网、邮箱服务器抽风时照样能翻能搜。
- **陌生邮件的排版动不了你的窗口**：HTML 正文只保留安全的那部分，远程图片默认不加载，某封信格式坏了就退回纯文字。
- **日常收信要用的都在**：搜索、筛选、按时间 / 发件人 / 大小等排序、按时间跳转、批量标已读未读、给邮件加旗标、附件下载和用默认程序打开。
- **关掉窗口也能收信**：右下角托盘里的小图标一直看着邮箱，未读数是图标上的红点。
- **下载就能跑**：两个 Native AOT 单文件，不用先装 .NET 运行时，不用另装数据库，也没有要注册的账号。UI 约 46 MB，后台进程约 13.6 MB。

## 下载

| 文件 | 说明 |
|---|---|
| `MailDog-0.1.1-win-x64-setup.exe` | 安装包，装完桌面有快捷方式 |
| `MailDog-0.1.1-win-x64.zip` | 64 位便携版，解压双击 `MailDog.UI.exe` |
| `MailDog-0.1.1-win-arm64.zip` | Arm 版便携包 |

都在 [Releases](https://github.com/refout/MailDog/releases/latest) 里。系统要求 Windows 10 1809+ 或 Windows 11；Windows 10 需要先有 [WebView2 Runtime](https://developer.microsoft.com/microsoft-edge/webview2/)（Windows 11 已内置）。

- **QQ 邮箱**：在 QQ 邮箱网页端开启 IMAP/SMTP，生成**授权码**（不是登录密码）填进去。
- **Outlook**：点“使用浏览器登录”，应用已内置 Entra 公共客户端（公共客户端 ID 不属于机密）。想换成自己注册的应用，设环境变量 `MAILDOG_ENTRA_CLIENT_ID`。
- **数据放哪**：数据库、日志和原生库都在 `%LOCALAPPDATA%\MailDog\`。

## 下一版 v1.1（代码已完成，还没发版）

撰写、回复、全部回复、转发、草稿存在 Drafts、SMTP 发件与失败重试（1/2/5/10/30/60 分钟退避，最多 8 次）、自定义 IMAP/SMTP 服务器、cid 内嵌图、托盘未读角标、开机自启与本地缓存清理，这些代码已经在 `main` 上。版本号还停在 0.1.1，等发版后再改下载区的说法。

## 开发

### 环境

- .NET SDK 11（锁定 `11.0.100`，见 `src/global.json`）
- Visual Studio Build Tools 2026，勾选 `Microsoft.VisualStudio.Component.VC.Tools.x86.x64` 与 `Microsoft.VisualStudio.Component.Windows11SDK.26100`
- Windows 10 1809+ / Windows 11

### 构建与测试

```powershell
# global.json 在 src 下
cd src
dotnet build MailDog.slnx -c Release
dotnet test MailDog.slnx -c Debug --no-restore
```

### 发布与打包

```powershell
# 发布两个 AOT 单文件（在仓库根执行）
pwsh -File src/build/publish.ps1 -Runtime win-x64

# 版本化压缩包：dist/MailDog-<版本>-win-x64.zip
pwsh -File src/build/package.ps1 -Runtime win-x64

# 打包 + 安装包（需要 Inno Setup 6，可 choco install innosetup）
pwsh -File src/build/package.ps1 -Runtime win-x64 -Installer
```

版本号默认读 `src/Directory.Build.props`，可用 `-Version 1.2.3` 覆盖。推 `v*` 标签会触发 [release 流水线](.github/workflows/release.yml)，构建 win-x64 / win-arm64 压缩包与 x64 安装包并创建 Release。

### 运行

```powershell
# 先起后台，托盘常驻，需要时再打开界面
dist/win-x64/MailDog/MailDog.Engine.exe

# 或者直接开界面（Engine 没跑会自动拉起，两个 exe 必须在同一目录）
dist/win-x64/MailDog/MailDog.UI.exe
```

Engine 支持 `--db <路径>` 换库、`--console` 附着终端输出、`--probe` 自检。界面通过 `MAILDOG_ENGINE_PATH` 找后台，后台通过 `MAILDOG_UI_PATH` 拉起界面。

## 技术构成

- **Engine**：MailKit（IMAP）+ SQLite 索引 + Win32 托盘 + OAuth2 PKCE + 命名管道服务，按需拉起 UI，UI 关闭后继续驻留。
- **UI**：Avalonia 12 + Fluent Design + NativeWebView，关窗即退。

| 维度 | 决策 |
|---|---|
| 目标框架 | .NET 11 / `net11.0` |
| UI | Avalonia 12.1.6 + `Avalonia.Themes.Fluent` |
| WebView | `Avalonia.Controls.WebView` 12.1.2（Windows 用 WebView2） |
| 邮件 | MailKit 5.0.0，不引用 `MimeKit.Cryptography` |
| 存储 | SQLite（`Microsoft.Data.Sqlite`），ADO.NET |
| 认证 | QQ 授权码；Outlook OAuth2 Authorization Code + PKCE（Engine 内自研回环） |
| IPC | 命名管道 + 4 字节长度前缀 + 源生成 JSON |
| 发布 | Native AOT 单文件 × 2，无 .NET 运行时依赖 |
| 平台 | 目前只做 Windows；macOS / Linux 预留抽象 |

## 文档

- [技术方案](docs/PLAN.md)：架构、库选型、IPC 协议、存储、认证、AOT 纪律、里程碑、风险
- [界面设计](docs/UI-DESIGN.md)：Fluent 设计令牌、三栏布局、组件规格、交互与可访问性
- [探针结论](docs/SPIKES.md)：AOT、原生库嵌入、WebView2 与工具链实测记录
- [官网源码](web/)：`index.html` + `assets/site.css`，纯静态，没有构建步骤，Pages 直接发布这个目录

## 仓库结构

```
MailDog/
├── docs/               方案、界面设计、探针结论
├── src/                代码根：MailDog.slnx、global.json、项目、tests、build 脚本
├── web/                官网页面（GitHub Pages 的发布目录）
├── .github/            CI 与 release 工作流
├── .editorconfig
└── README.md
```

## 交付产物

本机构建出来的东西在 `dist/`：

```text
dist/
├── win-x64/
│   ├── MailDog/
│   │   ├── MailDog.Engine.exe   13.6 MiB
│   │   └── MailDog.UI.exe       46.0 MiB
│   └── MailDog-win-x64.zip      27.2 MiB
├── MailDog-0.1.0-win-x64.zip        0.1.0 的便携包（旧）
└── MailDog-0.1.0-win-x64-setup.exe  0.1.0 的安装包（旧）
```

正式发布的 v0.1.1 在 Release 里：x64 便携包 27.2 MiB、arm64 便携包 26.1 MiB、x64 安装包 20.7 MiB。

- 两个 exe 都是自包含单文件，不需要 .NET 运行时；UI 必须和 Engine 在同一目录才能自动拉起后台。
- SQLite / Skia / HarfBuzz / ANGLE 原生库嵌在 exe 里，首次启动解到 `%LOCALAPPDATA%\MailDog\native\`。
- UI 优先用 ANGLE 渲染，这样云母 / 亚克力材质和透明窗口才生效；加载失败自动回退 Skia 软件渲染。WebView2 有自己的 GPU 进程。
- 关掉 UI 后 `MailDog.UI` 和 `msedgewebview2` 全部退出，Engine 托盘继续驻留。

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
| 7 | 撰写 / 回复 / 转发 / SMTP 发信 | ✅ 已完成（v1.1，待发版） |
| 8 | 代码签名、自动更新 | 待证书与发布链路 |

## 已知限制

- 已发的 v0.1.1 是只读版：不能写信、回复、转发。收信、读信、搜索筛选、批量标已读、加旗标都正常。附件能下载、能用默认程序打开，但不做内嵌预览。写信能力在 v1.1 的代码里（见上一节），等发版后才算能下载到。
- 新邮件靠 IMAP IDLE 实时推送（独立监听连接），服务器不支持或断线时回退到周期轮询。
- Outlook OAuth 与 QQ 真实账户需要真实凭据才能验收，自动化测试只覆盖到协议层。
- Engine 是 `WinExe`，从终端带 `--probe` / `--console` 启动时会附着父控制台。
- 只有 Windows x64 / arm64，没有 32 位，也没有 mac / Linux 版。

## 许可

待定（见 [开放问题](docs/PLAN.md#18-开放问题)）。在定下来之前，仓库里的代码和截图不要直接拿去商用。
