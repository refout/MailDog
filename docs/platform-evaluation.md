# 平台扩展评估

日期：2026-11-25

## Linux arm64

状态：已接入发布链路。

- 打包脚本支持 `linux-arm64`，但必须在原生 ARM64 主机上构建；当前 CI 使用 GitHub 的 `ubuntu-24.04-arm`。
- `linux-arm64` 与 `linux-x64` 使用同一套 Engine/UI/CLI 与 Skia/HarfBuzz 原生库结构。
- 仍需在真实 ARM64 Linux 桌面上验收托盘、WebKitGTK 正文与 `notify-send` 通知。

## Linux 无托盘兜底

状态：已落地。

- `LinuxTray` 创建失败时不再让 UI 启动失败，主窗口照常可用。
- 关闭窗口后 Engine 继续驻留；用随包 CLI 的 `maildog open` 重新打开界面，用 `maildog quit` 正常停止后台。
- 设置页“后台进程”区已写明这两个入口。

## macOS

状态：仅评估，暂不发布。

- 进入真实需求前不投入开发，避免无收益的签名、公证与长期维护成本。
- 若启动 macOS 版本，至少需要评估：AppKit 托盘、WKWebView 渲染、钥匙串、通知权限、Apple Silicon/Intel 双架构、Developer ID 签名与 notarization、自动更新包格式。
- 现有 Avalonia 跨平台代码可复用，但 `MailDog.Engine` 的 Windows 消息循环与托盘实现需要独立抽象。
