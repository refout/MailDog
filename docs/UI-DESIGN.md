# MailDog 界面设计方案（Fluent Design）

> 状态：已实现（v1 只读界面）
> 日期：2026-11-14
> 关联文档：[PLAN.md](./PLAN.md)
> 设计语言：Microsoft Fluent 2（Avalonia `FluentTheme` 落地）
> 图标：Microsoft Fluent UI System Icons（禁止手绘图标）

---

## 1. 设计目标与原则

**一句话**：像系统自带邮件客户端一样安静、快、可信。

| 原则 | 具体含义 |
|---|---|
| 内容优先 | 三栏布局中，邮件正文占据最大视觉权重；装饰性元素降到最低 |
| 安静 | 无渐变、无大面积高饱和色；颜色只用于状态与操作 |
| 快 | 首屏先出列表再补正文；骨架屏不超过 1 个呼吸周期；动效只解释空间关系 |
| 可信 | 认证、同步、渲染失败都有明确、可操作的反馈；不隐藏错误 |
| 一致 | 所有控件使用 `FluentTheme` 语义资源；不自定义“仿 Fluent”样式 |
| 可访问 | 键盘可达、焦点可见、对比度达标、尊重减少动效设置 |

**密度**：使用 `FluentTheme` 的 `Compact` 密度。邮件列表是高频扫描场景，紧凑比宽松更合适；阅读区仍保持 20px 行高与 24px 内边距。

---

## 2. 设计语言基础

### 2.1 颜色

颜色以语义令牌使用，画面中不直接写十六进制。所有令牌同时提供 Light / Dark / HighContrast 三套值。

#### 浅色

| 令牌 | 值 | 用途 |
|---|---|---|
| `MailDogColorBgBase` | `#F3F3F3` | 窗口底色（Mica 不可用时的回退） |
| `MailDogColorBgLayer` | `#FFFFFF` | 内容层、列表、阅读区 |
| `MailDogColorBgSubtle` | `#FAFAFA` | 标题栏、搜索框、次级面板 |
| `MailDogColorBgHover` | `#0A000000`（4% 黑） | 悬停 |
| `MailDogColorBgPressed` | `#06000000`（2.4% 黑） | 按下 |
| `MailDogColorStroke` | `#E0E0E0` | 控件描边、分隔线 |
| `MailDogColorStrokeSubtle` | `#EBEBEB` | 列表内分隔 |
| `MailDogColorTextPrimary` | `#1B1B1B` | 正文、标题 |
| `MailDogColorTextSecondary` | `#616161` | 摘要、发件人邮箱 |
| `MailDogColorTextTertiary` | `#8A8A8A` | 时间、辅助说明 |
| `MailDogColorTextDisabled` | `#BDBDBD` | 禁用 |
| `MailDogColorAccent` | `#0F6CBD` | 主操作、选中、未读点 |
| `MailDogColorAccentHover` | `#115EA3` | 主操作悬停 |
| `MailDogColorAccentPressed` | `#0C3B5E` | 主操作按下 |
| `MailDogColorAccentTint` | `#EBF3FC` | 选中行背景、徽标底色 |
| `MailDogColorDanger` | `#C42B1C` | 错误、删除 |
| `MailDogColorDangerTint` | `#FDF3F4` | 错误横幅底色 |
| `MailDogColorSuccess` | `#0F7B0F` | 已连接、成功 |
| `MailDogColorWarning` | `#9D5D00` | 离线、需注意 |
| `MailDogColorWarningTint` | `#FFF9F0` | 警告横幅底色 |

#### 深色

| 令牌 | 值 |
|---|---|
| `MailDogColorBgBase` | `#202020` |
| `MailDogColorBgLayer` | `#272727` |
| `MailDogColorBgSubtle` | `#1F1F1F` |
| `MailDogColorBgHover` | `#0FFFFFFF`（6% 白） |
| `MailDogColorBgPressed` | `#08FFFFFF`（3% 白） |
| `MailDogColorStroke` | `#3D3D3D` |
| `MailDogColorStrokeSubtle` | `#333333` |
| `MailDogColorTextPrimary` | `#FFFFFF` |
| `MailDogColorTextSecondary` | `#C7C7C7` |
| `MailDogColorTextTertiary` | `#9A9A9A` |
| `MailDogColorTextDisabled` | `#6E6E6E` |
| `MailDogColorAccent` | `#479EF5` |
| `MailDogColorAccentHover` | `#62ABF5` |
| `MailDogColorAccentPressed` | `#2886DE` |
| `MailDogColorAccentTint` | `#1A3A5C` |
| `MailDogColorDanger` | `#FF99A4` |
| `MailDogColorDangerTint` | `#3B1A1A` |
| `MailDogColorSuccess` | `#6CCB5F` |
| `MailDogColorWarning` | `#FCE100` |
| `MailDogColorWarningTint` | `#3A3010` |

**使用规则**

- 强调色只用于：主按钮、选中态、未读点、焦点环、链接。正文中不使用强调色。
- 破坏性操作（删除、移除账户）用 `Danger`，且必须有二次确认。
- 状态色不作为大面积背景；只用于图标、文字、2px 边条、低透明度底色。
- 深色模式下不使用纯黑 `#000000` 作为背景。

### 2.2 字体

| 角色 | 字体 | 字号 / 行高 | 字重 | 用途 |
|---|---|---|---|---|
| Display | Segoe UI Variable Display | 28 / 36 | 600 | 空状态大标题 |
| Title | Segoe UI Variable Display | 20 / 26 | 600 | 邮件主题、对话框标题 |
| Subtitle | Segoe UI Variable Text | 16 / 22 | 600 | 设置页分组标题 |
| BodyStrong | Segoe UI Variable Text | 14 / 20 | 600 | 未读发件人、按钮 |
| Body | Segoe UI Variable Text | 14 / 20 | 400 | 正文、列表 |
| Caption | Segoe UI Variable Text | 12 / 16 | 400 | 时间、摘要、辅助信息 |
| Code | Cascadia Mono | 13 / 20 | 400 | 纯文本降级中的等宽内容 |

