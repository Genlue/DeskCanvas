# DeskCanvas UI 基础规范（UI Foundation）

本文是 UI 冗余收敛（W0–W11）落地后的使用说明：四个唯一入口的结构、新 UI 接入指引、例外登记方法、兼容窗口与人工验收清单。规范由两道检查强制执行：

- `scripts/check-ui-conventions.ps1` —— 仓库静态规范（XML 节点解析 + 筛选 C# 字符串分析，带例外清单）；
- `tests/UiFoundationChecks` 中的 `RedundancyGuardChecks`（W11）—— 可加载契约的防回流断言（宿主类型名分支、9 弹窗外壳三件套、共享样式键、基线旧 DLL 装载验证）。

任何新增 UI 先读本文；检查不过时优先怀疑违反了某个入口，其次才是登记例外。

## 1. 四入口结构

同一职责只允许有一个实现入口：

| 入口 | 位置 | 职责 |
| --- | --- | --- |
| 基础外观 | `src/DeskCanvas/Styles/DesignTokens.axaml` + `Styles/InteractionResources.axaml` + `Styles/Controls/*.axaml` | 几何 Token、语义交互画刷（含成对文本选区）、输入规则、图标按钮族、分组标题、SecondaryPanelShell 模板 |
| 设置行组合 | `Views/Controls/Setting` + `LabeledSlider` + `ColorInput` | 单项设置的行布局、滑块＋读数、取色＋Hex 的共享行为 |
| 二级弹窗外壳 | `Views/Controls/SecondaryPanelShell`（+ `SecondaryPanelWindow` 生命周期基类） | 卡片、玻璃、标题区、关闭按钮、拖动、材质解析——弹窗只提供业务内容 |
| 宿主消费契约 | Core 的 `IWidgetSurfaceHints`、宿主 UI 层的 `IWidgetOverlayProvider` | 宿主只消费组件提供的外观提示与装饰控件，不按具体插件类型决定画面 |

资源加载顺序：Fluent 与 ColorPicker 基础主题 → 共享控件规则（`App.axaml` 的 StyleInclude）→ 页面合法的语义变体。`App.axaml` 挂接关系由 W11 守卫检查盯防，不得摘除。

## 2. 新增 UI 接入指引

### 2.1 输入与选区

- 一切输入控件的选区颜色来自 `TextSelectionBackgroundBrush` / `TextSelectionForegroundBrush` 成对资源（由 `TextSelectionPaletteResolver` 在每次主题应用后按明暗变体重算）。**反例**：在业务 XAML 里写 `SelectionBrush="#XXXXXX"`——脚本规则 R2 直接失败。
- 合法例外只有一处：`GridEditor.axaml` 作为固定浅色工具表面局部重声明深色选区成对资源（原因见脚本例外清单）。不要把它推广到别处，也不要删掉它的补丁。

### 2.2 图标按钮与分组标题

```xml
<!-- 圆形弹窗按钮（28 DIP；compact 变体 24 DIP） -->
<Button Classes="dc-icon-button" Content="✕"
        AutomationProperties.Name="{x:Static locales:Locale.XXX_Close}" />

<!-- 矩形工具条按钮（radius 6，自然尺寸） -->
<Button Classes="dc-toolbar-button" />

<!-- 分组标题 -->
<TextBlock Classes="dc-section-header" Text="{x:Static locales:Locale.XXX_Section}" />
```

**反例**：在插件 XAML 里定义 `Button.icon-btn` / `TextBlock.group-header` 基础样式（脚本规则 R1）。过渡别名 `icon-btn` / `icon-button` / `group-header` / `section-header` 仍可使用，但只许"用"，不许"定义"；新页面一律用 `dc-*` 前缀。只显示图标的按钮必须带本地化 `AutomationProperties.Name`，Tooltip 不能替代无障碍名。

### 2.3 设置行

