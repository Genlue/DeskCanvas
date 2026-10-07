# DeskCanvas v3.4.0 更新日志

> 一个围绕**多屏拓扑归属**的版本：显示器插拔 / 仅电脑屏幕↔扩展↔复制的每次切换都先等待 Avalonia 与 Win32 两套枚举几何一致并稳定后一次性提交，屏幕身份按「实例 ID → 硬件 ID → 键名」三级钉定，克隆/双胞胎/离线屏各有确定性归属；顺带修复了切换显示模式时 **UI 线程被 z 序纠正死循环拉满卡死**的问题。主程序升到 3.4.0，Clock 组件维持 1.5.0，编译 0 警告 0 错误。

---

### 🖥️ 1. 拓扑一致性门（新）

* **几何一致性**：新增 `DisplayTopology.HasSameGeometry`——Avalonia 屏幕矩形与 Win32 `EnumDisplaySettings` 设备矩形作为**集合**比较，不一致（热插拔过渡期两套 API 各自缓存）则进入 `IsTopologyChanging` 观察态，绝不把过渡期快照当真提交。
* **确定性配对**：`MatchDevice` 只按**精确坐标+分辨率**配对，删除了按分辨率猜测和枚举顺序兜底的旧路径——那正是「幸存屏被塞进已拔出显示器的身份、带走别人的组件」的来源。克隆模式（多输出同一桌面矩形）按「保留原属主，否则字典序稳定选择」归属（`DisplayTopology.SelectDevice`）。
* **事件链**：锚点窗口挂 `WM_DISPLAYCHANGE` / `WM_DPICHANGED` / `WM_SETTINGCHANGE(SPI_SETWORKAREA)` 钩子，收到即同步挂起位置持久化并进入观察态；`WM_DEVICECHANGE(DBT_DEVNODES_CHANGED)` 去抖后触发重读。观察态轮询 250ms，稳定 750ms + 通知沉降窗过后一次性提交，随后回到 1.5s。

### 🔖 2. 屏幕身份三级钉定

* `ScreenIdentity` / `ScreenLayout` 新增 `MonitorId`（Windows 监视器实例路径，区分同型号两台实体屏），匹配顺序：**MonitorId 精确实例 → 硬件 ID（唯一型号回退；双胞胎仅限未钉定旧条目升级）→ 键名（仅未钉定条目）**，匿名屏（远程工具虚拟屏等）永不借走任何配置。
* 每次提交把身份回填进存储条目并单次事务落盘；**离线屏的配置永远保留**（拓扑观察只读不删，旧版的陈旧条目修剪移除），重新插上即按原身份取回组件。
* 双胞胎屏（同友好名两台）的独立网格/边距/圆角配置得到保留（`Deduplicate` 只按 Id 去重）。

### 🧩 3. 组件生命周期与所有权

* **挂起/恢复**：`ScreensChanging` 触发 `SuspendForDisplayChange`——关闭所有次级面板、隐藏组件窗口，避免被 Windows 的显示器移除重定位带走；提交后 `OnScreensChanged` 全量对账：关闭已离开屏的窗口、重建回归屏的窗口、对全部窗口执行 `RefreshDisplayMetrics`（按属主屏恢复位置、DPI、网格尺寸、原生裁剪），期间的位置保存全部抑制。
* **跨屏拖动**：所有权转移前先按原屏解析跨度，转移时同步提交尺寸；存储读写一律经由当前存储快照（`ScreenConfigEditor`，杜绝按陈旧 UI 快照回写覆盖其他屏的并发编辑）。
* **旧版迁移**：legacy 主桶迁移移到组件工厂边界——先把活窗口退役再转换坐标格式；未转完成的桶仍以绝对坐标渲染、且只在整桶都落在真实屏上时才转换。

### 🐛 4. 修复：显示模式切换卡死

* **根因**：`WidgetZOrder` 底带的 `InBand()` 谓词把桌面窗口 Progman/WorkerW 也记作违规——组件压到 z 序最底时下方永远有可见桌面，谓词永假；平时无放置事件相安无事，显示切换的「统一隐藏→统一显示/重放」放置风暴让每次纠正触发下一次放置，UI 线程在 `PlaceAtTarget` ↔ 重放置之间无限自旋（进程无响应、单核 100%）。
* **修复**：带谓词豁免桌面壳窗口（组件带本来就是「桌面之上」），纠正真正收敛；锚点窗口注册进底带；显示管线事件订阅提前到首次 `Attach` 之前（原先启动对账的 `ScreensChanged` 打进空处）；设置窗口自动弹出的判定改用 `HasWidgets`（不再依赖 `Create()` 是否恰好新建）。
* **验证**：实机 `SetDisplayConfig` 切换矩阵（扩展启动 → 仅电脑屏幕 → 扩展 → 往返）全部健康，组件随屏关闭/重建，UI 始终响应；`dotnet-stack` 修复前抓到 UI 线程死循环于 `PlaceAtTarget`，修复后同一矩阵不再出现。

### 🧪 5. 验证与版本

* `scripts/check-structure.ps1` 通过；`LayoutOwnershipChecks`（含克隆桌面拓扑、顺序屏编辑、离线屏归属、旧版迁移累计）、`MultiScreenGridChecks`、`WidgetAdaptiveChecks`（重插归属、每屏一窗）全绿；`SettingsProbe`、`SingleInstanceChecks` 等既有套件回归通过。
* 版本号：主程序 3.4.0（AssemblyInfo + Package.wxs 同步），Clock 1.5.0（本版未变动）。
