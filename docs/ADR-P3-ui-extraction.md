# ADR-P3：公共 UI 状态与 Avalonia 编辑器切片

状态：已实施公共控件切片（2026-10-16）；已实施设置行/颜色输入切片（2026-10-16 第二批）；完整组件目录迁移仍分批执行。

`DeskCanvas.UI` 保留已有无头 `SharedStatusPresenter`，并新增真正的 Avalonia `Controls.LabeledSlider` 与 `Styles/DesignTokens.axaml`。UI 项目只引用 Contracts 项目和中央管理的 Avalonia 11.1.3 包；不引用宿主、Runtime、组件、存储或原生平台服务。

滑块编辑器的 AXAML、值同步、步进取整、辅助功能与提交策略均在共享程序集。宿主 `DeskCanvas.Views.Controls.LabeledSlider` 继承共享实现，保留公开静态 StyledProperty 字段和原有 `LabeledSliderValueCommittedEventArgs`/`ValueCommitted` 签名。宿主事件仅转发共享提交，不保留第二套编辑逻辑。原控件 `avares://DeskCanvas/Views/Controls/LabeledSlider.axaml` 继续实例化兼容外观；原字典 `avares://DeskCanvas/Styles/DesignTokens.axaml` 合并共享字典，旧键和值保持兼容。App 的原 include 无需调整。

第二消费者并非样例替身：Clock 的 `FramelessClockSettings` 通过兼容类型使用百分比不透明度编辑，Picture 的 `PictureSettings` 使用小数裁剪/缩放编辑；Folders 的 `FolderSettings` 与 `SingleFileSettings` 还通过旧提交事件签名执行生产持久化。上述页面无需改变 namespace 即使用新程序集中的同一生产实现。

`src/DeskCanvas.UI/Samples/SharedComponentCatalog.cs` 提供可直接嵌入的百分比、可编辑小数与禁用态目录；同目录 README 记录新的 XAML namespace、资源引用和 Value/ValueCommitted 使用契约。

独立 `tests/UiSharedAvaloniaChecks` 使用 Avalonia.Headless，分别验证共享类型与旧宿主类型的实际控件：Fluent 模板解析、TwoWay 外部加载不提交、保留非网格外部值、编辑后绑定继续有效、数值 Enter 后失焦仅一次提交、键盘完成/拖动完成去重、辅助名称、非有限值恢复、全部 token 值兼容与旧控件 URI 可加载。提交计数对应页面持久化订阅边界，无真实用户文件写入。

验证命令：`dotnet build src/DeskCanvas.UI/DeskCanvas.UI.csproj -c Debug --no-restore`；`dotnet run --project tests/UiSharedAvaloniaChecks -c Release --no-restore`。共享项目已零警告/零错误构建；独立夹具 58/58 全部通过，进程退出码 0。已有 `UiSharedChecks` 继续覆盖 Clock/Countdown 状态协议。

## 第二批：Setting、SettingLayoutPanel、ColorInput（2026-10-16）

`DeskCanvas.UI.Controls` 新增真实 Avalonia `Setting`（行模板 + `SettingLayoutPanel` 自适应布局引擎 + `SettingLayoutMode/SettingEffectiveLayout/SettingLayoutSlot`）与 `ColorInput`（ColorPicker + Hex 草稿 + 内联校验的提交状态机，含 `ColorInputAlphaPolicy`/`ColorInputFormat`）。UI 项目为此新增中央版本管理的 `Avalonia.Controls.ColorPicker` 11.1.3 包引用（ColorPicker 控件主题所在包）；Contracts/UI/Runtime 依赖边界守卫不变，UI 仍不引用宿主、Runtime 或原生平台服务。