```xml
<controls:Setting Title="{x:Static locales:Locale.Timetable_StartDate}"
                  LayoutMode="Stacked" EditorMinWidth="296"
                  EditorHorizontalAlignment="Stretch">
    <DatePicker SelectedDateChanged="OnStartDateChanged" />
</controls:Setting>
```

- 可开关/选择的单项设置用 `Setting`；播放器列表、搜索引擎编辑、课程节次表等保留专属编辑区域，内部仍共用分组标题、输入与按钮规范。
- 滑块＋当前值 → `LabeledSlider`（`Value/Minimum/Maximum/Step/Unit/DisplayFormat/ShowEditableValue/ValueCommitted`；拖动结束或数值 Enter/失焦才提交一次）。**反例**：Slider + TextBlock 的本地拼装。
- 取色＋Hex → `ColorInput`（`Value/AlphaPolicy/ColorFormat/AllowEmpty/IsReadOnly/ValueCommitted`；中间态草稿不落盘，alpha 默认保留）。**反例**：裸 ColorPicker 加事件联动。
- 页面迁移宿主滚动时声明 `SettingsPage.ScrollOwnership="Host"` 并**同批删除**自己的整体根 ScrollViewer（脚本规则 R4：Host 页面内只允许带 `MaxHeight`/`Height` 的有界内部滚动区）。
- 用户可见文案一律 `x:Static` Locale 资源，中英文两份 resx 同批补齐（脚本规则 R7）。

### 2.4 二级弹窗

```xml
<controls:SecondaryPanelShell Title="{x:Static locales:Locale.XXX_Title}">
    <controls:SecondaryPanelShell.HeaderContent>
        <!-- 动态业务标题（替代默认 Title 表现） -->
    </controls:SecondaryPanelShell.HeaderContent>
    <controls:SecondaryPanelShell.HeaderActions>
        <!-- 业务动作按钮，与共享关闭按钮并排 -->
    </controls:SecondaryPanelShell.HeaderActions>
    <!-- Content：业务主体；FooterContent：可选脚注/状态栏 -->
</controls:SecondaryPanelShell>
```

- 槽位：`Title/Subtitle`（默认标题）、`HeaderContent`（动态业务标题）、`HeaderActions`（业务按钮，与共享关闭按钮并排）、`Content`、`FooterContent`、`ContentPadding`；`CloseRequested` 由基类接管并走退出动画。
- 模板部件 `PART_Backdrop/PART_CardBorder/PART_GlassSurface/PART_HeaderDragArea/PART_CloseButton/PART_ContentPresenter` 处于 Shell 独立 namescope，业务 code-behind 不得直接引用。
- **反例**（脚本规则 R3）：弹窗 XAML 里本地声明 `x:Name="CardBorder"`、`<LiquidGlassSurfaceControl>`、`PART_CloseButton`。业务内容内部的普通 `Border` 不受限制。
- 窗口行为（定位、开合动画、Escape、外部点击 hook、拖动）全部来自 `SecondaryPanelWindow` 基类与 `PanelDragBehavior`；不要新建第二套 WindowManager，也不要把拖动附加到内容根 Panel。

### 2.5 宿主与插件解耦

- 组件想控制 Colorful 模式下的卡片画刷：在 Core `IWidgetSurfaceHints` 声明 `ColorfulBackgroundResourceKey`，宿主从组件资源上下文按变体解析，解析不到回退 `WidgetBackground`。**反例**：宿主代码里 `GetType().Name == "..."` 判断（脚本规则 R6 与 RedundancyGuardChecks 双重盯防；现存的 `IsMapWidget` 分支是登记例外，见 §3）。
- 组件想提供装饰（如 Stack 分页点）：实现 UI 层 `IWidgetOverlayProvider`（`Overlay` + `OverlayHints`）。装饰控件每个宿主实例只创建一次，不得在 getter 里重复 new，不得被多个窗口共享为 visual child。布局提示覆盖 outside-card 放置、右缘预留、原生 Acrylic 内移、移动/尺寸变化重布局、隐藏时可命中、Region 合并。
- 通用几何 Token（`ControlCornerRadius` 等）与语义画刷（`ControlHoverBrush` 等）从 `DesignTokens`/`InteractionResources` 消费。新共享 UI 文件里出现数字 `CornerRadius` 魔数会触发脚本规则 R5b；`#AA808080` 历史灰色哨兵全仓禁止回流（R5a）。

