# ADR-P3b：玻璃表面端口与面板壳迁移

状态：已实施（2026-10-16 第三批）。真机多屏/IME、性能、GPU、旧包二进制装载验证仍未执行。

## 决策

### 1. 玻璃表面端口 = UI 声明的抽象表面契约，实现保持唯一

`DeskCanvas.UI.Controls.GlassSurfaceBase`（抽象 `Control` 子类）声明面板壳与面板窗口需要的最小行为面：

- `CornerRadius`（抽象 CLR 属性——不迁移宿主 `CornerRadiusProperty` 的注册身份，宿主以 `override` 提供既有实现）；
- `RequestRender(bool immediate)`；
- `SetAnimationFrameScale(double scale, Point anchorRelativeToSurface)`。

宿主 `LiquidGlassSurface` 去掉 `sealed` 并派生该端口，全部光学属性（`Material`/`Preview`/`SettingsSurface`/`IgnoresAncestorTransform`）与渲染管线（`LiquidGlassDispatch`、GPU/CPU 渲染器、壁纸采集）**留在宿主，实现数量保持 1**。端口刻意不携带材质：`Theme` 是宿主 Core 模型，DeskCanvas.UI 只允许依赖 Contracts（check-structure 边界），材质经宿主外观层接驳。

**两个有差异的真实消费者验证端口成立**（执行纪律第 2 条）：

1. 共享 `SecondaryPanelShell`——模板槽位持有端口表面，镜像 `UseRenderedGlass→IsVisible` 与卡片 `CornerRadius`（跨模板重应用保持同步）；
2. `SecondaryPanelWindow`——开合缩放动画逐 tick 经端口调用 `SetAnimationFrameScale`、挂载后经端口 `RequestRender(immediate)`，字段类型从具体 `LiquidGlassSurface` 改为端口类型，不再向下转型。

独立夹具另以桩实现（`ProbeSurface`）证明端口可在宿主管线之外实现（抽象自洽性），不计入消费者数。

### 2. 面板壳迁移：共享实现 + 宿主外观外观（facade）

- `DeskCanvas.UI.Controls.SecondaryPanelShell`（共享实现）承载全部 chrome 逻辑：模板部件（Backdrop/CardBorder/HeaderDragArea/CloseButton/ContentHost）、`CloseRequested` 路由、背景点击关闭语义（仅 `PART_Backdrop` 本体）、拖动句柄挂接、玻璃槽位镜像、关闭按钮可访问名/tooltip 的模板期读取。纯 Avalonia 依赖，零宿主类型。
- 宿主 `DeskCanvas.Views.Controls.SecondaryPanelShell` 变为兼容外观：保留旧命名空间与成员面，声明宿主模型强类型的 `GlassMaterial`（`Theme?`）与 `SurfaceAppearance`（`PanelAppearance?`）属性并按**原分解顺序**写入共享属性；构造时创建宿主 `LiquidGlassSurface` 注入共享槽位；`OnApplyTemplate` 前注入 `Locale.SecondaryPanel_Close`（保持模板期本地化语义，语言切换后开面板仍正确）。
- 玻璃表面由**模板直建改为槽位属性**（`GlassSurface`，`GlassSurfaceBase?`）：模板以透明 `PART_GlassSurfaceHost` ContentPresenter 承载，未提供表面时壳以无玻璃卡片独立渲染。UI 库由此保持独立真实实现。

### 3. 样式归属：键控模板资源 + 两条薄样式（单一模板来源）

实证发现 **Avalonia 类型选择器只匹配精确类型**（夹具钉住：派生探针 `template=True, closeButton=False`——无模板时 OnApplyTemplate 也会运行）。因此：

