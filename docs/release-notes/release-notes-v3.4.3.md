# DeskCanvas v3.4.3 更新日志

> 一个核心架构重构版本：彻底解决**多屏环境下切换显示模式（扩展 ↔ 仅外屏主屏、热拔插、切换主屏）时，外接显示器意外套用内屏或其他屏幕配置，且回写覆写污染原有配置**的问题。引入 Windows CCD (`QueryDisplayConfig`) + SetupAPI 活动路径物理采集引擎，升级布局模型至 Layout v3（`PhysicalMonitorIdentity` + `ScreenBinding` 物理实例强绑定），切断 GDI 瞬态编号（`DISPLAY1`）跨屏盗用；新增持久化门禁防火墙与多屏管理状态识别；增补 CCD 探针与多屏所有权全场景回归测试。主程序版本升至 3.4.3，全解决方案 19 个项目编译 0 警告 0 错误。

---

### 🖥️ 1. 采集层重构：Windows CCD 活动路径引擎 (新)

* **现象与痛点**：旧逻辑基于 GDI `EnumDisplayDevices` 遍历显示适配器与监视器子设备。当子监视器为 `Generic PnP Monitor` 时，循环会反复覆写硬件标识；且 GDI 无法准确识别热插拔过渡状态及复制模式。在笔记本外接屏幕闭盖（切换为仅外屏）时，外屏抢占 `\\.\DISPLAY1`，导致配置错位。
* **CCD API 活动物理目标解析**：
  * 全面接入 Windows CCD API：只读调用 `QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS)`，获取真实的活动 source $\to$ target 路径与模式。
  * 精确设备信息提取：通过 `DisplayConfigGetDeviceInfo` 读取 `viewGdiDeviceName`、`monitorDevicePath`、EDID 厂商代号（如 `SAC`/`BOE`）、产品代号（如 `2463`/`0C8E`）及连接技术（嵌入式 DisplayPort `11` / 外接 DisplayPort `10` / HDMI `6` 等）。
  * PnP 实例 ID 规范化提取：从设备接口路径提取稳定唯一的物理设备实例 ID（如 `DISPLAY\SAC2463\5&1551B5BC&0&UID4352`），使设备身份完全独立于瞬态 GDI 编号。
  * WMI 友好名称与有效序列号过滤：通过规范化 PnP 实例 ID 精确检索 WMI `WmiMonitorID`；增加有效序列号过滤器，自动剔除 `0`、全零、全空及占位符序列号。
  * 优雅回退兜底：在远程桌面 (RDP) 或不支持 CCD 的虚拟化环境中平滑回退至 GDI 枚举，并将无实体身份的目标标记为 `Unresolved`，防止虚拟屏抢占实体屏幕配置。

---

### 🛡️ 2. 防火墙与持久化门禁：阻断错误覆写

* **根因消除**：旧版在候选匹配后，只要发现当前身份非空且与旧配置不同，就会在拓扑观察循环中直接覆写 `config.HardwareId`、`config.MonitorId` 与 `config.Key` 并写盘，导致单外屏模式下内屏配置的分辨率与硬件标识被强行篡改为外屏参数。
* **硬件冲突防火墙 (`HasHardwareConflict`)**：
  * 在 `ScreenMatcher` 中引入严格的硬件冲突校验，异构设备（如 `SAC2463` vs `BOE0C8E`）之间**绝对禁止**跨屏借用配置。
  * 无论是桌面编号匹配、Key 匹配、还是型号回退，只要存在硬件身份冲突一律直接拒绝，彻底关闭 GDI 编号（`\\.\DISPLAY1`）盗用异构屏幕配置的漏洞。
* **安全升级与持久化门禁**：
  * 拓扑观察期只读不改：正常拓扑变化只负责对账与渲染，**绝不自动修改或覆盖已存在的物理绑定**。
  * 安全补齐机制：仅当旧配置未绑定且硬件型号完全一致时，才在首次运行时安全附加 `ScreenBinding`（`Source = Migrated`）。
  * 身份未解析 (`Unresolved`) 或冲突 (`Conflict`) 状态下坚决不回写，原有配置受到严格保护。

---

### 📦 3. 数据模型解耦与 Layout v3 规范

* **模型全面分层**：
  * `PhysicalMonitorIdentity`：采集层获取的真实物理监视器身份描述。
  * `ScreenBinding`：随屏幕配置持久化的物理绑定实体，包含规范化 PnP 实例 ID、接口路径、型号、有效序列号、已验证历史实例别名列表 (`InstanceAliases`) 以及绑定状态。
  * `ActiveDisplayPath`：运行时单次快照的源/目标连接路径。
* **Layout v3 平滑升级**：
  * `ScreensLayout` 格式升级为 `Version = 3`，同时对历史 v1/v2 配置文件保持 100% 向后兼容。
  * `ScreenMatcher.MatchBatch`：支持集合级全局 1:1 事务匹配，优先级保障：`PnP 实例精确匹配` $\to$ `有效序列号匹配` $\to$ `无冲突手动绑定` $\to$ `受控型号升级`。

---

### 🎨 4. 多屏管理界面与交互提升

* **物理设备与配置别名双重展示**：
  * 页面卡片头部不再用配置名称单一掩盖设备，而是明确显示真实物理设备名（如 `G52 Max`、`BOE0C8E (内置屏幕)`）以及配置别名。
  * 增加绑定状态徽标提示：`已绑定`（绿色）、`未配置`（蓝色）、`绑定冲突`（橙红）、`未解析`（灰色）。
  * 硬件信息栏完整展示当前 GDI 编号与稳定硬件型号（如 `\\.\DISPLAY1 · SAC2463 · 2560×1440 · 100% DPI`）。
* **手动重绑物理化**：
  * 重新绑定操作直接将选中的配置与当前屏幕的物理 PnP 实例建立强绑定（`Source = Manual`），并解除目标设备上原有的冲突绑定，不再受瞬态桌面编号漂移影响。

---

### 🧪 5. 验证与版本

* **实机探针验证 (`tests/CcdTopologyProbe`)**：
  * 成功在实机环境验证 CCD API 与 WMI 抓取，准确输出 `G52 Max` 与 `BOE0C8E` 的物理接口路径、PnP 实例 ID 及连接技术。
* **全场景回归测试 (`tests/LayoutOwnershipChecks`)**：
  * 增补物理绑定、PnP 跨端口匹配、`MatchBatch` 状态分类、Layout v3 序列化持久化以及完整的「扩展 $\to$ 仅外屏主屏 $\to$ 恢复扩展」用户场景专项断言。
  * **所有 45 项所有权检查 100% 通过（PASS）**。
* **多屏与组件自适应检查**：
  * `MultiScreenGridChecks` 全部通过；
  * `WidgetAdaptiveChecks` 全部通过。
* **版本同步**：
  * 主程序：`3.4.2` $\to$ `3.4.3`（`src/DeskCanvas/AssemblyInfo.cs` + `installer/Package.wxs` 同步）；
  * 全解决方案 19 个项目编译 0 警告 0 错误。
