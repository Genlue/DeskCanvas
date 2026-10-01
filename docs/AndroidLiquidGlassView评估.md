# AndroidLiquidGlassView 研究评估（对 uWidgets 是否有帮助）

- **对象**：<https://github.com/QmDeve/AndroidLiquidGlassView>（MIT，(c) 2025-2026 QmDeve，v1.0.5）
- **评估基准**：uWidgets 1.7.x 的「液态玻璃」材质（`LiquidGlassRenderer` + `LiquidGlassSurface` + `LiquidGlassWallpaper`，见 `项目解构报告.md` §18）
- **结论一句话**：**它的"光学模型"不值得照搬，但它的"执行模型"很值得抄**——整条光学链路就是一个片元着色器 + GPU runtime effect。本次已把它的 AGSL 逐行移植成 Skia SkSL，并在 **Avalonia 11.1.3 + SkiaSharp 2.88.8 的 GPU 通道**上跑通：420×420 单卡 **0.25 ms**，同尺寸 uWidgets 现有 CPU 渲染器 **46.3 ms**（约 **185×**）。同时发现一个必须避开的坑：**SkiaSharp 2.88.8 在 CPU/软件渲染下光栅化 SkSL 会直接崩进程**。
- **另外一条结论（专项调研）**：Windows 上**没有**受支持的"实时桌面 backdrop 进自定义着色器"路径（组合器的自定义 HLSL 被 `[NoComposition]` 禁用；WGC/DXGI/放大镜各有硬伤，详见 §6）。所以"液态玻璃跟手动态壁纸"仍是不确定项，而"GPU 光学 + 现有抓图源"是确定能拿到收益的一步。
- **证据目录**：`docs/glass-study/`（对比图、移植后的 SkSL 全文、本次所有探针源码）
- **落地结果（已实现）**：本报告的 §5-A/B 已按"取长补短"落成第五个外观主题 **柔光玻璃（`SurfaceStyle.SoftGlow`）**——
  保留 uWidgets 自己的 `BevelField` 法线场与光照模型（即本报告判定"不要照搬"的那部分），只吸收 Android 库真正强的地方：
  **弥散柔光（无硬边亮线）+ 七抽光谱色散**。见 `项目解构报告.md` §33、`docs/soft-glow/`。
  GPU runtime effect（§5-A 的第一条，实测 0.25 ms/卡）仍**未**实施，是下一步的独立工作项。

---

## 1. 这个库到底做了什么（源码级）

| 文件 | 职责 |
|------|------|
| `core/.../widget/LiquidGlassView.java` | `ViewGroup`：圆角裁剪、可选拖拽/弹性/触摸光晕，子 View 承载内容 |
| `core/.../impl/LiquidGlassimpl.java` | **核心**：`RenderNode` 录制「玻璃背后的内容」→ `RenderEffect.createRuntimeShaderEffect(shader, "content")` → `createChainEffect(shaderEffect, blurEffect)` → `node.setRenderEffect(...)` |
| `core/src/main/res/raw/liquidglass_effect.agsl` | **核心**：186 行 AGSL 片元着色器（光学模型全在这里） |
| `core/.../Config.java` | 参数容器（corners/refraction/blur/dispersion/tint/contrast/whitePoint/chroma/depth） |

关键点（与 uWidgets 的可比性）：

1. **它折射的不是"系统桌面"，而是同一个窗口里位于玻璃下方的那个 View**。
   `record()` 里 `target.draw(rec)` 把内容源 View 录进 RenderNode，`host`/`target` 的窗口坐标差做平移。
   所以它和 uWidgets 的"把壁纸快照当折射源"是**同一类问题**（折射一张位图），只是它的位图**由系统每帧重新录制**，天然逐帧实时。
2. **光学模型只有三件事**：SDF 圆角矩形 → 球冠形位移剖面 `circleMap` → 7 次采样做光谱色散；外加线性 sRGB 里的饱和度/对比度/白点/tint。
   **它没有任何高光/描边/镜面/边缘染色**——iOS 那种"亮边"在这个库里是**没有**的。
3. 参数面很窄：`refractionHeight`（12–50dp）、`refractionOffset`（20–120dp，取负）、`dispersion`（0–1）、`blurRadius`、`tint`、`contrast`、`whitePoint`、`chromaMultiplier`、`depthEffect=0.3`。

