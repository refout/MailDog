# 界面统一方案：令牌、密度与布局（v1.1）

> 状态：**方案待批准**（尚未开始改代码）
> 日期：2026-11-18
> 关联：[UI-DESIGN.md](./UI-DESIGN.md)（现行为基准）、[PLAN.md](./PLAN.md)
> 落地后合并回 UI-DESIGN.md 的 2.1 / 2.2 / 2.3 / 2.4 / 4.4 / 5.x 章节

---

## 0. 一句话

把现在“每个区块自己决定颜色、每个控件自己决定高度”的写法，收敛成**三层结构 + 一套刻度 + 两档密度**：底色只属于列，颜色只来自令牌，控件高度只来自令牌，按钮只有四个变体。

## 1. 现状体检（2026-11-18 实测）

### 1.1 颜色有两个来源

| 来源 | 内容 | 问题 |
|---|---|---|
| `Themes/Tokens.axaml` | 21 个 `MailDogBrush*`，Light/Dark 各一套，全部十六进制 | 与 Fluent 的 `System*` 无关，控件和自绘面板各走一套 |
| `MainWindow.axaml.cs`（`ApplyPalette`） | 按用户背景色 + 透明度重算 Surface，文字/描边/悬停仍写死 | 与 Tokens 重复定义了 9 个值；换背景色后文字不重新定级 |

### 1.2 背景分给了“行”

- 设置页顶栏（56px）横跨整页，自身无底色只画下边框，压在“导航 `Subtle` + 内容 `Base`”两种底色之上 → 出现单独一条色带。
- 阅读区连续叠 `BgLayer` / `BgSubtle` 两层；列表栏自己又是 `BgLayer`；三栏靠 1px 描边补缝。
- 撰写页根节点铺 `BgLayer` + `Margin="24,18"`，与阅读区平铺层级不一致。

### 1.3 尺寸没有刻度

| 类别 | 去重 | 典型分布 |
|---|---|---|
| FontSize | 8 | 12×49、13×49、20×14、11×11、14×9、10.5×2、12.5×1、15×1 |
| CornerRadius | 10 | 8×14、6×7、4×4，另有 7、10、13、14、16、20、1.5 |
| Padding | 25 | `8,4`×14、`10,8`×7、`10,3`×7、`16`×6、`10,4`×5… |
| Margin | 36 | `0,0,8,8`×6、`0,0,16,4`×5、`0,0,0,8`×4… |
| 控件高度 | 15 | 同类控件 30 / 32 / 34 / 36 / 40 混用 |

### 1.4 按钮和输入控件

- 82 个 Button：59 个无 class（Fluent 默认），11 个 `.icon`（其中一个被改成 22×22），其余 `side-item` / `jump-item` / `date-field` / `calendar-day` / `account-add` / `accent` 各一套。
- 内联属性：`Padding` 15 种写法、`FontSize` 27 处（11/12）、`Height` 36×3 / 40×1 / 22×1。
- `TextBox` / `ComboBox` / `CheckBox` 共 30 个实例全部“未指定高度”，交给 Fluent 的 Compact 模板；而 `TextBox.single-line` 又单独写死 32 → 搜索框与下拉框不等高。

## 2. 四条硬规则

1. **底色只属于列**。窗口底、每个列容器、浮层可以有色；行、标题栏、工具条一律透明，只画 1px 分隔线。
2. **颜色只来自令牌**。XAML 与 C# 中不出现十六进制（`Tokens.axaml` 是唯一例外，且只允许写 Fluent 没有对应语义的值）。
3. **尺寸只来自令牌**。控件高度、行高、页面边距、圆角、字号都取 `MailDogSize*` / `MailDogType*`，视图里不写数字。
4. **一个控件类型一套规格**。按钮四种基础外观（默认/主色/轻量/图标）加危险态，各三档尺寸；输入类共用同一个高度令牌；导航项/列表项/字段触发器不再混进按钮体系。

## 3. 令牌体系

### 3.1 三层结构

