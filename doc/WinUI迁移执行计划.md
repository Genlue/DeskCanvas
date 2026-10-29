# DeskCanvas WinUI 3 迁移 — 执行计划与任务板

本文件是**执行过程中的唯一协调文档**（谁做什么、做到哪、什么不能碰）。
需求细节以 `C:\Users\25659\Desktop\DeskCanvas_WinUI3_GUI迁移报告.md` 为准：
报告的章节号在下面用「报告 §x」引用，不在这里重复抄写。

---

## 0. 环境事实（2026-10-29 实测，勿凭记忆改写）

| 项 | 实测结论 |
|---|---|
| .NET SDK | `10.0.303`（global.json 要求 10.0.100 + rollForward latestFeature，可用） |
| 目标框架 | 新入口 `net10.0-windows10.0.19041.0`；原宿主仍是 `net10.0` |
| Windows App SDK | 用 `Microsoft.WindowsAppSDK 2.6.4`（当前稳定）；Avalonia.WinUI 12.1.4 声明的最低依赖是 1.8.260209005 |
| Avalonia | 12.1.4（与宿主/组件一致） |
| 部署形态 | **unpackaged**：`WindowsPackageType=None` + `WindowsAppSDKSelfContained=true` + `SelfContained=true`。只设 `WindowsPackageType=None` 时实机会弹「This application could not be started / Required components」，因为引导器找不到可用的 Windows App Runtime 主包；自包含后正常启动 |
| 混合渲染 | WinUI 窗口内 `AvaloniaSwapChainPanel` 能显示 Avalonia 控件（已截图验证，ANGLE/GL 交换链） |
| 构建工具 | 机器上**没有 Visual Studio**，只有 dotnet CLI；XAML 编译器由 WindowsAppSDK 包自带，`dotnet build` 可用 |

### 0.1 三条硬约束（踩过的坑）

1. **命名空间冲突**：WinUI 工程里 `using Avalonia.Controls;` 会让 `Window`/`Button`/`Grid`/`Application` 与
   `Microsoft.UI.Xaml` 同名类型歧义（CS0104）。新 GUI 的 `.cs` 只 `using Microsoft.UI.Xaml...`；
   需要 Avalonia 类型时用**别名**（`using AvButton = Avalonia.Controls.Button;`）或全名限定。
2. **XamlCompiler WMC9999**：XAML 编译阶段的「未将对象引用设置到对象的实例」几乎总是 C# 编译失败的
   连带错误（WMC1509/9999 一起出现）。先修 C# 错误，别去改 XAML。
3. **构建必须串行**：多个执行者共用 `src/DeskCanvas.WinUI/obj`、`bin`。一律用
   `pwsh scripts/dev/winui-build.ps1`（内部独占锁排队），禁止直接 `dotnet build` 这个工程。

### 0.2 混合宿主的已知副作用

`AvaloniaSwapChainPanel` 的静态构造会把 Avalonia 的 `IPlatformDragSource` 换成 `WinUIDragSource`
（只认识嵌入面板）。按报告 §3 第一步处理：初始化 Win32 平台后保存原拖放服务，容器初始化后安装
路由实现——来源属于已注册嵌入预览用集成包服务，普通桌面组件用原 Win32 服务。

---

## 1. 不变量（所有执行者必须遵守）

1. **职责边界**：桌面组件（卡片、正文、尺寸档位、拖拽、渲染算法）继续用 Avalonia，不重写。
   软件操作界面（设置、组件编辑窗口、组件菜单、二级面板、通用弹窗、快捷指令编辑器、托盘菜单）必须是 WinUI。
   允许的 Avalonia 嵌入只有：组件库真实预览、纯背景材质绘制（报告 §1）。
   **2026-10-29 用户两次澄清后的口径：**
   - 聚焦搜索的**使用场景**（`SpotlightWindow` 的搜索框、应用网格、结果列表/菜单/预览、分类球、唤出与隐藏）
     与组件同等对待，**继续用现有 Avalonia 实现，不迁 WinUI**；报告 §3 第五步的聚焦搜索条目作废。
   - 「设置 → 聚焦搜索」**设置页仍然是 WinUI 3**（属于设置主窗口的一部分），键 `SettingsPageKeys.Spotlight`。
     因此 `SpotlightService/SpotlightViewModel` 的快捷键、索引、外观等**配置项**要迁到 WinUI 设置页，
     而**搜索结果/网格/预览的呈现**留在 Avalonia。
