# tests/ 本地检查项目登记

本目录共 54 个检查项目（不含 `_shortcuts_tools` Python 辅助脚本目录，2026-10-17 按非 bin/obj 的 csproj 统计）。分类口径：

- **正式门禁**：有明确通过/失败语义（退出码或异常），按改动范围纳入提交/发布门槛。
- **探针/复现程序**：测量、截图或复现 harness，产出供人工判读，不作门禁。

统一验证入口：`scripts/verify.ps1`（执行结构/约定检查、解决方案构建及下表标注「已验证套件」的项目）。当前入口共 30 个步骤：两项结构/约定守卫、解决方案 Debug 构建和 27 个 Release 检查程序，覆盖 P1–P8 架构与既有 UI/布局门禁。发布门禁聚合入口：`scripts/release-gate.ps1`（verify 全量 + ApiSnapshot 校验 + UpgradeRollbackChecks + SettingsReleaseChecks,摘要落 dist/release-gate-summary.json）。

架构重构门禁：`IdentityStorageChecks`（P1 身份迁移与原子保存）、`ContractsLifecycleChecks`（P2 纯 .NET 契约与会话生命周期）、`SchedulerChecks`（P5 owner 调度租约与失败隔离）。这三项已由 `scripts/verify.ps1` 一并执行。

