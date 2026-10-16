# ADR-P7a:只读资源账本(ResourceLedger)

状态:已实施(2026-10-16,P7 切片 A)。预算强制与压力策略为后续切片;真机双 4K/在途/压力验收未执行(见 docs/真机验收清单.md)。

## 决策

### 1. 观察先行:绝对值上报的进程级账本

按长期方案 §14.3"先加入观测与回收,不在结构迁移中偷偷改变外观默认值",P7 第一切片建立**只读观测**,不改变任何释放/淘汰/渲染语义:

- 契约(Contracts,纯 BCL):`ResourceKind`(GlassCapture/GlassBackdrop/GlassLease/WallpaperSnapshot/PictureFrame/PicturePendingDecode/GlyphMask/MapTile/FileIcon/Other)、`ResourceUsage(Owner, Kind, Count, EstimatedBytes, ReleaseThread)`、`IResourceLedger(Report/Snapshot)`、`GlassMemoryEstimator.EstimateBgraBytes`(BGRA Σ RowBytes×Height 紧凑式;行对齐差异在注释说明)、`ResourceBudget` 三档候选(192/384/768 MiB,**实验参数非发布值**)。
- 实现(Runtime,纯 BCL,组件可引用):`ResourceLedger.Instance` 单例。`Report` 为**绝对值上报**(计数器变化后报当前值,非增量),内部全吞异常+防御性钳制——观测绝不破坏生产路径;`Snapshot` 按 (Kind,Owner) 稳定排序便于快照差分;**有界表**:每 Kind 超过 256 个 owner 时最小行合并进 "…" 溢出行(含溢出行在内每 Kind ≤256 行,总量守恒)——落实 §15.1"不将无限 InstanceId 直接作为常驻 metrics 维度"。
- 接口放 Contracts、实现放 Runtime:组件(Picture/Clock)编译期引用 Runtime,可直接上报,无需反向依赖宿主。

### 2. 接线(5 个生产上报点,全部 ≤5 行、复用既有计数器)

| 上报点 | Owner | Kind | 内容 |
|---|---|---|---|
| `LiquidGlassSource.cs`(源缓存增/逐两处) | `glass.source-cache` | GlassBackdrop | 可淘汰缓存条目数与字节(3 项/64MiB 预算内) |
| `LiquidGlassWallpaper.cs` | `glass.wallpaper` | GlassCapture | 当前捕获帧字节(FrameBytes) |
| `LiquidGlassRenderer.cs`(WallpaperLease) | `glass.wallpaper-lease` | GlassLease | 活跃租约数与字节——**现成引用计数租约范本**(缓存引用移除与在途帧释放两时刻,ADR-P7 后续切片的租约统一以此为准绳) |
| `PictureViewModel.cs`(帧缓存变化) | `picture:{instanceIndex}` | PictureFrame | 解码帧数与 BGRA 字节(在途解码为同步路径,PicturePendingDecode 本切片不接线,夹具钉住) |
| `Clock FramelessGlassFrameCache.cs` | `clock.frame-cache:{instanceIndex}` | GlyphMask | 字形/帧缓存条目与字节 |

MapTile/FileIcon 仅占位枚举不接线(随 P8 批次迁移其缓存时接入),夹具以反向断言钉住"未越界接线"。

### 3. 与既有释放协议的关系

LiquidGlassSourceCache 的"淘汰只释放缓存引用"、WallpaperLease 的"最后一帧结束后才释放位图"语义**原样保留**;账本只读取它们的现有计数器。§14.1 的带类型租约统一与 §14.2 的预算强制(缓存+活跃租约+在途+双缓冲峰值分别报告后执行)是后续切片,设计时以 WallpaperLease 为范本、以账本快照为验收观测面。

## 路径偏差记录

实施代理按指示核实后修正了两处计划路径:LiquidGlassSourceCache 实际位于 `LiquidGlassSource.cs`;Clock 字形缓存实际为 `FramelessGlassFrameCache.cs`(GlyphMaskRenderer 不存在);Picture 计数器在 `PictureViewModel.cs`(PictureMemory.cs 不存在)。Picture/Clock csproj 显式补加 Runtime 引用(Clock 同时把既有传递 UI 引用显式化)。

## 边界与未完成验收

- **预算不强制**:ResourceBudget 三档仅可查询;强制淘汰/降级/逐级压力策略(可见性优先、恢复迟滞、质量档位)待切片 B,须先经账本真机观测标定。
- instanceIndex 维度的 owner 命名在 256 上限内;跨组件实例数极多时溢出行吸收,精确 per-instance 归因由诊断详情表承担(§15.1)。
- 真机验收:双 4K 场景账本数值核对、在途资源观测、压力策略(切片 B 后)——见 docs/真机验收清单.md M3-7(新增)。
- 旧包二进制兼容:上报点均为新增调用,不改签名;旧包不受影响。

## 验证

- 新增独立夹具 `tests/ResourceLedgerChecks`(Release,纯 BCL,注册进 verify.ps1/README/sln):**30/30**——聚合/覆盖/排序、有界表上限与守恒、Reset、估算数学、预算三档、防御输入、5 接线点源码扫描 + 2 反向断言。
- 集成期修复(主代理):`MergeSmallestIntoOverflow` 变量重名 CS0136;有界表 `if`→`while` 收敛(合并后新增超限 1 行)。
- 统一门禁 24 步执行结果见 P7 迁移报告。