```
FluentTheme.Palettes（只改 Accent）
   └─ System* 原始资源（SystemAccentColor / SystemBaseHighColor / SystemChrome*）
        └─ MailDogBrush* 语义令牌（本文定义）
             └─ Themes/Controls.axaml 组件样式
```

### 3.2 颜色令牌（22 个）

| 令牌 | 用途 | Light | Dark |
|---|---|---|---|
| `MailDogBrushSurfaceWindow` | 窗口底 | 动态：#F3F3F3 起 | #202020 起 |
| `MailDogBrushSurfacePane` | 列表/阅读列 | 比 Window 亮一档 → #FFFFFF | #272727 |
| `MailDogBrushSurfaceSubtle` | 侧栏/设置导航 | 比 Window 收一档 → #FAFAFA | #1F1F1F |
| `MailDogBrushSurfaceCard` | 正文卡、附件 chip、输入区 | 同 Pane，靠 1px 描边区分 | 同 Pane |
| `MailDogBrushSurfacePopup` | 菜单/浮层 | Pane 不透明版 | Pane 不透明版 |
| `MailDogBrushTextPrimary` | 正文/标题 | `SystemBaseHighColor` | 同 |
| `MailDogBrushTextSecondary` | 次要说明 | `SystemBaseMediumColor` | 同 |
| `MailDogBrushTextTertiary` | 时间、占位 | `SystemBaseMediumLowColor` | 同 |
| `MailDogBrushTextDisabled` | 禁用 | `SystemBaseLowColor` | 同 |
| `MailDogBrushTextOnAccent` | accent 上的字 | #FFFFFF | #0A0A0A |
| `MailDogBrushStrokeDefault` | 列分隔、控件描边 | `SystemChromeMediumLowColor` | 同 |
| `MailDogBrushStrokeSubtle` | 行内分隔 | `SystemChromeLowColor` | 同 |
| `MailDogBrushStrokeStrong` | 焦点/强调描边 | `SystemChromeMediumColor` | 同 |
| `MailDogBrushAccentBase` | 主色 | `SystemAccentColor` | 同 |
| `MailDogBrushAccentHover` | 悬停 | `SystemAccentColorDark1` | `SystemAccentColorLight1` |
| `MailDogBrushAccentPressed` | 按下 | `SystemAccentColorDark2` | `SystemAccentColorLight2` |
| `MailDogBrushAccentTint` | 选中底、提示条 | accent 10% | accent 14% |
| `MailDogBrushStatusDanger` | 错误文字 | #C42B1C | #FF99A4 |
| `MailDogBrushStatusDangerTint` | 错误条底 | #FDF3F4 | #3B1A1A |
| `MailDogBrushStatusSuccess` | 成功 | #0F7B0F | #6CCB5F |
| `MailDogBrushStatusWarning` | 警告 | #9D5D00 | #FCE100 |
| `MailDogBrushStatusWarningTint` | 警告条底 | #FFF9F0 | #3A3010 |

> 状态色 Fluent 无对应 `System*` 语义，是唯一允许在 Tokens 里写死的一组，集中在一处并注明原因。
> `System*` 资源名先按 Fluent 参考里的 7 个（Accent/BaseHigh/BaseMedium/BaseMediumLow/BaseLow/ChromeMediumLow/ChromeLow/ChromeMedium）落地，实施时逐个在 `FluentTheme` 资源里核对，不存在的用同级近似值并在注释里写明。

旧名 → 新名（机械替换表）：

```
BgBase→SurfaceWindow  BgLayer→SurfacePane   BgSubtle→SurfaceSubtle
Popup→SurfacePopup    Stroke→StrokeDefault StrokeSubtle→StrokeSubtle
Accent→AccentBase     AccentHover→AccentHover  AccentTint→AccentTint
Danger→StatusDanger   DangerTint→StatusDangerTint
Success→StatusSuccess Warning→StatusWarning  WarningTint→StatusWarningTint
Hover→FillHover（新增，悬停底色统一 5%/8% 透明）
```