- `avares://DeskCanvas.UI/Styles/Controls/SecondaryPanelShellTemplate.axaml`：键控 `PanelShellTemplate`（`TargetType="ui:SecondaryPanelShell"`），**唯一模板来源**；
- UI 共享样式（`ui|SecondaryPanelShell` 精确选择器）与宿主 shim 样式（`controls|SecondaryPanelShell` 精确选择器）各声明同一组 setters（ContentPadding/CornerRadius/Template → `StaticResource`），派生外观类型经 shim 获得同一模板；
- 宿主 `avares://DeskCanvas/Styles/Controls/SecondaryPanelShell.axaml` 旧 URI 保留为 shim（合并键控模板 + 外观样式），旧 URI 继续可加载（夹具断言）。
- 曾尝试并否决的路线（记录避免重走）：类选择器（setter 无 TargetType，AVLN2200）、`x:SetterTargetType`（XamlIl 内部编译错误）、`ControlTemplate.TargetType` 管道前缀（类型属性需冒号语法）。

### 4. 拖动行为归属

`PanelDragBehavior` 移入 `DeskCanvas.UI.Controls`（纯 Avalonia 逻辑，无宿主依赖）。宿主 `DeskCanvas.Views.Controls.PanelDragBehavior` 保留静态门面：`IsDragHandleProperty`/`IsInteractiveProperty` 直接**别名共享属性实例**（同一 AvaloniaProperty 身份，宿主 AXAML 与共享壳写同一存储、走同一 class handler），访问器与 `IsDragExcluded` 转发。宿主 AXAML（EditWidget/ShortcutsEditorWindow）与既有检查零改动。

### 5. LiquidGlassDispatch 归属

`LiquidGlassDispatch` 是 LiquidGlass 与 V2 的渲染分发（消费端为 `LiquidGlassSurface` 内部 CPU 回退路径），与壁纸采样、GPU 探针同属平台渲染闭包——**留在宿主**，不属于面板壳的端口面。后续 P7 渲染资源预算如需第二玻璃实现，经 `GlassSurfaceBase` 端口接入。

## 消费者切换

15 个二级弹窗（文档记"九个"为 W7 时点数字；Weather、Folders/BigFolder、Notes、Reminders、Hitokoto、Habits、Shortcuts、Countdown、Wheel、Fortune、Dice、Timetable、Tools/Calculator、Tools/Clipboard、Tools/Translator）的 AXAML 根仍声明宿主外观类型，经继承自动切换到共享实现，**零源码改动**；`UiFoundationChecks` 的 PanelShell/PopupMigration 系列（真实弹窗 AXAML + 无头窗口）验证切换后契约。Weather/BigFolder/Note 直触的 `SurfaceAppearanceProperty` 身份保留在外观类型上，无感知迁移。

## 验证

- 新增独立夹具 `tests/PanelShellAvaloniaChecks`（Release，注册进 verify.ps1/README/sln）：41 项——端口契约与独立桩实现、共享壳独立渲染（部件/关闭/背景/拖动/本地化注入）、玻璃槽位镜像（可见性/半径/替换表面重同步）、外观类型与槽位接线、Solid/Glass 两种 `PanelAppearance` 分解到共享属性与端口表面、旧 URI shim 装载、拖动门面同属性身份、面板窗口端口驱动（真实 `SecondaryPanelWindow` 子类 + `PanelAppearanceBinder` 挂接/计数）。
- `UiFoundationChecks` 的壳/弹窗断言按新结构同步三处：端口表面类型断言（`is LiquidGlassSurface { IgnoresAncestorTransform: true }`）、"逻辑树无玻璃"改为"视树中唯一玻璃 = 壳端口槽位表面"（四个文件；表面经 ContentPresenter 进入逻辑树属预期变化）。
- 统一门禁 `scripts/verify.ps1 -NoRestore` 由 18 步扩展为 **19 步**（新增 PanelShellAvaloniaChecks），执行结果见迁移报告。

## 范围限制与未完成验收

- 旧包二进制兼容：外观类型/旧 URI/属性身份由断言覆盖，**历史编译组件包实机装载矩阵仍未执行**；`LiquidGlassSurface` 去 sealed、`RequestRender(bool)` 收紧默认参数、表面经槽位进入逻辑树等结构性变化对旧二进制的实际影响需旧包验证。
- 真机多屏/IME、GPU 材质路径、性能与长期资源观测未执行，不得以无头检查替代。
- 面板窗口的 Win32 外点钩子、窗口区域、动画计时仍属宿主平台边界，本批未动。
- 每屏独立主题策略与其他共享数据源按执行计划继续推进。