2. **数据与存档**：`IAppSettingsProvider`/`IWidgetLayoutProvider`/各 Store/现有 JSON 是唯一事实源，
   不为 GUI 迁移新建一套存档或同步机制。提交点语义不变（滑块拖动完成才保存、初始化不保存、
   保存失败要显示错误而不是关窗）。
3. **旧类型身份**：`WidgetInfoAttribute`、组件类型名、`assembly:settingsViewFullName` 之类的键不变。
   旧 DLL 可能直接调用旧窗口类的 public/static 方法——需要兼容的类保留原签名并转发到新 UI；
   生产路径不得再实例化旧 Avalonia 设置窗口/面板。
4. **不反向依赖**：`DeskCanvas.Core`/`Contracts`/`Runtime`/`DeskCanvas.UI`/组件 都不引用 `DeskCanvas.WinUI`。
   组件通过 `ISoftwareUi` 与既有服务访问新界面。
5. **页面生命周期**：设置窗口关闭 = 隐藏 + 卸下页面内容并停止订阅；重开恢复上次页面。
   WinUI 的导航缓存不能代替这套释放逻辑（报告 §3 第二步）。
6. **本地化**：新 UI 文案走 `Locale` 资源（`x:Static`/`text VM`），不写死中文；
   守卫会扫描新的 `.xaml`（见 §4）。
7. **不改无关代码**：只动自己任务清单内的文件；不重排格式、不批量重命名、不删除别人的进行中文件。
8. **不执行 git 写操作**（commit/reset/checkout/stash）。提交由协调者统一做。

---

## 2. 目标结构

```
src/DeskCanvas/                     原宿主 → OutputType=Library，程序集名仍是 DeskCanvas
  Services/ISoftwareUi.cs           ★ 窄接口（已建）：OpenSettings/ActivateSettings/RequestExit
  Services/SettingsPageKeys.cs      ★ 页面稳定键（已并入 ISoftwareUi.cs）
src/DeskCanvas.WinUI/              ★ 新 GUI 入口（net10.0-windows…，唯一启动入口）
  Program.cs / App.xaml(.cs)       启动顺序与生命周期协调
  Host/                            Avalonia 引导、供应商桥、窗口互操作、拖放路由、WinUI 版 ISoftwareUi
  Controls/                        公共控件：SettingRow、LabeledSlider、ColorInput、CollapsibleSection、
                                   SecondaryPanelShell、ThemeButton、SharedStatusPresenter、图标/卡片样式
  Styles/ThemeResources.xaml        语义资源 Token（圆角/间距/字体）
  Settings/                        SettingsWindow + 页面工厂 + 系统设置页
  WidgetEditors/                   组件编辑窗口、编辑页注册表、内置组件的新编辑页
                                   （目录名**不能**叫 Widgets：WinUI 会把 XAML 的 XBF 按相对路径输出到
                                   bin\...\Widgets\**，而 Const.WidgetsFolder 用「exe 旁边有没有 Widgets 目录」
                                   判断 portable 模式，会连带把数据目录和组件扫描判断带偏）
  Panels/                          13 个二级面板
  Spotlight/                       聚焦搜索
  Shortcuts/                       快捷指令编辑器
  Menus/                           组件右键菜单宿主、手写菜单桥
```

原 `src/DeskCanvas/Views/Settings.axaml`、`EditWidget`、各弹窗在迁移完成前保留为**兼容/回退**，
但生产入口不得再打开它们；第六步统一删除（或保留转发桩）。

