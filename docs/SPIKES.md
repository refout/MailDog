# Stage 0 探针结论

> 日期：2026-11-13
> 环境：Windows 11 22621 · .NET SDK 11.0.100 · Visual Studio Build Tools 2026 18.11.3（VC.Tools.x86.x64 + Windows11SDK.26100）· WebView2 Runtime 157.0.4341.46

## 1. 结果总览

| 探针 | 结果 | 备注 |
|---|---|---|
| `net11.0` 控制台 Native AOT | ✅ | 无 `IL*` 警告 |
| Avalonia 12 空窗口 Native AOT | ✅ | 需 `app.manifest` 声明 `supportedOS` |
| `NativeWebView` + WebView2 Native AOT | ✅ | 需显式 `UserDataFolder`；需 `app.manifest` |
| MailKit 5.0.0（MIME + IMAP 类型）Native AOT | ✅ | 无 `IL*` 警告 |
| Microsoft.Data.Sqlite 11.0.0 Native AOT | ✅ | 原生库需嵌入并解压（见 3.3） |
| FluentIcons.Avalonia 2.1.345 Native AOT | ✅ | XAML 编译与运行时渲染均正常 |
| 关闭 UI 后 WebView2 子进程退出 | ✅ | 进程退出后无新增 `msedgewebview2` 残留 |

## 2. 实测产物

| 产物 | 大小 | 说明 |
|---|---|---|
| `MailDog.Engine.exe`（v1） | 12.48 MiB | 含 MailKit、SQLite、OAuth、Win32 托盘 |
| `MailDog.UI.exe`（v1） | 44.21 MiB | 含 Avalonia、WebView、净化器、Skia/HarfBuzz/ANGLE、ColorPicker |
| Stage 0 Engine 探针 | 6.00 MiB | 203 ms 执行到退出 |
| Stage 0 UI 探针 | 35.54 MiB | 445 ms 到首窗口 |

- 两个 exe 均为自包含单文件，无需 .NET 运行时；UI 优先拉起同目录的 Engine，开发构建下回退到同仓库 `MailDog.Engine/bin` 的构建产物。
- PDB 不进入 `dist`；`av_libglesv2.dll` 与 Skia/HarfBuzz 一样作为嵌入资源，不留在发布目录。
- UI 优先 ANGLE（用于云母/亚克力材质），加载失败自动回退 Skia 软件渲染；WebView2 自身仍使用其 GPU 进程。

## 3. 关键发现与修正

### 3.1 `NativeWebView` 需要应用清单声明支持的 OS

AOT 产物首次启动即崩溃：

```
System.InvalidOperationException: Unable to create child window for native control host.
Application manifest with supported OS list might be required.
```

修复：为 UI 项目添加 `app.manifest`，声明 Windows 10/11：

```xml
<compatibility xmlns="urn:schemas-microsoft-com:compatibility.v1">
  <application>
    <supportedOS Id="{8e0f7a12-bfb3-4fe8-b9a5-48fd50a15a9a}" />
  </application>
</compatibility>
```

并在 csproj 设置 `<ApplicationManifest>app.manifest</ApplicationManifest>`。非 AOT 的 `dotnet run` 可能借用宿主清单而不暴露该问题，必须以 AOT 产物验证。

### 3.2 WebView2 必须显式指定可写的 `UserDataFolder`

默认环境创建返回 `E_ACCESSDENIED`：

```
[WebView2]Failed to create the WebView2 environment:
System.UnauthorizedAccessException: 拒绝访问。 (0x80070005)
```

修复：在 `NativeWebView.EnvironmentRequested` 中设置：

```csharp
windows.UserDataFolder = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
    "MailDog", "WebView2");
windows.ProfileName = "MailDog";
windows.Language = "zh-CN";
```

设置后适配器创建成功，`AdapterCreated`、`NavigationCompleted(IsSuccess=true)` 均触发。

### 3.3 Native AOT 不会打包第三方原生库

- Engine：`Microsoft.Data.Sqlite` 依赖 `e_sqlite3.dll`；`IncludeNativeLibrariesForSelfExtract=true` 在 AOT 下无效，单独复制 exe 运行会 `DllNotFoundException`。
- UI：`libSkiaSharp.dll`、`libHarfBuzzSharp.dll` 同样是外置原生库；缺少 `libSkiaSharp` 时启动即崩溃。

解决：

1. 通过 `GeneratePathProperty="true"` 的直接包引用取得原生库路径，作为 `EmbeddedResource` 嵌入程序集。
2. 启动时解压到 `%LOCALAPPDATA%\MailDog\native\...`。
3. 调用 `NativeLibrary.Load` + `NativeLibrary.SetDllImportResolver`，把 `e_sqlite3` / `libSkiaSharp` / `libHarfBuzzSharp` 解析到解压后的句柄。

实现位置：

- `src/MailDog.Mail/Storage/SqliteNativeBootstrap.cs`
- `src/MailDog.UI/Services/NativeBootstrap.cs`

> 原生库版本必须与 NuGet 包锁定版本一致；升级 `Microsoft.Data.Sqlite` / `SkiaSharp` / `HarfBuzzSharp` 时同步检查嵌入路径。

