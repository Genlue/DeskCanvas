## DeskCanvas v2.7.0 更新日志

> 项目正式更名为 **DeskCanvas**——全新的名字、全新的图标、体系化的开源致谢；笔记组件新增标题栏开关，清单只读态不再有悬停高亮；顺手给仓库做了一次彻底的大扫除。

---

### 🎨 1. 项目更名：uWidgetsPlus → DeskCanvas

* **全线改名**：解决方案与项目（`DeskCanvas.sln` / `DeskCanvas` / `DeskCanvas.Core`）、命名空间、程序集名、exe 与 MSI 产物名、托盘提示、关于页、更新检查 UserAgent、安装器（产品名/注册表键/开始菜单目录）、构建脚本与 CI 工作流全部更新。
* **新图标**：应用图标、托盘图标、安装器图标与 README 徽标统一替换为 DeskCanvas 新图标（16–256 px 共 8 帧齐全）。
* **数据目录迁移**：`%LocalAppData%\uWidgets` → `%LocalAppData%\DeskCanvas`。首次启动自动复制旧目录的设置、布局、清单数据与已解包组件（旧目录保留原样作为回滚后备，迁移失败则静默走全新安装）。
* **开机自启动**：注册表 Run 键值名随 `AppName` 换为 DeskCanvas，每次启动会重新断言自启动路径，升级后自动登记。
* **无缝升级**：MSI `UpgradeCode` 保持不变，原地升级旧版；关于页保留对上游 creewick/uWidgets 的署名。

### 📦 2. 开源引用与致谢（新增 README 致谢表）

* **creewick/uWidgets**（CC BY-NC-SA 4.0）——上游项目，本项目由它 fork 而来；
* **Kyant0/AndroidLiquidGlass**（Apache-2.0）——「液态玻璃 2.0」材质是对其光学模型（圆角矩形折射透镜 + 深度效果 + 斜向色散 + 发丝描边高光）的忠实 GPU 移植；
* **QmDeve/AndroidLiquidGlassView**（MIT）——为 Avalonia 评估液态玻璃渲染时的参考实现；
* **Material Design 3 motion tokens**（CC BY 4.0 规范）——二级面板开合动画的缓动令牌（进场 `emphasized decelerate`、退场 `emphasized accelerate`）；
* **Avalonia UI / SkiaSharp**（MIT）——UI 框架与 2D 图形/运行时着色器。
* **修正**：README 此前误标为 MIT 许可，现与 `LICENSE.txt` 及上游一致，均为 **CC BY-NC-SA 4.0**。

### ✨ 3. 笔记组件：标题栏显示开关（1.0.9）

* 组件设置新增「**显示标题栏**」（默认开）：关闭后卡片头与分隔线整体收起，正文占满整卡，二级面板仍可双击唤出。

### 🐛 4. 清单组件：只读态悬停高亮清除（1.1.8）

* 卡片默认只读后，条目文本框悬停时不再出现 Fluent 底色高亮（视觉上"看起来能点却点不动"的误导）；打开「在卡片上直接编辑」后恢复标准悬停反馈。Small / Wide / Large 三种规格同步修正。

### 🧹 5. 仓库清理与文档归档

* 清理约 **1.2 GB** 构建缓存（bin/obj/.vs）、全部临时构建日志、视觉检查产物与旧安装包；`dist/` 回归纯构建产物目录。
* 历史更新日志（v1.9.9 – v2.6.1）归档至 **`docs/release-notes/`** 并纳入版本管理；架构解构报告、内存审计、液态玻璃评估三份内部文档归档至 **`docs/`**。
* README（双语）按新品牌重写：新增开源致谢表、四大材质说明与二级面板动效说明。

### 🔢 版本

* 主程序 **2.7.0**；笔记 **1.0.9**、清单 **1.1.8**（改动到的组件补丁位 +1）。