### 1.1 着色器逐行解读（这份是本次移植的蓝本）

```
sd = sdRoundedRect(centered, halfSize, radius)
if (-sd >= refractionHeight)  → 中心平坦区：直通采样 + saturate + 对比度 + tint
否则                          → 弯月区：
   d    = circleMap(1 - (-sd)/refractionHeight) * refractionAmount      // 球冠剖面，外缘 0 → 内缘最大
   grad = normalize(shapeGrad + depthEffect * normalize(centered))       // SDF 梯度与径向深度梯度混合
   refr = coord + d*grad
   disp = chromaticAberration * (x*y)/(hx*hy)                            // 色散强度按象限符号变化
   color = Σ 7 次采样（红/橙/黄/绿/青/蓝/紫，权重 1/3.5、1/3.0、1/7.0）   // 光谱式色散
   → saturate(线性 sRGB) → 对比度/白点 → tint
```

---

## 2. 与 uWidgets 现状逐项对比

| 维度 | AndroidLiquidGlassView | uWidgets 现状（1.7.x） | 谁更强 |
|------|------------------------|------------------------|--------|
| 折射剖面 | `circleMap` 球冠，外缘 0、峰值靠内 | `sin(πt)·(1−t)^1.4` 弯月剖面，带峰值位置/放大率/二阶差分断言 | uWidgets（有量化回归） |
| 法线场 | SDF 梯度 ⨁ 径向深度梯度（`depthEffect` 混合） | `BevelField`：圆角纯径向、直边纯正交、切点 C¹、**无中轴折痕** | **uWidgets 明显更强**（见 §4 图注） |
| 高光 | **无** | 定向发丝边（1.6 DIP）+ 双叶镜面（exp 28/8）+ Fresnel 掠射 + Screen 混合 + 天光垂向渐变 | **uWidgets 明显更强** |
| 色散 | **7 次采样的光谱色散**（红→紫，按象限变号） | RGB 三通道沿法线 ±n 位移（3 次采样） | **Android 更物理**（廉价且更好看） |
| 饱和度/色调 | 线性 sRGB 内做 saturate + chroma + contrast + whitePoint + tint | 屏幕空间饱和度 1.20 + 自适应霜化 + 涂层不透明度 | 各有取舍 |
| 采样源 | **同窗口 View，RenderNode 逐帧录制** → 真·实时 | Progman 抓图快照（2s TTL）+ 壁纸文件回退，静态 | **Android 的执行方式更强** |
| 执行 | GPU `RenderEffect`（系统合成器里跑），几乎零成本 | CPU 每像素 C#（`Parallel.For`）+ PNG 编码 + 缓存位图 | **Android 完胜** |
| 参数面 | 8 个 | 8 个（Blur/Refraction/EdgeWidth/Highlight/Dispersion/LightAngle/EdgeTint/壁纸偏移） | 平手，但语义不同 |

---

## 3. 本次实测（可复现）

探针工程（源码已存入 `docs/glass-study/probe/`）：

- `Program.uwidgets-reference.cs` + `Shader.cs` + `Backdrop.cs`：控制台探针，用同一张合成"壁纸"跑 uWidgets 现有 CPU 渲染器并计时；
- `Program.avalonia-gpu-probe.cs`：Avalonia Win32 窗口探针，经 `ISkiaSharpApiLeaseFeature` 拿到 Skia 画布，用 `SKRuntimeEffect` 画移植后的 Android 着色器，并计时/导出 PNG。

### 3.1 移植工作量：4 处纯语法差异

| AGSL（Android） | SkSL（Skia） |
|-----------------|--------------|
| `toLinearSrgb()` / `fromLinearSrgb()` | SkSL 无此内置 → 手工内联 sRGB 传递函数（3 行） |
| `step(a, b)` | SkSL **没有 `step()`** → `a >= b ? 1.0 : 0.0` |
| `content.eval(coord)` | 子着色器用 `sample(content, coord)` |
| `half3` 辅助运算 | 改 `float3`（精度语义差异） |

**其余（SDF、球冠剖面、梯度混合、7 次采样色散、权重系数）逐字照抄即可。**