---

## 3. 任务板

状态：`TODO` / `WIP` / `DONE` / `BLOCKED`。每完成一项由协调者更新（执行者不要改本文件）。

### 第 0 步：地基（协调者 + 并行执行者）
| # | 任务 | 状态 | 负责 | 主要文件 |
|---|---|---|---|---|
| F0 | WinUI 工程骨架、CPM 版本、sln 映射、自包含部署冒烟 | DONE | 协调者 | `src/DeskCanvas.WinUI/*`、`Directory.Packages.props`、`src/DeskCanvas.sln` |
| F1 | 构建串行化脚本 | DONE | 协调者 | `scripts/dev/winui-build.ps1` |
| F2 | `ISoftwareUi` 窄接口 + 页面键 | DONE | 协调者 | `src/DeskCanvas/Services/ISoftwareUi.cs` |

### 第一步：可运行的混合入口（报告 §3 第一步）
| # | 任务 | 状态 | 负责 | 说明 |
|---|---|---|---|---|
| S1.1 | 宿主工程改 Library，入口迁到 WinUI；单实例/数据初始化/自启动顺序保持 | DONE | A | `src/DeskCanvas/Services/AppStartup.cs`；旧 `Program.Main` 行为不变 |
| S1.2 | WinUI 入口内初始化 Avalonia（`SetupWithLifetime` + Classic lifetime，不调 `Start()`），构建原 DI 并启动组件/watcher/托盘/搜索 | DONE | A | `Host/AvaloniaHost.cs`；DI/启动编排仍留在 `App.OnFrameworkInitializationCompleted` |
| S1.3 | `ISoftwareUi` 的 WinUI 实现 + 旧路径转发 | DONE | A | `Host/WinUiSoftwareUi.cs`；二启动/--settings/无组件/Spotlight 全部走接缝 |
| S1.4 | 拖放服务路由（嵌入预览 → 集成包；桌面组件 → 原 Win32） | DONE（骨架） | A | `Host/DragSourceRouter.cs`；**每个 `new AvaloniaSwapChainPanel` 后必须调用 `EnsureInstalled()`**（S5 待接入） |
| S1.5 | 冒烟开关：`--smoke=N` 自动退出、退出后无残留进程 | DONE | A | `Host/SmokeLog.cs`、`scripts/dev/smoke-winui.ps1` |
| S1.6 | 守卫脚本覆盖 WinUI 范围（结构/UI 规范/verify/build.ps1） | DONE | B | 守卫扫描 WinUI `.xaml`；`verify.ps1 -BuildOnly` |

### 第二步：公共控件 + 设置窗口（报告 §3 第二步）
| # | 任务 | 状态 | 负责 | 说明 |
|---|---|---|---|---|
| S2.1 | 公共控件与会话资源 | DONE | C | `Controls/`（9 类控件 + `DevelopmentControlGallery`）、`Styles/ThemeResources.xaml` |
| S2.2 | `SettingsWindow`（NavigationView + 内容区 + 关闭隐藏/卸页/恢复页面） | WIP | D | `Settings/` |
| S2.3 | 页面目录与工厂（系统页 + 组件库分组，读 `WidgetInfoAttribute`） | WIP | D | `Settings/SettingsPageCatalog.cs`；要求页面**自注册**，页面执行者只加自己的文件 |
| S2.4 | General 页 | DONE | G | 语言/默认地区搜索选择清除/自启动/更新周期/代理/连通性/检查状态；VM 只加 3 个静态入口 |
| S2.5 | Appearance 页（材质/明暗/强调色/失焦/玻璃参数/字体/圆角/标题栏） | DONE | H | draft/commit、保存失败重试、UIA 实测写入；VM +49/−4（构造重载 + Dispose） |
| S2.6 | Advanced 页（网格/全屏/托盘/备份/数据目录） | TODO | 待排 | 显示 PagePlaceholder |
| S2.7 | Profiles 页（方案增删改导入导出） | TODO | 待排 | 显示 PagePlaceholder |
| S2.8 | MultiScreen 页（显示器绑定/布局迁移/导入导出） | TODO | 待排 | 显示 PagePlaceholder |
| S2.9 | Spotlight 设置页（录制/外观/索引/剪贴板/分类/玻璃） | TODO | 待排 | 显示 PagePlaceholder；**搜索窗口本身保持 Avalonia** |
| S2.10 | About 页 + 更新呈现 | DONE（更新窗口除外） | G | 版本/更新探测/仓库链接/卸载沿用 Inno unins000；统一 UpdatePopup 仍属 S4.3 |

