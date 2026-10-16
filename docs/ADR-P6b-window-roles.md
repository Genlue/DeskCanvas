# ADR-P6b:窗口角色登记与平台能力边界(WindowRoles)

状态:已实施(2026-10-16,P6 切片 2)。行为保持的声明式登记;档案驱动的行为迁移留待后续切片。真机矩阵未执行(见 docs/真机验收清单.md)。

## 决策

### 1. 六类窗口角色 + 能力档案,单一登记表

`DeskCanvas.Services.WindowRoles` 建立唯一登记:每类窗口声明角色,角色映射能力档案 `WindowRoleProfile(ShowInTaskbar, ZBand, ActivatesOnShow, TracksFocusTheme, CloseOnDisplayChange)`:

| 角色 | Taskbar | ZBand | 激活 | 失焦主题跟踪 | 显示变更关闭 | 依据(切片时点) |
|---|---|---|---|---|---|---|
| DesktopWidget | 否 | BottomBand | 否 | 是 | 否 | Widget.axaml:8 / :254 / :245;屏幕变更走挂起不关闭 |
| SecondaryPanel | 否 | AboveWidgets | 是 | 是 | 是 | SecondaryPanelWindow.cs:402 / :450 / :102 |
| Settings | 是 | None | 是 | 否 | 否(隐藏复用) | Settings.axaml.cs:114/:118/:290 |
| Dialog | 否(模态基线) | None | 是 | 否 | 否 | ConfirmDialog/InputDialog/ScaleDialog/ProcessPickerDialog/GuideWindow/ImportReviewWindow;EditWidget、ShortcutsEditorWindow 为 Taskbar=true 例外,GridEditor/WallpaperAlignDialog/UpdatePopup 非 Show()——**例外由注册点承载,档案记录基线** |
| Preview | — | — | — | — | — | 预留占位,全惰性 |
| Anchor | 否 | BottomBand | 否 | 否 | 否(全生命周期) | WidgetAnchorWindow.cs:21-55;App.axaml.cs:199 与组件同带钉扎 |

- `ZBand` 枚举(None/BottomBand/AboveWidgets)对应 WidgetZOrder 的两条钉扎路径;Anchor 属 BottomBand(带判定谓词需要它,避免"可见非成员在组件之下"误判)。
- `TracksFocusTheme` 对应 DesktopFurniture.Attach → FocusThemeCoordinator.Track。
- 档案值**如实记录现状**,不改变任何行为;后续切片把命令式调用点改为档案读取时,档案即唯一真源。

### 2. 注册语义与接线

- `Register(Window, WindowRole)` 幂等(ConditionalWeakTable,弱键不延寿;同窗重注册覆盖,创建者说了算);`GetRole`/`ProfileOf` 未注册返回 null;`ResetForChecks` 仅供夹具。
- 接线点(全部一行,行为保持):`Widget.OnOpened`(DesktopWidget)、`SecondaryPanelWindow.InitializePanelCore`(SecondaryPanel)、`Settings`(构造)、`App` 锚点创建(Anchor)、11 个对话框/编辑器构造器(Dialog:ConfirmDialog、InputDialog、ScaleDialog、ProcessPickerDialog、WallpaperAlignDialog、UpdatePopup、EditWidget、GridEditor + 组件侧 ShortcutsEditorWindow、GuideWindow、ImportReviewWindow)。
- 组件侧对话框窗口(Shortcuts/Timetable)可引用宿主 Services(组件编译期引用宿主),故一并注册;其余组件自有窗口随 P8 批次注册。

### 3. 平台能力边界守卫:"组件内不重复 Win32 互操作"

长期方案 §13.1"宿主窗口协调器以角色调用平台端口,不在组件内重复 Win32 Hook"在本切片落为**可执行守卫**:`tests/WindowRoleChecks` 源码扫描 `src/Widgets/**/*.cs` 的 `[DllImport]`,白名单钉死现有 10 个文件及精确出现次数(PowerService 5、SystemControlService 13、WidgetOleDropTarget 12、FolderIconService 12、ClipboardNative 12、SystemExecutors 4、Folders/BigFolder/SingleFile/PictureView 各 1)。新增文件或新增声明即失败——组件平台互操作只能经白名单评审,渲染/窗口/层级互操作收敛到宿主(WidgetZOrder/InteropService/SecondaryPanelWindow)。

## 边界与未完成验收

- 本切片**不迁移行为**:zorder 钉扎、任务栏、激活、主题跟踪的调用点保持原样;档案消费(profiles 驱动行为)是后续切片,届时以"两个差异消费者"纪律推进。
- Dialog 档案记录模态基线;EditWidget/ShortcutsEditorWindow(有任务栏)与 GridEditor/WallpaperAlignDialog/UpdatePopup(非模态 Show)的例外值留在注册点——把例外收进档案需要 per-window profile 覆盖机制,待有第二个真实需求再做。
- Widget.axaml.cs 的职责收敛(五协作单元拆分)不在本切片,按执行计划推进时窗口角色已就位。
- 真机验收:多屏/焦点/原生行为矩阵(清单 M1/M2/M4 分区)未执行,不以无头检查宣称通过。

## 验证

- 新增独立夹具 `tests/WindowRoleChecks`(Release,注册进 verify.ps1/README/sln):**34/34**——六档案逐字段断言(硬编码钉死)、注册语义(幂等/覆盖/null/全角色往返/Reset/ArgumentNullException)、14 个接线点源码扫描、组件 DllImport 白名单双向扫描(无新增+无漂移)。
- 统一门禁 23 步执行结果见 P6 迁移报告。