### 3.3 尺寸与字体令牌

| 令牌 | 紧凑 | 标准 | 用途 |
|---|---|---|---|
| `MailDogSizeControlHeight` | 28 | 32 | 按钮、输入框、下拉框默认 |
| `MailDogSizeControlHeightSm` | 24 | 28 | 行内小按钮、chip |
| `MailDogSizeControlHeightLg` | 32 | 36 | 首屏主按钮、对话框按钮 |
| `MailDogSizeRowOneLine` | 32 | 36 | 侧栏项、菜单项 |
| `MailDogSizeRowTwoLine` | 56 | 64 | 邮件列表行 |
| `MailDogSizeCommandBar` | 40 | 48 | 阅读区命令栏、页顶栏 |
| `MailDogSizePagePadding` | 16 | 24 | 设置内容、撰写页 |
| `MailDogSizeSectionGap` | 12 | 16 | 分组间距 |
| `MailDogSizeIcon` | 16 | 16 | 常规图标 |
| `MailDogSizeIconLg` | 20 | 20 | 页顶栏返回、空状态 |
| `MailDogRadiusControl` | 6 | 6 | 按钮、输入框 |
| `MailDogRadiusCard` | 8 | 8 | 正文卡、浮层 |
| `MailDogRadiusPill` | 999 | 999 | 徽标、头像 |

字号（两档密度共用一套，仅行高随密度微调）：

| 角色 | 字号 | 字重 | 用途 |
|---|---|---|---|
| `MailDogTypeDisplay` | 28 | 600 | 欢迎页标题 |
| `MailDogTypeTitle` | 20 | 600 | 页顶栏标题、邮件主题 |
| `MailDogTypeBody` | 13 | 400 | 列表、按钮、正文标签 |
| `MailDogTypeBodyStrong` | 13 | 600 | 未读发件人、选中项 |
| `MailDogTypeCaption` | 12 | 400 | 时间、摘要、辅助说明 |

> 与 UI-DESIGN.md 2.2 的差异：正文由 14 → 13、Caption 由 12 保持不变、Subtitle 16 取消并入 Title 20。理由：现网实现已全面是 13，收敛到 13 比全量放大到 14 改动小、也贴近 Fluent 紧凑档。

### 3.4 动态背景（保留现有能力，但收敛作用面）

设置项保持不变：主题（跟随系统/浅/深）、材质（无/云母/亚克力）、背景颜色、不透明度（40–100）。

规则：

1. 背景色只派生 `SurfaceWindow / SurfacePane / SurfaceSubtle / SurfaceCard / SurfacePopup` 五个令牌的 RGB。
2. 文字、描边、accent、状态色不随背景色改，跟 `ThemeVariant` 走。
3. 对比度守卫：按 WCAG 相对亮度计算，正文/次要文字对当前 Surface 不足 4.5:1 / 3:1 时，表面不透明度自动抬到 92%（最低），并在设置页提示“当前背景与文字对比偏低，已自动提高不透明度”。
4. 材质只影响 Surface 的 alpha，不影响其他令牌；云母不可用时按现有逻辑回退纯色。

## 4. 组件规格

### 4.1 按钮：4 种基础外观 + 危险态，各 3 档尺寸

| 变体 | 底色 | 描边 | 文字 | 悬停 | 按下 |
|---|---|---|---|---|---|
| 默认（secondary） | `SurfaceCard` | `StrokeDefault` | `TextPrimary` | `FillHover` | 再加一档 |
| `.primary` | `AccentBase` | 无 | `TextOnAccent` | `AccentHover` | `AccentPressed` |
| `.subtle` | 透明 | 无 | `TextPrimary` | `FillHover` | `FillHover` + 1 档 |
| `.icon` | 透明 | 无 | `TextSecondary` | `FillHover` | 同上 |
| `.danger` | 透明 | 无 | `StatusDanger` | 危险色 8% 底 | 12% 底 |

