# 内存审计 — 一言（Hitokoto）组件

审计范围：`src/Widgets/Hitokoto` 全部源码，以及该组件在宿主中**触发/长期挂靠**的进程级内存
（玻璃材质管道、二级面板弹窗、宿主契约 `Widget` / `WidgetFactory` / `Gallery`）。所有结论均读自源码并标注 `file:line`。
本次未改动任何代码。

> **先说结论。**
> 一言组件**自身的托管对象极小**（KB 级）：没有位图、大数组或按请求增长的缓存；
> 它"占用极大"来自三层叠加：
> 1. **组件触发的玻璃材质管道**（宿主级共享基建）：只要一言卡片在桌面上且主题为
>    `液态玻璃 / 柔光玻璃 / 新液态玻璃`，进程会保留**整桌面捕获位图**（单屏 8–33 MiB，
>    双屏/超宽 28–63 MiB），可能再叠一份 `stale` 副本、共享模糊背后景（预算上限 64 MiB）
>    和每窗口渲染表面；
> 2. **反复分配**（churn）：每次打开弹窗重建整棵窗口控件树；每次抓句新建一个 `HttpClient`
>    （独立连接池）；每次数据变更全量重建 50 张列表卡片；
> 3. **缓慢增长项**：收藏/点赞无上限、每次落盘整文件重写、若干生命周期卫生问题。
>
> 因此优化重点不是"给一言省几个 KB"，而是
> **①让纯文本卡片不进（或轻量化）玻璃管线；②让整桌面捕获在闲置时归还；③把弹窗/网络/存储的反复分配收干净。**

---

## 0. 内存构成速查（先量化）

| 资产 | 大小 | 归属 | 依据 |
|---|---|---|---|
| 整桌面 BGRA 捕获（`WallpaperSnapshot`，1920×1080 → 7680×2160） | **7.9 – 63.3 MiB** | 进程级静态（任何玻璃面活跃期间） | `LiquidGlassWallpaper.cs:39`、`LiquidGlassWallpaper.cs:1534-1541` |
| `stale` 副本（掩蔽失败帧用，Invalidate 后长期驻留） | 再 +7.9 – 63.3 MiB | 同上 | `LiquidGlassWallpaper.cs:45,370-392` |
| `probeBuffer`（仅实时采样开启时） | 再 +7.9 – 63.3 MiB | 同上 | `LiquidGlassWallpaper.cs:98,555-607` |
| 共享模糊背后景 ×≤3 条（清晰度 25% 默认） | 2560×1440≈0.9 MiB/条；4K≈2.0 MiB/条 | 共享缓存 | `LiquidGlassSource.cs:107,119,155-157` |
| 共享模糊背后景（清晰度 100%） | 14.1 / 31.6 MiB 每条，预算 64 MiB + 最新一条不可驱逐 | 共享缓存 | `LiquidGlassSource.cs:119,189-203` |
| 每张一言卡片：玻璃表面 `Prepared` | GPU 路径：仅持共享引用 + 染料小图（≈KB） | 每组件 | `LiquidGlassSurface.cs:139-165,594-617` |
| 每张一言卡片：CPU 回退位图（无 GPU/自检失败时） | 典型 0.4–0.6 MiB，上限 ≈4.6 MiB | 每组件 | `LiquidGlassSurface.cs:460-471,531-534` |
| 弹窗一次打开 | 480×560 窗口 + 全控件树 + 1 枚 `LiquidGlassSurface`；关闭即释放 | 瞬态 | `HitokotoPopupWindow.axaml.cs:89-105` |
| 组件自身对象（View/Store/模型/计时器） | **KB 级** | 每组件 | 见 §1 |

---

## 1. 一言组件**自身**持有的内存（源码级结论）

### 1.1 无大对象：没有位图/大数组/增长型缓存
`grep SKBitmap|RenderTargetBitmap|byte\[|MemoryStream` 在 `src/Widgets/Hitokoto` **零命中**
（唯一相关命中的是 3 处 `HttpClient`，见 §1.4）。历史记录有硬上限（50 条，`HitokotoStore.cs:17`），
每条约几百字节；`OfflineFallbackQuotes` 为 10 条常量（`HitokotoService.cs:33-45`）。
**结论：组件自身的静态内存占用在 KB 级，观察到的"极大"不可能是这些对象本身。**

