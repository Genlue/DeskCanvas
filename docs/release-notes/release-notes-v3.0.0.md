## DeskCanvas v3.0.0 更新日志

> 一次**不加功能、只消灭"多处副本"**的版本：构建属性的 19 份拷贝收成一份、界面字面量（圆角/字号/边距）收成一套设计令牌、主题资源读写从 `ThemeService` 拆成独立协作者、8 处硬编码的应用名收归 `Const.AppName`。主程序升到 3.0.0，全部改动行为等价，编译 0 警告 0 错误。

---

### 🏗 1. 构建属性单一真相源：新增 `Directory.Build.props`

* **问题**：`ImplicitUsings` / `Nullable` / `AvaloniaUseCompiledBindingsByDefault` / `GenerateAssemblyInfo` / `EnableDynamicLoading` 这五项在 **19 个 csproj** 里各自抄了一遍（17 个组件 + 宿主 + Core），共 91 行重复声明。漏抄一个项目，就得到一个静默的语义差异（例如某组件忘了 `Nullable` 就退回可空关闭）。
* **修复**：新增根级 `Directory.Build.props`，用条件属性集中下发——三项通用开关只在"调用方未显式设置"时给默认值（`Condition="'$(X)' == ''"`），保留任何项目的局部覆写权；widget 判定不靠项目清单硬编码，而是用 MSBuild 路径推导（比较项目父目录与 `src/Widgets`），命中即自动开 `EnableDynamicLoading`、自动关 `GenerateAssemblyInfo`。**测试探针项目刻意不继承** `GenerateAssemblyInfo=false`（它们需要 SDK 默认的程序集特性）。
* **收益**：19 个 csproj 净删 91 行、零新增；新增组件时不会再因为"忘了抄那几行"产生隐性差异。

### 🎨 2. 设计令牌单一真相源：新增 `Styles/DesignTokens.axaml`

* **问题**：圆角值在 12 个 XAML 里各写各的——同一个"菜单/弹窗外壳"在 `App.axaml` 是 8、在 `Widget.axaml` 是 12、在 7 个对话框里是 10，字号则在 11/12/13/14/15/18 之间散落。
* **修复**：新增 `Styles/DesignTokens.axaml`，在 `App.axaml` 以 `ResourceInclude` 合并进应用资源字典，然后把消费点全部改为 `DynamicResource`：
  * `HostSurfaceCornerRadius`（12）统管菜单、flyout、弹窗外壳；`HostSurfaceBorderThickness`（1）统管描边
  * 排版阶梯 `FontSizeCaption`（12）/ `FontSizeBody`（14）/ `FontSizeTitle`（18）
  * 控件与间距 `ControlNumericMinWidth`、`ControlNumericPadding`、`WidgetContentHostMargin`、`WidgetContextMenuPadding`、`WidgetContextMenuItemMargin`、`ScrollBarThumbCornerRadius`
  * 保留 `MenuCornerRadius` / `WidgetContextMenuCornerRadius` 作为既有组件样式的兼容别名
* **同步**：`Widget.axaml.cs` 里驱动菜单 `Clip` 与毛玻璃弹窗原生窗口区域裁剪的 `ContextMenuCornerRadius`，由**编译期常量 12** 改为**运行时读令牌**（资源不可用时回退 12）——改令牌不再需要回头改 C# 常量，v2.7.5 发布说明里警告过的"只改样式不改它会被裁回旧圆角"这个坑从根上消失。

### ♻️ 3. `ThemeService` 拆分：新增 `Services/ThemeResourceHelper.cs`

* `ThemeService` 里约 66 行与"解析主题"无关的资源读写——`ApplyAccent`（重写整套强调色渐变）、`ClearAccentOverrides`（把强调色交还 Fluent）、`ParseColor`、`SwitchStyle`（样式字典挂载/卸载）以及三张资源键表——整体搬进新的 `internal static` 类 `ThemeResourceHelper`。
* `ThemeService` 现在只做一件事：判断当前主题 → 调用 helper 落盘资源。调用点语义逐条对齐（含 v2.7.5 引入的"未手选强调色时交还 Fluent"分支），**行为完全等价**，纯搬家。

### 🧭 4. 硬编码收敛到 `Const.AppName`

* 8 处散落的 `"DeskCanvas"` 字面量改为引用 `Const.AppName`：`GlassDiagnostics`（调试日志）、`UpdateService`（临时更新目录）、`MapTileService`（地图瓦片缓存）、`ClipboardMonitorService`（剪贴板历史与缓存）、`Folders` 组件的两处文件/文件夹选择器标题、`Advanced` 与 `MultiScreen` 的选择器标题。
* 顺带清掉两处"手抄同步"隐患：`Const.cs` 里 5 个 `private static string` 属性改为 `const`；`LiquidGlassSettings` 主构造函数的默认参数由字面量改为直接引用 `DefaultEdgeTint` / `DefaultDyeSpread` / `DefaultLiveSamplingInterval` / `DefaultBackdropClarity` 常量，原先那两句"这是 = DefaultXxx，主构造函数默认值不能引用它"的注释随之删除。

### 🪟 5. 弹窗材质统一与可拖拽

* `EditWidget`、`UpdatePopup`、`InputDialog`、`ScaleDialog`、`CustomSizeDialog`、`WallpaperAlignDialog`、`ConfirmDialog` 七个窗口：窗口级 `TransparencyLevelHint` 由 `Transparent` 改为 `AcrylicBlur, None`，并补上 `TransparencyBackgroundFallback`（透明合成不可用时回退到原设置背景）；内容统一包一层 `MenuSurfaceBackground` + `HostSurfaceCornerRadius` 的圆角外壳，视觉上与其他菜单/弹窗归为同一族。
* `EditWidget` 新增标题栏左键拖拽（`OnHeaderPressed` → `BeginMoveDrag`），与其他无边框窗口的手感一致。

### 🛡 6. 新增结构契约校验与扩展指南

* `scripts/check-structure.ps1`（只读，退出码 0/1/2）：不编译、不加载程序集、不改文件，纯静态校验仓库形状与插件最小契约——必需目录、宿主必须引用 Core、宿主**不得**硬编码组件项目引用、Core **不得**反向依赖宿主/组件、`Directory.Packages.props` 中央包版本管理（版本只能声明一次、子项目不得内联版本）、每个组件必须具备 `AssemblyInfo.cs` / `Locales/Locale.resx`、项目引用必须 `Private=false` + `ExcludeAssets=runtime`、输出必须落在宿主的 `Widgets` 目录且不追加框架、`[WidgetInfo]` 与 `[Locale]` 特性必须存在。
* `docs/项目结构与扩展指南.md`：目录边界、新增插件流程、UI 资源入口（设计令牌）、安全与稳定性原则、维护检查清单；`README.ZH-HANS.md` 顶部导航已加入该指南链接。

---

**版本**：主程序 3.0.0（本次无组件源码变更，组件版本不变）；MSI 安装包 `DeskCanvas-3.0.0-win-x64.msi` 版本号已同步。

**为什么是 3.0.0**：本批改动重构了项目的"骨架契约"——构建属性的归属、界面数值的真相源、主题资源的读写边界、应用名的唯一来源。对外观与用户数据无破坏性影响（数据目录名仍为 `DeskCanvas`，配置无缝继承），但内部结构变化幅度足以按大版本对待。

**完整变更**: https://github.com/Genlue/DeskCanvas/compare/v2.7.5...v3.0.0
