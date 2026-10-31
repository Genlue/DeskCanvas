# DeskCanvas

<img src=".github/images/icon-light.png#gh-light-mode-only" width="120" alt="Logo" align="right">
<img src=".github/images/icon-dark.png#gh-dark-mode-only" width="120" alt="Logo" align="right">

<div align="center">
  <h3>🎨 macOS-Style Desktop Widgets Suite for Windows</h3>
  <p>Built with Avalonia 12 + .NET 10 · Native Acrylic Blur · 3D Liquid Glass Optics · Lockscreen Art-Font Clock · Custom Snap Grid Engine</p>
</div>

<h3 align="center">
  <b><a href="https://github.com/Genlue/DeskCanvas/releases">Download Latest Release</a></b> ・
  <a href="https://github.com/Genlue/DeskCanvas/issues">Report an Issue</a> ・
  <a href="doc/项目解析报告.md">Architecture Report (中文)</a>
</h3>

<div align="center">
  <img src="https://img.shields.io/badge/.NET-10.0-blue?logo=dotnet"/>
  <img src="https://img.shields.io/badge/Avalonia-12.1-purple?logo=avaloniaui"/>
  <img src="https://img.shields.io/badge/Platform-Windows%20x64-0078D6?logo=windows"/>
  <img src="https://img.shields.io/badge/Release-Single--File%20EXE-success"/>
  <img src="https://img.shields.io/badge/Installer-Inno%20Setup-blue?logo=windows"/>
</div>

<br />

### Languages
<kbd><img src="https://github.com/yammadev/flag-icons/blob/master/png/US.png?raw=true" height="10" /> English</kbd>
<a href="/README.ZH-HANS.md"><kbd><img src="https://github.com/yammadev/flag-icons/blob/master/png/CN.png?raw=true" height="10" /> 中文 (简体)</kbd></a>

---

> [!IMPORTANT]
> **Maintenance status: paused (2026-10-31)**
>
> DeskCanvas is no longer planned to receive new features, new widgets, or compatibility fixes. **v4.12.0 is the final release.**
> - The repository stays public — fork it, modify it, build it yourself (see [BUILDING.md](BUILDING.md)).
> - Issues and pull requests are no longer guaranteed a reply or a merge.
> - Existing installers keep working, but the third-party services they depend on (weather, maps, translation, music, …) may stop working over time.

## 🌟 Key Highlights

### 1. 🕒 Frameless Display Clock
- **Full-cell stretched bounds**: numerals occupy the entire widget cell with no card margins or font leading gaps; supports non-uniform stretching or uniform proportional centering;
- **Eight bundled display faces** — HarmonyOS Sans Condensed (the tall iOS / HarmonyOS poster face), Bebas Neue, DM Serif Display, Bungee Inline, Black Ops One, Titan One, Righteous and Orbitron — plus eight curated Windows faces (Segoe UI Variable, Bahnschrift, Impact, Arial Black, Georgia, Century Gothic, Cascadia Code, Consolas) and every installed system font;
- **Full font-weight spectrum** (100 Thin – 900 Black), per-widget theme override, color overlay with a native `ColorPicker` and live hex sync.

### 2. 💎 Three Surface Materials
- 🪟 **Acrylic (native OS blur)** — the window itself asks DWM for `AcrylicBlur`, so the desktop underneath is sampled live by the compositor: animated wallpapers (Wallpaper Engine), video wallpapers and anything moving behind the widget track at the display's refresh rate, at zero capture cost. Win32 `CreateRoundRectRgn` / `ExtCreateRegion` (`RGNDATA`) clips that blur to the exact card outline — and, for the frameless clock, to the glyph scanlines themselves, so blur fills the numerals while the surrounding desktop stays untouched and click-through;
- 💧 **Liquid Glass (3D optical refraction model)** — a lens, not a frosted plate: signed-distance-field normals over a rounded rect (or any closed polygon) drive rim displacement, chromatic dispersion, vibrancy and a hairline specular highlight, with a GPU (Skia runtime shader) fast path and an identical CPU fallback fed by one shared coefficient solver. It samples a **static** desktop snapshot — cached, refreshed on move / resize / theme change — which is what keeps it cheap; the frameless clock additionally refracts *inside* each numeral's stroke contours (Euclidean distance field over the glyph mask), so the digits themselves behave like lenses;
- 🎨 **Full Color** — the widget's own artwork at full opacity: no blur, no sampling, subpixel-antialiased vector fills. Accent color and the monochrome variants still apply.

Each widget can override the global material, and the frameless clock exposes the same three choices in its own settings.

### 3. 🔍 Spotlight Search
A global hotkey (Alt+Space by default, re-recordable, with an optional low-level hook to take over a shortcut Windows already owns) summons a Spotlight-style surface over any window: applications, files, shortcuts and clipboard history in one list, a Launchpad grid for an empty query, and a preview pane that renders the first page of a PDF. Arrow keys navigate, Enter opens, Esc dismisses.