### 第一步补充：内置组件加载桥（报告 §3 第一步 5/6）
| # | 任务 | 状态 | 说明 |
|---|---|---|---|
| S1.7 | WinUI 显式引用 26 个内置组件工程 + `WidgetAssemblyRegistry.Preload()` | DONE | E | 程序集名 ≠ 目录名：`Fixed→FixedWidgets`、`Stack→StackWidgets` |
| S1.8 | `AssemblyProvider` 优先 Default 上下文 + `GetAssemblyInfos` 合并内置清单 + 卸载改为重启生效 | DONE | E | 公开 API 未变（`ApiSnapshotChecks` 通过）；无预加载时行为与迁移前逐字一致 |
| S1.9 | `--dump-widgets=<path>` 导出编辑页覆盖率 | DONE | E | 实测：26 程序集 / 46 组件 / **45 个可编辑** / **37 个不同设置页**（与报告一致） |

### 第三步：组件菜单与编辑窗口（报告 §3 第三步）
| # | 任务 | 状态 | 说明 |
|---|---|---|---|
| S3.1 | 宿主调用链：`WidgetFactory` 编辑能力 + `Widget.EditWidget/OpenSettings/OpenScaleDialog` 转发 | DONE | F | `WidgetEditorRequest.cs`；`Widget.axaml.cs` +53/−14、`WidgetFactory` +106/−14；缩放弹窗返回 NotImplemented（第四步） |
| S3.2 | WinUI 编辑页注册表 `(assembly, viewTypeName)` + 覆盖率检查 | DONE | F | 自注册 + `--check-editors`：37 页 / 已迁移 4 / 未迁移 33 / 插件需适配 0 / 过期注册 0 |
| S3.3 | WinUI `EditWidgetWindow`（560×640、滚动正文、`WindowGeometryStore` 旧 key） | DONE | F | 实测几何 key 仍为 `Clock:Clock.Views.Settings.DigitalClockSettings`，改尺寸后新进程精确恢复 |
| S3.4 | 37 个组件设置页分批迁移 | **4/37 DONE**（Clock 全部） | 余 33 页打开为“未迁移”说明页 |
| S3.5 | 三个专门控件：裁剪预览、创作画板、ControlCenter 布局画布 | TODO | |
| S3.6 | 组件右键菜单 + `MenuHostWindow` + 组件内手写菜单 | TODO | |
| S3.7 | Stack 子项编辑（父 InstanceId + session token 定位） | TODO | |

### 第四步：二级面板与弹窗（报告 §3 第四步）
| # | 任务 | 状态 | 说明 |
|---|---|---|---|
| S4.1 | WinUI `SecondaryPanelWindow` + `PanelService` 句柄化适配 | TODO | |
| S4.2 | 13 个组件二级面板 | TODO | |
| S4.3 | Confirm/Input/Scale/ProcessPicker/WallpaperAlign/Update/GridEditor | TODO | |
| S4.4 | 课表指南与导入预览、工作流导入、`IUserPromptService` WinUI 实现 | TODO | |
| S4.5 | `WidgetZOrder` 原生句柄注册/注销 + 窗口角色表 | TODO | |

