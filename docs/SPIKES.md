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

| 产物 | 大小 | 启动 |
|---|---|---|
| `MailDog.Engine.exe` | 6.00 MiB | 203 ms（进程执行到退出） |
| `MailDog.UI.exe` | 35.54 MiB | 445 ms（进程启动到主窗口句柄可用） |

- 两个 exe 均可在**只复制 exe 本身**的空目录中独立运行。
- 发布目录仍会生成 PDB 与 `av_libglesv2.dll`，部署脚本只复制 exe；PDB 不进入 `dist`。
- UI 使用 Skia 软件渲染（不携带 ANGLE），WebView2 自身仍使用其 GPU 进程。

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

### 3.4 ANGLE 可选，决定使用软件渲染

`Avalonia.Angle.Windows.Natives` 会带出 `av_libglesv2.dll`（5.14 MiB）。删除该 DLL 后 Avalonia 记录：

```
[OpenGL]Unable to load ANGLE: DllNotFoundException 'av_libGLESv2.dll'
```

随后自动回退软件渲染，UI 与 WebView 均正常。为保持单文件与行为确定，v1 显式设置：

```csharp
new Win32PlatformOptions
{
    RenderingMode = new[] { Win32RenderingMode.Software }
}
```

代价：Avalonia 自身 UI 不使用 GPU；邮件列表滚动与动效需要在中低端设备上复测。WebView2 的渲染不受影响。

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

## 4. 复现命令

```powershell
# 构建
dotnet build MailDog.slnx -c Release

# Engine AOT
dotnet publish src/MailDog.Engine/MailDog.Engine.csproj -c Release -r win-x64 `
  -p:PublishAot=true -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true -p:StripSymbols=true `
  -o artifacts/engine-probe

# UI AOT
dotnet publish src/MailDog.UI/MailDog.UI.csproj -c Release -r win-x64 `
  -p:PublishAot=true -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true -p:StripSymbols=true `
  -o artifacts/ui-probe
```

独立运行验证：把生成的 exe 单独复制到空目录，删除 `%LOCALAPPDATA%\MailDog\native` 后运行，确认自动解压并正常启动。