### 3.2 编译与渲染

| 环境 | 结果 |
|------|------|
| SkiaSharp 2.88.8（= Avalonia 11.1.3 的版本），**CPU raster** | 编译 OK，**一画就崩**：`SEHException`，进程退出码 `0xC000001D` |
| SkiaSharp 2.88.8，**GPU（Avalonia `AngleEgl` → D3D11）** | 编译 OK、窗口画布绘制 OK、离屏 GPU surface + PNG 导出 OK |
| SkiaSharp 3.119.4（= Avalonia 12.x 的版本），CPU raster | 编译 OK、CPU 绘制 OK（Skia 新版补上了 SkSL→RasterPipeline 的 CPU 后端） |

> 崩溃不是用法问题：连 `half4 main(float2 p){return half4(1,0,0,1);}` 这种最简 runtime effect 在 2.88.8 的 CPU 路径上同样崩。**2.88 的旧 Skia 只能靠 GPU 后端跑 SkSL。**

### 3.3 性能（同一台机器，best-of-N）

| 渲染路径 | 尺寸 | 耗时 |
|----------|------|------|
| uWidgets `LiquidGlassRenderer`（CPU，含 PNG 编码） | 420×420（176k px） | **46.3 ms** |
| uWidgets `LiquidGlassRenderer`（CPU，含 PNG 编码） | 1200×800（960k px） | **184.5 ms** |
| Android AGSL→SkSL（GPU），仅绘制 | 420×420 | **0.25 ms** |
| Android AGSL→SkSL（GPU），绘制 + PNG 编码 + 解码 | 420×420 | 14.56 ms |
| Android AGSL→SkSL（GPU），调参版（blur 9 + tint） | 420×420 | **0.37 ms** |

两个可以直接引用的结论：

1. **光学计算的成本从"几十毫秒"降到"零点几毫秒"**（同像素数 ≈185×）。玻璃材质可以**逐帧画**，不再需要 90 ms 防抖 + 2 s 缓存位图；
2. 一旦走 GPU，"瓶颈"就从算法转到**采样源**：`draw + PNG encode` 仍要 14.6 ms —— 说明**别再走"渲染成位图再贴图"的老路**，直接在 Avalonia 的渲染通道里画 runtime effect，才能把 0.25 ms 拿到手。

---

## 4. 画质对比（重要：不要照搬它的光学模型）

`docs/glass-study/01-compare.png`（同一张合成壁纸、同一 420×420 区域、同一圆角 32）：

1. 左 1：原图（20 px 网格便于看弯曲）；
2. 左 2：**uWidgets 现版**——网格近乎笔直，边缘极克制，有涂层与亮边；
3. 左 3：**Android 默认**——网格在边缘明显弯折，**圆角处出现"蝴蝶结/螺旋"折痕**（左上、左下最明显），且没有亮边，观感更平；
4. 左 4：Android 调参版（blur 9 + tint）——变成偏磨砂的糊状，边缘弯折被模糊吃掉。

原因很明确：它把 **SDF 梯度**与**径向梯度**按 `depthEffect` 混合当法线（`grad = normalize(shapeGrad + 0.3*normalize(centered))`），在圆角扇区两者方向不一致 → 折痕。uWidgets §18.3 正是为了解决这个"X 型/蝴蝶结折痕"才写了 `BevelField`。**用它的模型替换现有模型 = 画质倒退。**

---

## 5. 那么，"对我这个项目是否有帮助"——分项裁决

### ✅ 值得拿（按性价比排序）

**A.（最高价值）把 uWidgets 自己的光学模型改写成 SkSL，跑在 GPU 上**
- 画质用 uWidgets 的（弯月剖面 + BevelField + 定向亮边 + 双叶镜面），执行方式用 Android 库的（GPU runtime effect）；
- 接入点：`LiquidGlassSurface.Render()` 里，`Material?.IsLiquidGlass == true` 且**有 GPU 上下文**时，改为 `context.Custom(new GlassDrawOp(...))`，在 `ICustomDrawOperation.Render` 里 `context.TryGetFeature<ISkiaSharpApiLeaseFeature>()` 取画布直接画；`LiquidGlassRenderer` 保留为软件渲染回退与测试 oracle；
- 收益：0.25 ms/卡 → 拖拽/缩放/换参**逐帧跟随**（现在要等 90 ms 防抖 + 2 s 抓图 TTL）；不再有 PNG 编码往返；超大组件不必降采样；
- 成本：约 1 个 SkSL 文件（150–250 行，uWidgets 现有公式是现成的）+ 一个 draw op + 一处守卫，**0.5–1 天**。
- 注意：`SKImage`/`SKShader` 的创建要在渲染线程做（`SKRuntimeEffect` 只编译一次并缓存）；材质参数变化只需更新 uniforms 对象。

