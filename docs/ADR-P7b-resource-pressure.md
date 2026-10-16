# ADR-P7b:资源压力评估与两级消费(ResourcePressureEvaluator)

状态:已实施(2026-10-16,P7 切片 B)。阈值为实验参数待真机标定;真机压力策略验收未执行(见 docs/真机验收清单.md)。

## 决策

### 1. 无状态纯函数评估器 + 消费者侧迟滞

`DeskCanvas.Contracts.ResourcePressure.cs`(纯 BCL):

- `ResourcePressureLevel { None, Watch, High }`——对应长期方案 §14.3"逐级策略"的观测分级。
- `ResourcePressureEvaluator.Evaluate(ResourcePressureState)`:ratio = 可回收字节/软上限;<0.5→None;<0.8→Watch;≥0.8→High;Cap≤0→None。阈值对齐 `ResourceBudget.Tiers` 的实验参数口径(观测 cap 取中间档 384 MiB)。
- `ApplyHysteresis(previous, candidate, stableRounds)`:候选与上次一致时维持;分歧即重置连续计数;**第 N 轮连续分歧候选在 N==stableRounds 时采纳**(夹具钉住该语义,防质量反复抖动)。
- `TotalReclaimableBytes(snapshot)`:Σ EstimatedBytes(null/异常防御为 0)。

### 2. 两个有差异的真实消费者

1. **MemoryTrimmerService**:每次既有 RequestTrim 时刻评估账本压力,High 时把请求的 trim 优先级**上调一档**(既有枚举序,已是 Force 不变);cooldown/单飞/floor 原样。不新增 trim 时机——只影响既有维护动作的强度。
2. **LiquidGlassWallpaper**:闲置捕获归还宽限(EvaluateIdleRelease)在 High 压力下缩短为原值/4(3 分钟量级→45 秒),Watch 保持原值;`DESKCANVAS_GLASS_IDLE_RELEASE_MS` 显式 env 配置**优先于压力干预**(尊重用户显式配置);配套把"武装延迟"(armed delay)从固定 60s 改为跟随有效宽限——否则缩短的宽限在首个闲置周期永不生效。

两个消费者各持**本地迟滞**(连续 2 轮同级别才切换),不共享静态迟滞状态——二者评估节奏不同,共享会互相干扰(实施代理论证,主代理复核采纳)。

### 3. 边界(刻意不做)

- **不引入任何用户可感知的外观/采样变化**:§14.3 要求"自动"模式按用户批准的策略处理——降采样/降帧率/材质降级等待真机标定与用户批准后的后续切片。
- 不新增 trim 时机、不改变淘汰语义;评估异常按无压力处理(观测绝不破坏生产路径)。

## 验证

- 新增独立夹具 `tests/ResourcePressureChecks`(Release,注册进 verify.ps1/README/sln):**37/37**——分级矩阵、迟滞语义(含采纳轮次钉死)、可回收字节求和、两消费者接线源码扫描(env 优先/宽限缩短/本地迟滞)、真实单例快照冒烟。
- 集成期修复(主代理):夹具一处迟滞断言与已钉死的采纳语义自相矛盾(期望第 2 轮仍 None,应为采纳 Watch),按语义修正。
- 统一门禁 26 步执行结果见 P7 迁移报告。