尺寸（`sm` / 默认 `md` / `lg`）：高度取对应 token；水平内边距 8 / 12 / 16；图标 16；图标与文字间距 8；圆角 `RadiusControl`；字号 `TypeBody`。图标按钮为正方形（边长 = 高度），列表行内用 `sm`。

现有 class 的去向：

```
.accent        → .primary（保留 alias，内部并入）
.account-add   → 删除，改用 .subtle + 自定义内容模板
.side-item     → 改名 .nav-item（导航项，不属于按钮体系）
.jump-item     → 改名 .menu-item（菜单项）
.date-field    → 改名 .field-trigger（字段触发器）
.calendar-day  → 改名 .calendar-cell
```

### 4.2 输入类：一个高度令牌管到底

对 `TextBox`、`ComboBox`、`CheckBox`、`AutoCompleteBox`、`DatePicker` 统一：

```
MinHeight = {DynamicResource MailDogSizeControlHeight}
Padding   = 10,0（多行例外）
VerticalContentAlignment = Center
CornerRadius = {DynamicResource MailDogRadiusControl}
BorderThickness = 1
```

- 删除 `TextBox.single-line` 特例；单行就是默认。
- `ComboBox` 的下拉项高度 = `MailDogSizeRowOneLine`（现在写死 32 的 `MenuItem` 改为 token）。
- `CheckBox` 只设 `MinHeight`，不设固定 `Height`，避免裁切勾选框。
- 撰写页正文框例外：`MinHeight = 240`，`Padding = 14,12`，其余不动。

**判定**：同一工具栏里搜索框、下拉框、按钮三者实际像素高度必须相等，两档密度下都相等。

### 4.3 页面顶栏（PageHeader）

- 高度 `MailDogSizeCommandBar`，`Background=Transparent`。
- 只有下边框 1px `StrokeSubtle`；不带阴影、不铺色。
- 内容：左返回图标按钮（`lg` 图标）+ 标题 `TypeTitle`，内边距左右 `MailDogSizePagePadding`。
- **只出现在内容列顶部，不跨导航列**。设置页因此不再出现横贯两色的色带。

### 4.4 列容器

| 页面 | 列 | 底色 |
|---|---|---|
| 主窗口 | 侧栏 | `SurfaceSubtle` + 右边框 |
| 主窗口 | 列表 | `SurfacePane` + 右边框 |
| 主窗口 | 阅读 | `SurfacePane`，内部不再叠第二层底 |
| 设置 | 导航（220，通高） | `SurfaceSubtle` + 右边框 |
| 设置 | 内容 | `SurfaceWindow`，顶部 PageHeader |
| 撰写 | 单列（替换阅读列） | `SurfacePane`，内部字段用 `SurfaceCard` |
| 欢迎页 | 单列 | `SurfaceWindow` |

侧栏不再用 `Margin="8,12"` 把底色裁在内部：底色贴到列边界，内容用内边距。

### 4.5 列表项 / 导航项

- 导航项：高 `RowOneLine`，圆角 `RadiusControl`，图标 16 + 文本 `TypeBody`，选中 `AccentTint` + `BodyStrong`，悬停 `FillHover`。
- 列表行：高 `RowTwoLine`，三行结构（发件人 13 强 / 主题 13 / 摘要 12），内边距 12,8；选中态是行内 inset 高亮（左右各留 4px），不改整行底色，不出现“选中一条新色带”。

### 4.6 菜单与浮层

- 底色 `SurfacePopup`（不透明），圆角 `RadiusCard`，1px `StrokeDefault`，内边距 6。
- 菜单项高 `RowOneLine`，字号 `TypeBody`，圆角 `RadiusControl`。

## 5. 布局规范

| 项 | 值 | 说明 |
|---|---|---|
| 侧栏宽 | 240（窄窗 220） | 最小窗口 900 时收窄 |
| 列表宽 | 360（窄窗 320） | 星号列，不写死 |
| 设置导航宽 | 220 | 通高 |
| 窗口最小 | 900×600 | 现状保留 |
| 阅读区命令栏 | `CommandBar` | 粘顶，不随正文滚动 |
| 页面内边距 | `PagePadding` | 设置内容、撰写页、欢迎页统一 |
| 分组间距 | `SectionGap` | 设置分组之间 |

