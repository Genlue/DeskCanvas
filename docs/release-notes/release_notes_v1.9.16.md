## uWidgetsPlus v1.9.16 更新日志

> 无边框时钟里三个属于旧版玻璃模型的设置项已全部删除 —— 组件现在**完全跟随全局主题**，不做任何组件级材质或光学覆盖。
> 另外修掉「覆盖颜色遮罩 → 跟随强调色」怎么选都是蓝的毛病。

---

### 🧹 无边框时钟：清掉三个旧版玻璃设置项

**现象**：无边框时钟的组件设置里有三项在新版材质模型下已经没有意义：

- **视觉主题** —— 下拉里把「液态玻璃」和「柔光玻璃」当成两个独立材质；
- **边缘染色强度**（默认 10%）；
- **边缘折射宽度**（默认 0 px）。

**根因**：这三项都是旧版玻璃模型的遗留。自 v1.9.9 柔光玻璃并入液态玻璃之后，柔光只是同一套光学参数里的**柔光晕 / 光谱弥散**两个旋钮，不再是第二个材质；后两项则是组件级对全局光学的私有覆盖 —— 属于「一个组件自己攒了一套算法」的历史包袱。

**修复**：三项设置连同**模型字段、界面控件、语言键、以及驱动它们的全部接线**一起删除。无边框时钟现在没有任何组件级材质或光学覆盖：

| 原来由组件设置决定 | 现在 |
|---|---|
| 视觉主题（毛玻璃 / 液态玻璃 / 纯色） | `FramelessThemeResolver` 从**全局主题**解析 |
| 边缘染色强度 | 全局 `LiquidGlassSettings.EdgeTint` |
| 边缘折射宽度 | 由全局「折射」与「边缘宽度」推导的**自适应镜头** |

**旧配置兼容**：`layout.json` 里残留的 `ThemeMode` / `DyeIntensity` / `RefractionWidth` 会被反序列化直接忽略，**不需要任何手工迁移**。

**顺带的行为变化（请留意）**：毛玻璃描边的高光染色强度以前取组件里的「边缘染色强度」（默认 10%），现在改取全局 `EdgeTint`（默认 50%）。也就是说开了「覆盖颜色遮罩」或主题设了自定义强调色时，无边框时钟数字外那圈高光会比以前**明显染色** —— 换来的是它与液态玻璃字形路径口径统一（后者本来就读全局 `EdgeTint`）。

---

### 🔵 遮罩颜色：「跟随强调色」为什么永远是蓝色

**现象**：无边框时钟 → 覆盖颜色遮罩 → 打开「跟随强调色」，遮罩永远是蓝色，跟外观页里挑的强调色毫无关系。

**根因**：`ResolveOverlayColor` 的「跟随强调色」分支**只读 `Theme.AccentColor`**，读不到就落到一个写死的 `Color.FromRgb(0, 120, 215)`：

```csharp
if (model.FollowAccentColor)
{
    var hex = theme?.AccentColor;          // ← 跟随系统强调色时这里是 null
    if (!string.IsNullOrEmpty(hex) && Color.TryParse(hex, out var parsed))
        baseColor = parsed;                 // ← 于是这行不执行，保持写死的蓝
}
```

关键在于 `Theme.AccentColor == null` **恰恰就是**「跟随系统强调色」这个默认项。`ThemeService` 在这种情况下**根本不会调用 `ApplyAccent`**，所以只看 `Theme.AccentColor` 的代码永远拿不到强调色，只能是那个写死的蓝。

**修复**：改成两步解析：

1. 用户**手选**的 `Theme.AccentColor` 优先；
2. 否则读应用资源 **`SystemAccentColor`** —— 手选颜色由 `ThemeService.ApplyAccent` 写在那里，跟随系统时则由 Avalonia 的 Fluent 主题用 **Windows 系统强调色**种入同一个键；
3. 两处都取不到才用默认色。

这跟 `Calendar/Views/Month.axaml.cs`、`Notes/Views/Note.axaml.cs`、`Fixed/ViewModels/AggregateViewModel` 里既有的写法完全一致。

**验证**：把强调色固定为绿色 `#12C46A` 渲染对照快照，按不透明像素的平均 RGB 比对：

- `4x2-overlay-tint`（跟随强调色）→ **(156, 227, 191)**，明显偏绿 ✓ 修复前这里恒为蓝色；
- `4x2-gold-tint`（自定义颜色）→ (250, 225, 125)，偏黄，未受影响；
- `4x2-impact-heavy`（无遮罩）→ (250, 250, 250)，中性白，未受影响。

`ClockThemeChecks` 另加了四条精确断言：跟随系统时解析到资源里的强调色、手选时用挑的颜色、自定义色忽略强调色、遮罩不透明度正确落到 alpha。

---

### 🧪 检查工程

`src\uWidgets.sln` 以 **0 警告 / 0 错误** 构建。无边框时钟相关检查全部通过：

- `ClockThemeChecks` — `ALL CHECKS PASSED`（新增全局材质解析、模型字段已移除、残留值可安全反序列化、遮罩强调色解析）；
- `ClockVisualChecks` — 11 个字体/跨度快照全部渲染通过；
- `LiquidGlassChecks` — 0 条 FAIL，光学 / 柔光 / GPU 材质 / 壁纸摆放四个子模块全过。
