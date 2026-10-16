# ADR-P4b：每屏独立主题解析（per-screen theme resolution）

> 状态：已实现（架构重构切片 2）。前置：P4（主题设置协议）。关联：`docs/失焦联动设计.md`（原始逐屏设计）、`docs/UI-Foundation.md`。

## 1. 决策

失焦自动切换（FocusLink）启用时，**每个窗口按"自己所在屏幕"的占位状态解析 focused/unfocused 主题**，恢复设计文档第 4 节的逐屏语义；uniform 折叠（把 `FocusStateSnapshot.UnfocusedScreens` 压成单一 bool、全部窗口跟随同一主题）降级为**回退路径**。设置模型不变：`FocusLinkSettings` 不加字段——逐屏解析是对既有 per-screen 观察结果（`UnfocusedScreens` 集合）的利用，不是新配置。

核心规则（`FocusThemeCoordinator`）：

- **每屏判定**（纯函数）：`ResolveFocused(snapshot, screen)` —— 该屏在 `UnfocusedScreens` 集合中 → 失焦主题；不在 → 聚焦主题。
- **窗口→屏幕映射**：`WindowScreenResolver`（可注入委托）。默认实现走 Avalonia 平台查询 `window.Screens.ScreenFromWindow(window)` 的物理像素 Bounds——与 `FocusWatchService` 构建 `UnfocusedScreens` 用的是同一来源，两边坐标天然对齐。检查注入假映射。
- **主题解析**：`ResolveWindowTheme(settings, snapshot, screen)`（纯）与 `ResolveWindowTheme(settings, window)`（实例，读活快照）。
- **窗口应用**：poke 窗口时先 `ApplyWindowTheme(window)`——按窗口自身解析结果写 `Window.RequestedThemeVariant`（亮暗变体逐屏独立）与窗口级颜色资源（普通材质：accent ramp `SystemAccentColor*`；多彩：并入 `ColorfulResources.axaml` + `ColorfulActive` 标记并清除 accent ramp），然后再走既有 `IFocusThemedWindow.OnFocusThemeChanged` 材质重解析路径。
- **唤醒规则**：快照在两屏之间**重排**失焦屏（{A}→{B}）时 uniform 亮暗布尔不变、全局应用短路，但两屏上的窗口确实要在两个状态主题之间移动——`Reapply` 对比 `lastPokedSnapshot`，快照变了即唤醒一轮。
- **应用级路径不动**：`ThemeService.ApplyFocusState` / `FocusLensResolver` / 设置窗口与对话框继续跟随 uniform 活状态；全局材质样式字典（StyleIncludes）与单色样式仍按 uniform 应用。

## 2. 消费者事实

| 消费者 | 变更 |
|---|---|
| `Views/Widget.axaml.cs` | `GlassMaterial` → `ResolveWindowTheme(settings, this)`：卡片画刷、玻璃图层、原生模糊提示、描边全部按本窗口所在屏解析；`WidgetCardBackground` 读 `ActualThemeVariant`（窗口变体已逐屏设置，`OnActualThemeVariantChanged` 既有监听自动生效） |
| `Services/PanelAppearanceResolver.cs` | 新增 `ResolveEffectiveTheme(settings, window)`；旧签名 `ResolveEffectiveTheme(settings)` 转发（window=null → uniform），行为不变 |
| `Services/PanelAppearanceResolver.cs`（Binder） | `PanelAppearanceBinder.Refresh` 传入面板自己的窗口——面板跟随打开它的屏幕 |
| `Views/SecondaryPanelWindow.cs` | `ApplyWindowRegion` / `UsesNativeBlurTheme()` 改为按 `this` 解析（UsesNativeBlurTheme 由 static 改 instance，无外部调用者） |
| `FocusThemeCoordinator.ResolveVisualTheme(visual)` | 从 visual 推导所在窗口后走逐屏解析；推导不出窗口回退 uniform。自行配色的组件内容（Stack、Notes、无边框时钟）随之逐屏 |
| 组件窗口/面板注册 | `DesktopFurniture.Attach` → `Track` 即时 poke：注册即拿到本屏变体 + accent |

