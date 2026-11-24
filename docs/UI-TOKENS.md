# 界面统一方案：令牌、密度与布局（v1.2）

> 状态：**已实施**（2026-11-19）；第 1/2/4/5/6/7 步完成，第 3 步已完成 `SettingsView` 拆分，其余视图待拆
> 日期：2026-11-18（方案）/ 2026-11-19（落地）
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
| 默认（secondary） | `SurfaceSubtle` | `StrokeDefault` | `TextPrimary` | `FillHover` | 再加一档 |
| `.primary` | `AccentBase` | 无 | `TextOnAccent` | `AccentHover` | `AccentPressed` |
| `.subtle` | 透明 | 无 | `TextPrimary` | `FillHover` | `FillHover` + 1 档 |
| `.icon` | 透明 | 无 | `TextSecondary` | `FillHover` | 同上 |
| `.danger` | 透明 | 无 | `StatusDanger` | 危险色 8% 底 | 12% 底 |

尺寸（`sm` / 默认 `md` / `lg`）：高度取对应 token；水平内边距 8 / 12 / 16；图标 16；图标与文字间距 8；圆角 `RadiusControl`；字号 `TypeBody`。图标按钮为正方形（边长 = 高度），列表行内用 `icon.sm`（24×24）。

现有 class 的去向：