### 第五步：组件库 / 快捷指令编辑器（报告 §3 第五步，聚焦搜索部分已按用户指示取消）
| # | 任务 | 状态 | 说明 |
|---|---|---|---|
| S5.1 | 组件库真实预览（`AvaloniaSwapChainPanel` + 懒创建/离页释放） | TODO | |
| S5.2 | 聚焦搜索窗口与结果菜单/预览 | **CANCELLED（用户 2026-10-29 指示）** | 搜索框/应用网格/结果与预览继续用 Avalonia；相关图像边界、MenuHost 适配、Spotlight 预览/菜单迁移都不做 |
| S2.9 | 设置 → 聚焦搜索页（快捷键/外观/索引/剪贴板/分类） | 仍在范围 | 页面是 WinUI，配置项照旧写入原 Store；预览按钮若需真实搜索窗口，调用现有 Avalonia 窗口 |
| S5.3 | 快捷指令编辑器（画布/节点/连线/缩略图/参数弹层） | TODO | |

### 第六步：切换入口、打包、清理（报告 §3 第六步）
| # | 任务 | 状态 | 说明 |
|---|---|---|---|
| S6.1 | Win32 托盘服务替换 Avalonia TrayIcon | TODO | |
| S6.2 | 生命周期协调器（退出/重启/安装后退出） | TODO | |
| S6.3 | `build.ps1` 发布目标、安装器收集完整 publish 目录 | TODO | |
| S6.4 | 删除失去用途的旧 GUI；兼容桩保留原签名 | TODO | |
| S6.5 | 实际冒烟（报告 §6 的 8 条）与结果记录 | TODO | |

---

## 4. 构建与验证

```powershell
# 只构建新 GUI（必须走这个脚本：内部排队等锁）
pwsh scripts/dev/winui-build.ps1

# 结构守卫 / UI 规范守卫（必须保持通过；新范围要写进守卫而不是关掉守卫）
pwsh scripts/check-structure.ps1
pwsh scripts/check-ui-conventions.ps1

# 完整验证（很慢：40 个检查工程；阶段性跑）
pwsh scripts/verify.ps1
```

**错误归属**：并行时构建会编译别人正在写的文件。只看自己路径下的错误；
别人的错误忽略并继续（不要修、不要删、不要注释掉）。

---

## 5. 风险与待确认

| # | 事项 | 状态 |
|---|---|---|
| R1 | ClassicDesktopStyleApplicationLifetime + `SetupWithLifetime` 不调 `Start()` 时，桌面组件窗口与 Avalonia 计时器是否正常（官方样例用 SingleViewLifetime，未实测本项目组合） | 待第一步冒烟确认 |
| R2 | 嵌入面板用 ANGLE/GL，桌面窗口用 WinUIComposition——同进程两套渲染路径的稳定性 | 待第二步组件库预览时确认 |
| R3 | 组件库预览拖放到 Stack（拖放路由） | 待确认 |
| R4 | 顶层窗口逐像素透明 / 液态玻璃背景嵌入（报告 §4.1 两个障碍） | 待外观页迁移时确认 |
| R5 | 第三方插件只有 Avalonia 编辑页时无法自动转换：保留加载并提示需适配 | 已知限制，需写入迁移说明 |
| R6 | 内置组件 DLL 热替换改为下次重启加载 | 已知限制，需写入迁移说明 |

---

## 6. 验证记录（协调者逐轮补）

### 第 1 轮（地基 + 第一步 + 控件 + 设置外壳）

| 检查 | 命令 | 结果 |
|---|---|---|
| 构建 | `pwsh scripts/dev/winui-build.ps1` | exit 0，0 警告 0 错误 |
| 结构守卫 | `pwsh scripts/check-structure.ps1` | exit 0（26 组件；host=Library；只有 WinUI 可引用组件） |
| UI 规范守卫 | `pwsh scripts/check-ui-conventions.ps1` | exit 0（184 Avalonia .axaml + 12 WinUI .xaml） |
| 构建阶段验证 | `pwsh scripts/verify.ps1 -BuildOnly` | 4/4 PASS |
| 混合宿主冒烟 | `pwsh scripts/dev/smoke-winui.ps1 -Seconds 8` | exit 0；9.7s 自行退出；残留进程 0；日志 6 阶段齐全 |
| 二次启动接管 | 启动后再启动一次 | 第二次 0.06s 退出，已最小化的设置窗口被拉回前台 |
| 控件目检 | `DevelopmentControlGallery` | 浅/深主题截图确认（行布局、窄宽换行、滑块+NumberBox、非法色值内联错误） |

