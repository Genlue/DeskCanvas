## uWidgetsPlus v1.9.15 更新日志

> 修掉两个「看着就不对」的毛病：强调色在深色模式下怎么选都是蓝的；设置窗口四角有两重圆角，内侧那圈还是透明的。
> 另外把整套构建警告清到 0，并把架构文档重写成一份纯解析。

---

### 🎨 强调色：深色模式下永远是蓝色

**现象**：把「外观 → 强调色」切到**手动**、挑一个颜色（比如红色），浅色模式下生效，**深色模式下所有强调色元素依然是蓝色** —— 组件标题、强调图标、时钟秒针与中心轴、日历标记、清单徽标等等。怎么选都不动。

**根因**：强调色不是通过一个资源下发的，而是 Avalonia Fluent 的一整套色阶。`Styles/Accent.axaml` 的浅色主题字典读 `SystemAccentColorDark1`、深色主题字典读 `SystemAccentColorLight2`；`ThemeService.Apply` 当时只写了三个键 —— `SystemAccentColor`、`SystemAccentColorDark1`、`SystemAccentColorLight1`。

`SystemAccentColorLight2` **从未被写过**。Fluent 主题本来就把这七个键全部预定义成自己那套固定的蓝，于是没被覆盖的键原样保留 —— 深色模式（`Monochrome.axaml`、`ThemeButton`、配置方案徽标同理）永远停在 Fluent 蓝。

**修复**：解析出强调色后，一次写全整条色阶：

```csharp
SystemAccentColor
SystemAccentColorDark1 / Dark2 / Dark3
SystemAccentColorLight1 / Light2 / Light3
```

明暗两种变体都拿到用户挑的那个颜色，不再有任何一个键留给 Fluent 的默认蓝。多彩主题走 Apple systemBlue（浅色 `#007AFF` / 深色 `#0A84FF`），同样是整条色阶。

**验证**（真机深色模式 + 手动 `#C0392B`）：设置窗口的滑块、导航选中项、「系统设置」标题、主题卡片选中框全部变为红色；日历当日标记跟随 `SystemAccentColor`，`AccentPersistenceChecks` 全绿。

---

### 🪟 设置窗口：两重圆角，内侧那圈是透明的

**现象**：设置窗口四角看起来有两圈圆角，靠里那圈发虚/透明。

**根因**：`Program.cs` 把 `WinUICompositionBackdropCornerRadius` 设成了 `settings.Dimensions.Radius` —— 那是**小组件卡片**的圆角，作者机器上是 **36**。于是：

1. 合成器按 36 DIP 把毛玻璃背板裁成圆角；
2. 窗口自己的内容（`SettingsWindowBackground` 那个 0.8 不透明度的矩形）**是直角、也不裁剪**；
3. Windows 又按自己的默认半径把整个顶层窗口圆一次。

背板圆弧之外、窗口直角之内那一圈，只有 0.8 的底色压在没有模糊的桌面上 —— 看上去就是「小一号的透明圆角」。

**修复**：把**窗口圆角**和**组件卡片圆角**解耦。

- 新增 `Program.WindowCornerRadius = 8`（DIP）作为应用自身窗口的圆角；
- 同一个值同时喂给两处：合成器背板（`WinUICompositionBackdropCornerRadius`）与设置窗口根 `Border` 的 `CornerRadius` + `ClipToBounds`（经 `SettingsWindowCornerRadius` 资源）。

Windows 会用**抗锯齿**圆弧 + 配套阴影把顶层窗口圆掉，背板半径不超过它就不会在四角露出第二道弧 —— 只剩一圈干净的圆角，玻璃铺满。

**注意**：这里**没有**用 `SetWindowRgn`。中途试过这条路（组件窗口和弹窗就是这么裁的），但 GDI 区域是 1 位掩码、没有抗锯齿，设置窗口四角立刻变得明显锯齿 —— 已回退。组件窗口/弹窗不受本次改动影响：它们本来就用 `SetWindowRgn` 裁到自己的卡片圆角，区域裁剪永远压过背板圆弧，把背板半径调小只可能让它们的四角更完整，不会更差。