回退链：

```
"Segoe UI Variable Text", "Segoe UI", "Microsoft YaHei UI", "PingFang SC",
"Noto Sans SC", "Inter", sans-serif
```

- Windows 11 优先 `Segoe UI Variable`；Windows 10 回退 `Segoe UI`。
- 中文由 `Microsoft YaHei UI` 渲染；不额外打包中文字体，控制体积。
- 邮件 HTML 内部字体由应用 CSS 统一覆盖，不使用邮件自带字体。

### 2.3 间距与尺寸

基础网格 4px。

| 令牌 | 值 | 用途 |
|---|---|---|
| `Space2` | 4 | 图标与文字 |
| `Space3` | 8 | 控件内边距 |
| `Space4` | 12 | 列表项内边距 |
| `Space5` | 16 | 面板内边距 |
| `Space6` | 20 | 分组间距 |
| `Space7` | 24 | 阅读区内边距 |
| `Space8` | 32 | 空状态留白 |

| 元素 | 高度 |
|---|---|
| 系统标题栏 | 由系统决定 |
| 搜索框 | 32 |
| 普通按钮 | 32 |
| 图标按钮 | 32×32 |
| 文件夹项 | 36 |
| 邮件列表项 | 76 |
| 阅读区命令栏 | 48 |
| 筛选 chip | 28 |
| 状态横幅 | 40 |

### 2.4 圆角、描边、阴影

| 令牌 | 值 | 用途 |
|---|---|---|
| `RadiusControl` | 6 | 按钮、输入框、下拉框 |
| `RadiusChip` | 6 | 筛选 chip、徽标 |
| `RadiusListItem` | 6 | 列表项悬停/选中背景 |
| `RadiusCard` | 8 | 卡片、附件 chip |
| `RadiusDialog` | 8 | 对话框、浮出层 |
| `StrokeThickness` | 1 | 控件描边 |

全局统一使用圆角：列表项选中、按钮、输入框、卡片均为圆角；头像使用圆形，不使用直角面板。

- 列表项不使用阴影；层级靠底色与 1px 分隔线。
- 阴影只用于对话框、浮出菜单、拖拽预览。
- 浮出层阴影使用 Fluent 的 `ThemeShadow` 等价资源，不手写多层 `BoxShadow`。

### 2.5 动效

| 场景 | 时长 | 曲线 |
|---|---|---|
| 悬停/按下颜色 | 120ms | `CubicEaseOut` |
| 选中条/焦点环 | 120ms | `CubicEaseOut` |
| 面板展开/折叠 | 200ms | `CubicEaseOut` |
| 阅读区滑入（窄屏） | 250ms | `CubicEaseOut` |
| 对话框出现 | 200ms | `CubicEaseOut` |

- 遵守系统“减少动画”设置：开启后所有非必要动效降为 0ms，仅保留透明度切换。
- 不使用弹跳、缩放超过 1.02 的动效；邮件客户端不应“活泼”。

### 2.6 图标

**来源**：Microsoft Fluent UI System Icons（MIT），通过 `FluentIcons.Avalonia` 渲染，禁止手绘 Path。

| 尺寸 | 用途 |
|---|---|
| 16 | 行内状态（附件、旗标、未读） |
| 20 | 文件夹、命令栏 |
| 24 | 空状态、对话框标题 |

| 语义 | 图标名 | 变体 |
|---|---|---|
| 应用/账户 | `Mail` | Filled |
| 收件箱 | `MailInbox` | Regular / Filled（选中） |
| 已发送 | `Send` | Regular |
| 草稿 | `Drafts` | Regular |
| 已删除 | `Delete` | Regular |
| 垃圾邮件 | `MailAlert` | Regular |
| 归档 | `Archive` | Regular |
| 文件夹 | `Folder` | Regular |
| 未读 | `Mail` | Filled |
| 旗标 | `Flag` | Regular / Filled（已标记） |
| 附件 | `Attach` | Regular |
| 搜索 | `Search` | Regular |
| 设置 | `Settings` | Regular |
| 同步 | `ArrowSync` | Regular |
| 添加 | `Add` | Regular |
| 账户 | `Person` | Regular |
| 展开/收起 | `ChevronDown` / `ChevronRight` | Regular |
| 返回 | `ChevronLeft` | Regular |
| 关闭 | `Dismiss` | Regular |
| 更多 | `MoreHorizontal` | Regular |
| 回复 | `ArrowReply` | Regular |
| 全部回复 | `ArrowReplyAll` | Regular |
| 转发 | `ArrowForward` | Regular |
| 外部打开 | `OpenInNewWindow` | Regular |
| 图片 | `Image` | Regular |
| 安全 | `Shield` | Regular |
| 警告 | `Warning` | Regular |
| 信息 | `Info` | Regular |
| 成功 | `CheckmarkCircle` | Regular |
| 离线 | `CloudOff` | Regular |
| 加载 | `SpinnerIos`（旋转） | Regular |

- 未读/已读用 `Mail` 的 Filled/Regular 区分，不用颜色单独承担语义。
- 图标与文字并排时，间距 8px；纯图标按钮必须有 `ToolTip` 与 `AutomationProperties.Name`。
- 应用图标从官方 `Mail` 图标导出多尺寸 ICO，见 `PLAN.md` 3.3。