## 3. 例外登记方法

检查不是为了逼人绕过，而是把"合法的不一致"显式化。登记位置与格式：

- **脚本例外**：`scripts/check-ui-conventions.ps1` 顶部的 `$TypeNameExceptions`、`$SelectionColorExceptions`、`$ShellExceptions`、`$ScrollOwnershipExceptions`、`$CornerRadiusExceptions`、`$GreySentinelExceptions`、`$LocaleLiteralExceptions`。每条是 `[pscustomobject]@{ File = '相对路径'; Attribute/Value/Token = …; Reason = '原因与移除条件' }`，**Reason 必写**：说明为什么允许、谁在使用、什么条件下移除。
- **C# 守卫例外**：`tests/UiFoundationChecks/RedundancyGuardChecks.cs` 的 `HostTypeNameExceptions`（与脚本同源维护，清单和源码互相校验防腐烂）。
- 当前已登记的例外一览：
  - `MapView`（宿主 Widget.axaml.cs）：右键菜单「地图预设」可见性开关，属上下文菜单职责而非外观着色路径；移除条件——Map 组件经提示接口声明自己的固定跨度预设。
  - `GridEditor.axaml` 成对选区资源：固定浅色工具表面，不得提升到应用级。
  - `ThemeButton.axaml` 三处圆角：存量控件视觉设计值，待主题样片接入语义 Token 时迁移。
  - 36 条用户可见中文字面量（GridEditor 参数标签、InputDialog 按钮、ControlCenter 胶囊说明、Hitokoto 弹窗/卡片的运行时覆盖默认值与示例数据、Timetable 周导航工具提示）：均为存量登记，迁移各自的批次时换 Locale 键并删除对应条目。

登记例外不需要改本白名单之外的任何业务代码；反之，为了让检查通过而删改业务行为是禁止的。

## 4. 兼容窗口

| 项 | 状态 | 使用者与移除条件 |
| --- | --- | --- |
| `WeatherCardBackground` / `ProgressCardBackground` 全局资源键 | 保留 | 新组件请改走 `IWidgetSurfaceHints.ColorfulBackgroundResourceKey`；全局键在确认不再有旧 DLL 依赖专属渐变后（见下两行）才可删除。Stack 插件 `WidgetStackView.ResolveCardBackground` 仍按子卡片类型名消费这些键——属插件内部行为，已登记例外，不重构。 |
| `IWidgetSurfaceHints` | 可选接口 | 未实现（含旧 DLL）自然回退 `WidgetBackground`；不在现有必需接口上加抽象成员。 |
| `IWidgetOverlayProvider` | 可选接口 | 只实现旧 `IStackWidget` 的内容由宿主侧 `LegacyStackOverlayAdapter` 服务。 |
| `LegacyStackOverlayAdapter` | 过渡适配器 | 使用者：只实现 `IStackWidget` 的已部署组件 DLL（W11 已用 dist 下 3.5.0 基线 DLL 实测装载：类型仍派生自 `SecondaryPanelWindow`、仍实现 `IStackWidget`、未实现新接口）。移除条件：确认所有支持版本的组件包都重新解包为提供新装饰的版本（`WidgetBundle` 以 SHA-256 标记驱动整包重解压，见 `src/DeskCanvas/Services/WidgetBundle.cs`）；不得同时声称旧 DLL 兼容又删除其唯一显示路径。 |
| `IStackWidget.AutoHideIndicators` | 默认实现 | 默认 `false`＝圆点常显；`true`＝空闲隐藏、悬停/切换过渡期显现（谓词 `IsTransitionActive || pointer-over`），过渡路径的真实时钟行为属人工验收（§5）。 |
| `icon-btn` / `icon-button` / `group-header` / `section-header` 别名 | 过渡别名 | 新页面用 `dc-*` 前缀；别名只许消费。当确认没有页面再使用别名类名时删除别名入口。 |

