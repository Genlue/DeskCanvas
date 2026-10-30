# DeskCanvas 构建与模块化说明（个人使用版）

基于官方 [creewick/uWidgets](https://github.com/creewick/uWidgets) v0.6.0 的本地 fork。
仅保留 **简体中文 + 英语** 语言，并增设了自定义 **Folders**（文件夹）小组件。

## 目录结构（模块化）

```
├── Directory.Packages.props   ← 集中包版本管理 (CPM)，所有包版本只在这里改
├── Directory.Build.props      ← 组件 SDK 公共配置（含 OutputPath / 目录深度判定）
├── build.ps1                  ← 一键构建脚本（单 exe / 便携 zip）
├── scripts/
│   └── build-widgets-bundle.ps1 ← widget 打包脚本（被 MSBuild 目标调用）
└── src/
    ├── DeskCanvas.sln
    ├── DeskCanvas/              ← 主程序（Avalonia 桌面应用）
    │   └── Services/WidgetBundle.cs ← 单文件模式自解压逻辑
    ├── DeskCanvas.Core/         ← 小组件 SDK（插件契约：WidgetInfo/IAssemblyProvider/…）
    └── Widgets/                 ← 小组件，按 8 个分类分组，每个目录一个模块
        ├── ClockCalendar/       Clock  Calendar  TickClock
        ├── Weather/             Weather
        ├── SystemStatus/        Monitor  Batteries  Storage  ControlCenter
        ├── Productivity/        Notes  Reminders  Pomodoro  Habits  Countdown  Timetable  TimeProgress
        ├── Launcher/            Folders  Search  Shortcuts
        ├── Lifestyle/           Music  AudioVisualizer  Picture  Map  Hitokoto
        ├── Utilities/           Toolbox            ← 剪贴板 / 翻译 / 计算器三合一
        └── Container/           Aggregate  Stack    ← 聚合卡 / 组件重叠
```

### 组件目录规范

1. **第一层 = 分类**，与 `WidgetInfoAttribute.Category` 一一对应（`WidgetCategory_ClockCalendar` ↔ `ClockCalendar/`），
   顺序与 `Views/Pages/Gallery.axaml.cs` 的 `CategoryOrder` 一致。
2. **第二层 = 组件**，PascalCase 语义名词：不带 `Widget`/`Widgets` 后缀（在 `Widgets/` 下冗余），
   不用缩写、版本号，不用 `Tools` / `Common` / `Misc` 这类泛词。
3. **目录名必须等于 csproj 名**（`Widgets/<Category>/<Widget>/<Widget>.csproj`）——打包脚本按此定位工程。
4. **程序集身份冻结**：目录可以为了准确而改名，但 `AssemblyName` / `RootNamespace` 一律保持历史值
   （如 `Container/Aggregate/Aggregate.csproj` 里仍是 `FixedWidgets`，`Utilities/Toolbox` 里仍是 `Tools`）。
   DLL 名、`layout.json` 里保存的历史类型引用、热更新路径都依赖它，改名只发生在路径层。
5. 组件产物路径由 `Directory.Build.props` 统一给出，工程文件里不再写 `<OutputPath>`；
   新增一层目录也不会影响产物落点。
6. 新增小组件：复制任一组件目录结构，修改 `AssemblyInfo.cs` 里的 `[WidgetInfo(...)]` 与 `[Locale(...)]`，
   加入 sln 对应分类的解决方案文件夹即可（打包目标递归发现全部 `*.csproj`）。

### 预览图

每个组件目录下的 `Preview/` 存放该组件**所有形态 × 所有预设尺寸**的预览图（不参与编译，不会被塞进 DLL）：

```
Clock/Preview/
├── analog-i@2x2.png
├── digital@2x1.png
├── digital@4x2.png
└── flip-clock@4x2.png
```

- 文件名：`<ViewType 的 kebab-case>@<列>x<行>.png`
- 尺寸口径：1 格 = 96 DIP（默认栅格 = 1920 宽屏的 5%），导出 2× 像素
- 统一深色卡底；无边框组件（`IFramelessWidget`）保持透明底
- 重新生成：`DeskCanvas.exe --export-widget-previews src/Widgets`（开发机便携模式下运行；
  内容取自当次运行的真实数据，联网组件需要网络可用）

### 模块边界
- 每个小组件是独立 `csproj`，产出 DLL，由主程序通过 `AssemblyLoadContext` 从 `Widgets/` 目录动态加载（`DeskCanvas.Core/Services/AssemblyProvider.cs`）。
- 小组件**不携带** Avalonia/Core 副本（`PrivateAssets=all` + `ExcludeAssets=runtime`），运行时由宿主提供。
- 新增小组件：复制 `Widgets/ClockCalendar/Clock/` 结构，修改 `AssemblyInfo.cs` 里的 `[WidgetInfo(...)]` 与 `[Locale(...)]`，加入 sln 即可（打包目标自动发现）。

## 打包：单文件 exe

`build.ps1` 默认产出**自包含单文件 exe**（.NET 10 运行时打进 exe，目标机无需安装任何东西）：

> 所有构建/校验脚本都要求 **PowerShell 7（`pwsh`）**。用 5.1 启动（例如资源管理器右键「使用 PowerShell 运行」——该动词写死在 `System32\WindowsPowerShell\v1.0\powershell.exe`）时，脚本会经 `scripts/ensure-pwsh7.ps1` 检测到并在 pwsh 下自动重启，脚本主体不会在 5.1 上执行。

```
./build.ps1                      # dist\win-x64\DeskCanvas.exe
./build.ps1 -Runtime win-arm64   # ARM64
./build.ps1 -Portable            # 额外产出官方式便携 zip（exe + Widgets + json）
./build.ps1 -Setup               # 生成 Inno Setup 安装包（发行版唯一形式）
./build.ps1 -Msi                 # 生成 Windows 标准 MSI 安装包 (dist\installer\...)
```

### 原理
1. `dotnet publish -r <rid> --self-contained true`，csproj 已开启 `PublishSingleFile` + `PublishReadyToRun` + `IncludeNativeLibrariesForSelfExtract`（.NET 运行时与 Avalonia 原生库随 exe 打包，运行时自动解压到临时目录）。
2. MSBuild 目标 `BuildWidgetsBundle`（挂 `AssignTargetPaths`）发布全部小组件 → 压缩为 `widgets.zip` → 作为内嵌资源打入 exe；`appSettings.json` / `layout.json` 默认值同样内嵌。
3. 启动时 `WidgetBundle.ExtractIfNeeded()`（`Program.Main` 最先执行）：
   - **便携模式**：exe 旁存在 `Widgets/` 文件夹 → 全部走本地文件（官方/开发模式，行为不变）。
   - **打包模式**：解压到 `%LocalAppData%\DeskCanvas\`，`.version` 标记记录 exe 版本，版本变化才重新解压。

### 热更新
打包模式下 widget 是普通磁盘文件（`%LocalAppData%\DeskCanvas\Widgets\`），直接替换 DLL 即可热更新，无需重新打包 exe。

### 注意
- **不要开启 Trim**（`PublishTrimmed`）：widget 类型靠反射激活，裁剪会破坏插件机制。
- `dotnet publish` 裸命令也能出包，但推荐 `build.ps1`（会清理散落文件，保证 dist 里只有一个 exe）。
- 构建产物（exe 与 dist 目录）会**清除强制完整性标签**：Windows 按 `min(启动方令牌 IL, 文件标签)` 创建进程，残留的 Medium 标签会把应用压到比提权 shell（UAC 关闭 / 提权 Explorer）更低，UIPI 随即拒绝其 `PrintWindow` 抓取壁纸宿主窗口——实时采样拿不到任何一帧。清除标签后进程跟随启动会话的完整性级别，与普通应用一致；Low 标签污染防护仍由脚本的 Repair 步骤兜底。

## 配置迁移（从旧便携版）
把旧目录的 `layout.json` 和 `appSettings.json` 复制到 `%LocalAppData%\DeskCanvas\` 即可无缝迁移（旧版 widget 位置/设置全部保留）；或直接把新 exe 放入旧目录走便携模式。

## 版本管理
- 主程序版本：`src/DeskCanvas/AssemblyInfo.cs`（`AssemblyVersion`），同时作为 widget 包重解压标记。
- 小组件版本：各自 `AssemblyInfo.cs`。
- 包版本：`Directory.Packages.props` 一处集中修改。

## UI 框架：Avalonia 12

- **Avalonia 12.1.4**（全家桶同版本）+ **SkiaSharp 3.119.4**（必须跟随 `Avalonia.Skia` 的依赖版本，2.88 已不受支持）。
- `Avalonia.ReactiveUI` 在 12 中拆出官方仓库，改为独立发版的 **`ReactiveUI.Avalonia`**。本项目只用 `ReactiveObject`，不涉及 Avalonia 集成类型。
- **`Avalonia.Diagnostics` 已被移除**：DevTools 转为 Avalonia Plus 商业包 `AvaloniaUI.DiagnosticsSupport`（`AttachDevTools()` → `AttachDeveloperTools()`）。本项目从未调用 DevTools，故直接去掉引用；需要运行时检查工具时自行加商业包。
- ReactiveUI 25 起不再依赖 `System.Reactive`，而代码里的 `observable.Subscribe(Action<T>)` 来自它，已在 `DeskCanvas.UI` 显式引用 `System.Reactive` 恢复。
- **离线/CI 构建**：Avalonia 12 的构建期遥测（`AvaloniaStatsTask`）在无外网时会长时间挂起（表现为构建卡在某个项目、CPU 几乎不动）。设环境变量 `AVALONIA_TELEMETRY_OPTOUT=1` 即可跳过，构建恢复正常耗时。
- `Window.SystemDecorations` / `ExtendClientAreaChromeHints` 已由 `Window.WindowDecorations`（`None` / `BorderOnly` / `Full`）+ `ExtendClientAreaToDecorationsHint` 取代；本项目把原来的 `NoChrome`（保留原生边框、隐藏标题按钮）映射为 `BorderOnly`。
- **SkiaSharp 3 的两个渲染语义变化**（本项目已适配）：
  - SkSL 的 `sample(shader, coord)` 已被 Skia m130+ 删除，子着色器采样改用 `content.eval(coord)`；
  - `SKImageFilter.CreateBlur(σ, σ, tileMode)` 带 tile mode 时会把钳制后的输入扩展到整个裁剪区（包括为模糊外溢预留的画布留白），导致留白区被内容填充；用不带 tile mode 的 `CreateBlur(σ, σ)` 才是 2.88 的旧语义（透明填充、模糊柔和外溢）。
  - `Screen` 变为 abstract 且构造函数 `private protected`：外部代码无法 new / 继承，测试需要假屏幕时用 `Avalonia.Platform.PlatformScreen` 反射构造（见 `tests/UiFoundationChecks/TestScreen.cs`）。

## 液态玻璃（1.7.0）

在 **外观 → 应用主题 → 液态玻璃** 启用。默认模糊度 12、折射 28%（= 实机约 9 DIP 的透镜位移）、边缘宽度 24、高光 65%（= 实机亮边线强度）、色散 18%（≈ 0.35 px，几乎不可见）、光照方向 225°（左上方）、边缘染色 50%（可选：按边缘内外的实际壁纸颜色采样并适当提饱和度，柔和地染出轮廓色，0 关闭）；每项支持滑块和数值输入，即时保存，可重置参数或刷新壁纸。若桌面被任务栏替换程序（myDockFinder 等）、壁纸引擎或 DWM 改动、自动对齐仍偏差，可用「壁纸对准校正…」按钮手动微调采样偏移（水平/垂直，±1000 DIP），调整即时全局生效并保存在玻璃参数里。颜色区的不透明度控制玻璃染色浓度，深浅背景色决定染色颜色；圆角沿用高级设置。液态玻璃**不绘制**「描边」高光环（那是毛玻璃主题的可选项，液态玻璃的边饰是材质自身的均匀亮边线）。

这是静态壁纸材质：优先抓取**真实合成桌面**（`PrintWindow(Progman)` / `WorkerW` 候选链的 1:1 位图，短 TTL 缓存）作为采样源——与 widget 实际背后的画面逐像素一致，不受壁纸布局差异影响（myDockFinder 等任务栏替换程序、壁纸引擎、DWM 的任意摆放规则都会导致"文件+注册表公式"摆放结果与真实显示错位，实测可偏差数百像素）；当 shell 或壁纸引擎运行在更高的完整性级别（提权 shell / UAC 关闭的会话）时 `PrintWindow` 被 UIPI 拒绝，此时自动切换为**屏幕 BitBlt** 抓取（对本进程所有窗口设 `WDA_EXCLUDEFROMCAPTURE`，保证玻璃不会采样到自己）；都不可用才回退到 Windows 壁纸文件+注册表样式。文字与图标独立绘制，保持清晰。切换主题、改变参数、移动和缩放窗口时重新生成缓存（含 2 秒桌面抓图缓存），不持续截图或动画。支持跨屏布局负坐标（抓图为整个虚拟桌面）及回退模式的填充、适应、拉伸、居中、平铺。

实现位于 `LiquidGlassRenderer`、`LiquidGlassWallpaper`（含 `DesktopCapturer`）、`LiquidGlassSurface`；光学设置保存在 `Theme.LiquidGlass`。光学模型按 **iOS 26 Liquid Glass**（Apple HIG + 逐项对照实机测量的复刻实现）搭建——**这是透镜，不是磨砂板**：圆角矩形距离场（平滑最大值求梯度，法线在圆角弧心与中轴线处连续）给出外向法线与内向深度，深度驱动三层叠加的边缘带——**透镜环**（`lensW` 宽，`bend = rim²` 向内位移，边缘放大、中心清晰）、**边缘反射带**（`0.7 × 短边` 宽，叠加 2× 位移，形成 iOS 药丸顶/底的倒影回声）、**边缘保护带**（最外侧几 px 把位移收敛到 0，既压噪点又让位移上升快于像素位置）；面部再做 **vibrancy**（过饱和透过的内容）+ **自适应霜化**（暗背景抬亮、永不是奶白填充），最后叠 **1 px 均匀亮边线**（`1 − smoothstep(0, 2 DIP, depth)`，与光照无关）+ 方向性镜面/Fresnel + 对角光泽带 + 背光侧内阴影；色散只有位移的 0.06 倍。模糊是**折射主导的轻雾**：`σ = 模糊度 × 缩放 / 8`（默认 12 → σ1.5 px，中心保留约 70% 壁纸结构），因为重模糊会把透镜要弯的细节先糊掉。SkiaSharp 版本与 Avalonia 自带版本保持一致，全部为 CPU 像素采样，不依赖自定义 GPU 着色器。后台任务合并高频修改，最多同时渲染两块材质，单块最多约 120 万像素；内容仍按原 DPI 渲染。

运行光学和配置回归检查（无需额外测试框架）：

```powershell
dotnet run --project tests/LiquidGlassChecks -c Release -- dist/glass-checks
```

检查配置兼容、参数保存与范围、圆角、模糊、折射、高光、色散、移动取样、纯色桌面回退及极端尺寸；`OpticsProfile` 进一步把「好不好看」变成可断言的数字：透镜剖面（平静边缘/肩部峰值/放大率/二阶差分）、圆角与中轴线法线连续性、渲染像素与生产位移公式预测的偏差、亮边线（峰值位置/FWHM/截白比例/迎光 vs 背光）、vibrancy 与自适应霜化、色散集中度与性能预算。同时生成 `liquid-glass-preview.png`（合成壁纸三卡片对比）、`liquid-glass-real.png`（真实壁纸四组参数 + ×3 边缘放大）、`optics-profile.png`（透镜与亮边线剖面）与 `optics-displacement.csv`。