---

## 3. 信息架构

```
MailDog
├── 主窗口
│   ├── 系统标题栏（由 Windows 绘制）
│   ├── 文件夹栏
│   │   ├── 账户列表（纵向排列）
│   │   ├── 收藏（收件箱、旗标邮件、草稿）
│   │   ├── 文件夹树（按账户分组）
│   │   └── 添加账户（顶部） / 同步（“文件夹”标题行右侧） / 设置（左下角，仅图标）
│   ├── 邮件列表
│   │   ├── 搜索框
│   │   ├── 筛选（全部 / 未读 / 已标记 / 有附件）
│   │   ├── 列表项
│   │   └── 滚动到底部自动加载（底部细进度条）
│   └── 阅读区
│       ├── 命令栏（已读、旗标、显示图片）
│       ├── 邮件头（主题、发件人、收件人、时间）
│       ├── 正文（HTML / 纯文本降级）
│       └── 附件条
├── 设置页（主窗口内）
│   ├── 账户
│   ├── 外观
│   ├── 通知
│   ├── 存储
│   └── 关于
├── 添加账户页（主窗口内）
│   ├── 选择服务商（Outlook / QQ）
│   ├── 凭据输入
│   └── 登录进度
└── 托盘菜单
    ├── 打开 MailDog
    ├── 立即同步
    ├── 账户状态
    └── 退出
```

---

## 4. 主窗口布局

### 4.1 线框图（≥1280px）

```
┌───────────────────────────────────────────────────────────────────────────────┐
│ MailDog（系统标题栏）                                        ─  □  ✕          │
├────────────────┼──────────────────────────────┼───────────────────────────────┤
│ 账户        ⌄  │ 收件箱            12 未读    │  项目周报                     │
│  ● Outlook     │ 搜索邮件、发件人…            │  ───────────────────────────  │
│  ● QQ          │ [全部] [未读] [已标记] [附件]│  (A) Alice Chen              │
│                │ ┌──────────────────────────┐ │      alice@example.com  10:24│
│                │ │● Alice Chen   10:24  📎  │ │                              │
│ 收藏           │ │  项目周报                 │ │  收件人：我  ⌄                │
│  ✉ 收件箱  12  │ │  本周进展与下周计划…      │ │ ─────────────────────────────│
│  ⚑ 旗标邮件 3  │ ├──────────────────────────┤ │ [✓已读] [⚑旗标] [🖼图片] [⋯] │
│  ✎ 草稿    2   │ │  Bob Li       09:02      │ │                               │
│                │ │  会议纪要                 │ │ ─────────────────────────────│
│ 文件夹         │ │  附件：Q3 预算.xlsx…      │ │                               │
│  ▸ 工作        │ ├──────────────────────────┤ │   邮件正文（净化后的 HTML）   │
│  ▸ 项目        │ │  Carol        昨天       │ │                               │
│  ▸ 账单        │ │  你的订单已发货           │ │   段落、列表、表格、引用      │
│                │ └──────────────────────────┘ │                               │
│                │                              │ │  ┌─────────────────────────┐  │
│                │                              │ │  │ 📎 Q3 预算.xlsx  1.2 MB │  │
│                │                              │ │  └─────────────────────────┘  │
└────────────────┴──────────────────────────────┴───────────────────────────────┘
      240px                    360px                        自适应（≥480px）
```

### 4.2 尺寸规则

| 区域 | 默认 | 最小 | 最大 | 可调整 |
|---|---|---|---|---|
| 文件夹栏 | 240 | 64（折叠为图标栏） | 320 | 是（分隔条） |
| 邮件列表 | 360 | 300 | 480 | 是（分隔条） |
| 阅读区 | 剩余 | 480 | — | 随窗口 |
| 系统标题栏 | 由系统决定 | — | — | 否 |

- 分隔条 1px，命中区域 8px；悬停时变为强调色。
- 折叠文件夹栏时只保留图标（64px），账户与文件夹通过 `Flyout` 展开。
- 窗口最小尺寸 800×560；默认 1280×800；启动时恢复上次位置与尺寸（校验是否在可见屏幕内）。

### 4.3 响应式断点

| 宽度 | 布局 |
|---|---|
| ≥1280 | 三栏：文件夹 240 + 列表 360 + 阅读区 |
| 1024–1279 | 文件夹折叠为 64 图标栏；列表 340；阅读区自适应 |
| 800–1023 | 文件夹 64 + 列表；阅读区变为右侧 `SplitView` 浮层（宽 480，遮罩 32% 黑） |
| <800 | 不允许；窗口最小宽度 800 |

- 浮层模式按 `Esc` 或点击遮罩关闭；关闭后焦点回到原列表项。
- 搜索框固定在邮件列表顶部，随列表宽度自适应，不折叠。

### 4.4 背景与材质

- Windows 11：v1 使用不透明 `BgBase`，保证对比度与渲染确定性；Mica 作为后续视觉增强，不改变布局。
- Mica 不可用：回退 `BgBase` 纯色；不做半透明模糊降级。
- 深色模式下同样使用 Mica（Dark），不做“纯黑 OLED 特调”。

---

## 5. 组件规格

### 5.1 标题栏

- 使用 Windows 原生标题栏，由系统绘制应用图标、标题与最小化/最大化/关闭按钮。
- 不扩展客户区，不使用 `ExtendClientAreaToDecorationsHint`，也不自定义拖动区域。
- 标题栏明暗随应用主题同步：`DwmSetWindowAttribute` 的 `DWMWA_USE_IMMERSIVE_DARK_MODE`。
- Windows 11 22000+ 同时设置 `DWMWA_CAPTION_COLOR` / `DWMWA_TEXT_COLOR`，标题栏颜色跟随当前背景色与明暗。
- 搜索、同步、设置不再放在标题栏：搜索框位于邮件列表顶部，设置按钮（仅图标、整行宽）位于文件夹栏左下角，同步按钮位于“文件夹”标题行右侧。

