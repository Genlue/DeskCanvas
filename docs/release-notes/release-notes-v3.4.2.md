# DeskCanvas v3.4.2 更新日志

> 一个专项修复版本：解决**文件展示框（Folders / Folder）组件在“展示文件夹”模式下，外部资源管理器中内容发生新建、修改、重命名、删除时组件无法实时反应（监听不生效）**的缺陷。补齐 `IWidgetSelfRefreshing` 契约，将监听节流彻底重构为线程安全的尾部防抖（Trailing Debounce, 200ms），全面监听文件属性并添加并发 IO 异常安全防护；新增 `FolderWatcherChecks` 自动化检查套件。主程序版本升至 3.4.2，Folders 组件升至 0.2.3，编译 0 警告 0 错误。

---

### 📂 1. 修复：文件展示框展示文件夹实时监听失效

* **现象**：在文件展示框设置中选择“展示文件夹”（如“桌面”或任意工作目录）后，外部在 Windows 资源管理器中新建文件、编辑保存、批量粘贴、重命名或删除文件时，桌面上的小组件无法实时更新，内容始终保持初始状态或需要手动重载。
* **根因**：
  1. **缺少自刷新契约 (`IWidgetSelfRefreshing`)**：整个项目中 `BigFolder`、`SingleFile` 等组件均已实现 `IWidgetSelfRefreshing`，普通 `Folder` 遗漏该契约。当在设置面板中选择或切换监视文件夹时，宿主将其判定为无状态组件而暴力重建 UserControl；新实例的 `StartWatcher()` 仅挂在 `OnLoaded` 阶段，特定生命周期分支下监听器直接丢失。
  2. **朴素时间差节流丢弃尾部事件**：旧逻辑使用简单的 `if ((DateTime.Now - lastWatcherEvent).TotalMilliseconds < 300) return;`。资源管理器对文件的写盘、重命名等操作是密集爆发的连发事件（`Created` $\to$ `Changed` $\to$ `Renamed`），0ms 触发第一个事件后，后续真正完成落盘与命名的关键事件因间隔 `< 300ms` 被简单丢弃且没有任何延迟补发机制，导致最终生成的文件永远无法在 UI 上刷新。
  3. **`FileSystemWatcher` 缓冲与过滤不全**：遗漏了 `Size`、`Attributes`、`CreationTime` 变更过滤，默认缓冲区小易溢出，且未处理 `Error` 事件导致异常中断后无法自动恢复。
  4. **并发与异常防护缺失**：在文件刚生成或被删除时，直接调用 `File.GetLastWriteTimeUtc` 排序容易遭遇瞬时文件锁或不存在而抛出 IO 异常导致整个刷新中断。
* **修复**：
  * **契约与生命周期对齐**：`Folder` 实现 `IWidgetSelfRefreshing` 与 `INotifyPropertyChanged`，在 `Refresh(WidgetLayout)` 中即时比对并启动/更新/停止监听器，保留在位状态并触发数据绑定更新。
  * **后台线程安全尾部防抖 (Trailing Debounce)**：采用轻量 `System.Threading.Timer` 锁机制；密集事件爆发期只重置 200ms 倒计时，不消耗 UI 消息队列资源；待文件系统 IO 完全静止 200ms 后，向 UI 线程派发一次平滑、准确的 `Refresh()`。
  * **过滤器与容错升级**：缓冲区扩展至 64KB，全面覆盖 `FileName | DirectoryName | LastWrite | Size | Attributes | CreationTime`，增加 `OnWatcherError` 自动重连机制。
  * **安全排序与求值**：引入 `SafeGetCreationTime` 与 `SafeGetLastWriteTime` 保护，文件条目枚举立即求值并捕获异常，杜绝并发文件占用崩溃。
  * **路径规范化**：设置选择器对选中的文件夹路径进行尾部斜杠清理（保留根驱动器如 `C:\`），保存后即时同步设置面板。

---

### 🧪 2. 验证与版本

* **自动化测试套件**：新增 [`tests/FolderWatcherChecks`](file:///c:/Users/25659/Desktop/Project/DeskCanvas/tests/FolderWatcherChecks/Program.cs) 验证程序，全流程覆盖：
  * `IWidgetSelfRefreshing` 与 `INotifyPropertyChanged` 接口契约校验；
  * 单文件新增即时响应；
  * 多文件并发创建、重命名爆发期防抖与沉降刷新；
  * 文件删除实时同步；
  * 监视目录热切换与清空停止。
  * **所有检查项 100% 通过（PASS）**。
* **版本号同步**：
  * 主程序：`3.4.1` $\to$ `3.4.2`（`src/DeskCanvas/AssemblyInfo.cs` + `installer/Package.wxs` 同步）；
  * 组件：`Folders` `0.2.2` $\to$ `0.2.3`（`src/Widgets/Folders/AssemblyInfo.cs`）；
  * 全解决方案编译通过：0 警告，0 错误。