窄窗（<1000）时：侧栏 220、列表 320、阅读列自适应最小 380，低于 380 时阅读区让位给列表（保持现有行为，不新增模式）。

## 6. 文件结构（拆分）

> 下表的 `现 L…` 行号按 2026-11-19 的 `MainWindow.axaml`（1937 行）；并行改动合流后会漂移，拆分时以注释锚点为准（`<!-- 文件夹栏 -->` 等）。

| 新文件 | 现区段（锚点） |
|---|---|
| `SidebarView.axaml` | L49–352（`<!-- 文件夹栏 -->` 至 `<!-- 邮件列表 -->` 前） |
| `MailListView.axaml` | L353–755（含跳转/筛选/排序浮层） |
| `ReaderView.axaml` | L756–991 |
| `WelcomeView.axaml` | L1003–1033 |
| `SettingsView.axaml` | L1034 起设置区 |
| `AccountFormsView.axaml` | 设置区之后的添加账户/删除确认等覆盖页 |

```
src/MailDog.UI/
├── Themes/
│   ├── Tokens.axaml          # 颜色 + 尺寸 + 字体 + 两档密度资源
│   └── Controls.axaml        # 组件样式（从 App.axaml.Styles 迁出）
├── Controls/
│   ├── PageHeader.axaml      # 透明顶栏
│   ├── SidebarItem.axaml     # 导航项
│   ├── MailListRow.axaml     # 列表行
│   └── SettingsRow.axaml     # 设置行（标题 + 说明 + 控件）
├── Views/
│   ├── MainWindow.axaml      # 壳：三栏 + 页面切换，≤200 行
│   ├── SidebarView.axaml     # 现 L49–271
│   ├── MailListView.axaml    # 现 L272–674（含跳转/筛选/排序浮层）
│   ├── ReaderView.axaml      # 现 L675–880
│   ├── ComposeView.axaml     # 已独立，按新规格重排
│   ├── WelcomeView.axaml     # 现 L892–922
│   ├── SettingsView.axaml    # 现 L923 起设置区
│   └── AccountFormsView.axaml# 添加账户、账户管理、删除确认等覆盖页
└── Services/
    └── ThemeService.cs       # 密度/材质/背景色的应用与持久化（从 MainWindow.axaml.cs 抽出）
```

`MainWindow.axaml.cs` 里 `ApplyPalette` / `GetThemeBaseColor` / 窗口材质相关逻辑整体迁入 `ThemeService`；`MainWindow` 只保留窗口生命周期与视图切换。

## 7. 密度作为设置项

- 新增设置键 `density`，取值 `"compact"` / `"normal"`，默认 `"compact"`（升级后界面不跳）。
- 设置页“外观”新增一行“界面密度”，两个横向单选项：紧凑 / 标准；修改后立即生效并写入库。
- 应用方式：
  - `FluentTheme.DensityStyle` → `Compact` / `Normal`；
  - 同时切换 `MailDogSize*` 的取值（两套 `ResourceDictionary` 或由 `ThemeService` 逐键推送）。
- **待验证（0.5h spike）**：`FluentTheme.DensityStyle` 运行中修改是否会立即重建控件主题。若不会，则在 `Application.Styles` 中替换整个 `FluentTheme` 实例（保留 `Palettes` 与当前 `ThemeVariant`），切换瞬间接受一次重绘。

## 8. 实施步骤

> 前置：当前有并行 UI 改动在途（`MainWindow.axaml` 正在加“移动邮件”、`MoveTargetViewModel`）。**第 1 步之前先等这批提交并合并**，避免在同一文件上打架。