- 同步按钮旋转动画只在同步中显示；完成后恢复 `ArrowSync`，不弹 toast。
- 账户异常时同步按钮旁显示 `Warning` 图标，点击打开账户状态浮出层。

### 5.2 文件夹栏

**账户列表（顶部）**

- 多个账户纵向排列，不使用下拉；项高 48，头像 28 + 名称 13 + 邮箱 12（次要色）+ 同步状态 11（强调色）+ 右侧状态点。
- 没有账户时，列表顶部显示一个与账户项同宽同高的“添加账户”入口（加号头像 + 标题 + 说明），占满侧边栏宽度。
- 选中态与文件夹项一致，使用强调色背景；超过 4 个账户时列表内部滚动（最大高度 200）。
- 状态点：`已连接`（成功色）、`需重新登录`（警告色）、`离线/同步失败`（灰色）。
- 同步状态只显示在账户项内（名称/邮箱下方的第三行），不占用独立的状态条。

**收藏区**

- 标题“收藏”使用 Caption/次要色，左内边距 16。
- 固定项：收件箱、旗标邮件、草稿。未读数以右侧徽标显示。
- 徽标：最小宽 20、高 16、圆角 6；有未读时强调色底 + 白字；0 时不显示。

**文件夹树**

- 标题“文件夹”；按账户分组；支持展开/收起（`ChevronRight` 旋转 90°）。
- 项高 36；左内边距 12；图标 20；文字 14；右侧未读数。
- 悬停：`BgHover` 圆角 6；选中：`AccentTint` + 图标强调色 + 左侧 3px 强调条（圆角 2）。
- 键盘：`↑/↓` 移动，`→` 展开，`←` 收起，`Enter` 打开。
- 文件夹加载失败：该项显示 `Warning` 图标，点击重试。

**折叠态（64px）**

- 只显示账户头像、收件箱、旗标、草稿、文件夹图标；`ToolTip` 显示名称与未读数。
- 底部 `ChevronRight` 按钮展开完整栏。

### 5.3 邮件列表

**头部**

- 标题：当前文件夹名称，Title/20；右侧未读数（Caption/次要色）。
- 搜索框：位于标题下方，宽度随列表自适应，高 32，占位文字居中；`Ctrl+F` 聚焦。
- 筛选 chips：全部 / 未读 / 已标记 / 有附件；选中态强调色底 + 白字；未选中透明底 + 描边。
- 右侧 `MoreHorizontal`：按日期/发件人排序（v1 仅日期）。

**列表项（76px）**

```
┌────────────────────────────────────────────────────────┐
│ ●  Alice Chen                        10:24   📎  ⚑    │  20
│    项目周报                                            │  20
│    本周进展与下周计划，附件为 Q3 预算…                 │  16
└────────────────────────────────────────────────────────┘
```

| 元素 | 规格 |
|---|---|
| 未读条 | 3px 强调色竖条，占用左侧 12px 固定槽位，垂直居中 |
| 头像 | 32px 圆形，首字母（中文取姓，英文取首字母），底色由发件人地址哈希到 6 色 Fluent 色板，文字白色 12/600 |
| 发件人 | 14/20；未读 600，已读 400；最大宽度 160，尾部省略 |
| 时间 | 12/16 次要色；今天显示 `HH:mm`，昨天显示“昨天”，本年显示 `M月d日`，跨年显示 `yyyy/M/d` |
| 主题 | 14/20；未读 600，已读 400；单行省略 |
| 摘要 | 12/16 次要色；单行省略；不包含引用历史 |
| 状态图标 | 16px：附件 `Attach`、旗标 `Flag`（Filled，强调色）；最多 2 个 |
| 分隔线 | 1px `StrokeSubtle`，距左 60（头像右侧对齐） |

**状态**

| 状态 | 表现 |
|---|---|
| 悬停 | 背景 `BgHover`；时间保持不变 |
| 选中 | 背景 `AccentTint`；左侧 3px 强调条；不再显示悬停底色 |
| 键盘焦点 | 2px 强调色焦点环，内缩 2px |
| 未读 | 发件人/主题 600；左侧未读点 |
| 已读 | 常规字重；无未读点 |
| 加载中 | 6 行骨架：头像圆 + 两条 12px 圆角条；1.2s 透明度呼吸；减少动效时静态 |
| 空 | 居中图标 `Mail` 48 + “这个文件夹没有邮件” + 次要说明 |
| 搜索无结果 | `Search` 48 + “没有找到匹配的邮件” + “清除搜索”按钮 |
| 加载更多 | 不显示按钮；滚动接近底部时自动加载，底部显示 2px 进度条 |

**虚拟化**

- 使用 `ListBox` + 默认虚拟化；10000 条滚动不掉帧。
- 列表项模板全部编译绑定；不为每行创建 WebView 或图片解码。
- 滚动时延迟加载头像颜色计算（纯字符串哈希，无 IO）。

### 5.4 阅读区

**命令栏（48px）**

| 按钮 | 图标 | 行为 | v1 |
|---|---|---|---|
| 标记已读/未读 | `Mail` / `MailRead` | 切换并同步 IMAP | ✅ |
| 旗标 | `Flag` | 切换 `\Flagged` | ✅ |
| 显示图片 | `Image` | 切换 `img-src` 策略并重渲染 | ✅（有远程图片时可见） |
| 回复 / 全部回复 / 转发 | `ArrowReply` 等 | 打开撰写窗口 | v1.1（禁用 + ToolTip） |
| 更多 | `MoreHorizontal` | 复制主题、查看邮件详情 | ✅（仅复制主题/详情） |

