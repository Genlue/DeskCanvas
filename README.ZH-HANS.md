# DeskCanvas

<img src=".github/images/icon-light.png#gh-light-mode-only" width="120" alt="Logo" align="right">
<img src=".github/images/icon-dark.png#gh-dark-mode-only" width="120" alt="Logo" align="right">

<div align="center">
  <h3>🎨 Windows 下一代 macOS 风格多功能桌面小组件套件</h3>
  <p>基于 Avalonia 12 + .NET 10 打造 · 原生亚克力实时模糊 · 3D 光学液态玻璃 · 手机锁屏艺术大字时钟 · 自定义吸附网格系统</p>
</div>

<h3 align="center">
  <b><a href="https://github.com/Genlue/DeskCanvas/releases">下载最新版本</a></b> ・
  <a href="https://github.com/Genlue/DeskCanvas/issues">问题反馈</a> ・
  <a href="doc/项目解析报告.md">项目解析报告</a> ・
  <a href="doc/开发规范.md">开发规范</a>
</h3>

<div align="center">
  <img src="https://img.shields.io/badge/.NET-10.0-blue?logo=dotnet"/>
  <img src="https://img.shields.io/badge/Avalonia-12.1-purple?logo=avaloniaui"/>
  <img src="https://img.shields.io/badge/Platform-Windows%20x64-0078D6?logo=windows"/>
  <img src="https://img.shields.io/badge/Release-Single--File%20EXE-success"/>
  <img src="https://img.shields.io/badge/Installer-Inno%20Setup-blue?logo=windows"/>
</div>

<br />

### 语言版本 / Language
<a href="/README.md"><kbd><img src="https://github.com/yammadev/flag-icons/blob/master/png/US.png?raw=true" height="10" /> English</kbd></a>
<kbd><img src="https://github.com/yammadev/flag-icons/blob/master/png/CN.png?raw=true" height="10" /> 中文 (简体)</kbd>

---

> [!IMPORTANT]
> **维护状态：已暂停维护（2026-10-31）**
>
> DeskCanvas 不再计划新功能、新组件或兼容性修复，**v4.12.0 是最后一个版本**。
> - 仓库继续公开，代码可自由 fork、修改、自行构建（见 [BUILDING.md](BUILDING.md)）。
> - Issue / PR 不再保证回复或合并。
> - 已发布的安装包仍然可用，但它依赖的第三方服务（天气、地图、翻译、音乐等）可能随时间失效，请自行评估。

## 🌟 核心特色

### 1. 🕒 无边框艺术大字时钟（Frameless Clock）
- **满格顶天立地**：数字直接占据整个小组件单元格，上下严格贴紧边缘（消除字体自带空隙），支持横向自由拉伸（`StretchFill`）与等比居中；
- **内置 8 款艺术大字**——华为锁屏超窄体（`HarmonyOS Sans Condensed`，内置打包）、`Bebas Neue`（iOS 经典硬朗窄体）、`DM Serif Display`（高对比 Didone 衬线）、`Bungee Inline`（中空双线轨体）、`Black Ops One`（军工切角镂空）、`Titan One`（圆润泡芙体）、`Righteous`（装饰艺术几何）、`Orbitron`（赛博数码体）；另有 8 款精选系统字体（`Segoe UI Variable`、`Bahnschrift`、`Impact`、`Arial Black`、`Georgia`、`Century Gothic`、`Cascadia Code`、`Consolas`）以及全部已安装系统字体；
- **全阶梯字重调节**（`100 Thin` – `900 Black`）、小组件级独立材质/主题覆盖、官方 `ColorPicker` 遮罩选色与十六进制双向联动。