### 1.2 `HitokotoStore`（单例）— 小但有两个卫生问题
* `Favorites` / `LikedUuids` **无上限**（`HitokotoStore.cs:33-35`）。单条开销小，但：
  * `IsLiked` / `IsFavorite` 为 **线性扫描**（`:75-91`），且每次 `RenderCurrentItem()` 各调一次
    （`HitokotoView.axaml.cs:294,298`）——列表大时是 CPU 问题，也会拖慢 UI 线程；
  * `RecordQuote` / `MarkLiked` / `ToggleFavorite` 每次都把**整个 `data` 序列化重写**
    （`SaveToDisk`，`HitokotoStore.cs:117,141,178,224-241`，`WriteIndented=true`）。连点"赞/收藏"
    时产生连续的 LOH 字符串 + 磁盘写放大；收藏上千条后每次写全量。
* 崩溃安全（`.tmp` + Move）做得对，无需改动机制。

### 1.3 `HitokotoView` — 生命周期基本正确，但有两处边界语义
* 订阅发生在**构造函数**：`HitokotoStore.Instance.Changed / QuoteSelected`（`HitokotoView.axaml.cs:89-90`），
  退订在 `Unloaded → Dispose`（`:578,580-596`）。正常路径平衡；但：
  * **"从未装载"的实例**（构造后未进可视化树）会一直挂在静态单例事件上——虽然宿主两条创建路径
    （桌面 `Widget`、组件库 `Gallery.axaml.cs:80-97` 显式 `Dispose`）都正确，但任何未来"create-then-drop"
    的用法都会静默悬挂；
  * **"卸载 → 再装载"**（控件被移出再放回可视树）会让已 `disposed=true` 的视图静默失效（不再订阅、不再刷新），
    属于功能缺陷而非内存缺陷；
  * `Dispose()` 只 `Stop()` 计时器、不置空/退订 `Tick`（`:592-595`）——对象可被回收，属卫生问题。
* 宿主对它的刷新策略是**安全的**：一言实现了 `IWidgetSelfRefreshing`，设置保存走
  `selfRefreshing.Refresh(layout)`（`Widget.axaml.cs:1445-1452`），**不会**像非自刷新组件那样被整体重建，
  因此不存在"每次保存泄漏一个实例"的放大效应。

### 1.4 `HitokotoService` — 每次调用新建 `HttpClient`（3 处）
`FetchQuoteAsync` / `FetchLikeCountAsync` / `SubmitLikeAsync` 各自
`using var client = ProxySettings.CreateHttpClient(...)`（`HitokotoService.cs:67,133,189`）；
而 `CreateHttpClient` 每次 `new HttpClient(CreateHandler())`（`ProxySettings.cs:142-146`）——
即**每次抓句/查询点赞/点赞都新建一个 `SocketsHttpHandler` + 独立连接池**，连接关闭后套接字进 TIME_WAIT。
单卡片每 30 分钟一次可忽略；但结合"双击换一句"、多张一言卡片、点赞/查询路径，属于该组件独有的稳定 churn 源。
（对照：Tools/翻译引擎已用 `static readonly HttpClient`，此处与本仓库既有模式不一致。）

### 1.5 `HitokotoPopupWindow` — 每次打开都是"新建整窗"，关闭即释放
* `ShowPopup` 每次 `new HitokotoPopupWindow(...)`（`HitokotoPopupWindow.axaml.cs:103-104`），
  `SecondaryPanelWindow` 基类在 `Closed` 时把它从静态 `ActivePanels` 移除（`SecondaryPanelWindow.cs:302-309`），
  且组件自身在 `Closed` 退订 store 事件（`:482-486`）。**正常路径无泄漏，但打开/关闭是纯 churn。**