- 图标按钮 32×32，图标 20；带文字的主操作使用 32 高、左图标 20 + 文字 14。
- 按钮组之间用 1px、16 高分隔线。
- 禁用按钮不隐藏，ToolTip 说明“将在后续版本提供”。

**邮件头**

- 主题：Title/20/600，最多 3 行，超出省略；可选中复制。
- 发件人行：头像 32 + 名称 14/600 + `<email>` 12/次要色 + 右侧时间 12/次要色。
- 收件人：默认“收件人：我”，点击 `ChevronDown` 展开完整 To/Cc；展开后 `ChevronUp`。
- 底部 1px 分隔线，距左右 24。

**正文**

- 内边距 24；最大宽度 900，居中；背景 `BgLayer`。
- HTML 由 `NativeWebView` 渲染；正文放在浅色“纸张”卡片中，卡片外使用阅读区背景色，保证深色下文字仍可读。
- 只给 `table` 限制最大宽度，不注入单元格边框与内边距，保留邮件自身表格样式。
- 纯文本降级：`SelectableTextBlock`，14/22，保留换行；代码块用 `Cascadia Mono` 13/20。
- 顶部提示条（仅在需要时出现）：
  - 图片被拦截：“为保护隐私，远程图片已阻止。[显示图片]”
  - 内容截断：“邮件内容较大，已截断。[以纯文本查看全文]”
  - 渲染降级：“HTML 渲染失败，已切换为纯文本。[重试]”
- 提示条 40px，`WarningTint` 背景，左侧 `Info`/`Warning` 16 图标，右侧文本按钮。

**附件条**

- 位于邮件头下方、正文上方，距上 8；`WrapPanel` 排列。
- 附件 chip：高 40，圆角 8，描边 1px；`Attach` 16 + 文件名 14（最大 220，省略）+ 大小 12 次要色。
- v1 不可下载：悬停显示 ToolTip“附件下载将在后续版本提供”；不显示下载按钮，避免死路。

**空状态**

- 未选择邮件：居中 `Mail` 48 + “选择一封邮件开始阅读” + 次要说明“使用 ↑ ↓ 浏览，Enter 打开”。
- 账户未添加：`Mail` 48 + “添加你的第一个邮箱账户” + 主按钮“添加账户”。

### 5.5 状态横幅（StatusBanner）

用于全局错误/离线/认证过期，位于内容区顶部、三栏之上。

| 级别 | 背景 | 图标 | 示例 |
|---|---|---|---|
| Info | `AccentTint` | `Info` | “正在同步 2 个账户…” |
| Warning | `WarningTint` | `Warning` | “QQ 账户离线，正在重试” |
| Error | `DangerTint` | `Warning` | “Outlook 登录已过期” + “重新登录” |

- 高 40；左内边距 16；右侧操作按钮 + 关闭按钮。
- 同一级别同时只显示一条；新消息替换旧消息。
- 错误横幅不自动消失；信息类 5s 后自动消失。

### 5.6 设置页与关于页（主窗口内）

> 设置不打开独立弹窗：在主窗口内容区显示整页覆盖层，左上角返回邮件。

- 覆盖层铺满整个内容区（宽高自适应窗口），使用 `BgBase`，与欢迎页、添加账户页使用同一背景，材质/不透明度与其他页面一致。
- 左侧导航固定 220（外观 / 通知 / 阅读 / 同步；底部“添加账户”“关于”），铺满整个高度；右侧单页纵向滚动，底部为自动保存状态提示（不设保存按钮）。
- 从设置进入添加账户时，返回键回到设置；从欢迎页进入时返回到邮件列表。
- 内容区不设固定宽度：随窗口拉伸，窄窗口自动收缩，仅保留 32px 内边距。
- 所有设置项修改后自动保存（500ms 防抖），底部提示“已自动保存”或失败原因。

**外观**

- 主题：跟随系统 / 浅色 / 深色，使用横向单选项，实时应用。
- 背景材质：无 / 云母 / 亚克力，使用横向单选项。云母需要 Windows 11；不可用时自动回退纯色。
- 背景颜色：8 个颜色块（深/浅主题各一套），点击即应用；末尾一个渐变自定义色块，打开 `ColorView` 取色器（仅色带与色板，不显示 HEX / RGB 输入）。
- 背景不透明度：40–100%，始终可用；无材质且低于 100% 时窗口透出桌面，启用材质时控制材质强度。
- 面板颜色由背景色派生，文字与描边按背景明度自动切换，保证对比度。

**通知**

- 新邮件通知开关（默认开）。
- 通知声音开关（默认开）。
- 通知铃声：默认 / 邮件 / 提醒 / 提示，使用横向单选项；关闭声音时选项禁用。

**阅读**

- 渲染方式：两个互斥复选框（全量渲染 HTML / 纯文本），默认 HTML；纯文本模式下缺少纯文本正文时，从 HTML 提取纯文本而不是回退渲染 HTML。
- HTML 渲染保留邮件自带的安全内联样式与 `<style>` 样式表（剥离 `url()`、`@import`、`expression()` 等危险声明），以尽量还原原始排版。
- 打开邮件时标记为已读（默认开）。
- 默认显示远程图片（默认关）。

**同步**

- 同步间隔：1 / 5 / 15 / 30 / 60 分钟，默认 1 分钟，使用横向单选项。

**关于（当前窗口内的模态卡片）**

