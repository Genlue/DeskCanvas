# ADR-P6a:owner 面板服务化(PanelService)

状态:已实施(2026-10-16,P6 切片 1)。真机多面板/owner 关闭/屏幕断开行为验收未执行(见 docs/真机验收清单.md)。

## 决策

### 1. 面板单例状态从静态字典迁入 PanelService,身份显式化

`SecondaryPanelWindow` 原以 `static Dictionary<Type, SecondaryPanelWindow> ActivePanels` + `Dictionary<Type, DateTime> LastCloseTimes` 决定"每种面板进程级单例"——身份=CLR 类型,无 owner 概念:两个同类组件的面板互相顶替,owner 关闭后面板残留。

新 `DeskCanvas.Services.PanelService`(静态类,锁风格对齐 WidgetZOrder)拥有全部单例状态:

- `PanelScope { PerOwner, ProcessSingleton }`:每 owner 实例一面板 / 进程单例。**每屏(PerScreen)作用域暂不实现**——当前无真实消费者(面板跟随打开屏幕由 ADR-P4b 的 PanelAppearanceBinder 承担,屏幕断开由 CloseForDisplayChange 全量关闭),按纪律第 2 条不预留死代码。
- `PanelRequest(WidgetInstanceId OwnerInstanceId, Window? OwnerWindow, string PanelKey, PanelScope Scope, Func<SecondaryPanelWindow> Factory, int CooldownMs = 250)`:契约与长期方案 §13.1 一致(owner 身份、PanelKey、作用域、内容工厂);锚点(spawn 位置/圆角)仍由组件经弹窗构造器传入(它们是面板内容语义,不是服务调度语义)。
- `PanelOpenDecision { Opened, ToggledClosed, CoolingDown }`:同身份活跃→动画 toggle 关闭(维持既有交互语义:"复用"=同身份不叠开,二次打开=关闭);冷却中→拒绝;否则工厂创建→重绑身份→`ShowAsSecondaryPanel(owner)`。
- 身份键:`"process|PanelKey"` 或 `"owner:{InstanceId}|PanelKey"`(InstanceId 为空回退 `"win:{window hash}"`);冷却按身份键记录。
- owner 生命周期契约:PerOwner 打开时订阅 owner 窗口 `Closed` → `CloseForOwner(owner)`(幂等订阅、注销时退订);`WidgetFactory.CloseScreen/CloseAll` 在关闭每个组件窗口前调 `PanelService.CloseForOwner(widget.InstanceId)`——**owner 关闭/移除时归属面板不再残留**(此前缺失的契约)。
- `CloseForDisplayChange` 语义原样迁入(全部活跃面板 Hide+立即 Close,无动画),`SecondaryPanelWindow.CloseForDisplayChange()` 保留公共入口转发。

### 2. 兼容路径逐位保留,消费者原子迁移

- 13 个未迁移弹窗的 `PanelCoolingDown<T>`/`TryCloseActivePanel<T>`/`AbandonPanel` 签名不变,转发服务,键=`"process|类型全名"`(与旧静态字典逐位一致)。面板构造时以 legacy 键自注册(`PanelService.RegisterPanel`),`Closed` 时注销+记冷却——legacy 消费者行为不变。
- 服务 `Open` 在工厂构建后、显示前把新面板从 legacy 键**重绑**到请求键,再订阅 owner 守卫。
- 工厂抛异常:水位线(`lastRegisteredPanel`,UI 线程)识别幽灵注册并丢弃,不留残留、不记冷却(等价旧 `AbandonPanel` 契约);Show 失败同样走 Abandon(不启动冷却)。
- 失败构造与 Shortcuts 教训:服务侧订阅的 owner.Closed 在注销与 ResetForChecks 时一律退订。

### 3. 两个有差异的真实消费者

1. **Weather = PerOwner**(行为升级):两个天气组件各自拥有自己的面板,不再互相顶替;owner 关闭时面板随之关闭。公开签名 `ShowPopup(...)` 不变。
2. **Dice = ProcessSingleton 显式声明**(行为保持单例):`ShowPopup` 内部改走 `PanelService.Open(PanelKey "dice")`;删除私有 `activeInstance` 静态字段——同时**修复其裸 `Show()` 缺陷**(原来不钉组件带上方、不激活)。

Wheel/Fortune 的同类裸 `Show()` 缺陷以最小改动修复(`Show()` → `ShowAsSecondaryPanel(owner)`),P8 再迁服务;其余 12 个面板不动,P8 随组件批次迁移。

## 行为变化(有意)

| 变化 | 说明 |
|---|---|
| 天气面板按 owner 隔离 | 两个 Weather 组件可同时各开一个面板(原为互相顶替);P6 验收矩阵覆盖 |
| Wheel/Fortune/Dice 面板出现在组件带上方并被激活 | 修复裸 Show 缺陷,与其余 12 个面板一致;真机 M1-2/M2 验证 |
| Dice 冷却键从类型全名改为 "process\|dice" | 时序语义不变(同为 250ms);仅键名变化 |

## 边界与未完成验收

- **混合打开模式不支持**:同一面板类型一边 legacy 自建、一边经服务打开同类型会破坏 legacy 键活跃表(服务重绑会清掉 legacy 键);迁移必须按类型原子完成(Weather/Dice 已原子迁移,其余 13 个 P8 处理)。无头夹具直接构造面板实例不受影响。
- 托盘隐藏(SetWidgetsHidden)不关面板——记录边界,面板语义归属"屏幕变更"而非"隐藏"。
- 真机验收:owner 关闭面板联动、双天气组件独立面板、Wheel/Fortune/Dice 层级/激活,见 docs/真机验收清单.md(新增 M1-5、M2-6)。
- 旧包二进制兼容:legacy 静态转发签名不变;旧组件包实机装载矩阵仍未执行(累积项)。

## 验证

- 新增独立夹具 `tests/PanelOwnershipChecks`(Release,注册进 verify.ps1/README/sln):**34/34**——进程单例 toggle/冷却、PerOwner 隔离与冷却独立、窗口哈希回退、owner 窗口关闭联动、显示变更全量关闭、legacy 类型键逐位兼容、工厂失败无残留、AbandonPanel 契约。
- `tests/UiFoundationChecks` PanelShellChecks 三处反射断言从已删除的 `ActivePanels` 迁移到 `PanelService.activePanels`(同键强断言);`tests/WidgetReleaseChecks` 静态注册表信息行同步。
- 统一门禁 23 步执行结果见 P6 迁移报告。