* **真实缺陷（小概率、可修）**：构造顺序是"先订阅 store（`:65`）→ 后挂 `Closed`（`:71`）→ 中途还有
  `ApplyTheme/PopulateSpotlight/RefreshList`（`:67-69`）"。若 `:65` 与 `:71` 之间抛异常，窗口已注册进
  `ActivePanels`（`InitializePanel`，`:62`）却永远不会触发 `Closed` → **窗口 + store 订阅双泄漏**。
* `RefreshList()` 每次 store 变更（含桌面卡片自动刷新、点赞、收藏）都 `ItemsStack.Children.Clear()` +
  每项 `BuildItemCard` 重建（`:205-243,245-363`）——≤50 项的控件树 + 每卡片 3 个按钮闭包，纯 churn。

### 1.6 `HitokotoSettings` — 枚举全系统字体（一次性）
`FontManager.Current.SystemFonts.Select(f => f.Name)...ToList()`（`HitokotoSettings.axaml.cs:126-140`）：
会实例化全部已安装字体族并持有名称列表。一次性（设置页复用），量级 MB 内，列为低优先级。

---

## 2. 一言卡片**触发**的进程级内存（真正的"极大"来源）

### 2.1 每张一言卡片 = 一个玻璃渲染窗口（宿主级契约）
`Widget.axaml` 每个组件窗口内置一枚 `LiquidGlassSurface`（`Widget.axaml:111-114`），
显示条件是 `ShowsGlassMaterial => !isFrameless && GlassMaterial.UsesRenderedGlass`
（`Widget.axaml.cs:510`）。即：**只要当前主题是三种渲染玻璃之一、且不是无边框模式，
一张一言卡片就会加入玻璃管线**——这是"组件内存极大"的第一现场。

### 2.2 管线里最贵的资产：整桌面捕获（`cached` + `stale` + `probeBuffer`）
* 抓取是全虚拟桌面物理分辨率的 BGRA `SKBitmap`（`LiquidGlassWallpaper.cs:1534-1541`），
  **释放只发生在两个时点**：最后一个组件关闭（`WidgetFactory.cs:272-280`），或全屏进入 `SuspendAll`
  （`WidgetFactory.cs:340-382`）。稳态下**它一直驻留**（`LiquidGlassWallpaper.cs:39`）。
* 另有两处同级备份：`stale`（掩蔽失败帧；`Invalidate` 会把上一张整桌面帧存进去长期保留，`:45,370-392`）
  和 `probeBuffer`（仅实时采样开启；`:98`）。
* 单卡片的算术：单屏 1080p ≈ **7.9 MiB**，2K ≈ **14.1 MiB**，4K ≈ **31.6 MiB**，双 2K ≈ **28.1 MiB**，
  超宽 7680×2160 ≈ **63.3 MiB**（`像素数 × 4B`）。**这就是"一张文本框占几十 MB"的直接答案。**
* 宿主已有的缓解：`SuspendAll`（全屏）与"最后一个组件关闭"会调 `LiquidGlassSourceCache.Clear()` +
  `LiquidGlassWallpaper.Release()`——但**没有任何"闲置归还"路径**；`MemoryTrimmerService` 只做
  working-set/GC 整理（下限 256 MiB，`MemoryTrimmerService.cs:62`），不会释放这些受管的原生位图。

### 2.3 共享模糊背后景缓存（上限偏高，且最新一条不可驱逐）
`LiquidGlassSourceCache`：`Capacity=3`、`BudgetBytes=64 MiB`，且**刚构建的一条永不被预算驱逐**
（`LiquidGlassSource.cs:107,119,189-203`）。默认清晰度 25% 时每条很小（2K≈0.9 MiB、4K≈2.0 MiB），
但用户把"背景清晰度"拉高后，每条就是 14–32 MiB，缓存上限实际可到 **64 MiB + 一条**。
这一项由"一言卡片开着"与其它玻璃组件共享触发。