**本轮暴露并已修复的问题**
1. 宿主是 `WinExe` 时自包含入口无法引用（NETSDK1150）→ 宿主改 Library。
2. 开发版与已安装成品版共用单实例作用域 → 加 `DESKCANVAS_INSTANCE_SCOPE` 覆盖（`SingleInstance`）。
3. 冒烟与并行构建抢 `bin/` → 冒烟脚本也走构建锁。
4. `dotnet publish` 不带出 `.pri`/`.xbf`；只重命名 apphost 会因 PRI 名不匹配崩溃 `0xC000027B` → `build.ps1` 显式收集并补 `DeskCanvas.pri` 别名。

**已知缺口（本轮不修，登记在案）**
- 托盘「打开设置」仍是旧 Avalonia 设置窗口（第六步 S6.1 替换为 Win32 通知图标 + WinUI 菜单）。
- `WinUiSoftwareUi` 目前按方法名反射调用 `SettingsWindowHost.CloseForShutdown()`，待接口化。
- 嵌入面板（组件库预览）尚未接入，拖放路由的 `EnsureInstalled()` 还没有调用点。
- 内置组件加载桥（S1.7–S1.9）本轮未做，日志里 widgets-shown=0。
- 安装器是否随包复制 `Widgets/`（影响 portable 数据目录规则）留给第六步 7 决定。
### 第 1 轮补充：内置组件加载桥验证

| 检查 | 结果 |
|---|---|
| `--dump-widgets` | 26 程序集 / 46 组件 / 45 个 editType 非空 / 37 个不同设置页，`loadedFrom` 全部为 exe 目录 |
| 重复程序集回归 | 往 `DataFolder\\Widgets` 塞 40 个 DLL 后仍 26 程序集/46 组件；`LoadFrom` 与 `LoadAssembly` 返回同一实例（ReferenceEquals=True） |
| 桌面组件实机 | 冒烟数据里 3 个组件窗口（Clock Digital 400×200、Notes Note 420×320、Calendar Date 205×205）真实渲染 |
| API 基线 | `tests/ApiSnapshotChecks` ALL PASS（Core 未增公开成员） |

**待办（第六步）**：发布/安装器必须把这 26 个组件 DLL 留在 exe 根目录，否则 `Preload` 全部失败、`LoadAssembly` 退回插件上下文，两份实例的问题会悄悄回来；`build.ps1` 应加这条校验。
---

## 7. 第 2 轮验证记录（设置页 + 编辑窗口）

| 检查 | 命令 | 结果 |
|---|---|---|
| 构建 | `pwsh scripts/dev/winui-build.ps1` | exit 0，0 警告 0 错误 |
| 结构守卫 / UI 规范 | `check-structure.ps1` / `check-ui-conventions.ps1` | exit 0（26 组件；184 Avalonia .axaml + 17 WinUI .xaml） |
| 构建阶段验证 | `pwsh scripts/verify.ps1 -BuildOnly` | 4/4 PASS |
| 设置页冒烟 | `smoke-winui.ps1 --settings=General/About/Appearance` | 三次 exit 0、残留 0、窗口 1522×1031 物理 = 1000×650 DIP @150% |
| 编辑窗口冒烟 | `smoke-winui.ps1 --edit-widget=Clock/Digital` | exit 0；窗口 560×640 DIP 起、滚动正常、几何记忆生效 |
| 旧页语义回归 | `SettingsIntegrationChecks` / `UiFoundationChecks` / `WindowRoleChecks` / `LayoutOwnershipChecks` / `GeometryThreadChecks` / `ThemeSettingsChecks` / `SettingsReleaseChecks` / `ApiSnapshotChecks` | 全部 PASS |
| 编辑页覆盖率 | `--check-editors=artifacts/smoke/editors.json` | total 37 / migrated 4 / notMigrated 33 / plugin 0 / errors 0 / stale 0 |