```
.accent        → .primary（视图里已全部替换，不再保留 alias）
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

## 12. 实施记录（2026-11-19）

### 12.1 已落地

| 项 | 实际结果 |
|---|---|
| 颜色单源 | `Tokens.axaml` 23 个语义色；视图与 C# 全部改用新名，旧 `MailDogBrushBg*` 别名已删除；`grep` 旧名 0 命中 |
| 尺寸单源 | 控件高度/行高/页边距/图标/圆角/字号全部走 `MailDogSize*`、`MailDogRadius*`、`MailDogType*` |
| 输入类等高 | `TextBox`、`ComboBox`、`CheckBox` 统一 `MinHeight=ControlHeight`、`Padding=10,0`、居中、圆角 6 |
| 按钮 | 74 个按钮的内联 `Padding`/`FontSize` 全部移除，统一由 `Button` 默认规格 + `.primary/.subtle/.danger/.icon/.sm/.lg` 承担；新增 `.toolbar` 只负责间距 |
| 密度 | 设置页“外观 → 界面密度”两档；`density` 入库；`ThemeService.ApplyDensity` 同时推送 Fluent `DensityStyle` 与尺寸令牌；重启保留 |
| 顶栏 | 新增 `Controls/PageHeader`（透明、仅 1px 下边框、返回 + 标题）；设置页改为导航列通高 + 内容列 `Auto/*/Auto`，顶栏不再横贯导航列 |
| 背景归属 | 侧栏 `SurfaceSubtle` / 列表与阅读 `SurfacePane` / 设置内容 `SurfaceWindow` + `SurfaceCard` 卡片；行、工具条透明 |
| 拆分 | 设置页整块（约 930 行）迁入 `Views/SettingsView.axaml(.cs)`，账户/发件箱/备份事件处理器同步迁入；`MainWindow.axaml` 由 1937 行降到约 1035 行 |
| 短页留白 | 通知、阅读、同步分区改为 `Border.settings-card` 卡片分组，并补一条产品说明；不再是大片无结构空白 |
| 阅读工具栏 | 11 个动作按钮改为 `WrapPanel + .icon.toolbar`；窄窗（916px）自动折成两行，不再右侧裁切 |

### 12.2 验证

- `dotnet build src/MailDog.slnx`：0 警告 0 错误。
- `dotnet test src/MailDog.slnx`：66 通过（Contracts 6 / Html 29 / Mail 31）。
- 真机 916×639：暗色收件箱、设置外观/通知/阅读/同步/账户管理（含“编辑账户”进入表单）、亮色设置页均正常；设置页顶部返回按钮回到收件箱。
- 密度切换后行高与控件高度变化，`density=standard` 入库并在重启后保留。

### 12.3 写信页富文本（2026-11-19）

| 项 | 实际结果 |
|---|---|
| 默认模式 | 正文改为 `MailDogRichEditor`（继承 `AvaloniaRichEditor.RichEditor` 1.3.0，MIT，纯 C#），启动即富文本；不再使用 `NativeWebView` + TipTap，不依赖 WebView2 |
| 工具栏 | 加粗、斜体、下划线、删除线、H1/H2/H3/正文、项目符号、编号、引用、链接、插入图片、撤销/重做；按钮统一 `.icon.toolbar`，选中态用 AccentTint；`GetCaretFormat()` 同步状态 |
| 标题与图片 | `RichEditor.SetHeading(1..3/0)` 作用于当前段落，不扩大到整篇；`AllowImages=True`，工具栏「插入图片」走系统文件选择器并调用 `InsertImageFromFileAsync()`，图片以 data URI 进入正文，出站清洗已放行 `data:image/*` |
| 链接 | 走编辑器内置 `Ctrl+K` 对话框；工具栏按钮由 `MailDogRichEditor.OpenLinkDialog()` 复用同一路径；标题、确定、取消、右键菜单链接项已注册中文 |
| Markdown | 复选框切换为 Markdown 源码 + 实时预览；源码用现有 `MarkdownRenderer`，HTML→Markdown 由新增 `HtmlMarkdownConverter` 负责；`~~删除线~~` 已补渲染 |
| 预览布局 | 宽度 ≥ 590 时左右分栏：编辑区 `*`、6px `GridSplitter`、预览像素列；预览初始 340、夹在 220–420，并为编辑区预留至少 380；拖动或窗口缩放都保留用户宽度。宽度 < 590 时上下堆叠（编辑 `*`、6px 分隔、预览 `0.45*`）。正文区为星号行，预览与编辑区都填满剩余高度 |
| 主题 | 编辑器 `DefaultForeground` / `CaretBrush` / `SelectionBrush` 走语义令牌；预览用显式 `BuildMarkdownPreviewHtml(bool dark)`，标题、列表、表格、代码、引用、链接颜色跟主题一致 |
| 数据 | `BodyHtml` 从 `SendMailRequest` / `SaveDraftRequest` 贯通到 `OutboxRecord.body_html`（schema v8）；`MailComposer` 输出 HTML + `HtmlTextConverter` 纯文本回退，出站 HTML 经 `HtmlSanitizer` 清洗；MIME 头 `X-MailDog-Format: html|markdown` |
| 引用 | 回复/转发改成 HTML 引用块（发件人、时间、收件人、抄送 + 原文），不再拼纯文本破折线 |
| 只读 | 发送/保存时 `RichEditor.IsReadOnly` 跟随 `CanEdit`，工具栏同步禁用 |
| 附件 | 固定底栏右侧「附件」按钮始终可见，调用系统文件选择器；已选附件在底栏左侧显示名称与移除按钮（总大小见悬停提示），无附件时显示「未添加附件」；附件区用 `WrapPanel` 横向优先换行，最大高度 102，超出改纵向滚动，不再出现横向滚动条遮挡 chip |
| 按钮 | 全局 `Button` 默认改为 `SurfaceSubtle + 1px StrokeDefault` 中性描边；`primary` 主色，`icon` 32×32，`icon.sm` 24×24；写信底栏保存/关闭/发送均带 14px 图标并使用 `Padding="8,0"`，与主界面写邮件、保存统一走同一套规格 |
| 旧内核清理 | `Controls/RichTextEditor.*`、`Assets/ComposeEditor/`、`tools/compose-editor/` 及其资源项已删除；仓库不再保留 WebView + TipTap 写信实现，WebView 仅用于读信与 Markdown 预览 |

**验证**

- `dotnet build src/MailDog.slnx`：0 警告 0 错误。
- `dotnet test src/MailDog.slnx`：71 通过（Contracts 6 / Html 34 / Mail 31）；新增 `HtmlMarkdownConverter` 标题、样式粗斜体、空段落与脚本清理、编辑器标题不重复加粗等断言。
- 真机 916×639：亮色富文本输入、Ctrl+A + 工具栏加粗、链接对话框（中文标题与按钮）、Markdown 源码往返（标题/粗斜/列表/引用/链接保持）、写信页滚动与固定底栏均正常。
- 附件：底栏「附件」入口可打开系统文件选择器；实机选中 `sample-attachment.txt` 后底栏出现名称/移除 chip，移除后恢复「未添加附件」；`ComposeViewModel` 的数量与大小汇总验证正确；MIME 附件由已有 `MailComposerTests` 覆盖。
- 未验证：真实账户草稿保存（演示库账户缺 IMAP 凭据，UI 返回“认证失败，请重新登录”）、发送 MIME 端到端、中文输入法逐字输入、AOT 发布。

### 12.4 仍待处理

- 第 3 步剩余视图（侧栏、列表、阅读、欢迎页、账户表单）尚未独立成文件；`MainWindow` 目前保留这三栏与壳。
- `ApplyPalette` 仍按自定义背景色重算表面色与文字/描边回退色；这些值不再被 XAML 直接引用，但尚未全部从令牌读取。
- 自定义背景色预设本身是产品数据（16 进制字面量），不属于界面令牌，保留在 ViewModel。
- 页内仍有少量内联字号（13/12/11）与 `Margin`；已不影响控件等高与按钮一致性，后续可按视图逐步替换为 `Body/Caption`。

### 12.5 正文高度、附件换行与邮件签名（2026-11-19）

| 项 | 实际结果 |
|---|---|
| 正文高度 | 写信页根布局改为 `Auto,*,Auto`：头部随内容、正文占剩余高度、底栏固定；去掉外层整页 `ScrollViewer`。富文本与 Markdown 源码 `MinHeight=160`，实际高度跟随星号行拉伸，输入超过一屏时在编辑区内滚动，不再被外层裁切 |
| 头部压缩 | 抄送/密送合并为一行 `64,*,64,*`；头部 `RowSpacing` 10→8、上边距 18→14，为正文腾出高度；主题行保持「主题」标签对齐 |
| 附件换行 | 附件面板由横向 `ScrollViewer + StackPanel` 改为 `WrapPanel`；chip 增加 `0,0,6,6` 外边距，`MaxHeight=102`，关闭横向滚动条。916×639 实测 5 个长文件名附件排成 3 行，第 5 个仍完整可见 |
| 签名存储 | `AppSettingsDto.Signature` → SQLite `signature` → `MainWindowViewModel.SignatureText`，修改后自动保存；设置页账户分区顶部新增「邮件签名」多行输入，说明「新写、回复和转发时自动插入到正文下方；留空则不插入」 |
| 签名插入 | 新写、回复、回复全部、转发统一由 `OpenCompose` 传入签名；草稿打开不重复插入。签名按行转成独立 `<p>`，空行用 `<p><br/></p>`；开头加一个 `&nbsp;` 空段落作为输入起点，避免用户输入与签名首行合并 |
| 仅签名判定 | `ComposeViewModel` 记录签名基线 `_signatureOnlyHtml/_signatureOnlyText`；新写页只有签名、无收件人/主题/附件时视为空，关闭不会误存草稿 |
| 图片与签名共存 | 全新写信页从工具栏插入示例图片后，签名段落仍在图片下方显示；旧截图中「签名消失」来自已输入文字/H1/选区状态不确定的自动化测试，不是图片插入删除了签名 |

**验证**

- `dotnet build src/MailDog.slnx`：0 警告 0 错误。
- 真机 916×639：富文本正文占满剩余高度；Markdown 左右分栏，编辑区约 310–380px、预览约 220–280px 且占满高度；拖动分隔条与键盘左右键均可调整预览宽度。
- 工具栏 H1 只放大当前标题行，签名保持正文字号；插入图片后签名仍在。
- 5 个长文件名附件显示 3 行，底栏未被横向滚动条遮挡。
- 仅签名的新写信页点「关闭写信」直接回到收件箱，不弹草稿保存。
- `dotnet test src/MailDog.slnx`：71 通过（Contracts 6 / Html 34 / Mail 31）；签名逐行段落改动后重跑通过。

### 12.6 设置去卡片、只保留富文本与可选工作台（2026-11-21）

| 项 | 当前结果 |
|---|---|
| 设置页 | 通知 / 阅读 / 同步 / 账户 / 本地文件 / 关于的内容直接铺在页面，删除 `settings-card` 与 section 外层 Border；账户列表改为底部分隔线，不再使用卡片行 |
| 启动 | 从“外观”移到独立“启动”分区，导航顺序为外观 / 通知 / 阅读 / 同步 / 功能 / 账户管理 / 本地文件 / 启动 / 关于；启动页直接显示复选框 |
| 写信页 | 删除 Markdown 开关、源码框、预览分栏和 WebView 预览；正文只保留 `MailDogRichEditor`，发送/存草稿始终写 `BodyHtml`，`IsMarkdown=false`；旧 Markdown 草稿首次打开时转成 HTML |
| 工作台 | 左侧“工具”分组 2×2 入口；日历、联系人、待办、记事本共用整页覆盖层；设置 → 功能可单独开关，关闭后入口隐藏、数据保留。12.7 已改为单列入口与右侧内容页 |
| 账户关联 | 每条记录可绑定邮箱账户；数据存 `%LOCALAPPDATA%\MailDog\workspace.json`；Outlook Graph 同步尚未接入 |
| 状态栏 | 设置、工作台、写信覆盖层打开时隐藏底部状态栏，避免邮件同步状态串到设置/工作台页面 |

### 12.7 工具入口单列、工作台右侧内容页与内联编辑（2026-11-22）

| 项 | 当前结果 |
|---|---|
| 工具入口 | 左侧“工具”分组改为每行一个菜单项（日历 / 联系人 / 待办 / 记事本），复用 `side-item` 与 `active` 选中态，不再使用 2×2 宫格 |
| 工作台容器 | `ToolsView` 从整页覆盖层移到主窗口 `Grid.Column=1`、`ColumnSpan=2`，占用邮件列表与阅读区；左栏账户/文件夹常驻，打开工作台时邮件列表与阅读区隐藏，避免内容透出 |
| 导航 | 删除工作台内部 220px 导航；顶部 `PageHeader` 显示“工作台”与返回按钮；点击邮件文件夹也直接回到邮件 |
| 编辑 | 新建/编辑改为右侧 340px 内联编辑栏，与内容同页展开，左侧同步收窄；不使用遮罩、独立窗口或居中弹窗 |
| 日历 | 去掉日期区外层卡片，星期、日期格和当天日程直接铺在页面；动作行在标题下换行排列，窄窗口不挤压 |
| 反馈 | 保存/删除结果显示在工作台底部状态条；主窗口状态栏在工作台打开时仍隐藏 |
| 最小高度 | 窗口最小高度从 600 调到 680，保证四行工具入口与账户树同时可用；默认高度仍为 800 |
| 验证 | 真机 916×839 与 916×719 检查单列入口、日历、待办、内联编辑器、保存后列表和返回收件箱；`dotnet build` 0 警告 0 错误，67 测试通过 |

### 12.8 移除可选工作台，只保留邮件（2026-11-22）

| 项 | 当前结果 |
|---|---|
| 范围 | 删除日历、联系人、待办、记事本四个入口、页面、视图模型、`WorkspaceStore`、模块专用控件与样式；MailDog 回到纯邮件客户端 |
| 设置 | 删除“功能”页和四个模块开关，导航顺序变为外观 / 通知 / 阅读 / 同步 / 账户管理 / 本地文件 / 启动 / 关于；账户、本地文件、启动、关于的索引同步前移 |
| 布局 | 邮件列表与阅读区不再受 `IsFeaturesOpen` 遮挡；左侧栏行结构改为标题、文件夹树、设置三段 |
| 保留 | 邮件“跳转”面板继续使用 `CalendarDayViewModel`、`CalendarDays` 及 `date-field` / `calendar-day` / `calendar-weekday` / `jump-item` 样式 |
| 数据 | `%LOCALAPPDATA%\MailDog\workspace.json` 未删除，作为本地历史数据保留；应用不再读取或写入该文件 |
### 12.9 列表与写信交互补齐（2026-11-22）

| 项 | 当前结果 |
|---|---|
| 悬停操作 | `ListBoxItem` 容器上订阅指针事件，行右侧浮出归档 / 切换已读 / 删除 3 个 24×24 图标按钮；不改变行高，容器复用时退订复位；已读按钮的 tooltip、无障碍名称与图标随邮件状态变化（未读→标记为已读 / 已读→标记为未读） |
| 三栏底边距 | 中栏内容 `Grid` 增加 `Margin="0,0,0,12"`，邮件列表与底部状态栏之间保留 12px；与左栏内边距一致，不再贴窗口底边 |
| 紧凑预览 | 紧凑档两行（发件人+时间 / 主题），标准档恢复摘要第三行；附件、旗标、会话徽标移到主题行尾部，不产生裁切 |
| 无障碍 | 容器生成时写 `AutomationProperties.Name`（未读前缀 + 发件人 + 主题）；监听 `IsRead` 变化即时刷新名称；图标按钮全部有可读名称 |
| 搜索 | 清除按钮（`Dismiss`）清空关键词并把范围复位到当前文件夹；「当前文件夹 / 全部邮箱」范围切换；命中词浅蓝底 + 加粗 |
| 会话 | 列表按 `ThreadKey` 折叠，行尾数字徽标；阅读页「会话 N 封」按钮打开 Flyout，选较早邮件只换正文、不改列表选中项 |
| 写信收件人 | To/Cc/Bcc 三列 chip + 联系人候选；chip 是发送/草稿/判空的权威值，输入框只保留未提交文本；分隔符与 Enter 均可提交 |
| 快捷键 | `Ctrl+N` / `Ctrl+R` / `Ctrl+Shift+R` / `Ctrl+Shift+F` / `Delete` / `F5` / `Ctrl+A` / `Esc` / `Ctrl+F` / `Ctrl+,`；`Esc` 在写信页按关闭语义保存草稿 |
| 验证 | `dotnet build` 0 警告 0 错误；`dotnet test` 67 通过（Html 30 / Contracts 6 / Mail 31）；实机 1296×839 验收悬停、无障碍名称刷新、紧凑列表、全局搜索、会话折叠与 Flyout、收件人 chips |

**验证备注**

- 会话折叠曾用临时 `thread_key='thread-test-r28'` 把两封 QQ 收件箱邮件归组，确认列表「2」徽标、阅读页「会话 2 封」和 Flyout 两项；验收后已恢复原 `thread_key`，生产数据不再带测试分组。
- 全局搜索清空后范围复位为「当前文件夹」；统一收件箱未读徽标在后台同步完成后可能从局部的 354 跳到全量 1862，属于账户文件夹计数刷新，不是搜索残留。