### 2.4 弹窗 = 第二个玻璃表面
`HitokotoPopupWindow` 在主题为渲染玻璃时启用 `LiquidGlassSurfaceControl`
（`HitokotoPopupWindow.axaml.cs:129-138,107-113`）：打开期间它加入 `Active`，令
`HasActiveSurfaces` 保持真（`LiquidGlassSurface.cs:75-83`），**即使桌面上其它组件都被隐藏，
弹窗单独存在也会让整桌面捕获与采样管线保持工作**。关闭后表面随窗口释放（`LiquidGlassSurface.cs:206-220`）。

### 2.5 CPU 回退路径：每组件持有自己的卡片位图
无 GPU（或材质自检失败 `gpuState=2`）时，每张卡片在 `Prepared.Cpu` 持有自身渲染位图，
尺寸上限 = `min(2048 长边, 1.2 MP)`（`LiquidGlassSurface.cs:531-534`）→ **≤ 4.6 MiB/组件**，
典型 4×2 卡片（@1.25 缩放）约 0.4–0.6 MiB；每次位置/主题/尺寸变化都重渲染（`RenderSlots` 并发 2，`:30,460-471`）。

### 2.6 每窗口原生表面
每个一言窗口 = 一个原生窗口 + 合成表面（窗口像素 ×4B × 缓冲数，未计入托管堆）。
多张一言卡片线性叠加；组件库为所有组件类型创建实时预览（`Gallery.axaml.cs:99-132`），
离开页面时显式 `Dispose`（`:80-97`），一言在此路径**安全**。

---

## 3. 订阅/定时器/窗口 生命周期核查表

| # | 订阅点 | 目标 | 退订 | file:line | 判定 |
|---|---|---|---|---|---|
| H1 | `HitokotoView` | 静态 `HitokotoStore.Instance.Changed / QuoteSelected` | `Unloaded → Dispose` | `HitokotoView.axaml.cs:89-90,578,588-589` | ✅ 正常路径 / ⚠️ 构造即订阅、卸载后再装载失效 |
| H2 | `HitokotoView.autoRefreshTimer` | 自身 DispatcherTimer（构造即启动） | `Stop()`（未置空） | `:35,304-315,592-593` | ✅ 可回收 / ⚠️ 卫生 |
| H3 | `HitokotoView.toastTimer` / `fetchCts` | 自身 | `Stop()` / `Cancel+Dispose` | `:33-37,521-531,594-595` | ✅ |
| H4 | `HitokotoPopupWindow` | 静态 store `Changed` | `Closed → OnWindowClosed` | `HitokotoPopupWindow.axaml.cs:65,71,482-486` | ⚠️ 构造半段异常则悬挂 |
| H5 | `HitokotoPopupWindow` ∈ `ActivePanels` | 静态字典（按类型） | `Closed → OnPanelWindowClosed` | `SecondaryPanelWindow.cs:94,218,302-309` | ✅ |
| H6 | 宿主设置保存 | `IWidgetSelfRefreshing.Refresh` | 不重建 | `Widget.axaml.cs:1445-1452` | ✅ 无放大 |
| H7 | 组件库预览 | 显式 `Dispose` | `Gallery.axaml.cs:80-97,164-179` | ✅ |
| H8 | `GlassDiagnostics` | 文件日志，2400 行封顶 | — | `GlassDiagnostics.cs:24,137-157` | ✅ 无内存增长 |

**已确认无泄漏的项**：历史列表上限 50；`.tmp+Move` 崩溃安全落盘；弹窗关闭释放表面与订阅；
Gallery/设置保存两条宿主路径均不泄漏一言视图。

---

## 4. 优化方案（按 收益/风险 排序）

### P0 — 组件内修复（低风险，1–2 天，直接消除 churn 与慢速增长）

1. **共享 HttpClient**（`HitokotoService.cs:67,133,189`）
   * 改为 `Lazy<HttpClient>` 单例（含代理设置变更的重建监听，参考 `ProxySettings.GetProxySetting()`），
     全组件复用同一 `SocketsHttpHandler` 与连接池；
   * 预期：消除每次抓句/点赞的 handler+连接池+套接字创建；"双击换一句/点赞/多卡片"场景下收益稳定。