**B. 借它的 7 次采样光谱色散**
- 现在 uWidgets 只做 RGB 三通道分离；改成"沿法线 7 抽样 + 光谱权重"，是**低成本、高收益**的观感升级（尤其高 `Dispersion` 档）；
- 代价：`tests/LiquidGlassChecks` 里"色散 0% 时 |R−B| 恒为 0 / 100% 时边缘 13.2 级"的断言需要同步更新。

**C. 借它的"采样源抽象"**
- 它把"折射源"做成 shader 的 `content` 输入，与光学模型解耦；uWidgets 现在是 `LiquidGlassWallpaper` 硬绑 Progman 抓图。把采样源抽成"一张 `SKImage`/着色器"后，未来换任何实时源（见 §6）都只动一处。

**D.（低优先级）交互手感**
- `LiquidTracker`（弹性/果冻拖拽）+ 触摸处径向光晕（`RadialGradient` 40% 白）+ 按下 1.02× 缩放。桌面组件上可做成"悬停光晕跟随指针 / 拖拽时的轻微果冻"，与玻璃材质气质契合。属于锦上添花。

### ❌ 不要拿

1. **别换它的光学模型**（§4，圆角折痕 + 无高光）；
2. **别抄它的采样方式**：它折射"同一窗口内玻璃下方的 View"，桌面组件背后是**桌面**，不是 Avalonia 窗口内部的兄弟控件，Windows/Avalonia 没有等价的 backdrop-shader 输入（详见 §6）；
3. **别只用它的 8 个参数**：没有边缘宽度、光照方向、边缘染色，与现设置页/回归断言不匹配。

---

## 6. 采样源：能否让液态玻璃"跟手动态壁纸"（专项调研结论）

Android 库之所以"实时"，靠的是 `RenderNode` + `RenderEffect`——**系统合成器直接把"玻璃下方内容"喂给着色器**。Windows 侧到底有没有等价物？逐项查证后的结论是：**没有一条又实时、又能拿到"桌面且不含自己"、又能进 shader 的路**。

| 方案 | 实时 | 能排除自身窗口 | GPU 驻留 | 裁决 |
|------|------|----------------|----------|------|
| WGC 显示器捕获 + `SetWindowDisplayAffinity(WDA_EXCLUDEFROMCAPTURE=0x11)` | ✅ | 文档说"完全不出现"，**实测历史上时好时坏**（1909 黑块 / 19041 透明 / 21H2 又黑 / 24H2 更严） | ✅ D3D11 纹理 | **唯一有希望的实时路线，但必须先 spike**（结果直接决定成败） |
| WGC **窗口项**捕获（直接抓 `Progman`/`WorkerW`） | ✅ | 天然不需要排除（抓的是别的窗口） | ✅ | 桌面级组件值得先试，成本最低 |
| Windows.UI.Composition `CreateBackdropBrush()` + **自定义 HLSL** | ✅ | — | ✅ | ❌ **不可能**：Win2D `PixelShaderEffect` 被标 `[NoComposition]`；组合器只允许内置效果（blur/saturate/colorsource/blend/affine） |
| 同上但只用**内置**效果图 | ✅ | — | ✅ | ⚠️ 可行（Avalonia 自己就是这么做的），但**做不出真折射/色散**，最佳效果 ≈ 你现在的毛玻璃 |
| `IDXGIOutputDuplication` 桌面复制 | ✅ | ❌ 无任何 HWND 过滤参数 | ✅ | ❌ 自身窗口必然入画 |
| 放大镜 API `MagSetWindowFilterList(MW_FILTERMODE_EXCLUDE)` | ✅ | ✅ **唯一文档化的 HWND 排除** | ❌ 只画进 magnifier 窗口，无回读 | ⚠️ 排除语义最明确，但要靠"再抓自己的放大镜窗口"叠 hack |
| `DwmRegisterThumbnail` | ✅ | — | ❌ 无回读 API | ❌ 不能当纹理源 |
| WinAppSDK `DesktopAcrylicController` / `SystemBackdrop` | ✅ | — | ✅ | ❌ 只有系统材质，叠不了自定义效果 |

