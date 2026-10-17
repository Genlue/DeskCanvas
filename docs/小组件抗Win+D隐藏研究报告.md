# 小组件抗 Win+D 隐藏研究报告

> 2026-10-17 · 三代理并行研究（机制调研 / Win32 实验 / Avalonia 落地评估）+ 汇总裁决
> 实验环境：Windows 11 build 26300.9711（26H2 分支，24H2 系 shell 层级）、PS 7.6.7、主屏 1280x800。

> ⚠️ **实施状态（2026-10-17 同日）**：本文第 5 节的方案 A'（事件驱动 + 压 shell）与备选"topmost residency"在真实应用内**实施失败**，与本报告的隔离实验结论相悖，源码已全部回滚（`WidgetZOrder.cs` / `WidgetAnchorWindow.cs` 恢复原状），研究脚本与子报告临时目录 `.research-wind/` 已删除。**被实施推翻的关键结论**：①"压 shell"在应用进程内被 DWM 拒绝（`SetWindowPos` ret=False，隔离实验成功是因为调用时机在 shell 状态机安静之后，不能外推）；②"widget 提入 topmost band"自救在原生 Win32 下完全稳定（40 轮对照零异常），但在 Avalonia 内陷入 ~5ms 一次的拉锯——`WS_EX_TOPMOST` 位在每轮窗口消息处理后约 5ms 内被抹掉，`AddWindowStylesCallback` 注入、Avalonia `Topmost` 属性同步、WndProc hook 补位全部拦不住，且清位动作不走 `WM_WINDOWPOSCHANGED`（hook 无感）。**卡点 = Avalonia 在哪条非消息路径重写 exStyle，黑盒已到边界**；继续需要源码级调试（dotnet symbol/反编译单步）或换产品思路。研究阶段的机制结论（第 1-4 节：最小化集合、z 序抬高、`IsWindowVisible` 检测不出、24H2 变化、事件信号）依然成立。
>
> 下面的 TL;DR 第 2/3 条中"现有代码在工作"与"推荐方案 A'"按上述实施结果**作废**，仅保留机制部分作为参考。

> 原始产物（已删除）：`.research-wind/` 曾存有 1-shell-mechanism.md、2-experiment-results.md、3-avalonia-integration.md 及 exp/ 实验脚本与原始数据；如需复核可依据本报告第 7 节的来源重做。

---

## TL;DR

1. **小组件窗口从未被 Win+D 最小化**。Win+D 第一步只最小化"支持最小化命令"的窗口，`WS_EX_TOOLWINDOW` / `WS_POPUP` / 有隐藏 owner 的窗口全部被跳过（Raymond Chen 官方机制 + 本机实验 1 证实）。**真正的问题是第二步**：桌面 shell（Progman）被抬到所有非 topmost 窗口之上（实测 z 序 373→39），物理盖住小组件，且是**稳态**——不会自动恢复。
2. **现有代码在工作**：`WidgetZOrder.EnsureDesktopBarrierWatch` 的"把 shell 往下压"（`SetWindowPos(shell, widget, …)`，WidgetZOrder.cs:319）经实验 5 裁决**有效**（2 轮 4 次全部成功，0.4-4.5ms，widget 立即脱困）。缺陷只在**触发源**：150ms 轮询的盲区内桌面会盖住小组件（≈160ms 可见闪烁）。
3. **推荐方案（A'）**：保留全部现有 band 机制与压 shell 修正，**只把触发源换成 `SetWinEventHook(EVENT_OBJECT_REORDER)` 事件驱动**。实验 4 实证：小组件窗口收到**自身 REORDER** 在 Win+D 后 **4.2-9.0ms** 必达 → 检查 shell z 序 → 复用现有 `KeepDesktopBelowBand`。闪烁窗口期从 ≤160ms 压到 **~10-15ms（1-2 帧）**，轮询降频为 1s 纯兜底。
4. **否决**：隐藏 owner 方案（症状不匹配，零收益）；WorkerW/Progman 嵌入作为主方案（毛玻璃失效 + Avalonia 状态机风险，仅可作未来实验性"嵌入桌面"模式）。

---

## 1. 机制篇（Win+D 到底做了什么）

Win+D（Show Desktop）= 两步，无可绕过：

| 步骤 | 行为 | 对小组件的影响 |
|---|---|---|
| ① 最小化 | 只最小化**支持最小化命令**（WS_MINIMIZEBOX 语义）的顶层窗口 | 小组件（unowned + TOOLWINDOW + popup）**不在集合内，不受影响** |
| ② 抬桌面 | 桌面 shell 被插到**所有非 topmost 窗口之上**（topmost 是唯一文档化豁免） | 小组件被物理盖住，**稳态**，不自动恢复 |