- 应用 logo、名称、版本、.NET 运行时、Avalonia / MailKit / SQLite。
- 项目主页按钮；图标许可说明（Fluent UI System Icons，MIT）。
- 从设置页底部“关于”打开，显示在主窗口中央的半透明遮罩上，关闭后回到原页面。

### 5.7 添加账户（当前窗口内的模态卡片）

> 添加账户不打开新窗口：在主窗口中央显示模态卡片，背后保留原页面，遮罩不可点击。

- 宽 560，`RadiusCard` 8，标题“添加账户”，左上角返回。
- **步骤 1：选择服务商**
  - 两张卡片（Outlook / QQ），高 96，图标 32 + 名称 + 一句说明。
  - Outlook：Outlook.com / Microsoft 365。
  - QQ：QQ 邮箱 / Foxmail。
- **步骤 2：输入凭据**
  - Outlook：邮箱（可选，用于 `login_hint`）+ 主按钮“使用浏览器登录”。
  - QQ：邮箱 + 授权码 + “如何获取授权码？”链接（打开 QQ 邮箱帮助页）。
  - 输入框下方 12px 辅助说明；错误就地显示，红色 12px。
- **步骤 3：登录进度**
  - Outlook：`SpinnerIos` 20 + “已在浏览器中打开登录页面” + “完成登录后此窗口会自动更新” + “取消”。
  - 超时 5 分钟；失败显示错误码 + “重试”。
- **成功**：显示 `CheckmarkCircle` + “账户已添加”，1.5s 后自动关闭并选中收件箱。
- 键盘：`Enter` 提交，`Esc` 取消；焦点初始在第一个输入框。

### 5.8 托盘菜单

- 原生 Win32 菜单，跟随应用主题（跟随系统 / 浅色 / 深色），而不是固定使用系统默认。
- 第一项加粗：“打开 MailDog”。
- “立即同步”在同步中禁用并显示“正在同步…”。
- “账户状态”子菜单：每账户一行，显示 `● 名称 — 已连接 / 需重新登录 / 离线`；点击打开设置账户页。
- 分隔线 + “退出”。
- 悬停 ToolTip：`MailDog — 3 个账户，12 封未读`。

### 5.9 通知

- 使用 Windows 气泡通知（`Shell_NotifyIcon` + `NIF_INFO`）。
- 内容：标题“新邮件 — Alice Chen”，正文主题（最多 80 字符）。
- 同一账户 30s 内最多 1 条；批量到达时合并为“Alice 等 5 封新邮件”。
- 点击通知：打开/聚焦 UI，选中对应邮件。
- 免打扰/通知关闭时不弹气泡，仅在托盘图标上显示未读状态。

---

## 6. 关键流程

### 6.1 首次启动

1. UI 启动，连接 Engine；无账户。
2. 显示空状态：`Mail` 48 + “添加你的第一个邮箱账户” + “添加账户”。
3. 点击进入添加账户页；完成认证后，Engine 开始首次同步。
4. 列表显示骨架屏；同步完成后显示最近 50 封。
5. 若同步失败：状态横幅 + 空列表说明 + “重试”。

### 6.2 添加 QQ 账户

1. 选择 QQ → 输入邮箱与授权码。
2. “如何获取授权码？”打开 QQ 邮箱设置页（系统浏览器）。
3. 点击“连接”：按钮进入加载态（`SpinnerIos` 16 + “正在验证…”）。
4. 成功：Engine 保存账户，返回 `accountChanged`；页面显示成功并返回邮件。
5. 失败：输入框下方显示 `错误：授权码无效或 IMAP 未开启`；提供“重新输入”和帮助链接。
6. 不在 UI 内存中保留授权码；提交后立即清空输入框。

### 6.3 添加 Outlook 账户

1. 选择 Outlook → 可填邮箱 → “使用浏览器登录”。
2. Engine 打开系统浏览器；页面切换到“等待浏览器登录”，显示取消。
3. 用户在浏览器完成登录/MFA；Engine 捕获回调、换取 token。
4. 成功：`authState=connected`；页面成功态并返回邮件；开始同步。
5. 用户取消/超时：`authState=failed`；页面显示原因 + “重试”。
6. 用户在浏览器中关闭页面但未完成：5 分钟后超时，提示“未完成登录”。

### 6.4 阅读邮件与显示图片

1. 点击列表项 → 阅读区显示邮件头 + 骨架（正文加载中）。
2. Engine 返回正文 → UI 净化 → WebView 渲染。
3. 若有远程图片且默认拦截：正文顶部提示条“远程图片已阻止”。
4. 点击“显示图片” → 重新构建文档（`img-src https:`）→ 重新导航；提示条消失。
5. 渲染失败 → 自动切纯文本 + 提示条“HTML 渲染失败”。

### 6.5 离线与认证过期

- **离线**：状态横幅“网络不可用，正在重试”；列表显示已缓存邮件，正文未缓存的显示“离线，无法加载正文”。
- **Outlook token 过期**：Engine 自动刷新；刷新失败 → 横幅“Outlook 登录已过期 [重新登录]”；点击打开添加账户/重新登录流程。
- **QQ 授权码失效**：横幅“QQ 邮箱认证失败 [更新授权码]”；点击打开设置账户页，输入新授权码。

### 6.6 新邮件

1. Engine IDLE/轮询发现新邮件 → 写入 SQLite → 发 `newMail`。
2. UI 已打开：列表顶部插入（若当前在收件箱），未读数 +1；不自动选中。
3. UI 未打开：托盘气泡；托盘图标叠加未读点（后续）。
4. 点击气泡：启动/聚焦 UI → 选中该邮件。

---

## 7. 交互规范

### 7.1 键盘