**用户可见文案是嵌入应用的责任，不是共享控件的责任。** 共享模板不再 `x:Static` 宿主 Locale：`Setting` 的 `AdminRequiredText`/`RestartRequiredText` 与 `ColorInput` 的 `InvalidFormatText` 是 StyledProperty 注入点，携带中性英文默认值供独立使用；宿主兼容外观在构造时写入 `Locale.Settings_AdminRequired`/`Locale.Settings_RestartRequired`/`Locale.Settings_ColorInput_InvalidFormat`，现有页面与组件无需任何改动即保持原中文/英文渲染。共享模板与 UI AXAML 不含任何 CJK 字面量（R7 守卫口径不变）。

宿主保留三个兼容外观类型，全部继承共享实现：

- `DeskCanvas.Views.Controls.Setting`：保留宿主 StyledProperty 字段别名与 legacy shape getters（`ShowIcon`/`SubtitleVisible`/`TitleRowSpan` 等），构造时注入本地化提示文案；旧 `avares://DeskCanvas/Views/Controls/Setting.axaml` 改为空 legacy 声明，继续可加载并实例化兼容外观。
- `DeskCanvas.Views.Controls.SettingLayoutPanel`：保留 `Slot` 附加属性别名（旧 AXAML `controls:SettingLayoutPanel.Slot="Icon"` 按宿主类型解析，属性身份与共享实现同一）；`GetSlot`/`SetSlot` 静态转发共享实现。宿主命名空间不再声明三个布局枚举，属性类型统一为共享枚举——AXAML 中的 `LayoutMode="Stacked"` 等字面量经属性类型转换继续可用，全仓库 C# 消费者（含检查程序）已改用共享类型别名。
- `DeskCanvas.Views.Controls.ColorInput`：保留宿主 StyledProperty 字段别名、旧 `ColorInputValueCommittedEventArgs`/`ValueCommitted` 事件签名（转发共享提交），构造时注入本地化错误文案；旧 `.../ColorInput.axaml` 改为空 legacy 声明。

`UiFoundationChecks` 的既有断言继续成立：`RedundancyGuardChecks` 的"LabeledSlider / ColorInput 与宿主同程序集"在兼容外观模式下仍为真（宿主类型是旧二进制的编译/装载面）；`Program.cs` 的自适应布局段与两批设置页检查改用共享 `SettingLayoutPanel`/枚举别名断言同一实现，页面断言（Setting 行数量、ColorInput 数量与格式策略）对兼容外观实例继续有效。

`SharedComponentCatalog` 扩展为 4 个滑块 + 2 个 Setting 行 + 2 个 ColorInput 的可嵌入样例；Samples README 记录 `ui:` namespace、注入文案策略与旧外观契约。

验证（2026-10-16）：`UiSharedAvaloniaChecks` 扩展至 **96/96**（新增 38 项：两种类型的宿主/共享基类关系与属性身份、Setting 模板/槽位/文案注入/Stacked 布局、ColorInput 外部加载不提交、Hex Enter 一次提交、重复 Enter 静默、无效草稿内联错误且不触碰模型、picker 同路提交、事件桥转发、只读、Preserve 保留前值 alpha、ForceOpaque、AllowEmpty 空值提交、两个旧 URI 加载、目录计数）；`UiFoundationChecks` 全部通过（含 W3 自适应布局与两批设置页对共享实现的断言）；`SettingsIntegrationChecks` 36/36 不受影响。解决方案 Debug 构建 0 警告 0 错误。

范围限制：未声明全部 Avalonia 控件、全部主题资源和全部组件已迁出；**SecondaryPanelShell 仍在宿主**——它依赖宿主 `LiquidGlassSurface`（玻璃渲染与 `LiquidGlassDispatch` 平台路径）、`PanelDragBehavior`、`PanelAppearance` 模型与 `PanelAppearanceResolver`，且被九个二级弹窗与 `SecondaryPanelWindow` 集成消费；迁移它需要先定义玻璃表面端口与拖动行为归属，作为独立后续切片执行，不在本批强行抽取。真机焦点、IME、多屏与 GPU 行为需后续人工回归。