| 项目 | 分类 | 用途 |
|---|---|---|
| UiFoundationChecks | 正式门禁（已验证套件） | UI 基础契约：玻璃起源/沉降、面板壳、弹窗迁移、设置本地化、冗余守卫等 |
| IdentityStorageChecks | 正式门禁（已验证套件） | P1 稳定 InstanceId 幂等迁移、几何变化匹配、保存失败语义 |
| ContractsLifecycleChecks | 正式门禁（已验证套件） | P2 Contracts/Runtime 会话串行生命周期与幂等释放 |
| SchedulerChecks | 正式门禁（已验证套件） | P5 owner 调度租约、暂停恢复、失败隔离与统计 |
| UiSharedChecks | 正式门禁（已验证套件） | P3 公共 UI 状态呈现、重复通知和差异消费者适配 |
| UiSharedAvaloniaChecks | 正式门禁（已验证套件） | P3 共享滑块/设置行/颜色输入与宿主兼容门面、实际数值/键盘/颜色提交、自适应布局槽位、旧/新编译资源 URI 与全部 token、可嵌入样例目录 |
| PanelShellAvaloniaChecks | 正式门禁（已验证套件） | P3 玻璃表面端口：共享面板壳独立渲染、GlassSurfaceBase 端口契约（独立桩实现）、外观端口槽镜像、宿主外观分解与旧 URI shim、拖动行为同属性身份、面板窗口端口驱动 |
| PerScreenThemeChecks | 正式门禁（已验证套件） | P4 每屏独立主题：按屏幕作用域的焦点状态解析矩阵、窗口 variant/窗口级颜色资源应用、快照重排 poke、uniform 回退与旧 API 兼容 |
| SharedDataSourcesChecks | 正式门禁（已验证套件） | P5 Storage 卷枚举与 MediaSession 共享 provider：请求单飞、TTL、owner lease、取消隔离、组件外观投影等价、两消费者共享轮次；冻结注入，无真实 SMTC/磁盘外设 |
| WindowRoleChecks | 正式门禁（已验证套件） | P6 窗口角色登记：六角色能力档案、注册语义（幂等/覆盖/未注册 null）、各创建点接线源码扫描、组件平台边界守卫（src/Widgets 的 [DllImport] 白名单钉死） |
| PanelOwnershipChecks | 正式门禁（已验证套件） | P6 owner 面板服务：PanelService 双作用域（PerOwner/ProcessSingleton）toggle/冷却/owner 窗口关闭联动/显示变更关闭、legacy 类型键兼容逐位、工厂失败无残留、AbandonPanel 契约 |
| ResourceLedgerChecks | 正式门禁（已验证套件） | P7 只读资源账本：Report/Snapshot 聚合、有界表溢出合并、BGRA 字节估算、预算三档、异常输入防御、6 处玻璃链/Picture/Clock 接线源码扫描与 MapTile/FileIcon 防越界断言 |
| ResourcePressureChecks | 正式门禁（已验证套件） | P7 资源压力评估：分级矩阵（None/Watch/High）、恢复迟滞、可回收字节求和、MemoryTrimmer 优先级升级与壁纸闲置宽限缩短接线源码扫描、真实单例快照冒烟 |
| SessionHookChecks | 正式门禁（已验证套件） | P8-A0/A1 会话钩子与数字时钟试点：注册表身份/移除幂等、宿主创建-活动-拆除全链路（真实 WidgetFactory 管线）、IWidgetSession 构造注入、时钟活动门控/时区等价、SharedTimeZone 解析与回退、接线源码扫描 |
| CalendarSessionChecks | 正式门禁（已验证套件） | P8-A2 日历生命周期：Month/Date 活动门控对账（无会话 legacy/有会话门控/Teardown 停订）、Start/Stop 交叠、TodayBrush 解析未动（CalendarHollowChecks 断言对象保持） |
| ProgressChecks | 正式门禁（已验证套件） | P8-A3 Progress：DarkMode==null 回退逐屏化（uniform 回退/窗口变体）、会话门控、自刷新契约、设置页 ScrollOwnership=Host 接线扫描 |
| BatteriesChecks | 正式门禁（已验证套件） | P8-A4 Batteries：3s 轮询收敛到 TimerService（分频对账/全屏暂停/会话门控）、主题变体逐屏解析（三分支值等价/兜底钉死）、appSettingsProvider 两级解析、PowerService 保留（单消费者决策） |
| ApiSnapshotChecks | 正式门禁（已验证套件） | P9 公开 API 快照：Contracts/Runtime/Core/UI 四程序集 public 面（类型/方法签名/属性/枚举值）与 Baseline.txt 逐行比对；DESKCANVAS_API_SNAPSHOT_RECORD=1 录制模式；关键 SDK 类型存在断言 |
| UpgradeRollbackChecks | 正式门禁（release-gate 采，未入 verify） | P9 升级回退演练：v1 旧布局幂等迁移与往返、保存失败不发布成功、Generation 代守卫、空 Widgets 目录扫描不抛 |
| ThemeSettingsChecks | 正式门禁（已验证套件） | P4 ThemeSnapshot、draft/commit、revision 冲突与影响域 |
| SettingsIntegrationChecks | 正式门禁（已验证套件） | P4 真实 Appearance 设置页与原子 JSON 保存、失败/冲突重试、窗口资源隔离、Avalonia 文字预编辑与滑块完成提交 |
| WeatherSharedChecks | 正式门禁（已验证套件） | P5 Weather/Fixed 共享请求单飞、参数键与 owner lease |
| ProviderIntegrationChecks | 正式门禁（已验证套件） | P5 真实 Weather/Fixed/Monitor 兼容入口、完整响应投影、城市搜索与取消隔离、共享指标；冻结响应，无外网/WMI |
| LayoutOwnershipChecks | 正式门禁（已验证套件） | 布局归属：宿主与各面板布局职责边界 |
| FocusSwitchChecks | 正式门禁（已验证套件） | 失焦自动切换主题契约与原生枚举冒烟 |
| MonochromeAccentChecks | 正式门禁（已验证套件） | 单色强调色模式下的配色契约 |
| WidgetAdaptiveChecks | 正式门禁（已验证套件） | 组件自适应档位/尺寸契约 |
| FullscreenChecks | 正式门禁（已验证套件） | 全屏行为与原生窗口枚举冒烟 |
| LiquidGlassChecks | 正式门禁 | 液态玻璃材质（V2、GPU 材质、壁纸布局等）契约 |
| GlassGpuProbe | 正式门禁 | 玻璃 GPU/CPU 渲染路径对比（附录 B 列入发布门） |
| ClockThemeChecks | 正式门禁 | 时钟组件主题覆盖与全局材质优先级 |
| ClockVisualChecks | 正式门禁 | 时钟组件视觉渲染检查 |
| DragCommitChecks | 正式门禁 | 拖拽提交（落格/吸附）行为契约 |
| MultiScreenGridChecks | 正式门禁 | 多屏网格坐标与映射契约 |
| BottomBandChecks | 正式门禁 | 底部条带布局/行为契约 |
| SettingsReleaseChecks | 正式门禁 | 设置页关闭后的资源释放契约 |
| PictureMemoryChecks | 正式门禁 | 图片组件内存释放契约 |
| HitokotoMemoryChecks | 正式门禁 | 一言组件内存释放契约 |
| CalendarHollowChecks | 正式门禁 | 日历/聚合仪表盘「今天」镂空标记回归（明暗双主题像素验证） |
| CountdownChecks | 正式门禁 | Countdown 组件模型/服务/视图契约 |
| FixedSpanChecks | 正式门禁 | 固定组件 4×2 卡片的跨度晶格（2:1 等比整数跨度） |
| HabitsChecks | 正式门禁 | Habits 组件模型/服务/视图契约 |
| ScrollBarHoverChecks | 正式门禁 | 全局滚动条悬停展开态回归（悬停与空闲像素一致） |
| SearchVisualChecks | 正式门禁 | Search 组件视觉/布局边界检查（截图产出至 dist/） |
| ShortcutsSmoke | 正式门禁 | Shortcuts 无头冒烟：字形解析、存储往返、顺序执行、控制流（沙箱运行） |
| TimetableChecks | 正式门禁 | 课程表数据契约：周表达式、导入器、学期计算与配色 |
| UpdateAtomChecks | 正式门禁 | releases.atom 回退解析器对冻结 fixture 的契约（自述按改动范围作提交门槛） |
| WidgetReleaseChecks | 正式门禁 | 切换 profile 时组件真实释放的内存契约（跑真实宿主管线） |
| ShortcutsAllocProbe | 探针/复现程序 | Shortcuts 缓存助手每次调用的托管分配对比测量 |
| ShortcutsRender | 探针/复现程序 | Shortcuts 卡片无头渲染 harness（参数化尺寸/场景截图） |
| TimetableMemoryProbe | 探针/复现程序 | 课程表单实例内存成本量化（托管堆/GC/工作集归因） |
| TimetableRepro | 探针/复现程序 | 课程表设置页与组件多尺寸渲染复现 harness |
| _glassshot | 探针/复现程序 | 全组件液态玻璃渲染截图 harness（真实管线逐视图 45 张 + 实机批处理截图；产出 dist/glass-shots/，人工审查） |
| _uiprobe | 探针/复现程序 | Shortcuts UI 场景截图探针（编辑器/卡片/弹窗/设置等场景） |

备注：`_shortcuts_tools/` 为 Python 测量/生成辅助脚本，非 .NET 项目，不计入上述清单。