| 快捷键 | 行为 |
|---|---|
| `Ctrl+F` | 聚焦搜索框 |
| `Esc` | 清空搜索 / 关闭浮层 / 取消对话框 |
| `F5` | 同步当前账户 |
| `Ctrl+R` | 同步当前账户 |
| `Ctrl+,` | 打开设置 |
| `↑` / `↓` | 列表项移动（不打开） |
| `J` / `K` | 同 `↓` / `↑`（Vim 风格） |
| `Enter` | 打开选中邮件（焦点移入阅读区） |
| `Backspace` | 焦点返回列表 |
| `U` | 切换已读/未读 |
| `S` | 切换旗标 |
| `Tab` / `Shift+Tab` | 焦点顺序：文件夹 → 列表 → 阅读区命令栏 → 正文 |
| `Ctrl+Q` | 退出 UI（Engine 继续驻留） |

- 焦点进入阅读区后，`PageUp/PageDown` 滚动正文；`Esc` 返回列表。
- 所有快捷键在输入框聚焦时不生效（除 `Esc`）。

### 7.2 鼠标与菜单

- 单击列表项：选中并加载正文。
- 双击列表项：在独立窗口中打开（v1.1；v1 双击等同单击）。
- 右键列表项：标记已读/未读、旗标、复制主题、查看详情。
- 右键正文：仅保留“复制”和“全选”；屏蔽 WebView 默认菜单。
- 链接：左键交系统浏览器；不打开新窗口。
- 分隔条拖动：实时调整；双击恢复默认宽度。

### 7.3 选择与焦点

- 列表为单选；`Ctrl+单击`/`Shift+单击` v1 不支持。
- 选中项在列表失焦时仍保留 `AccentTint`，但降低饱和度（60% 透明度）。
- 焦点环始终可见；鼠标点击不隐藏焦点环（`:focus-visible` 语义由 Avalonia 伪类实现）。
- 切换文件夹时保留该文件夹上次选中的邮件（按 `folderId` 记忆）。

### 7.4 加载与反馈

- 所有网络操作 >300ms 才显示加载指示，避免闪烁。
- 正文加载显示 3 行骨架，不显示全屏 Spinner。
- 操作成功不弹 toast（除账户添加）；失败必须有横幅或就地错误。
- 按钮点击后禁用 500ms，防止重复提交。

---

## 8. 可访问性

| 项 | 要求 |
|---|---|
| 对比度 | 正文 ≥ 4.5:1；大字号 ≥ 3:1；禁用态除外 |
| 焦点 | 所有可交互元素有 2px 可见焦点环，内缩 2px 或外扩 2px |
| 键盘 | 全流程无需鼠标；对话框焦点陷阱；`Esc` 可退出 |
| 屏幕阅读器 | 每个图标按钮有 `AutomationProperties.Name`；列表项提供“发件人、主题、时间、未读”组合名称；新邮件通过 LiveRegion 播报 |
| 语义 | 标题层级正确；列表使用 `ListBox`；对话框使用 `Window`/`ContentDialog` 语义 |
| 动效 | 尊重系统“减少动画”；不闪烁超过 3 次/秒 |
| 缩放 | 支持 100%–200% 系统缩放；不出现文字截断或重叠 |
| 高对比度 | 提供 `HighContrast` 主题字典；描边与焦点环使用系统色 |
| 触控 | 最小命中区域 32×32；列表项 76 高，满足触控 |

---

## 9. 空、错、加载状态总表

| 场景 | 视觉 | 主操作 |
|---|---|---|
| 无账户 | `Mail` 48 + 标题 + 说明 | 添加账户 |
| 文件夹为空 | `Mail` 48 + “这个文件夹没有邮件” | 无 |
| 搜索无结果 | `Search` 48 + “没有找到匹配的邮件” | 清除搜索 |
| 列表加载中 | 6 行骨架 | 无 |
| 正文加载中 | 3 行骨架 | 无 |
| 正文渲染失败 | 纯文本 + Warning 横幅 | 重试 |
| 离线 | Warning 横幅 | 重试 |
| 认证过期 | Error 横幅 | 重新登录 |
| Engine 未启动/连接失败 | 全窗口错误页 + `Warning` 48 | 重试 / 查看日志 |
| WebView2 缺失 | Info 横幅 + 纯文本正文 | 安装 WebView2 |
| 数据库损坏 | 全窗口错误页 | 打开数据目录 / 重建缓存 |

**Engine 连接失败页**（独立状态，不显示三栏）：

```
┌──────────────────────────────────────────┐
│                                          │
│                 ⚠                        │
│        无法连接到 MailDog 后台            │
│   后台进程可能未启动或已退出。            │
│                                          │
│        [ 重试 ]   [ 查看日志 ]            │
│                                          │
└──────────────────────────────────────────┘
```

---

## 10. Avalonia 实现映射

### 10.1 主题资源

```
Themes/
├── Tokens.axaml          # 颜色/圆角/间距/字号语义令牌
├── Controls.axaml        # Button/TextBox/ListBoxItem 覆盖样式
├── MessageList.axaml     # 列表项 DataTemplate 与状态样式
└── Icons.axaml           # 图标尺寸与默认变体
```

`Tokens.axaml` 使用 `ThemeDictionaries`：

```xml
<ResourceDictionary.ThemeDictionaries>
  <ResourceDictionary x:Key="Light">
    <Color x:Key="MailDogColorBgBase">#F3F3F3</Color>
    <SolidColorBrush x:Key="MailDogBrushBgBase" Color="{StaticResource MailDogColorBgBase}" />
    <!-- ... -->
  </ResourceDictionary>
  <ResourceDictionary x:Key="Dark"> ... </ResourceDictionary>
  <ResourceDictionary x:Key="HighContrast"> ... </ResourceDictionary>
</ResourceDictionary.ThemeDictionaries>
```