**本轮修掉的集成问题**
1. 设置窗口首帧尺寸在 150% 缩放下偶尔按 96 DPI 落地（1014×687 物理）→ `CorrectInitialSizeOnce` 改为取 `GetDpiForWindow` 与 `XamlRoot.RasterizationScale` 的较大值，并在缩放稳定前允许再次校正。
2. 冒烟脚本在非 DPI 感知的 PowerShell 里截图被虚拟化裁切 → 脚本先声明 PerMonitorV2 再量窗口。
3. `WinUiSoftwareUi` 里按方法名反射关闭设置窗口 → `CloseForShutdown` 提升到 `ISettingsSurface`。
4. `--settings=<pageKey>` 直达开关（页面开发/冒烟用）与 `DESKCANVAS_INSTANCE_SCOPE` 隔离。

**仍存在的具体缺口（按报告步骤）**
- 第二步：Advanced / Profiles / MultiScreen / Spotlight 四个系统页未迁（打开为“未迁移”说明页）；分组标题专用 Locale 键未加（沿用“设置/组件”）。
- 第三步：其余 33/37 组件编辑页未迁；三个专门编辑控件（裁剪 / 创作画板 / ControlCenter 布局）未做；组件右键菜单仍是旧 Avalonia 菜单；组件“缩放”弹窗未实现（返回 NotImplemented 并记录）。
- 第四步：13 个二级面板、Confirm/Input/Scale/ProcessPicker/WallpaperAlign/UpdatePopup/GridEditor、课表指南与导入预览、工作流导入、IUserPromptService 全部未迁；WindowRoles/HWND 版与 z-order 未接。
- 第五步：组件库真实预览（AvaloniaSwapChainPanel + 拖放路由 EnsureInstalled）未接入；快捷指令编辑器未迁；**聚焦搜索按用户指示保持 Avalonia**（只迁其设置页）。
- 第六步：托盘仍是 Avalonia TrayIcon；生命周期协调器只覆盖退出；build.ps1 已改造但未端到端实跑；安装器未更新；旧 GUI 未删除。

---

## 8. 构建/运行入口（给使用者）

```powershell
# 1) 只构建 Debug 入口（独占锁，多个执行者并发时自动排队）
pwsh scripts\dev\winui-build.ps1
#    产物：src\DeskCanvas.WinUI\bin\Debug\net10.0-windows10.0.19041.0\win-x64\DeskCanvas.WinUI.exe

# 2) 启动开发版（隔离数据目录 %LocalAppData%\DeskCanvas-dev + 独立单实例作用域）
pwsh scripts\dev\run-winui.ps1
pwsh scripts\dev\run-winui.ps1 -Rebuild -Settings General     # 先构建并直达某页
pwsh scripts\dev\run-winui.ps1 -Arguments '--edit-widget=Clock/Digital'
pwsh scripts\dev\run-winui.ps1 -Live                          # 用真实数据（会与安装版共用）

# 3) 无头冒烟（隔离数据、自动退出、抓窗口截图、查残留进程）
pwsh scripts\dev\smoke-winui.ps1 -Seconds 12 -Arguments '--settings=Appearance','--smoke=12000'

# 4) Release 目录发布（产品名仍是 DeskCanvas.exe，落在 dist\<Runtime>\）
pwsh build.ps1
```

注意：原 `src/DeskCanvas` 已改为库，不再生成 exe（报告 §3 第一步 2）；新增/修改界面后必须重新跑
步骤 1 或 4，旧 exe 不会被更新。