补充事实（都与 uWidgets 直接相关）：

1. **Avalonia 内部的 backdrop brush 拿不到**：`WinUiCompositionUtils` / `WinUiCompositedWindow` 全是 `internal`，唯一公开旋钮只有 `Win32PlatformOptions.WinUICompositionBackdropCornerRadius`；想插自己的 visual 只能反射进 MicroCom 私有字段（高危）。**注意**：Avalonia 在 Win11 22000+ 用的是 `ICompositor3::CreateHostBackdropBrush()`，而微软明确写了该 brush **"应用无法回读像素"**。
2. **圆角裁剪可以比 `SetWindowRgn` 更好**：WinUIComposition 模式下 Avalonia 已用 `ICompositor5.CreateRoundedRectangleGeometry` + `ICompositor6.CreateGeometricClipWithGeometry` 给 backdrop visual 打几何裁剪（抗锯齿），优于 `SetWindowRgn`（锯齿 + 每次改区域触发重排）。这与 `项目解构报告.md` §8.1-3 的"后续建议"是同一条线索，值得单独排期。
3. **同类先例都是"抓自己"**：`KaranocaVe/LiquidGlassAvaloniaUI`、`Alpaq92/Fluid.Avalonia.Acrylic` 都是抓**应用自身已渲染内容**（visual tree 快照）再用 Skia shader 折射，不算桌面背景，所以同样解决不了"动态壁纸跟手"。
4. **Skia 侧并不缺"backdrop 输入"**：`SKRuntimeEffect` 支持 `uniform shader` 子着色器输入（本次已验证），缺的只是**一块实时的桌面纹理**。也就是说：一旦拿到实时纹理，折射/色散在 GPU 上跑是现成的。

**结论**：把"实时桌面折射"当作**独立 R&D spike**（1–2 天），不要把它和"换 GPU 着色器"绑在一起。**A 方案（GPU 光学 + 现有抓图源）本身已经能拿到"逐帧渲染"的全部收益**，只是抓图 TTL 需按场景放宽（拖拽/改参期间降到 100–200 ms，静止时保持 2 s）。

若真要做这个 spike，第一步与真实成本：

1. **判定性实验**（半天）：对任一窗口 `SetWindowDisplayAffinity(hwnd, 0x11)`，用 WGC 显示器捕获（`Direct3D11CaptureFramePool.CreateFreeThreaded` + `FrameArrived`）dump `frame.Surface` 中间像素 —— 判定被排除区域是**桌面内容**还是**黑块**。这一步决定整条路是否成立。
2. **更省的备选**（半天）：`GraphicsCaptureItem.TryCreateFromWindowId(Progman / WorkerW)` 直接抓桌面窗口，天然不含自己的窗口、无需 WDA。
3. **接进 Skia 的真实成本**：SkiaSharp 2.88 不暴露 D3D 后端，D3D11 纹理要零拷贝进 Skia 得走 `EGL_ANGLE_d3d_texture_client_buffer` → EGLImage → GL texture → `GRBackendTexture`；或走 `Win32PlatformOptions.CustomPlatformGraphics` 自建 `ISkiaGpu`（但那时 `CompositionMode` 只能 `RedirectionSurface`，与现在用的 `WinUIComposition` 冲突）。**这才是这条路的深水区。**
4. **额外干扰**：WGC 捕获会带一圈黄框，`GraphicsCaptureSession.IsBorderRequired = false` 需要打包应用能力 + 用户同意，未打包的桌面程序基本拿不到 —— 对常年贴在桌面上的小组件是致命体验问题。

**因此本报告的立场**：近期先把 A 方案做完（收益确定、风险可控），把实时源列为"值得验证、但不押注"的后续项。

---

## 7. 风险与坑（务必先看）