关键事实：

- `IsWindowVisible` / `IsIconic` **检测不出** Show Desktop 状态——只能看 z 序（与现有 `InBand()` 链遍历判定一致）。
- **24H2 变化（26100.2454+，含本机 26300）**：`SHELLDLL_DefView` 与 `WorkerW` 都成了 Progman 的子窗口，微软关闭了老一代 OnDesktop 方案的 loophole（Rainmeter issue #377 原话"微软关闭了一个 loophole"）。历史资料中"挂 WorkerW"的可靠性论述对新 shell 一律打折。
- 探测 shell 版本的方法：`GetProcAddress(user32, "GetCurrentMonitorTopologyId")`。
- `Shell.Application.ToggleDesktop()` COM 路径在本机**不稳定**（冷启动静默失效，后续可自行恢复）——生产验证一律用 `keybd_event` 注入真 Win+D。

来源：Raymond Chen 2004（The Old New Thing "Show the desktop" 机制）、rainmeter#377、wind-researcher 报告 `.research-wind/1-shell-mechanism.md`（含全部链接与置信度标注）。

## 2. 实验结论矩阵（本机 26300.9711 实测，实验 1-5）

| 窗口技术 | 被 Win+D 最小化？ | 被抬起的桌面覆盖？ | 备注 |
|---|---|---|---|
| 普通 `WS_OVERLAPPEDWINDOW` | ✅ 被最小化 | — | 对照组，证实注入有效 |
| `WS_POPUP` ± `WS_EX_TOOLWINDOW`（± 隐藏 owner） | ❌ 不被 | ✅ 被盖 | **小组件现状**，问题只在覆盖 |
| `SetParent` → Progman | ❌ 不被 | ❌ **不被** | 唯一"兼得"，但见方案 C 否决理由 |
| topmost 窗口 | ❌ 不被 | ❌ 不被 | z 数据旁证；但与"钉底"语义相反 |

**z 序硬模型（含实验 5 勘误后的完整版）**：

- shell 的 z **不能被普通进程上抬**（`SetWindowPos(Progman, HWND_TOP)` 返回 True 但纹丝不动，DWM 保护），**但能被下压**（insertAfter 指向具体窗口，0.4-4.5ms 生效）——两个方向行为不对称，实验 5 直接复现了 `WidgetZOrder.cs:319` 的逐字调用。
- 普通窗口 `HWND_BOTTOM`：shell 正常态 = 落在 Progman 之上一位（正常钉底）；**shell 抬起态 = 穿透 shell 沉到链尾**（桌面组正常栖位之下，不可见且难恢复）——**自救代码禁用 `HWND_BOTTOM`**。
- 自救的正确姿势是**抬自己**：`HWND_TOP` 实测 <10ms 生效；理论上 `insertAfter = shell`（精确插到桌面之上一位）同样可行（与压 shell 对偶，未单独实测）。
- 隐藏 owner 的 z 序约束（owned 永远在 owner 之上一位）在 owner 不可见时依然生效，但不妨碍窗口坐到最低位——知识补全，非推荐路线。

## 3. 事件信号实证（实验 4：SetWinEventHook + 真 Win+D 注入）

| 信号 | 结果 | 用途 |
|---|---|---|
| `EVENT_OBJECT_REORDER`（0x8004）on **Progman 本体** | ❌ 全程 0 条 | ~~主信号~~ 不可行，integrator 原设想修正 |
| `EVENT_OBJECT_REORDER` on **小组件窗口自身** | ✅ 每次 Win+D 必达，**+4.2 ~ +9.0ms** | **主信号**：监听自身 REORDER → 对比 shell z 序 → 修正 |
| `REORDER` 爆发 on 桌面层宿主 `#32769`（Progman 父级） | ✅ 每次 4-7 条 | 辅助信号 |
| `EVENT_SYSTEM_DESKTOPSWITCH`（0x20） | ❌ 全程未出现 | 只对应虚拟桌面切换，排除 |
| `EVENT_SYSTEM_MINIMIZESTART/END`（0x16/0x17） | ✅ +4.6~9.6ms 精确跟随 | 冗余备选 |

工程要点：OUTOFCONTEXT 回调递送到安装线程的消息队列——**必须在 UI 线程安装**（Avalonia UI 线程即泵线程，回调内轻量预检后 `Dispatcher.Post` 修正）；`DESKTOPSWITCH` 无关可省。

## 4. 方案裁决