### 4. 🪟 Secondary Panels with Spring Motion
Weather forecast, reminders, big folders, clipboard and notes open in shared secondary panels that **grow out of the triggering widget** (anchor-scaled, corner radius propagated) with glass rendered by the same surface as level-1 widgets. The zoom is a **damped spring**, not a fixed easing curve — ported from the Windhawk [Mac Island & Dock](https://windhawk.net/mods) mod and integrated on a fixed 1/480 s substep so the trajectory never depends on frame rate. Opening uses the under-damped profile (~3.9 % overshoot, which is what makes the panel read as physical); closing and the opacity fade stay critically damped. A click anywhere outside the panel dismisses it.

### 5. 📐 Advanced Desktop Grid Management
Custom square-cell grid (m×n cells stored as percentages, responsive across resolutions and DPI); widgets always snap to cells with customizable margins and corner radius, and stay pinned to the desktop bottom band.

### 6. 🧩 Widget Ecosystem (26 packages · 46 widget views)
⏰ Clock (analog ×3 / digital / flip / frameless) · 🕘 Tick Clock (dial-edged square card) · 📅 Calendar & Schedule (date card ×2 with lunar calendar, month grid, schedule management in 2x2/4x2/4x4) · 🎶 Audio Rhythm (spectrum bars / fluid wave lines / dancing dots / custom hand-drawn paths / WASAPI loopback internal audio & microphone capture) · 🌤️ Weather (7-day forecast, 4-day square card, temperature, UV index, sun times, pressure, AQI, proxy) · 📊 System Monitor (single metric / multi dashboard) · 📝 Notes (Markdown typography, secondary panel) · ✅ Reminders (interactive checklist with secondary panel) · 🗓️ Timetable · 🔁 Habits · ⏳ Countdown · 🔗 Shortcuts · 📁 Folders (desktop quick launcher with live file watching; folder / big folder / single file / single link, the last opening a URL in a chosen browser with a custom icon from a local file, an image URL or the site's own favicon) · 🎵 Music controls · 🔍 Search · 📈 Progress · 🖼️ Picture · 🔋 Batteries · 🗺️ Map · 🍅 Pomodoro · 📚 Stack · 🧰 Tools (clipboard, translator, calculator) · 🎛️ Control Center · 💽 Storage · 💬 Hitokoto Quotes (poetry / literature / anime quote card) · 📌 Fixed (multi-widget aggregate cards)

### 7. 🚀 Standalone Single-File Distribution
Builds to a self-contained `DeskCanvas.exe` with an embedded widget bundle verified by the SHA-256 of its own zip, so any change — a bumped widget DLL, a new widget, a resource edit — triggers a re-extract on next launch. Widgets are extracted to `%LocalAppData%\DeskCanvas` and stay hot-updatable. Releases ship **only** the Inno Setup `DeskCanvas-<version>-x64-Setup.exe` (with the standalone uninstaller UI and optional data-directory cleanup); MSI and portable zips are no longer distributed.

---

## 🛠️ Building from Source

### Prerequisites
- Windows 10 / 11 (x64)
- [.NET 10.0 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) or higher
- PowerShell 7 (`pwsh`); Inno Setup 6 for the Setup installer

### Build Commands
```powershell
# Clone the repository
git clone https://github.com/Genlue/DeskCanvas.git
cd DeskCanvas

# Compile and package single-file EXE
pwsh -File ./build.ps1

# Also produce the classic portable zip (exe + Widgets + settings) — local debugging only, never shipped
pwsh -File ./build.ps1 -Portable

# Compile and package the Setup installer (the only form releases ship)
pwsh -File ./build.ps1 -Setup

# Output locations:
# dist/win-x64/DeskCanvas.exe
# dist/DeskCanvas-win-x64-portable.zip
# dist/installer/DeskCanvas-<version>-x64-Setup.exe
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
| [Kyant0/AndroidLiquidGlass](https://github.com/Kyant0/AndroidLiquidGlass) | Apache-2.0 | The liquid-glass material is a faithful port of its 2.0 optical model (rounded-rect refraction lens, depth effect, chromatic aberration, outline highlight) |
| [QmDeve/AndroidLiquidGlassView](https://github.com/QmDeve/AndroidLiquidGlassView) | MIT | Reference implementation studied while evaluating liquid-glass rendering for Avalonia |
| [Avalonia UI](https://github.com/AvaloniaUI/Avalonia) | MIT | Cross-platform .NET UI framework |
| [SkiaSharp](https://github.com/mono/SkiaSharp) | MIT | 2D graphics and the runtime shader used by the GPU glass compositors |
| [Docnet.Core](https://github.com/GowenGit/Docnet.Core) | MIT | Renders a PDF's first page for the Spotlight preview pane (bundles PDFium) |
| [PDFium](https://pdfium.googlesource.com/pdfium/) | BSD-3-Clause | PDF rendering engine shipped inside Docnet.Core |

> Panel open/close and hover motion is a managed port of the damped-spring integrator from the Windhawk **"Mac Island & Dock"** mod; see [windhawk.net/mods](https://windhawk.net/mods).

> Note: the project was formerly published as **uWidgetsPlus** (a fork of uWidgets); it has been renamed to **DeskCanvas** since v2.7.0. Data in `%LocalAppData%\uWidgets` is migrated to `%LocalAppData%\DeskCanvas` automatically on first launch.