| 步 | 内容 | 产出 | 验证 |
|---|---|---|---|
| 1 | 写 `Themes/Tokens.axaml`（22 色 + 尺寸 + 字体 + 两档密度）和 `Themes/Controls.axaml`（按钮/输入类/导航项/列表项/浮层） | 令牌与样式落地，视图未动 | 编译通过；现有界面配色不回归（截图对比） |
| 2 | 密度 spike + 设置项（模型、存储、界面、`ThemeService`） | 两档可切 | 切换后控件高度确实变化；重启后保留 |
| 3 | 拆 `MainWindow` → 六个视图 + 四个部件；`PageHeader` 按 4.3 落地 | 文件拆分 | 功能不回归（列表/阅读/设置/撰写都能进） |
| 4 | 扫色值：视图里的 `Background/Foreground/BorderBrush` 全改令牌；删 `ApplyPalette` 里的写死值 | 颜色单源 | grep 0 命中 |
| 5 | 扫尺寸：字号/圆角/间距/高度按类替换；按钮映射到四变体三尺寸；剥内联属性 | 尺寸单源 | 工具栏三者等高；截图 |
| 6 | 设置页顶栏重做、撰写页去掉整页底、阅读区去第二层底 | 列归属规则落地 | 亮暗截图无横向色带 |
| 7 | 验收矩阵 + 文档回写 | 截图集 + UI-DESIGN 更新 | 见第 9 节 |

每步独立提交；第 3、4、5 步按“视图 × 类型”拆分小提交，便于 review 与回滚。

## 9. 验收清单

**自动检查（脚本，CI 只告警）**

- XAML 中十六进制字面量 = 0（`Tokens.axaml` 豁免）；CS 中十六进制 = 0。
- `Button` / `TextBox` / `ComboBox` 标签上不出现 `Height=`、`Padding=`、`FontSize=`、`CornerRadius=`。
- `Background=` 只出现在列容器（`SidebarView` / `MailListView` / `ReaderView` / `SettingsView` / `ComposeView` / `WelcomeView` 根节点）与浮层；行级元素出现即报错。
- FontSize 去重 ≤ 5（对应 Type 令牌）；CornerRadius 去重 ≤ 3（6 / 8 / 999）。

**人工与截图**

- 矩阵：亮/暗 × 纯色/云母/亚克力 × 不透明度 100/85/40 × 紧凑/标准。
- 同一视图在四个组合下逐张比对：无“两种白/两种灰”接缝、无跨列色带。
- 工具栏同行控件高度像素一致（搜索框、下拉框、按钮）。
- 对比度：正文 ≥ 4.5:1，次要 ≥ 3:1；自定义 6 组背景色抽样。
- 键盘：焦点环可见、Tab 顺序不乱、菜单关闭后焦点回原位。

## 10. 风险与回滚

| 风险 | 影响 | 处理 |
|---|---|---|
| 运行中换 `DensityStyle` 不生效 | 密度切换无效 | 走替换 `FluentTheme` 实例的兜底；第 2 步先 spike |
| 令牌改名触及 100+ 处引用 | 编译失败面大 | 脚本替换 + 一次编译；按旧→新表核对 |
| 动态背景叠加 alpha | 半透明下发灰/发糊 | 对比度守卫 + 表面不透明度下限 92% |
| WebView2 邮件正文白底 | 暗色下出现白卡 | 视为内容卡：加 1px `StrokeSubtle` + 圆角，明确“正文区”而非窗口底色；不改邮件样式 |
| 拆分文件与并行开发冲突 | 合并冲突 | 等并行提交合流后再开始第 3 步 |

## 11. 待决定 / 待验证

- [ ] 密度默认值：建议 `compact`（保持现状观感）。
- [ ] 是否本期加 HighContrast：建议本期不做，`ThemeDictionaries` 预留 `HighContrast` 键。
- [ ] `FluentTheme.DensityStyle` 运行时行为（第 2 步 spike 结论）。
- [ ] 字号统一到 13（而非 UI-DESIGN 里的 14）——需你确认，确认后同步改文档。
