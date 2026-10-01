# DeskCanvas 构建与模块化说明（个人使用版）

基于官方 [creewick/uWidgets](https://github.com/creewick/uWidgets) v0.6.0 的本地 fork。
仅保留 **简体中文 + 英语** 语言，并增设了自定义 **Folders**（文件夹）小组件。

## 目录结构（模块化）

```
├── Directory.Packages.props   ← 集中包版本管理 (CPM)，所有包版本只在这里改
├── build.ps1                  ← 一键构建脚本（单 exe / 便携 zip）
├── scripts/
│   └── build-widgets-bundle.ps1 ← widget 打包脚本（被 MSBuild 目标调用）
└── src/
    ├── DeskCanvas.sln
    ├── DeskCanvas/              ← 主程序（Avalonia 桌面应用）
    │   └── Services/WidgetBundle.cs ← 单文件模式自解压逻辑
    ├── DeskCanvas.Core/         ← 小组件 SDK（插件契约：WidgetInfo/IAssemblyProvider/…）
    └── Widgets/               ← 小组件，每个目录一个模块
        ├── Clock/ Calendar/ Weather/ Monitor/ Notes/ Reminders/
        └── Folders/           ← 自定义：文件夹快捷方式 / 监视文件夹实时刷新
```

### 模块边界
- 每个小组件是独立 `csproj`，产出 DLL，由主程序通过 `AssemblyLoadContext` 从 `Widgets/` 目录动态加载（`DeskCanvas.Core/Services/AssemblyProvider.cs`）。
- 小组件**不携带** Avalonia/Core 副本（`PrivateAssets=all` + `ExcludeAssets=runtime`），运行时由宿主提供。
- 新增小组件：复制 `Widgets/Clock/` 结构，修改 `AssemblyInfo.cs` 里的 `[WidgetInfo(...)]` 与 `[Locale(...)]`，加入 sln 即可（打包目标自动发现）。

## 打包：单文件 exe

`build.ps1` 默认产出**框架依赖单文件 exe**（需目标机安装 .NET 8 运行时）：

```
./build.ps1                      # dist\win-x64\DeskCanvas.exe
./build.ps1 -Runtime win-arm64   # ARM64
./build.ps1 -Portable            # 额外产出官方式便携 zip（exe + Widgets + json）
./build.ps1 -Msi                 # 生成 Windows 标准 MSI 安装包 (dist\installer\...)
```

### 原理
1. `dotnet publish -r <rid> --self-contained false`，csproj 已开启 `PublishSingleFile` + `IncludeNativeLibrariesForSelfExtract`（Avalonia 原生库随 exe 打包，运行时自动解压到临时目录）。
2. MSBuild 目标 `BuildWidgetsBundle`（挂 `AssignTargetPaths`）发布全部小组件 → 压缩为 `widgets.zip` → 作为内嵌资源打入 exe；`appSettings.json` / `layout.json` 默认值同样内嵌。
3. 启动时 `WidgetBundle.ExtractIfNeeded()`（`Program.Main` 最先执行）：
   - **便携模式**：exe 旁存在 `Widgets/` 文件夹 → 全部走本地文件（官方/开发模式，行为不变）。
   - **打包模式**：解压到 `%LocalAppData%\DeskCanvas\`，`.version` 标记记录 exe 版本，版本变化才重新解压。

### 热更新
打包模式下 widget 是普通磁盘文件（`%LocalAppData%\DeskCanvas\Widgets\`），直接替换 DLL 即可热更新，无需重新打包 exe。

### 注意
- **不要开启 Trim**（`PublishTrimmed`）：widget 类型靠反射激活，裁剪会破坏插件机制。
- `dotnet publish` 裸命令也能出包，但推荐 `build.ps1`（会清理散落文件，保证 dist 里只有一个 exe）。

## 配置迁移（从旧便携版）
把旧目录的 `layout.json` 和 `appSettings.json` 复制到 `%LocalAppData%\DeskCanvas\` 即可无缝迁移（旧版 widget 位置/设置全部保留）；或直接把新 exe 放入旧目录走便携模式。

## 版本管理
- 主程序版本：`src/DeskCanvas/AssemblyInfo.cs`（`AssemblyVersion`），同时作为 widget 包重解压标记。
- 小组件版本：各自 `AssemblyInfo.cs`。
- 包版本：`Directory.Packages.props` 一处集中修改。

## 液态玻璃（1.7.0）

在 **外观 → 应用主题 → 液态玻璃** 启用。默认模糊度 12、折射 28%（= 实机约 9 DIP 的透镜位移）、边缘宽度 24、高光 65%（= 实机亮边线强度）、色散 18%（≈ 0.35 px，几乎不可见）、光照方向 225°（左上方）、边缘染色 50%（可选：按边缘内外的实际壁纸颜色采样并适当提饱和度，柔和地染出轮廓色，0 关闭）；每项支持滑块和数值输入，即时保存，可重置参数或刷新壁纸。若桌面被任务栏替换程序（myDockFinder 等）、壁纸引擎或 DWM 改动、自动对齐仍偏差，可用「壁纸对准校正…」按钮手动微调采样偏移（水平/垂直，±1000 DIP），调整即时全局生效并保存在玻璃参数里。颜色区的不透明度控制玻璃染色浓度，深浅背景色决定染色颜色；圆角沿用高级设置。液态玻璃**不绘制**「描边」高光环（那是毛玻璃主题的可选项，液态玻璃的边饰是材质自身的均匀亮边线）。

这是静态壁纸材质：优先抓取**真实合成桌面**（`PrintWindow(Progman)` 1:1 位图，短 TTL 缓存）作为采样源——与 widget 实际背后的画面逐像素一致，不受壁纸布局差异影响（myDockFinder 等任务栏替换程序、壁纸引擎、DWM 的任意摆放规则都会导致"文件+注册表公式"摆放结果与真实显示错位，实测可偏差数百像素）；抓取不可用时回退到 Windows 壁纸文件+注册表样式。文字与图标独立绘制，保持清晰。切换主题、改变参数、移动和缩放窗口时重新生成缓存（含 2 秒桌面抓图缓存），不持续截图或动画。支持跨屏布局负坐标（抓图为整个虚拟桌面）及回退模式的填充、适应、拉伸、居中、平铺。

实现位于 `LiquidGlassRenderer`、`LiquidGlassWallpaper`（含 `DesktopCapturer`）、`LiquidGlassSurface`；光学设置保存在 `Theme.LiquidGlass`。光学模型按 **iOS 26 Liquid Glass**（Apple HIG + 逐项对照实机测量的复刻实现）搭建——**这是透镜，不是磨砂板**：圆角矩形距离场（平滑最大值求梯度，法线在圆角弧心与中轴线处连续）给出外向法线与内向深度，深度驱动三层叠加的边缘带——**透镜环**（`lensW` 宽，`bend = rim²` 向内位移，边缘放大、中心清晰）、**边缘反射带**（`0.7 × 短边` 宽，叠加 2× 位移，形成 iOS 药丸顶/底的倒影回声）、**边缘保护带**（最外侧几 px 把位移收敛到 0，既压噪点又让位移上升快于像素位置）；面部再做 **vibrancy**（过饱和透过的内容）+ **自适应霜化**（暗背景抬亮、永不是奶白填充），最后叠 **1 px 均匀亮边线**（`1 − smoothstep(0, 2 DIP, depth)`，与光照无关）+ 方向性镜面/Fresnel + 对角光泽带 + 背光侧内阴影；色散只有位移的 0.06 倍。模糊是**折射主导的轻雾**：`σ = 模糊度 × 缩放 / 8`（默认 12 → σ1.5 px，中心保留约 70% 壁纸结构），因为重模糊会把透镜要弯的细节先糊掉。SkiaSharp 版本与 Avalonia 自带版本保持一致，全部为 CPU 像素采样，不依赖自定义 GPU 着色器。后台任务合并高频修改，最多同时渲染两块材质，单块最多约 120 万像素；内容仍按原 DPI 渲染。

运行光学和配置回归检查（无需额外测试框架）：

```powershell
dotnet run --project tests/LiquidGlassChecks -c Release -- dist/glass-checks
```

检查配置兼容、参数保存与范围、圆角、模糊、折射、高光、色散、移动取样、纯色桌面回退及极端尺寸；`OpticsProfile` 进一步把「好不好看」变成可断言的数字：透镜剖面（平静边缘/肩部峰值/放大率/二阶差分）、圆角与中轴线法线连续性、渲染像素与生产位移公式预测的偏差、亮边线（峰值位置/FWHM/截白比例/迎光 vs 背光）、vibrancy 与自适应霜化、色散集中度与性能预算。同时生成 `liquid-glass-preview.png`（合成壁纸三卡片对比）、`liquid-glass-real.png`（真实壁纸四组参数 + ×3 边缘放大）、`optics-profile.png`（透镜与亮边线剖面）与 `optics-displacement.csv`。
