## uWidgetsPlus v1.9.14 更新日志

> 修复液态玻璃下**纯色背景色完全不起作用、卡片像盖了一层黑色遮罩**的问题。

### 🐞 问题

液态玻璃模式下，无论把「纯色」背景色设成什么（白色、浅蓝……），卡片看上去都被一层
**黑色遮罩**压暗，颜色设置毫无效果；而且「不透明度」拉得越高越黑。

### 🔍 根因：涂层颜色少乘了 255

CPU 渲染器把涂层作为**字节（0–255）**混入：

```csharp
var r = Channel(channelRed, coating.Red);      // 0..255
return c * (1 - localTint) + coat * localTint; // c 也是 0..255
```

GPU 传给着色器的却是归一化值：

```csharp
["coating"] = new[] { p.Coating.Red / 255f, ... }   // 0..1  ← 错了
```

于是涂层的贡献只有应有的 **1/255**，恒等于零。剩下的只有 `c * (1 - localTint)` 这一步压暗 ——
不透明度 60% 就是整体压暗 60%，看起来正好像一层黑色遮罩；不透明度越高越黑，
而背景色本身完全被吞掉。

修复：按 0–255 原样传入，与 CPU 渲染器一致。

### 📏 验证（同帧 GPU vs CPU 逐像素对比，`tests/GlassGpuProbe`）

| 涂层设置 | GPU 中心 | CPU 中心 | 平均 \|Δ\| |
|---|---|---|---|
| dark `#2E2E2E` @ 18% | `#c5c7c6` | `#d0d0d0` | 17.8 / 765 |
| dark `#101418` @ 75% | `#484b4e` | `#4b4e51` | **6.0** |
| **light `#FFFFFF` @ 60%** | **`#f5f6f5`** | `#fafafa` | **9.4** |
| **light `#E8F0FF` @ 60%** | **`#e7edf5`** | **`#ecf1fa`** | **9.3** |

关键场景（浅色 + 60% 不透明度）现在是**近白色**而不是黑色遮罩，浅蓝 `#E8F0FF` 也如实呈现为偏蓝。
修复前这四个场景的 GPU 结果都会比 CPU 暗约 150 个单位。

整体误差同步下降：背景清晰度 100% 档由 32.9 降到 20.2，50% 档由 51.0 降到 39.6。

### 🛡 新增守卫

`GpuMaterialCheck` 增加源码级断言：涂层必须以 0–255 交给着色器，不得归一化。
这是单位错误而非像素错误，所以按该文件既有做法在源码层面锁死。

### 🧪 回归

LiquidGlassChecks / ClockGlassChecks / ClockThemeChecks / FullscreenChecks /
WidgetStackChecks / ColorfulThemeChecks / GalleryVisualChecks 全绿；真机 `GlassGpuProbe` 0 失败。
