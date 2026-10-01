# DeskCanvas

<img src=".github/images/icon-light.png#gh-light-mode-only" width="120" alt="Logo" align="right">
<img src=".github/images/icon-dark.png#gh-dark-mode-only" width="120" alt="Logo" align="right">

<div align="center">
  <h3>🎨 macOS-Style Desktop Widgets Suite for Windows</h3>
  <p>Built with Avalonia 11 + .NET 8 · Hardware Acrylic Blur · 3D Liquid Glass Optics · Lockscreen Art-Font Clock · Flexible Desktop Grid Engine</p>
</div>

<h3 align="center">
  <b><a href="https://github.com/Genlue/DeskCanvas/releases">Download Latest Release</a></b> ・
  <a href="https://github.com/Genlue/DeskCanvas/issues">Report an Issue</a> ・
  <a href="docs/项目解构报告.md">Architecture Report (中文)</a>
</h3>

<div align="center">
  <img src="https://img.shields.io/badge/.NET-8.0-blue?logo=dotnet"/>
  <img src="https://img.shields.io/badge/Avalonia-11.1-purple?logo=avaloniaui"/>
  <img src="https://img.shields.io/badge/Platform-Windows%20x64-0078D6?logo=windows"/>
  <img src="https://img.shields.io/badge/Release-Single--File%20EXE-success"/>
  <img src="https://img.shields.io/badge/Installer-MSI%20Package-blue?logo=windows"/>
</div>

<br />

### Languages
<kbd><img src="https://github.com/yammadev/flag-icons/blob/master/png/US.png?raw=true" height="10" /> English</kbd>
<a href="/README.ZH-HANS.md"><kbd><img src="https://github.com/yammadev/flag-icons/blob/master/png/CN.png?raw=true" height="10" /> 中文 (简体)</kbd></a>

---

## 🌟 Key Highlights

### 1. 🕒 Frameless Display Clock
- **Full-cell stretched bounds**: numerals occupy the entire widget cell with no card margins or font leading gaps; supports non-uniform stretching or uniform proportional centering;
- **Curated lockscreen display fonts**: HarmonyOS Sans Condensed (embedded), Impact, Bahnschrift (DIN), Arial Black, Georgia, Century Gothic, Cascadia Code, Ink Free, Palatino Linotype and every installed system font;
- **Full font-weight spectrum** (100 Thin – 900 Black), per-widget theme override, color overlay with a native `ColorPicker` and live hex sync.