2. **`HitokotoStore` 索引化 + 上限 + 写防抖**（`HitokotoStore.cs`）
   * `HashSet<string> likedIndex`、`Dictionary<string, HitokotoItem> favoriteIndex` → O(1) 判定，
     消除每次渲染的两次线性扫描；
   * `Favorites` 软上限（如 500，溢出按时间淘汰并提示），避免长期无界增长；
   * `SaveToDisk` 合并写入（250–500 ms 防抖）：连续点赞/收藏只落盘一次，消除 LOH 字符串与写放大。
3. **`HitokotoView` 生命周期卫生化**（`HitokotoView.axaml.cs`）
   * 订阅从构造函数移到 `Loaded`，`Unloaded` 退订；`Dispose` 幂等且可由重新装载恢复；
   * `Dispose` 中 `autoRefreshTimer/toastTimer` 停止并置空、退订 `Tick`；
   * `OnStoreChanged/OnQuoteSelected` 先比较 `Uuid+文本` 再决定是否重渲染，减少无谓重排。
4. **`HitokotoPopupWindow` 健壮性 + 列表复用**（`HitokotoPopupWindow.axaml.cs`）
   * 构造函数后段用 `try/catch` 包裹：失败时回滚 `ActivePanels` 注册并退订 store（消除 H4 悬挂路径）；
   * `RefreshList` 从"Clear + 全量重建"改为按 `Uuid` 增量刷新（或 `ItemsControl` + 模板化卡片），
     上限 50 项、变更频率低，改动小收益明确。
5. **`HitokotoSettings` 字体列表**（`HitokotoSettings.axaml.cs:126-140`）
   * `static Lazy<List<string>>` 缓存 + 只在需要时枚举（或做常用字体的短名单）。

### P1 — 玻璃管道："让纯文本卡片和闲置桌面不背几十 MB"（收益最大，需回归）

6. **闲置归还整桌面捕获**（首选，影响所有组件但一言是最典型的"只读文本卡片"）
   * 现状：`cached` 只在"所有组件关闭/全屏"时释放（§2.2）。
   * 方案：当**所有玻璃面在 N 分钟内不再请求新渲染**（位置/尺寸/主题稳定）时调用
     `LiquidGlassWallpaper.Release()`（保留 `LiquidGlassSourceCache` 的缩放背后景，采样继续用缓存）；
     下一次结构变化（拖动/缩放/换主题/换壁纸）按需重拍（一次性 ~10–30 ms）。
   * 预期：单屏归还 **7.9–31.6 MiB**，双屏/超宽 **28–63 MiB**；`stale` 同步降级为仅保留
     16×9 采样网格（`IdentityColumns/Rows`，`LiquidGlassWallpaper.cs:58-61`）而非整位图。
7. **文本类组件"轻量材质档"**（结构性，收益 = 完全避免捕获产生）
   * 为"一言/时钟日期"这类纯文本卡片提供三档材质：`毛玻璃（OS acrylic，0 应用位图）` /
     `静态背后景（复用既有捕获，不采样）` / `液态玻璃（现状）`；
   * 挂点已存在：`UsesNativeBlur`（毛玻璃走系统级模糊，无需捕获）与
     `ShowsGlassMaterial`（`Widget.axaml.cs:510`）判定；一言的 `IsFrameless` 已天然绕过玻璃。
   * 预期：文本卡片开箱即省 **8–63 MiB**（不产生整桌面捕获），且视觉上文本可读性无损。
8. **共享背后景缓存预算自适应**（`LiquidGlassSource.cs:107,119`）
   * `64 MiB + 最新一条不可驱逐` 在"高清晰度 + 多屏"下逼近 100 MiB；建议预算按
     `最大单屏物理像素 × 4B × 系数` 裁剪（如 ≤32 MiB），并把"最新一条不可驱逐"改为
     "按构建代数保底 1 条，其余按 LRU"。