| 方案 | 裁决 | 一句话理由 |
|---|---|---|
| **A'：事件驱动 + 现有压 shell** | ✅ **推荐** | 全组件实证；最小 diff；闪烁 ≤160ms → ~10-15ms |
| A2：自抬（HWND_TOP / insertAfter=shell） | 备选 | 有效（<10ms）但语义是"抢跑"，且会短暂越过其他应用；留作压 shell 失效时的 fallback |
| Rainmeter 式（临时 topmost band 搬运，不碰 shell） | 不采用 | 4.5.22+ 源码实证可行，但复杂度高，且 Avalonia `WindowImpl.SetTopmost/EnsureTopmost` 会重 assert topmost，需额外状态同步；现有压 shell 更简单且已验证 |
| B：隐藏 owner | ❌ 否决 | 防的是"被最小化"——小组件根本不在最小化集合；对 shell 抬高零影响；且与 WidgetZOrder "必须 unowned" 正面冲突 |
| C：WorkerW/Progman 嵌入 | ❌ 主方案否决 | 24H2 后风险更高；毛玻璃 = OS 级 DWM AcrylicBlur（top-level 专属）嵌入即失效；Avalonia 状态机全按 top-level 假设（WS_CHILD 会被样式重算冲掉，owner 重挂对抗无 API 钩子）；第三方实践实录"必须放弃输入、残影"。**可另立项做默认关闭的实验性"嵌入桌面"模式（仅 Transparent 主题）** |

## 5. 推荐落地设计（方案 A'，改动全部收敛在 WidgetZOrder.cs）

1. **安装**：`EnsureDesktopBarrierWatch` 内（UI 线程）调 `SetWinEventHook(0x8004, 0x8004, 0, WinEventProc, 0, 0, 0 /*OUTOFCONTEXT*/)`，句柄存静态字段。
2. **回调**：`WinEventProc` 首行轻量过滤——event ≠ 0x8004 直接返回；可选：只对 `widgetHandles` 成员或 `#32769` 的 hwnd 反应；命中后 `Dispatcher.UIThread.Post(KeepDesktopBelowBand, DispatcherPriority.Send)`（**不**直接在回调里做链遍历修正，回调要快）。
3. **修正**：复用现有 `KeepDesktopBelowBand`（压 shell，逐字不动）。
4. **轮询降级为兜底**：`desktopBarrierTimer` 150ms → **1000ms**，仅在事件驱动失效（未来 build 行为漂移）时兜住，正常时永不触发。
5. **防抖**：REORDER 在拖动/最小化风暴时高频——修正路径已有幂等谓词（`InBand()`）+ `correctionPending` 合并，天然收敛；回调预检保持 O(1)（先比对 shell 与 band 底的序号关系再 Post）。
6. **生命周期**：现有 `Closed` 清理处（最后一个 widget 关闭）补 `UnhookWinEvent`；应用退出兜底 Unhook。
7. **哨兵**：`KeepDesktopBelowBand` 中 `SetWindowPos` 失败（ret=false）时 Debug 日志——为未来 Windows 版本行为漂移留观测点。

**预期效果**：Win+D 后感知 4-9ms + 修正 0.4-4.5ms ≈ **15ms 内脱困（1-2 帧）**，肉眼无闪烁；Win+D 双 toggle、任务栏 show-desktop 按钮、Explorer 重启等场景由轮询兜底覆盖。

## 6. 验证清单（落地后）

- [ ] Win+D 双 toggle ×5：小组件闪烁肉眼不可察；被最小化的其他窗口正常恢复
- [ ] 任务栏 show-desktop 按钮、Win+M、Aero 摇晃全部窗口：同上
- [ ] 拖动小组件 / 面板开关 / 菜单弹出：REORDER 风暴下无修正抖动、无 UI 线程占满（对照现有 InBand 收敛论证）
- [ ] Explorer 重启（taskkill /f /im explorer.exe & start explorer）：小组件 band 不丢
- [ ] 多显示器（副屏接时）：两块屏的 widget 都脱困
- [ ] `scripts/verify.ps1` 39 步全绿

## 7. 附：研究产物与关键来源

- `.research-wind/1-shell-mechanism.md` — 机制调研（Raymond Chen / rainmeter#377 / Rainmeter System.cpp+Skin.cpp 源码引用 / 24H2 变化 / 8 项候选方案一览表）
- `.research-wind/2-experiment-results.md` — 实验 1-5 全数据（脚本与原始输出在 `.research-wind/exp/`）
- `.research-wind/3-avalonia-integration.md` — Avalonia 11.1.3 事实清单（owner 重挂 F2、styles 回调 F3、外部 restore 安全 F4、SetParent 负面证据 F5）+ 三方案 diff 草图
- 精选来源：Raymond Chen《The Old New Thing》Show Desktop 机制（2004）；rainmeter/rainmeter issue #377 与 Library/System.cpp、Skin.cpp；Avalonia `src/Windows/Avalonia.Win32/WindowImpl.cs`；EarthBackground（Avalonia 壁纸应用）实践记录
