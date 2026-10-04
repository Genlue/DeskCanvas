# DeskCanvas

<img src=".github/images/icon-light.png#gh-light-mode-only" width="120" alt="Logo" align="right">
<img src=".github/images/icon-dark.png#gh-dark-mode-only" width="120" alt="Logo" align="right">

<div align="center">
  <h3>🎨 Windows 下一代 macOS 风格多功能桌面小组件套件</h3>
  <p>基于 Avalonia 11 + .NET 8 打造 · 硬件级实时毛玻璃 · 3D 光学液态玻璃 · 手机锁屏艺术大字时钟 · 灵活桌面网格系统</p>
</div>

<h3 align="center">
  <b><a href="https://github.com/Genlue/DeskCanvas/releases">下载最新版本</a></b> ・
  <a href="https://github.com/Genlue/DeskCanvas/issues">问题反馈</a> ・
  <a href="docs/项目解构报告.md">项目解构报告</a> ・
  <a href="docs/项目结构与扩展指南.md">结构与扩展指南</a>
</h3>

<div align="center">
  <img src="https://img.shields.io/badge/.NET-8.0-blue?logo=dotnet"/>
  <img src="https://img.shields.io/badge/Avalonia-11.1-purple?logo=avaloniaui"/>
  <img src="https://img.shields.io/badge/Platform-Windows%20x64-0078D6?logo=windows"/>
  <img src="https://img.shields.io/badge/Release-Single--File%20EXE-success"/>
  <img src="https://img.shields.io/badge/Installer-MSI%20Package-blue?logo=windows"/>
</div>

<br />

### 语言版本 / Language
<a href="/README.md"><kbd><img src="https://github.com/yammadev/flag-icons/blob/master/png/US.png?raw=true" height="10" /> English</kbd></a>
<kbd><img src="https://github.com/yammadev/flag-icons/blob/master/png/CN.png?raw=true" height="10" /> 中文 (简体)</kbd>

---

## 🌟 核心特色

### 1. 🕒 无边框艺术大字时钟（Frameless Clock）
- **满格顶天立地**：数字直接占据整个小组件单元格，上下严格贴紧边缘（消除字体自带空隙），支持横向自由拉伸（`StretchFill`）与等比居中；
- **精选手机锁屏艺术大字集**：华为锁屏超窄体（`HarmonyOS Sans Condensed`，内置打包）、iOS 经典厚重块体（`Impact`）、德国工业精工 DIN（`Bahnschrift`）、超粗硬核（`Arial Black`）、复古衬线（`Georgia`）、包豪斯几何（`Century Gothic`）、`Cascadia Code`、`Ink Free`、`Palatino` 等全部系统字体；
- **全阶梯字重调节**（`100 Thin` – `900 Black`）、小组件级独立主题覆盖、官方 `ColorPicker` 遮罩选色与十六进制双向联动。