旧版兼容的其他保障：不改模型字段名、组件程序集名、视图类型名与布局存储标识；新增 `ui-state.json` 可被旧版本直接忽略。

## 5. 人工验收清单（无头检查做不了的项目）

以下项目在各批次检查文件中均有注释说明为什么 headless 不做假断言，发布前需在真实 Windows 上过一遍：

- [ ] **真实标题拖动**：EditWidget 与 9 弹窗标题区 `BeginMoveDrag` 实际移动窗口；标题内按钮点击不启动拖动（合成 MouseDown 在 headless 下被开场缩放动画时钟吞掉，无法可靠复现）。
- [ ] **弹窗开合动画**：320 ms 进入 / 180 ms 退出的 `DispatcherTimer` 完成帧；重复打开关闭无订阅叠加。
- [ ] **DWM 玻璃 / HWND Region**：LiquidGlass、LiquidGlassV2、原生 Acrylic 材质下弹窗与组件卡片的圆角裁剪正确；Stack 圆点条在原生模糊下不被 Region 裁掉、不被糊成毛玻璃（Acrylic 内移实测）。
- [ ] **外部点击 hook**：真实 Windows 子进程验证 Win32 外部鼠标 hook 关闭面板、Popup（ComboBox/ColorPicker 弹层）不误关。
- [ ] **BigFolder 抽屉优先 Escape**：设置抽屉打开时第一次 Escape 只关抽屉不关面板；失焦宽限 150 ms 不误关。
- [ ] **多屏 / DPI / 负坐标**：左侧/上方显示器的合法负坐标保留；100/150/200% 缩放、拔屏、小工作区夹取；窗口几何记忆（`ui-state.json`）在真机恢复。
- [ ] **选区观感矩阵**：黑/白/黄/浅青/深蓝强调色 × 明暗主题下实际选中文字对比度 ≥ 4.5:1，主题切换无需重开窗口。
- [ ] **旧 DLL 真机回归**：把未随包重新解包的旧组件 DLL 放进 Widgets 目录（或直接用 %LocalAppData% 旧安装目录），确认无 TypeLoad、无资源丢失、Stack 旧分页点经 Legacy 适配器正常显示。
- [ ] **zh-Hans / en 双语过一遍**迁移过的设置页与弹窗：长英文标签不截断、Admin/Restart 提示本地化。

## 6. 检查与回归命令

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\check-structure.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\check-ui-conventions.ps1
dotnet build .\src\DeskCanvas.sln -c Debug
dotnet run --project .\tests\UiFoundationChecks -c Release
dotnet run --project .\tests\MonochromeAccentChecks -c Release
dotnet run --project .\tests\WidgetAdaptiveChecks -c Release
dotnet run --project .\tests\SearchVisualChecks -c Release
dotnet run --project .\tests\SettingsReleaseChecks -c Release
# 附加：LiquidGlassChecks、HitokotoMemoryChecks、DragCommitChecks、MultiScreenGridChecks、TimetableChecks
# BottomBandChecks 有 2 条预先存在的环境时序失败（z-order，见 W11 提交说明），记录现状即可。
```

发布包验证：`build.ps1` 产出单文件 exe（组件包以 `DeskCanvas.Resources.widgets.zip` 嵌入，`WidgetBundle` 按哈希驱动解包）；`scripts/build-widgets-bundle.ps1` 可单独产出组件包 zip 供检查——包内不得出现宿主/Core/Avalonia DLL（共享控件主题编译在宿主 `DeskCanvas.dll` 内）。
