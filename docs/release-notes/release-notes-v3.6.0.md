# DeskCanvas v3.6.0 更新日志

> 一个功能与治理并重的版本：新增**翻页时钟**、**系统控制中心**、**磁盘存储**三个组件；**无边框时钟**解除毛玻璃锁定、回归六材质自选，内置 8 款艺术字体与等宽数字防抖，字形几何改 Skia PathOps 简化；全链路**内存与资源释放按需化**——定时扫描改为事件驱动（单飞 / 冷却 / 地板 / 优先级），设置窗口关闭即分离页面并即时回收，组件缓存全部加上字节预算，最后一个组件关闭时释放共享材质缓存。主程序版本升至 3.6.0，全解决方案 21 个项目编译 0 警告 0 错误。

---

### 🕰️ 1. 翻页时钟组件（全新）

* **4×2 2:1 机械翻页**：`FlipCard` 上下半扇叶翻页动画，配中缝 / 铰链 / 描边五支刷子；预设跨度 4×2 / 6×3 / 8×4，`SnapSpanOf` 把 6×2 → 6×3、1×1 → 4×2。
* **秒显示两样式**：三卡 `HH:MM:SS`，或分钟卡右下角徽章；**日期三位置**：顶栏 / 底栏 / 卡内嵌角标。
* **12 / 24 小时制**、时区与 AM/PM 全部可配；2 卡与 3 卡模式的卡宽间距均精确拟合 328 DIP 零溢出。
* 归入 Clock 组件包（`Clock` 1.5.0 → 1.6.0），与既有表盘共用设置页框架。

---

### 🎛️ 2. 系统控制中心组件（全新）

* **八项磁贴**：亮度 / 音量 / 音频输出 / 麦克风 / Wi-Fi / 蓝牙 / 深浅色 / 桌面图标显隐，`SystemControlService` 承接全部系统调用。
* **三档布局各自默认排布**：2×2 / 4×2 / 4×4；`NormalizeLayout` 按类型去重 + 越界钳制 + 占位冲突自动挪槽，配置永不打架。
* **悬停滚轮调值**：亮度 / 音量 / 麦克风支持在磁贴上直接滚动调节，步进 1–10% 可调。
* 单控制磁贴子组件 + 两套设置页（整体面板 + 单磁贴），组件库可单独放置任一控制。

---

### 💽 3. 磁盘存储组件（全新）

* **双展示形态**：进度条列表与环形仪表，剩余空间 / 已用总量 / 百分比三种文本格式。
* **盘符过滤**：可移动盘与网络盘显隐、隐藏盘符清单；占用警告色随用量阈值变化。
* **双击打开资源管理器**；`StorageService` 采集，10 秒刷新。
* **刷新不再重建控件树**：按「布局签名 + 盘符身份」增量更新值（文本 / 条宽 / 颜色 / 提示），拓扑变化（盘增删）、跨度 / DPI 变化或模型切换才走全量重建。

---

### 🎨 4. 无边框时钟：多材质回归 + 字体系统 + 字形几何重写

* **彻底解除毛玻璃锁定**：新增 `FramelessThemeResolver` 按 `SurfaceOverride` 解析 跟随全局 / 亚克力 / 液态玻璃 / 新液态玻璃 / 纯色 / 多彩，`GlyphLiquidGlassRenderer` 与 `FramelessGlassFrameCache` 整文件回归；`ApplyCurrentMaterial` 成为唯一写入点，一次性同步计时器 / 材质快照 / 帧缓存 / 窗口透明度 / 原生字形区域，杜绝分步事件子集复刻引发的主题跟随回归。
* **字体系统**：内置 8 款艺术字体（HarmonyOS Sans Condensed / Bebas Neue / DM Serif Display / Bungee Inline / Black Ops One / Titan One / Righteous / Orbitron Variable，随附 7 个 TTF）；字体来源分艺术字体 / 系统字体两栏（系统栏精选集 + 本机枚举兜底）；字重滑杆仅对多字重 / 可变字体生效；新增**等宽数字防抖** `FixedWidthDigits`，`TabularMetrics` 按最大字宽钉槽，时钟永不横向抖动。
* **字形几何重写**：`FramelessGlyphGeometry` 改 Skia `SKPath.Simplify(Winding)` 溶解变体与 CJK 系统字体的内部重叠轮廓，修掉半透明玻璃填充与高光描边在尖角处的内部杂线；`SKTypeface` 按族 + 字重解析缓存。
* **挂起契约**：新增 `IWidgetSuspendable`，全屏挂起时释放可重建图形与定时器，恢复时重建；48 MB 帧缓存预算 + 预渲染取消令牌 + in-flight 去重 + 壁纸过期标记。