- 只覆盖必要的 Fluent 资源（`SystemAccentColor`、`TextFillColorPrimaryBrush` 等），其余继承 `FluentTheme`。
- 不在 View 中写死颜色；所有颜色通过 `{DynamicResource MailDogBrush...}`。
- `RequestedThemeVariant` 由 `ThemeService` 设置，持久化到 Engine 设置。

### 10.2 控件映射

| 设计元素 | Avalonia 实现 |
|---|---|
| 窗口 | `Window`，使用系统原生标题栏；标题栏明暗通过 `DwmSetWindowAttribute` 与应用主题同步 |
| 三栏 | `Grid` + 两个 `GridSplitter` |
| 文件夹树 | `TreeView`（或分组 `ListBox`，v1 用 `TreeView`） |
| 邮件列表 | `ListBox` + `VirtualizingStackPanel`（默认）+ 编译绑定 `DataTemplate` |
| 搜索框 | `TextBox`，位于邮件列表顶部，占位文字居中 |
| 图标 | `FluentIcons.Avalonia` 的 `FluentIcon`；尺寸 16/20/24 |
| 头像 | `Border`（CornerRadius=16）+ `TextBlock` 首字母 |
| 徽标 | `Border` + `TextBlock`，圆角 6 |
| 筛选 chip | `ToggleButton` + 自定义样式 |
| 命令栏按钮 | `Button` + `StackPanel`（图标 + 文本） |
| 阅读区 HTML | `NativeWebView`（复用单实例） |
| 纯文本降级 | `SelectableTextBlock` |
| 状态横幅 | 自定义 `StatusBanner`（`Border` + 图标 + 文本 + 操作） |
| 设置页 | 主窗口内容区整页覆盖；关于 / 添加账户为当前窗口内的模态卡片 |
| 添加账户 | `Window`（模态）+ 步骤切换 `ContentControl` |
| 浮出账户菜单 | `Flyout` + `ListBox` |
| 骨架屏 | `Border` + `Animation`（透明度 0.4→0.8，1.2s 往复） |
| 旋转加载 | `FluentIcon` + `RotateTransform` 动画（1s 线性） |

### 10.3 编译绑定示例

```xml
<DataTemplate x:DataType="vm:MailSummaryViewModel">
  <Grid ColumnDefinitions="Auto,*,Auto" Height="76" Margin="12,0">
    <Border Grid.Column="0" Width="32" Height="32" CornerRadius="16"
            Background="{Binding AvatarBrush}">
      <TextBlock Text="{Binding AvatarInitial}" />
    </Border>
    <StackPanel Grid.Column="1" Margin="12,0" VerticalAlignment="Center">
      <TextBlock Text="{Binding Sender}" FontWeight="{Binding SenderWeight}" />
      <TextBlock Text="{Binding Subject}" TextTrimming="CharacterEllipsis" />
      <TextBlock Text="{Binding Preview}" Classes="caption" />
    </StackPanel>
    <TextBlock Grid.Column="2" Text="{Binding TimeText}" Classes="caption" />
  </Grid>
</DataTemplate>
```

- 禁止 `{Binding}` 无 `x:DataType` 的反射绑定。
- 视图模型暴露最终显示字符串（时间格式、发件人权重），减少转换器与绑定逻辑。

### 10.4 主题与窗口行为

- `App.axaml` 中 `FluentTheme` + `DensityStyle="Compact"` + 合并 `Tokens.axaml`。
- 窗口背景使用 `TransparencyLevelHint`；若返回 `None`，回退 `MailDogBrushBgBase`。
- 使用系统原生标题栏；明暗随应用主题同步，不自定义非客户区。
- 关闭窗口：`ShutdownMode.OnMainWindowClose`；在 `Closing` 中保存窗口尺寸到 Engine 设置。

### 10.5 性能注意事项

- 列表项不绑定图片 URL；头像为纯色 + 文本。
- 正文切换使用代次号，避免快速切换时旧渲染覆盖新内容。
- WebView 只在阅读区可见时创建；首次选中邮件时延迟 1 帧创建，避免窗口启动卡顿。
- 骨架动画使用 `Transitions`/`Animation`，不使用每帧代码。

---

## 11. 验收清单

### 视觉

- [ ] Light/Dark 下所有文字对比度达标，无“灰底灰字”。
- [x] Mica / 亚克力材质：设置中可选，Windows 11 生效，失败回退纯色。
- [ ] 三栏在 1280/1100/900 宽度下无重叠、无横向滚动条。
- [ ] 未读/已读、选中/悬停/焦点四种状态可区分。
- [ ] 图标全部来自 Fluent UI System Icons，无手绘 Path。
- [ ] 空状态、错误状态、加载状态均有设计稿对应实现。

### 交互

- [ ] 键盘可完成“切换文件夹 → 选邮件 → 阅读 → 标记已读”全流程。
- [ ] `Esc` 在搜索/浮层/对话框中的行为符合规范。
- [ ] 列表快速滚动 10000 条无卡顿。
- [ ] 切换 20 封邮件后 WebView 内存不持续增长。
- [ ] 关闭窗口后 UI 进程退出，Engine 托盘仍在。

### 可访问性

- [ ] 所有图标按钮有可读名称。
- [ ] 200% 缩放无截断。
- [ ] 高对比度主题下焦点环与选中态可见。
- [ ] 减少动效设置生效。

### 文案

- [ ] 所有错误信息包含“发生了什么 + 可以做什么”。
- [ ] 不使用“未知错误”作为唯一信息；至少给出错误码。
- [ ] 中英文标点统一；中文文案使用全角标点。