## 3. 回退行为（uniform 兼容）

满足任一条件时窗口解析结果与切片前完全一致：

- `FocusLink.Enabled == false`：`ResolveWindowTheme` 返回共享主题；`ApplyWindowTheme` 清除本类写入的窗口级键（accent ramp、ColorfulResources include、`ColorfulActive`）并把 `RequestedThemeVariant` 复位为 `Default`（重新继承应用级）——即设计文档第 4 节"禁用 → 清空变体与窗口级资源"的语义。只清本类写入的键，**组件自己的窗口级语义色（如 AggregateViewModel 写的 `CalendarTodayBrush`）不动**。
- 窗口映射不到屏幕（启动早期窗口未显示、无平台屏幕、headless 未注入映射）：null screen → uniform 规则 `ResolveFocused(snapshot)`。
- 旧 API：`ResolveWindowTheme(settings)`（无窗口参数）保留为 uniform 入口；`PanelAppearanceResolver.ResolveEffectiveTheme(settings)` 旧签名原样可用。

## 4. 边界（本切片明确不做）

- **全局材质样式字典仍 uniform**：`ThemeService` 的 StyleIncludes（Transparent/Solid/LiquidGlass/Colorful…）按活状态单一材质装载。逐屏不同材质（如 A 屏液态玻璃、B 屏纯色同时挂载字典并集）需要重构 StyleIncludes 结构，本切片不强行完成；窗口的卡片画刷/玻璃参数本来就按 `GlassMaterial` 在代码中解析（不依赖全局资源），受影响的只是靠全局 `DynamicResource` 命中材质字典键的部分。
- **单色样式（Monochrome.axaml 等）仍 uniform**：应用级样式对全部窗口生效，窗口作用域只能增不能删——B 屏的黑白单色无法压过 A 屏状态的 app 级单色样式。accent ramp 已逐屏写入，单色**颜色**正确；单色**样式化**（accent 与文字同色）跟随 app 级策略。
- **全屏挂起期间**不结算（既有行为），退出全屏一次性 settle。
- **未做真机验证**：多屏 + 双状态同时显示的实机观感（材质字典边界内）、`window.Screens` 在窗口移动跨屏后的刷新时机（依赖 Avalonia 的 screen 缓存 + Reapply 的既有触发源）。
- `FocusScope`（各屏独立/全局统一判定方式切换）不在本切片：观察层始终 per-screen，判定方式若需用户可切属后续切片。

## 5. 可测性与验证

- **新检查套件**：`tests/PerScreenThemeChecks`（独立 console 检查项目，模式同 `ThemeSettingsChecks`/`FocusSwitchChecks`；**未登记 sln**，由主代理统一登记）。
- 运行：`dotnet run --project tests/PerScreenThemeChecks`
- 覆盖（约 30 条断言）：每屏判定矩阵（本屏占用/邻屏清空/无映射回退）；纯主题解析矩阵（两窗两屏两状态→两主题不同、同屏→同主题、无屏幕→uniform、功能关→共享主题、材质快照携带强调色/不透明度）；实例路径（假 `WindowScreenResolver` + `InjectSnapshotForChecks` 假快照）；窗口状态应用（逐窗变体 Light/Dark、逐窗 accent ramp、多彩窗口并字典 + 标记、功能关回退清除）；重排唤醒（{A}→{B} 唤醒、同快照不唤醒）；uniform 兼容（旧 API 断言）。
- 既有回归面：`tests/FocusSwitchChecks`（uniform 判定/状态主题/资源作用域/开关刷新全部保留，旧 API 未变）、`tests/UiFoundationChecks`（PanelShell 的 `ResolveEffectiveTheme` 旧签名断言不变）。