1. **软件渲染会崩**：Avalonia 未固定 `RenderingMode` 时默认 `[AngleEgl, Software]`，一旦回退到软件渲染（老旧 GPU、远程桌面、虚拟机、驱动被禁），SkSL 绘制会**让整个进程退出**。必须 `lease.GrContext != null` 守卫，否则走现有 CPU 路径。
   ```csharp
   using var lease = context.TryGetFeature<ISkiaSharpApiLeaseFeature>()?.Lease();
   if (lease?.GrContext is null) { /* 回退：现有 LiquidGlassRenderer CPU 位图 */ return; }
   ```
2. **API 会变**：`ISkiaSharpApiLeaseFeature`（读 Skia 画布的唯一公开入口）在 Avalonia 里被标为 `[Unstable]`，升级时可能改名/改签名；SkiaSharp 2.88 的 `SKRuntimeEffect.Create` 在 3.x 改名 `CreateShader`、`ToShader(bool)` 重载被移除。升级 Avalonia → 12.x（配 SkiaSharp 3.119.4）时只改 3 行，但**不要**只想升到 11.3.2：它仍锁 SkiaSharp 2.88.9（CPU 崩溃大概率依旧）。
3. **SkSL 是旧方言**：2.88 里没有 `step()`、子着色器必须 `sample()`、无 `toLinearSrgb()`。写新 shader 时别再踩这几脚。
4. **测试链路**：`tests/LiquidGlassChecks` 是 CPU/headless 的 → 在 2.88 下**无法**在其中验证 shader。建议：shader 只走 GPU 冒烟测试（像本次的 Avalonia 探针那样开窗渲染 + 导 PNG），CPU 端继续用现有渲染器当**画质 oracle**做像素对比。
5. **许可证**：MIT，移植/改写需保留版权与许可声明（本次 `docs/glass-study/liquidglass_effect.sksl` 头部已注明来源与 MIT）。

---

## 8. 落地建议（最小改动版）

```
src/uWidgets/Services/LiquidGlassShader.sksl           ← 新：uWidgets 光学模型的 SkSL 版（B 方案的 7 抽色散可并入）
src/uWidgets/Services/LiquidGlassEffect.cs             ← 新：SKRuntimeEffect 编译缓存 + uniforms 组装（单例、线程安全）
src/uWidgets/Views/Controls/LiquidGlassSurface.cs      ← 改：GPU 时 context.Custom(new GlassDrawOp(...))；无 GPU 时保持现状
src/uWidgets/Views/Controls/GlassDrawOp.cs             ← 新：ICustomDrawOperation + ISkiaSharpApiLeaseFeature + GrContext 守卫
tests/LiquidGlassChecks/                               ← 改：CPU 渲染器降级为 oracle；新增 GPU 冒烟（开窗 1 帧 + 导 PNG + 与 oracle 比对容差）
项目解构报告.md §18.4                                  ← 改：渲染管线与性能一节更新为 GPU 主路径
```

验收标准（可直接沿用现有量化口径）：GPU 与 CPU oracle 的像素平均差 ≤ 1 级；同参数下 420×420 单帧 ≤ 1 ms；拖拽期间材质刷新 ≥ 30 fps；软件渲染模式下**不崩**且自动回退。

---

## 附录

- `docs/glass-study/liquidglass_effect.sksl` —— Android 库着色器的 **SkSL 移植全文**（含方言差异注释，可直接喂给 `SKRuntimeEffect`）
- `docs/glass-study/probe/` —— 本次全部探针源码
  - `Program.uwidgets-reference.cs`：uWidgets CPU 渲染器计时/出图
  - `Program.avalonia-gpu-probe.cs`：Avalonia GPU runtime effect 探针（计时 + 出图 + 软件渲染崩溃复现）
  - `Shader.cs`、`Backdrop.cs`、`Diag.cs`：着色器、合成壁纸、最小化崩溃定位
- 图片：`01-compare.png`（四方对比）、`02-android-shader-default.png`、`03-android-shader-tuned.png`、`04-uwidgets-current.png`、`05-backdrop-source.png`
- 上游参考：[QmDeve/AndroidLiquidGlassView](https://github.com/QmDeve/AndroidLiquidGlassView)；文档站 <https://liquidglass.qmdeve.com/>