### 2. 💎 三种视觉材质
- 🪟 **亚克力（Acrylic）· 系统级实时模糊**：窗口直接向 DWM 申请 `AcrylicBlur`，玻璃背后的桌面由合成器逐帧采样——动态壁纸（Wallpaper Engine）、视频壁纸、任何在组件背后运动的画面都以显示器刷新率实时跟手，且零抓图开销。再用 Win32 `CreateRoundRectRgn` / `ExtCreateRegion`（`RGNDATA`）把模糊裁到精确的卡片轮廓；无边框时钟更把裁剪区绑成字形扫描线，模糊只填满数字本身，周围桌面保持原样并可穿透点击；
- 💧 **液态玻璃（Liquid Glass）· 3D 光学折射模型**：这是**透镜，不是磨砂板**——圆角矩形（或任意闭合多边形）的距离场法线驱动边缘位移、色散、vibrancy 与发丝级高光，GPU（Skia 运行时着色器）加速 + 完全等价的 CPU 回退，两条路径共享同一套系数求解器。采样源是**静态**桌面快照（带缓存，移动 / 缩放 / 换主题时重建），所以开销可控；无边框时钟还会在每个数字的笔画轮廓内部再做一次折射（字形掩膜的欧几里得距离场），让数字本身成为透镜；
- 🎨 **全彩（Full Color）**：组件自身配色的全不透明呈现——不模糊、不采样，纯矢量抗锯齿填充；强调色与单色（黑白 / 背景色）档依然生效。

每个组件都可以单独覆盖全局材质，无边框时钟的设置里同样提供这三种选择。

### 3. 🔍 聚焦搜索（Spotlight）
全局热键（默认 `Alt+Space`，可重录，并提供底层键盘钩子接管 Windows 已占用的组合键）在任意窗口之上唤出聚焦面板：应用、文件、快捷方式与剪贴板历史同列呈现，空查询时展开 Launchpad 式应用网格，预览栏可直接渲染 PDF 首页。方向键切换、回车打开、`Esc` 关闭。

