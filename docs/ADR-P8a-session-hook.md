# ADR-P8a:会话钩子与共享时间源(P8-A0)+ 数字时钟试点(P8-A1)

状态:已实施(2026-10-16,P8 批次 A 前两个切片)。Legacy 路径全部保留;真机验收未执行。

## 决策

### 1. WidgetSessionRegistry:每实例一会话,宿主生命周期桥接

`DeskCanvas.Runtime.WidgetSessionRegistry`(静态类,Runtime 可被组件引用):

- `GetOrCreate(WidgetInstanceId)`——同 id 同实例;内部构造 Runtime `WidgetSession`(串行生命周期+幂等释放,P2 契约);空 id 拒绝。
- `ReportActivity(id, ActivitySnapshot)` / `Remove(id, finalActivity)`——Remove 前先记录最终活动(拆除语义),幂等,DisposeAsync 火忘(关闭序列永不 await)。
- `IWidgetActivityConsumer { OnSessionActivity(ActivitySnapshot) }`(Runtime 接口):宿主把会话活动**直接转发给挂载的内容控件**——控件实现即收推送,不必查询注册表。两个消费通道(注册表直查/接口推送)服务不同场景:服务与检查用前者,控件用后者。

宿主接线(全部行为保持,Legacy 组件零改动):

- **创建**:`WidgetFactory.CreateInternal` 在 `HasStableIdentity` 时 `GetOrCreate`(Legacy 布局条目=会话无关,与预览一致);`CreateWidgetControl` 新增可选 `IWidgetSession` 参数,按构造签名探测注入(探测模式与 IWidgetLayoutProvider 相同)——**不带该参数的 26 个组件激活路径逐位不变**。
- **活动**:`Widget.SuspendContent/ResumeContent` 尾部追加 `ReportSessionActivity(ScreenCovered/DesktopVisible)`,经注册表+接口双通道推送。
- **拆除**:`PrepareForTeardown` 尾部 `OnSessionActivity(TeardownActivity)+Remove(id, TeardownActivity)`。

### 2. SharedTimeZone:第 5 份重复时区样板的共享化起点

`DeskCanvas.Runtime.SharedTimeZone.Resolve(string? windowsId)`:有界缓存(30 条,满则清)+ Windows id/IanaId 兼容+失败回退 **Utc**。语义与 DigitalClockViewModel 的私有样板等价,仅一处刻意差异:**非法 id 的回退从 Local 改为 Utc**(非法 id 属异常配置,UTC 更确定性;夹具钉住)。本切片只迁 Digital 一份;其余 4 份样板(FramelessDigital/Analog/Flip/TickDial)待各自批次迁移——共享实现的第二个消费者(日历)落地后此抽象才算成立(当前按纪律保留可回退)。

### 3. 数字时钟试点(A1)

`DigitalClockViewModel(clockModel, IWidgetSession? session = null)`:

- 订阅状态机:`wanted = isRunning && renderActive`,每次变化**对账**(而非开关切换)——覆盖中卸载等交叠转换不会重复订阅/重复退订。
- `OnSessionActivity(push)`:会话为 null(legacy/预览)完全忽略;有会话时 `renderActive = activity.RenderActive`。
- 时区解析换 `SharedTimeZone`。
- 检查缝(公开只读):`TickSubscriptionCountForChecks`/`IsTickingForChecks`/`SessionForChecks`——检查程序与未来组件夹具共用。

Digital 视图实现 `IWidgetActivityConsumer` 转发到 VM;`Loaded/Unloaded` 的 Start/Stop 原样保留(isRunning 输入不变)。

## 集成期发现与修复(主代理)

1. **类型转换**:`WidgetLayout.InstanceId` 是 `Guid`(Core 模型),注册表 API 收 `WidgetInstanceId`——三处调用点显式包装。
2. **测试桩激活保真**:夹具的 AssemblyProviderStub 原只复刻真实 `AssemblyProvider.Activate` 的第一级(DI 激活);ClockModel 无无参构造导致默认模型创建失败→激活回退到无参 `Digital()`→会话未注入。修复:桩复刻真实三级回退(反射/最佳构造匹配),与生产行为逐位对齐。
3. **夹具布局生产真实化**:恢复的组件总是携带存储的模型设置——夹具布局补序列化 `ClockModel`,使"带会话的全构造"可达(无参回退路径另由夹具显式覆盖)。
4. **headless HWND 防御**:`InteropService.RemoveWindowFromAltTab` 对无效句柄 `SetWindowLong` 抛 Win32Exception(1400)——加 `IsWindow` 守卫(生产有效句柄零变化,顺带消除真实句柄竞态崩溃面);夹具 headless 配置对齐 WidgetReleaseChecks 先例(`UseHeadlessDrawing=false + UseSkia`,真实窗口句柄)。

## 边界与未完成验收

- **Legacy 路径不删**:`IWidgetSelfRefreshing`/`ISuspendable` 仍为 26 个组件的运行方式;会话是叠加通道。删 Legacy 消费属后续批次(每组件迁移完成时)。
- **WidgetScheduler 仍未接线**(调度租约属后续切片);TimerService 原样。
- 数字时钟的 tick 仅门控 TimerService 订阅;逐实例渲染暂停(RenderActive→绘制)未做(渲染层跟随 TimerService 停订自然静止)。
- 真机验收:数字时钟挂起/恢复的实机 tick 行为、多实例会话隔离——见 docs/真机验收清单.md M3-8(新增)。

## 验证

- 新增独立夹具 `tests/SessionHookChecks`(Release,注册进 verify.ps1/README/sln):**59/59**——注册表身份/移除幂等、活动语义(ScreenCovered/DesktopVisible/Preview)、SharedTimeZone(已知/未知/缓存有界/回退)、数字时钟直接构造(legacy 忽略推送/会话门控/Stop 对账)、宿主管线端到端(真实 WidgetFactory 创建→注入→挂起停订→恢复重订→拆除释放)、接线源码扫描。
- 统一门禁 26 步执行结果见迁移报告。