### 3.4 ANGLE 与窗口材质

`Avalonia.Angle.Windows.Natives` 带出 `av_libglesv2.dll`（5.14 MiB）。早期为保持单文件与行为确定，v1 显式使用软件渲染（`Win32RenderingMode.Software`）。该模式下 Avalonia 报告的 `ActualTransparencyLevel` 恒为 `None`，云母/亚克力无法生效。

加入外观材质后调整为：

- `av_libglesv2.dll` 与 Skia/HarfBuzz 一样作为嵌入资源，启动时解压到 `%LOCALAPPDATA%\MailDog\native\ui`，并注册到 `Avalonia.Win32` 程序集的 DllImport 解析器。
- `RenderingMode = [AngleEgl, Software]`：优先 ANGLE，加载失败自动回退软件渲染。
- `TransparencyLevelHint` 按设置切换 `Mica` / `AcrylicBlur` / `None`；若 `ActualTransparencyLevel` 仍为 `None`，500ms 后把面板回退为不透明，避免半透明叠在黑底上。

代价：UI 单文件增加约 5.2 MiB（38.05 → 43.27 MiB），仍在 55 MiB 目标内。WebView2 渲染不受影响。

### 3.5 `NativeWebView` 没有公开 `Dispose()`

12.1.2 的 `NativeWebView` 不实现公开 `IDisposable`。清理策略调整为：

- 窗口 `Closed` / 控件 `DetachedFromVisualTree` 时退订 `EnvironmentRequested`、`AdapterCreated`、`AdapterDestroyed`、`NavigationStarted`、`NavigationCompleted`、`WebMessageReceived`。
- 阅读窗格复用同一个 `NativeWebView` 实例，切换邮件只更新内容。
- 以“关闭 UI 后无 `MailDog.UI` 与新增 `msedgewebview2` 进程”为验收标准。

### 3.6 Build Tools 安装注意

- 只安装 `Microsoft.VisualStudio.BuildTools` 本体不会带 `link.exe` 与 Windows SDK。
- 必须添加：
  - `Microsoft.VisualStudio.Component.VC.Tools.x86.x64`
  - `Microsoft.VisualStudio.Component.Windows11SDK.26100`
- `vswhere -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64` 必须能返回安装路径，否则 AOT 链接会失败。
- 本次安装后系统提示重启；未重启状态下 AOT 已可正常工作，但 CI 与正式构建机应完成重启。

### 3.7 Win32 托盘 AOT 与生命周期（v1 验收）

AOT 产物实测：

- Engine 启动后创建 `MailDog.Tray` message-only 窗口与托盘图标。
- 通过 tray `WM_COMMAND` 打开 UI：`MailDog.UI` 启动并显示主窗口。
- 关闭 UI：`MailDog.UI` 进程数回到 0，`msedgewebview2` 从 12 回到 6（基线），Engine 仍驻留。
- 通过 tray “退出”：Engine 正常结束，托盘图标移除。

注意：

- `GetConsoleWindow` 位于 **kernel32.dll**，不是 user32.dll；放错 DLL 会在启动时抛 `EntryPointNotFoundException`。
- `Shell_NotifyIcon` 的 `NOTIFYICONDATA` 在 Native AOT 下按普通 `DllImport` 封送可用。
- Engine 仍是控制台子系统进程；托盘启动后用 `ShowWindow(SW_HIDE)` 隐藏控制台。`--console` 可保留控制台用于排障。

### 3.8 命名管道只能接受一次 UI 连接

`NamedPipeServerStream` 的 `maxNumberOfServerInstances` 曾设为 1。第一封 UI 连接建立后，接受循环再创建下一个实例会抛异常并退出；关闭 UI 后再次打开会一直停在“正在连接”，直到重启 Engine。

修正：

- 最大实例数改为 4（UI 有单实例互斥，实际仍只有一个 UI）。
- 实例创建包在 try/catch 中，失败等待 250ms 后重试，避免接受循环退出。

### 3.8 托盘图标来源

- 图标由微软 Fluent UI System Icons 的 `Mail`（filled）官方 SVG 导出，填充色改为 Fluent 品牌蓝 `#0F6CBD`。
- 使用 `Svg.Skia` 在构建期渲染为 256×256 PNG，再封装为 ICO；运行时不依赖 Svg.Skia。
- `ApplicationIcon` 将 ICO 嵌入 exe；托盘用 `ExtractIconEx` 从当前 exe 取图标，保持单文件。
- 未手绘任何图标。

## 4. 复现命令

```powershell
# 构建（global.json 在 src 下）
cd src
dotnet build MailDog.slnx -c Release

# Engine AOT
dotnet publish MailDog.Engine/MailDog.Engine.csproj -c Release -r win-x64 `
  -p:PublishAot=true -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true -p:StripSymbols=true `
  -o ../artifacts/engine-probe

# UI AOT
dotnet publish MailDog.UI/MailDog.UI.csproj -c Release -r win-x64 `
  -p:PublishAot=true -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true -p:StripSymbols=true `
  -o ../artifacts/ui-probe
```

独立运行验证：把生成的 exe 单独复制到空目录，删除 `%LOCALAPPDATA%\MailDog\native` 后运行，确认自动解压并正常启动。