### 2. 💎 Four Deeply Adapted Visual Materials
- 🪟 **Acrylic Blur (OS-level live hardware blur)** — Win32 `ExtCreateRegion` (`RGNDATA`) binds glyph scanline spans directly to the HWND region; DWM samples the desktop underneath at 60/144 fps with zero latency, tracking live wallpapers (Wallpaper Engine) and background video seamlessly;
- 💧 **Liquid Glass (3D optical refraction model)** — Euclidean Distance Transform computes surface normals across stroke contours for convex/concave lens displacement, chromatic dispersion and specular glints, with a GPU (Skia runtime shader) fast path and a CPU fallback;
- 💎 **Liquid Glass 2.0** — a faithful GPU port of [Kyant0/AndroidLiquidGlass](https://github.com/Kyant0/AndroidLiquidGlass) 2.0's optical model: rounded-rect refraction lens with depth effect, diagonal chromatic aberration and hairline outline highlight;
- 🎨 **Solid Fill** — pure anti-aliased vector fill with opacity slider and accent colors.

### 3. 🪟 Secondary Panels with Material 3 Motion
Weather forecast, reminders, big folders, clipboard and notes open in shared secondary panels that **grow out of the triggering widget** (anchor-scaled, corner radius propagated) with glass rendered by the same surface as level-1 widgets. The open/close transitions follow the [Material Design 3 motion tokens](https://m3.material.io/styles/motion/easing-and-duration/tokens-specs) (`emphasized decelerate` in, `emphasized accelerate` out).

### 4. 📐 Advanced Desktop Grid Management
Manual grid (m×n square cells stored as percentages, responsive across resolutions and DPI), virtual grid and free placement; widgets snap to cells with customizable margins and corner radius, always pinned to the desktop bottom band.

### 5. 🧩 Widget Ecosystem (17 built-in widgets)
⏰ Clock (analog ×3 / digital / world clock / frameless) · 📅 Calendar · 🌤️ Weather (7-day forecast, sun times, UV, AQI, proxy) · 📊 System Monitor · 📝 Notes (Markdown typography, secondary panel) · ✅ Reminders (interactive checklist with secondary panel) · 📁 Folders (desktop quick launcher with live file watching) · 🎵 Music controls · 🔍 Search · 📈 Progress · 🖼️ Picture · 🔋 Batteries · 🗺️ Map · 🍅 Pomodoro · 📚 Stack · 🧰 Tools · 📌 Fixed (multi-widget aggregate cards)

### 6. 🚀 Standalone Single-File Distribution
Builds to a self-contained `DeskCanvas.exe` with a content-hash-verified embedded widget bundle; widgets are extracted to `%LocalAppData%\DeskCanvas` on first run and stay hot-updatable. An MSI installer (WiX v5) is also produced per release.

---

## 🛠️ Building from Source

### Prerequisites
- Windows 10 / 11 (x64)
- [.NET 8.0 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) or higher
- PowerShell (pwsh or Windows PowerShell); WiX v5 for the MSI

### Build Commands
```powershell
# Clone the repository
git clone https://github.com/Genlue/DeskCanvas.git
cd DeskCanvas

# Compile and package single-file EXE
powershell -ExecutionPolicy Bypass -File .\build.ps1

# Also produce the classic portable zip (exe + Widgets + settings)
powershell -ExecutionPolicy Bypass -File .\build.ps1 -Portable

# Compile and package the Windows MSI installer
powershell -ExecutionPolicy Bypass -File .\build.ps1 -Msi

# Output locations:
# dist/win-x64/DeskCanvas.exe
# dist/DeskCanvas-win-x64-portable.zip
# dist/installer/DeskCanvas-2.7.0-win-x64.msi
```

---

## 📄 License & Credits

### License
DeskCanvas is licensed under the [Creative Commons Attribution-NonCommercial-ShareAlike 4.0 International License (CC BY-NC-SA 4.0)](LICENSE.txt), the same license as the upstream project.

### Credits
This project would not exist without the following open-source projects:

| Project | License | Contribution |
|---|---|---|
| [creewick/uWidgets](https://github.com/creewick/uWidgets) | CC BY-NC-SA 4.0 | Upstream project — DeskCanvas started as a fork of it |
| [Kyant0/AndroidLiquidGlass](https://github.com/Kyant0/AndroidLiquidGlass) | Apache-2.0 | The "Liquid Glass 2.0" material is a faithful port of its optical model (rounded-rect refraction lens, depth effect, chromatic aberration, outline highlight) |
| [QmDeve/AndroidLiquidGlassView](https://github.com/QmDeve/AndroidLiquidGlassView) | MIT | Reference implementation studied while evaluating liquid-glass rendering for Avalonia |
| [Material Design 3](https://m3.material.io/styles/motion/easing-and-duration/tokens-specs) | CC BY 4.0 (spec) | Motion easing tokens (`emphasized decelerate/accelerate`) for the secondary-panel open/close animations |
| [Avalonia UI](https://github.com/AvaloniaUI/Avalonia) | MIT | Cross-platform .NET UI framework |
| [SkiaSharp](https://github.com/mono/SkiaSharp) | MIT | 2D graphics and the runtime shader used by the GPU glass compositors |

> Note: the project was formerly published as **uWidgetsPlus** (a fork of uWidgets); it has been renamed to **DeskCanvas** since v2.7.0. Data in `%LocalAppData%\uWidgets` is migrated to `%LocalAppData%\DeskCanvas` automatically on first launch.