### 4. 🪟 二级面板 · 弹簧动效
天气、清单、大文件夹、剪贴板、笔记共用统一的二级面板：从触发组件**锚点缩放展开**（圆角全程跟随组件），玻璃与一级组件完全同源。缩放用的是**阻尼弹簧**而非固定缓动曲线——移植自 Windhawk 的 [Mac Island & Dock](https://windhawk.net/mods) 模组，以固定 1/480 秒子步长积分，轨迹不随帧率变化。展开走欠阻尼档（约 3.9% 过冲，这正是面板「有物理感」的来源），收起与透明度淡入淡出保持临界阻尼。点击面板外部任意位置即可关闭。

### 5. 📐 灵活桌面网格系统
自定义方格网格（m×n 方格按百分比存储，跨分辨率/跨 DPI 自适应）；组件恒吸附单元格，边距与圆角自定义，永久钉在桌面置底层。

### 6. 🧩 丰富的小组件生态（26 个插件 · 46 个组件视图）
⏰ 时钟（模拟×3 / 数字 / 翻页 / 无边框）· 🕘 刻度时钟（刻度方卡）· 📅 日历与日程（日期卡×2 含农历、月历网格、日程管理，2x2/4x2/4x4 尺寸）· 🎶 音频律动（频谱柱/线条波浪/粒子光点/自定义手绘路径/系统内声音与麦克风监测）· 🌤️ 天气（7 日预报、四日方格卡、温度、紫外线、日出日落、气压、空气质量、代理）· 📊 系统监控（单指标 / 多指标仪表盘）· 📝 笔记（Markdown 排版、二级面板）· ✅ 清单（交互式待办 + 二级面板）· 🗓️ 课表 · 🔁 习惯打卡 · ⏳ 倒计时 · 🔗 快捷方式 · 📁 文件夹（桌面快速启动器 + 实时文件监听；文件夹 / 大文件夹 / 单文件 / 单链接，单链接可指定浏览器，支持本地文件 / 图片直链 / 抓取站点图标三种自定义图标）· 🎵 音乐控制 · 🔍 搜索 · 📈 进度 · 🖼️ 相册 · 🔋 电池 · 🗺️ 地图 · 🍅 番茄钟 · 📚 堆叠 · 🧰 工具（剪贴板、翻译、计算器）· 🎛️ 系统控制中心 · 💽 磁盘存储 · 💬 一言（诗词 / 文学 / 动漫语录卡）· 📌 固定（多组件聚合卡）

### 7. 🚀 单文件分发
构建产出自包含的单文件 `DeskCanvas.exe`，内嵌组件包以其 zip 的 SHA-256 作为版本标记——组件 DLL 版本号变化、新增组件、资源改动都会触发下次启动重新解包。组件解包到 `%LocalAppData%\DeskCanvas`，可直接替换 DLL 热更新。每个版本**只**提供 Inno Setup 打包的 `DeskCanvas-<版本>-x64-Setup.exe`（自带独立卸载界面，可选择性清理数据目录），不再分发 MSI、便携 zip 等其它安装形式。

---

## 🛠️ 从源码构建

### 环境要求
- Windows 10 / 11（x64）
- [.NET 10.0 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) 或更高
- PowerShell 7（`pwsh`）；构建 Setup 安装器需 Inno Setup 6

### 构建命令
```powershell
# 克隆仓库
git clone https://github.com/Genlue/DeskCanvas.git
cd DeskCanvas

# 编译打包单文件 EXE
pwsh -File ./build.ps1

# 额外产出经典便携版 zip（exe + Widgets + 配置）——仅供本地调试，不随发行版发布
pwsh -File ./build.ps1 -Portable

# 编译打包 Setup 安装器（发行版唯一分发的形式）
pwsh -File ./build.ps1 -Setup

# 产物位置：
# dist/win-x64/DeskCanvas.exe
# dist/DeskCanvas-win-x64-portable.zip
# dist/installer/DeskCanvas-<version>-x64-Setup.exe
```

---

## 📄 许可证与开源致谢

### 许可证
DeskCanvas 采用 [知识共享 署名-非商业性使用-相同方式共享 4.0 国际许可协议（CC BY-NC-SA 4.0）](LICENSE.txt) 授权，与上游项目一致。

### 开源致谢
本项目站在以下开源项目的肩膀上：

| 项目 | 许可证 | 贡献 |
|---|---|---|
| [creewick/uWidgets](https://github.com/creewick/uWidgets) | CC BY-NC-SA 4.0 | 上游项目——DeskCanvas 由它 fork 而来 |
| [Kyant0/AndroidLiquidGlass](https://github.com/Kyant0/AndroidLiquidGlass) | Apache-2.0 | 「液态玻璃」材质是对其 2.0 光学模型（圆角矩形折射透镜、深度效果、色散、描边高光）的忠实移植 |
| [QmDeve/AndroidLiquidGlassView](https://github.com/QmDeve/AndroidLiquidGlassView) | MIT | 为 Avalonia 评估液态玻璃渲染时的参考实现 |
| [Avalonia UI](https://github.com/AvaloniaUI/Avalonia) | MIT | 跨平台 .NET UI 框架 |
| [SkiaSharp](https://github.com/mono/SkiaSharp) | MIT | 2D 图形库与 GPU 玻璃合成器使用的运行时着色器 |
| [Docnet.Core](https://github.com/GowenGit/Docnet.Core) | MIT | 聚焦搜索预览栏渲染 PDF 首页（内置 PDFium） |
| [PDFium](https://pdfium.googlesource.com/pdfium/) | BSD-3-Clause | Docnet.Core 内置的 PDF 渲染引擎 |

> 面板开合与悬停动效的阻尼弹簧积分器移植自 Windhawk 的 **「Mac Island & Dock」** 模组，见 [windhawk.net/mods](https://windhawk.net/mods)。

> 说明：本项目曾以 **uWidgetsPlus**（uWidgets 的 fork）名义发布，自 v2.7.0 起更名为 **DeskCanvas**。旧数据目录 `%LocalAppData%\uWidgets` 会在首次启动时自动迁移到 `%LocalAppData%\DeskCanvas`。