**真机测量**（2560×1600 @125%，浅底 + 深色窗口，逐像素扫描角部）：单一圆弧，半径 ≈ 8–11 DIP，边界是 5 像素左右的平滑过渡（抗锯齿），没有任何第二道边界，窗外是连续的 DWM 阴影梯度。

---

### 🧹 构建警告：113 → 0

整套解决方案的编译/分析警告从 **113 处**清到 **0**（`dotnet build src\uWidgets.sln -c Debug` → 0 错误 / 0 警告）。

| 代码 | 处数 | 处理 |
|---|---|---|
| CS8618 | 24 | 天气 JSON DTO 加就地初始化（类型与 JSON 属性名不变） |
| CA1416 | 26 | `[SupportedOSPlatform("windows")]` + 既有 `OperatingSystem.IsWindows()` 守卫 |
| AVLN3001 | 19 | 按项目 `<NoWarn>`（视图由代码构造，本就不走运行时 XAML 加载器） |
| CS1591 / CS1573 | 16 / 4 | 补齐 `uWidgets.Core` 的 `<summary>` / `<param>` |
| CS0618 | 10 | 迁移到替代 API（`Screen.Primary` → `Screen.IsPrimary`、`IStyleable` → `StyleKeyOverride`、`Checked/Unchecked` → `IsCheckedChanged`） |
| CS8602 / CS8604 | 2 / 2 | 真实空值守卫或已证不变量，未用 pragma 掩盖 |
| CS0108 | 2 | 加 `new` 关键字 |
| CS0419 / CS1574 | 1 / 1 | 修正写坏的 `<see cref>` |

唯一保留的过时调用是文件夹拖放的 `GetFileNames()`：同一 `DataFormats.Files` 的 `GetFiles()` 分支已经在前，替换会静默改变拖放语义，因此用一段带注释的窄 `#pragma` 圈住。

---

### 🗑 杂散文件

- 删除仓库根目录的 `weather_crash_log.txt`（Open-Meteo 503 的旧崩溃转储）；
- 删除 `tests/TrayTest.cs`（未纳入任何工程的游离文件）。

---

### 📄 文档

`项目解构报告.md` 重写为一份**纯项目解析**：删掉全部版本发展史（原 §9–§42 的逐版本更新日志）与历史遗留问题叙述，只保留当前代码的真实结构 —— 三层架构、目录解构、关键流程、配置体系、网格与布局引擎、材质与主题系统、18 个小组件模块、构建发布、测试与验证体系。

---

### 🧪 回归

`tests/` 下 22 个检查工程逐个跑：

- **20 个全绿**，包括 `LiquidGlassChecks` / `ClockGlassChecks` / `ClockThemeChecks` / `ClockVisualChecks` /
  `AccentPersistenceChecks` / `FullscreenChecks` / `WidgetStackChecks` / `ColorfulThemeChecks` /
  `GalleryVisualChecks` / `CalendarHollowChecks` / `MultiScreenGridChecks` / `MusicVisualChecks` /
  `SearchVisualChecks` / `SingleInstanceChecks` / `TrayIconChecks` / `VisualVerification` /
  `FixedSpanChecks` / `LayoutOwnershipChecks` / `LayoutCleanup` / `PictureStressChecks`。
- **`MapAndTitleBarChecks`**：本轮修好了它长期不绿的两条**过期断言** —— 一条把
  `SettingsWindowBackground` 的不透明度钉死在 `0.96`（1.9.1 起实际为 `0.80`，于是此后每个版本都误报失败），
  一条钉死了剪贴板弹窗早已改名的 `TabCode`/`TabLink`。现在两条改成断言真正的性质与真实的
  `TabAll/TabText/TabImage/TabFiles`，该套件全绿。
- **`ProgressAndPictureChecks`**：在本机崩在 `BigFolderPopupWindow` 渲染处（`0xC0000005`）。
  已用 HEAD 的干净 worktree 复现 —— **同一个崩溃在未改动的基线上同样出现**（且基线第一次跑是通过的），
  因此这是既有的偶发崩溃，与本次改动无关。同一日志里 SkSL 编译报错
  （`'n' has not been assigned` / `'depth' has not been assigned`）在基线上也一样存在，同样不是本轮引入。

真机截图验证了本次的两个修复（见上文「验证」与「真机测量」）。