9. **CPU 回退位图上限下调**（`LiquidGlassSurface.cs:531-534`）
   * 仅在无 GPU/自检失败的机器上有意义：把 `1200000 px` 上限降至 ~0.6 MP（视觉损失可忽略），
     并把同参数卡片（同尺寸/同主题/同位置）的渲染结果共享去重。

### P2 — 验证与长期防控

10. **新增 `tests/HitokotoMemoryChecks`**（参考 `tests/PictureMemoryChecks` 的结构）：
    * 开关弹窗 N 次：断言 `ActivePanels` 无残留、store 订阅数不增长（弱引用探测）、`Active` 玻璃面回到基线；
    * 收藏/Liked 上限与索引行为；
    * 空闲归还：断言闲置后 `LiquidGlassWallpaper` 的整桌面捕获已释放（建议先给
      `LiquidGlassWallpaper` / `LiquidGlassSourceCache` / `LiquidGlassSurface.Active` 增加
      `internal` 诊断计数属性，如 `RetainedBytes`，供测试与日志使用）。
11. **人工验收测量法**（在当前无 instrumentation 时最快可用）：
    * 基线：只放 1 张一言卡片，记录 `PrivateMemorySize64`；
    * 主题对照：`毛玻璃 ↔ 液态玻璃` 各测一次 → 差值 ≈ 整桌面捕获 + 背后景缓存；
    * 弹窗循环：开/关 50 次 → 是否回到基线（验证 §1.5 churn 是否收敛）；
    * 多卡片：1 → 4 张 → 差值 ≈ N×每窗口表面 + 每表面 `Prepared`；
    * 实时采样开关对照：验证 `probeBuffer` 的第三条整桌面副本；
    * 全屏进出一次：验证 `SuspendAll` 的归还路径仍有效（回归保护）。

---

## 5. 结论一句话

**一言组件本身很轻（KB 级）；它"内存极大"的全部解释是：一张文本卡片把进程级玻璃管线全都拉起来了
——整桌面捕获（8–63 MiB）+ 可能的 stale/探针副本 + 共享背后景缓存（上限 64 MiB+1 条）+ 每窗口表面。**
优先做 P0（组件内 churn 与慢速增长）与 P1-6/7（闲置归还 + 文本卡片轻量档），
即可在不牺牲观感的前提下，把"一言在桌面上"的稳态内存从数十 MB 降到个位数 MB。

---

## 6. 修复记录（本次已实施）

> 实施原则：只改被点名的内存行为，不动光学/视觉；每一项都有检查套件覆盖或回归验证。

### 已修复