### 2. 💎 四大深度适配视觉材质
- 🪟 **毛玻璃（Acrylic）· OS 硬件实时模糊**：Win32 原生 `ExtCreateRegion`（`RGNDATA`）把数字字形扫描线直接绑成 HWND Region；DWM 硬件级逐帧采样桌面，对动态壁纸（Wallpaper Engine）、视频壁纸实现 60/144fps 零延迟实时跟手；
- 💧 **液态玻璃（Liquid Glass）· 3D 光学物理折射**：欧几里得距离场（EDT）精密计算字符轮廓法线，真实凹凸透镜折射、色散光斑与镜面高光，GPU（Skia 运行时着色器）加速 + CPU 回退双路径；
- 💎 **液态玻璃 2.0**：对 [Kyant0/AndroidLiquidGlass](https://github.com/Kyant0/AndroidLiquidGlass) 2.0 光学模型的忠实 GPU 移植——圆角矩形折射透镜 + 深度效果 + 斜向色散 + 发丝级描边高光；
- 🎨 **纯色（Solid）**：纯粹矢量抗锯齿填充，透明度滑块与强调色定制，零模糊、极度省电。

### 3. 🪟 二级面板 · Material 3 动效
天气、清单、大文件夹、剪贴板、笔记共用统一的二级面板：从触发组件**锚点缩放展开**（圆角全程跟随组件），玻璃与一级组件完全同源；开合过渡严格遵循 [Material Design 3 motion tokens](https://m3.material.io/styles/motion/easing-and-duration/tokens-specs)（进场 `emphasized decelerate`、退场 `emphasized accelerate`）。

### 4. 📐 灵活桌面网格系统
手动网格（m×n 方格按百分比存储，跨分辨率/跨 DPI 自适应）、虚拟网格与自由放置三种模式；组件吸附单元格，边距与圆角自定义，永久钉在桌面置底层。

### 5. 🧩 丰富的小组件生态（17 款内置）
⏰ 时钟（模拟×3 / 数字 / 世界时钟 / 无边框）· 📅 日历 · 🌤️ 天气（7 日预报、日出日落、紫外线、空气质量、代理）· 📊 系统监控 · 📝 笔记（Markdown 排版、二级面板）· ✅ 清单（交互式待办 + 二级面板）· 📁 文件夹（桌面快速启动器 + 实时文件监听）· 🎵 音乐控制 · 🔍 搜索 · 📈 进度 · 🖼️ 相册 · 🔋 电池 · 🗺️ 地图 · 🍅 番茄钟 · 📚 堆叠 · 🧰 工具 · 📌 固定（多组件聚合卡）

### 6. 🚀 单文件分发
构建产出自带组件包（内容哈希校验、首次启动解包）的单文件 `DeskCanvas.exe`；组件解包到 `%LocalAppData%\DeskCanvas`，可直接替换 DLL 热更新。每个版本同步提供 WiX v5 打包的 MSI 安装器。

---

## 🛠️ 从源码构建

### 环境要求
- Windows 10 / 11（x64）
- [.NET 8.0 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) 或更高
- PowerShell（pwsh 或 Windows PowerShell）；构建 MSI 需 WiX v5

### 构建命令
```powershell
# 克隆仓库
git clone https://github.com/Genlue/DeskCanvas.git
cd DeskCanvas

# 编译打包单文件 EXE
powershell -ExecutionPolicy Bypass -File .\build.ps1

# 额外产出经典便携版 zip（exe + Widgets + 配置）
powershell -ExecutionPolicy Bypass -File .\build.ps1 -Portable

# 编译打包 Windows MSI 安装器
powershell -ExecutionPolicy Bypass -File .\build.ps1 -Msi

# 产物位置：
# dist/win-x64/DeskCanvas.exe
# dist/DeskCanvas-win-x64-portable.zip
# dist/installer/DeskCanvas-2.7.0-win-x64.msi
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
| [Kyant0/AndroidLiquidGlass](https://github.com/Kyant0/AndroidLiquidGlass) | Apache-2.0 | 「液态玻璃 2.0」材质是对其光学模型（圆角矩形折射透镜、深度效果、色散、描边高光）的忠实移植 |
| [QmDeve/AndroidLiquidGlassView](https://github.com/QmDeve/AndroidLiquidGlassView) | MIT | 为 Avalonia 评估液态玻璃渲染时的参考实现 |
| [Material Design 3](https://m3.material.io/styles/motion/easing-and-duration/tokens-specs) | CC BY 4.0（规范） | 二级面板开合动画的缓动令牌（`emphasized decelerate/accelerate`） |
| [Avalonia UI](https://github.com/AvaloniaUI/Avalonia) | MIT | 跨平台 .NET UI 框架 |
| [SkiaSharp](https://github.com/mono/SkiaSharp) | MIT | 2D 图形库与 GPU 玻璃合成器使用的运行时着色器 |

> 说明：本项目曾以 **uWidgetsPlus**（uWidgets 的 fork）名义发布，自 v2.7.0 起更名为 **DeskCanvas**。旧数据目录 `%LocalAppData%\uWidgets` 会在首次启动时自动迁移到 `%LocalAppData%\DeskCanvas`。