---

### 🧠 5. 内存与资源释放全链路按需化

* **`MemoryTrimmerService` 重写**：定时扫描（每 5 分钟 / 150 MiB 阈值）改为**按需触发**——只在真正有大块原生内存释放的时刻请求；三重守卫：单飞（进行中的请求合并为一次跟进）、冷却（90 秒）、地板（256 MiB）；两级优先级：`Coalesced`（组件关闭、Profile 切换等后台突发合并为一次）与 `Immediate`（用户直接看着的释放——设置窗口关闭——跳过冷却）；被压制的请求合并为**一次**延迟重试，突发结束后内存仍会回落。
* **设置窗口生命周期**：关闭（隐藏）路径**分离当前页**（含组件库的实时预览与订阅，走标准 Unloaded 契约）并请求 `Immediate` trim；重新打开用无过渡 swap **同步恢复缓存的同一页实例**并重订阅——修复「设置页占内存，关掉永不还」。
* **组件关闭联动**：`WidgetFactory` 在组件关闭时请求合并 trim；**最后一个组件关闭时释放共享材质缓存**（全桌面抓图与模糊背板，大桌面上各几十 MB），按需重建。
* **`UpdateTimer`**：订阅者注销时立即发布快照——最后一个订阅者离开即停表，已离开组件的委托不再被快照钉住。
* **缓存字节预算**：`LiquidGlassSourceCache` 64 MiB（4K 多屏单张背板超预算时清晰度优先）、`FolderIconService` 16 MiB、`MapTileService` 64 MiB（含 `trimGate` 串行化）、Clock 帧缓存 48 MB——条目数上限之外再兜一层真实像素字节预算。
* **`PictureViewModel` 孤儿清扫**：修复「切换图片时每个换片持住一张全尺寸解码位图」——失去所有者的图片一律进队列，渲染槽放行后立即释放，不再等 GC 终结器。
* **`MetricService` 快照共享**：900 ms TTL + 单飞——同一指标的多个视图（多屏、组件栈子卡）共享一次 WMI 采集，慢速查询不再叠加；`MultiDashboardViewModel` 加飞行中闩锁，慢轮次直接跳过。
* **组件栈挂起转发**：`WidgetStackView` 实现 `IWidgetSuspendable`，全屏时把挂起下发给所有子卡（含隐藏卡），子卡自行释放与恢复。

---

### 🧪 6. 验证与版本

* **新增 `tests/SettingsReleaseChecks`**：钉死本轮交付契约——trimmer 策略（`Coalesced` 冷却内不 trim、`Immediate` 穿透冷却）与窗口生命周期（关闭分离页面、订阅归零、组件库预览释放、重开恢复缓存实例并重建预览）；利用真实 Skia 后端 + Inter 字体跑无头窗口。
* 既有检查同步：`ClockThemeChecks` 重写为多主题架构（材质覆写 / DI 全入口断言）、`ClockVisualChecks` 改多材质渲染路径、`FixedSpanChecks` 增翻页时钟晶格与三档 VM 断言（328 DIP 精确拟合）、`LiquidGlassChecks` 同步默认值断言；`PictureMemoryChecks` / `FullscreenChecks` / `WidgetAdaptiveChecks` 全绿。
* **版本同步**：
  * 主程序：`3.5.0 → 3.6.0`（`src/DeskCanvas/AssemblyInfo.cs` + `installer/Package.wxs` 同步）；
  * Clock：`1.5.0 → 1.6.0`（新增翻页时钟组件）；
  * ControlCenter / Storage：全新组件（1.0.0）；
  * 全解决方案 21 个项目编译 0 警告 0 错误。