| # | 修复 | 涉及文件 | 验证 |
|---|---|---|---|
| 1 | **共享 HttpClient**：3 处"每次抓句/查询/点赞新建 `SocketsHttpHandler`+连接池"改为进程级单例（代理设置变更时才重建）；每次请求用 linked CTS 施加 6s/5s 预算 | `HitokotoService.cs:36-94`（`GetSharedClient` / `TryGetStringAsync`） | 编译 + 既有网络路径检查 |
| 2 | **Store 索引化**：`IsLiked`/`IsFavorite` 从 O(n) 线性扫描改为 O(1) 集合查询（每次渲渲染调用 2 次的热路径） | `HitokotoStore.cs`（`likedSet`/`favoriteSet`，`RebuildIndexes`） | HitokotoMemoryChecks |
| 3 | **收藏上限**：`MaxFavoriteCount = 500`，载入与新增都裁剪最旧项（index 0 为最新） | `HitokotoStore.cs`（`TrimFavorites`） | HitokotoMemoryChecks |
| 4 | **落盘防抖**：每次点赞/收藏/换句都全量重写 JSON → 400 ms 合并写入（`System.Threading.Timer` 一次性重置） | `HitokotoStore.cs`（`ScheduleSave`/`SaveToDisk`） | HitokotoMemoryChecks（写入窗口内不动、窗口后落盘） |
| 5 | **载入归一化**：损坏文件回退空 store；手工编辑导致的集合 `null` 归一化；载入即执行历史/收藏裁剪 | `HitokotoStore.cs`（`LoadFromDisk`/`RebuildIndexes`） | HitokotoMemoryChecks |
| 6 | **视图生命周期**：store 订阅从构造函数移到 `Loaded`、`Unloaded` 只解绑不销毁（可重新装载）、`Dispose` 幂等且为唯一永久销毁路径；定时器在脱离时停止、Dispose 置空 | `HitokotoView.axaml.cs`（`OnLoaded`/`OnUnloaded`/`SubscribeStore`/`Dispose`） | 编译 + 宿主契约（`IWidgetSelfRefreshing`）回归 |
| 7 | **弹窗构造加固**：`Closed` 先挂、store 订阅最后挂；构造失败回滚订阅并从 `ActivePanels` 移除（新增基类 `AbandonPanel`） | `HitokotoPopupWindow.axaml.cs`、`SecondaryPanelWindow.cs`（`AbandonPanel`） | 编译 + SettingsReleaseChecks |
| 8 | **弹窗列表增量判定**：按（页签 + 搜索词 + 每项 key + 收藏态）签名跳过无变化的整表重建 | `HitokotoPopupWindow.axaml.cs`（`BuildListSignature`） | 编译 + 弹窗行为逻辑核对 |
| 9 | **设置页字体枚举缓存**：`FontManager.SystemFonts` 全量枚举改为进程级 `Lazy` 缓存 | `HitokotoSettings.axaml.cs`（`CachedSystemFontNames`） | 编译 |
| 10 | **玻璃管线：闲置归还整桌面捕获**（收益最大项）：任何渲染 `Get()` 都会记录需求并武装一次延迟检查；空闲超过宽限期（默认 3 分钟，可用 `DESKCANVAS_GLASS_IDLE_RELEASE_MS` 覆盖）且无渲染进行中、未开实时采样、非全屏挂起时，调用 `Release()` 归还整桌面捕获（保留采样所需的缩放背后景）；下次渲染按需重拍 | `LiquidGlassWallpaper.cs`（`NoteFrameDemand`/`RunIdleReleaseCheck`/`EvaluateIdleRelease`/`HasCachedCapture`/`RetainedCaptureBytes`，`Get()` 内埋点） | **HitokotoMemoryChecks（实测：14.1 MiB 捕获 → 宽限期后归零 → 再渲染重新捕获）** + FullscreenChecks + LiquidGlassChecks 全套 |
| 11 | **新增 `tests/HitokotoMemoryChecks`**：20 项 store 断言 + 4 项玻璃闲置归还断言，store 部分在检查自身 exe 旁的隔离数据目录运行，不触碰真实数据 | `tests/HitokotoMemoryChecks/` | 本次运行 24/24 PASS |

### 未实施（保留为后续建议）

* **P1-7 文本卡片轻量材质档**（毛玻璃/静态背后景/液态玻璃三档）——产品决策项，涉及主题模型与设置界面，未动。
* **P1-8 `LiquidGlassSourceCache` 预算自适应**——现状注释明确写了"clarity wins over the constant"是刻意设计，
  改预算需要设计确认；且默认清晰度（25%）下背后景每条仅 ~1 MB，收益远小于 #10。
* **P1-9 CPU 回退位图上限下调**——只在无 GPU 的机器上有意义，涉及画质权衡，未动。
* **弹窗开/关 soak 与订阅数弱引用探测**——需要真实的 widget 宿主进程（检查套件里无法建 `Widget` 窗口），
  保留为人工验证项（§4 的测量法仍然适用）。

### 回归验证汇总（本次运行）

```
tests/HitokotoMemoryChecks   ALL HITOKOTO MEMORY CHECKS PASSED (24/24)
tests/LiquidGlassChecks      光学 / 光学剖面 / 柔光 / GPU 材质 / 新液态玻璃 / 背景清晰度 全部通过 (exit 0)
tests/FullscreenChecks       ALL CHECKS PASSED（SuspendAll / 归还路径）
tests/SettingsReleaseChecks  ALL SETTINGS RELEASE CHECKS PASSED
src/DeskCanvas.sln           Debug 全量构建 0 警告 0 错误
```

