# ADR-P5b：Storage 卷枚举与 MediaSession 按共享数据源模式迁移（Clipboard 明确不迁移）

状态：已实施共享数据源切片（2026-10-16）

## 决策

延续 P5 的通用 `SharedDataProvider<TKey,TValue>`（请求单飞、成功完成起算 TTL、owner lease、取消隔离、请求/命中/合并/失败统计），本切片将剩余两个存在跨消费者重复采集的共享数据源迁入同一引擎，并明确记录一个不迁移的决策。

### Storage 卷枚举

Runtime 新增 `SharedDriveSnapshot`（Name/DriveType/TotalBytes/FreeBytes/VolumeLabel/DriveFormat）与 `StorageDrivesSharedProvider`，默认 TTL 10 秒。采集函数 `CaptureAll` 只用纯 BCL `DriveInfo`（Runtime 允许 System.IO），逐盘保留原实现的异常保护：仅枚举 Fixed/Removable/Network 且 IsReady、TotalSize>0 的盘；VolumeLabel/DriveFormat 访问各自 try/catch，单盘失败跳过不断链，整体枚举失败返回空。数值均为 BCL 原始值（InvariantCulture 语义），不含展示逻辑。

宿主 `SharedDataServices.Storage` 是每进程唯一生产实例,实际枚举经 `Task.Run(CaptureAll)` 在线程池执行。Storage 组件的 `StorageService` 变为薄外观:`GetDrives`/`GetAllCandidateDrives`/`NormalizeDriveKey`/`FormatBytes`/`OpenDriveInExplorer` 及 `DriveStorageInfo` 记录签名全部不变,StorageView、StorageViewModel、StorageSettings 零改动;内部改为获取共享快照后按原逻辑投影(系统盘识别、本地化回退标签、DisplayTitle、用量钳制与百分比、按盘符排序、可移动/网络/隐藏盘过滤)。原实现的 `DriveLetter` 语义保持逐字符等价:根路径仅去掉末尾反斜杠(即 "C:" 而非 "C"),非空 VolumeLabel 永远优先、空 label 才落到本地化回退。新增 `DriveSource` 静态属性作为注入缝(默认走宿主单例,回退求值是惰性的,注入源不会加载宿主程序集),测试用它替换为可控 provider。外观以同步签名保留(视图按 10s 周期轮询、设置页一次性枚举),阻塞等待仅发生在真实枚举轮次上;多实例并发轮询被单飞合并,TTL 内直接命中缓存。同步等待的前提:`SharedDataProvider` 内部全部 `ConfigureAwait(false)`,宿主 fetch 委托为 `Task.Run`(纯线程池完成),因此外观在 UI 线程 `GetAwaiter().GetResult()` 不会死锁;注入方必须遵守同一前提——fetch 不得依赖调用方同步上下文完成(测试注入为 `Task.FromResult` 同步完成)。

### MediaSession（SMTC 媒体信息）

Runtime 新增 `SharedMediaSnapshot`（SourceAppId/Title/Artist/AlbumTitle/PlayerName/IsPlaying/Position/Duration/三个 Can* 控制位/ThumbnailData）与 `MediaSessionSharedProvider`。默认 TTL 仅 250ms：它的职责只是把推事件与轮询tick 竞争产生的突发读取合并为一次 SMTC 轮次，远小于 1.5s 轮询周期，因此计划内轮询仍是全新读取；位置外推与 SMTC 相关的时序计算留在采集侧完成。

Music 组件内 `MediaManagerService` 重构为进程级共享引擎的薄外观,公开 API 与事件语义逐项保留(构造函数、TrackChanged/TimelineChanged、CurrentTrack、UpdateRules、RefreshActiveSessionAsync、GetActiveSessionsAsync、四个控制命令、PausePolling/ResumePolling、Dispose、IsDisposed),MusicViewModel 与 MusicSettings 两处 `new MediaManagerService()` 消费者零改动。SMTC 平台访问(RequestAsync、会话枚举/选择/绑定、属性与封面读取、传输命令、友好名解析)全部收在 Music 侧 `SmtcGateway`(internal),经默认 provider 的注入 fetch 委托进入共享引擎;Runtime 不含任何 Windows API。引擎按 provider 实例注册并引用计数:最后一个消费者离开时停轮询、解绑会话、释放 SMTC 订阅;任一消费者 Dispose 只移除自己的事件订阅,其余消费者继续共享同一轮询。事件由引擎每轮触发一次,各外观投影出**各自的** `MediaTrackInfo`(消费者之间不共享可变模型);无会话时保留原行为(每轮重置并通知空曲目)。原实现的等待期 Dispose 竞态防护(初始化 await 后检查、计时器清理)在引擎层等价保留。与原单实例实现不同,共享引擎的并发刷新(轮询 tick 与推事件竞争、被合并的等待者并行恢复)必须串行化快照状态迁移与事件触发(引擎内 `applyGate` 锁),否则同一轮快照会被多个并发恢复方各自判为"新身份"而重复触发 TrackChanged;Seek 与快照路径共享同一把锁保证 seek 冷却状态一致。控制命令路径(含 Shortcuts 的 MediaExecutor)未改动。

行为偏差说明：原实现绑定会话的 Timeline/PlaybackInfo 推事件触发"仅位置"的廉价读取，现统一走快照轮次（由单飞与 250ms TTL 合并）；进度条由视图自身 250ms 插值计时器平滑，可感知差异可忽略。provider TTL 选择 250ms 而非轮询周期，是为保证推事件路径的新鲜度，这是与 Storage（10s）不同 TTL 的原因。

### Clipboard —— 明确不迁移

`src/Widgets/Tools/Services/ClipboardMonitorService.cs` 已是组件内单例：视图、快捷弹窗与设置页三个消费者共享同一实例与同一份历史，不存在第二个有差异的跨组件消费者，也没有重复轮询或重复平台访问可消除。按执行纪律"新抽象需两个有差异的真实消费者"判定：抽公共抽象只会增加间接层而不减少任何资源使用。若未来出现第二个独立剪贴板消费组件（如独立的剪贴板历史 widget），再按本切片模式迁移。

## 消费者事实表

| 数据源 | 采集 | TTL | 迁移前消费者 | 迁移后 | 共享证据 |
|---|---|---|---|---|---|
| Storage 卷枚举 | `DriveInfo.GetDrives`（BCL，宿主 Task.Run） | 10s | 每个 Storage 实例视图每 10s 各自枚举 + 设置页一次性枚举 | `SharedDataServices.Storage` 单例；`StorageService` 外观投影 | 多次外观调用 `Requests` 仅 +1、`CacheHits` 增长 |
| MediaSession (SMTC) | Music 侧 `SmtcGateway`（WinRT，注入委托） | 250ms（仅合并突发） | Music 视图与设置页各自 new 实例、各自 1.5s DispatcherTimer 轮询、各自 RequestAsync | 进程级共享引擎（按 provider 注册、引用计数）；单次 1.5s 轮询 | 两消费者并发刷新 `Coalesced=2`、SMTC 访问计数=1 |
| Clipboard | 组件内 `ClipboardMonitorService` 单例 | — | 视图/弹窗/设置三消费者已共享同一实例 | **不迁移** | 无重复采集可消除 |

## 验证与边界

`tests/SharedDataSourcesChecks`（net8.0-windows，因引用 Music 的 WinRT 投影；引用 Runtime + Storage + Music）覆盖：Storage 共享单飞/独立 owner lease/TTL 重取/取消隔离；StorageService 外观投影等价（字段、回退标签、NTFS 默认、用量计算、排序、类型与隐藏盘过滤、CDROM 排除）；外观调用共享同一快照轮次；共享 fetch 失败返回空表不抛出；MediaSession 两消费者共享一轮 SMTC（访问计数=1、Coalesced=2）、晚加入者零读取拉取当前曲目、同身份仅 TimelineChanged、播放状态变化广播、无会话重置语义、Dispose 隔离（视图销毁后设置页继续轮询；全部离开后无后台轮次；重新获取重建引擎）。全部使用注入假 fetch 与可控任务，不访问真实 SMTC/磁盘外设。

边界：真机 SMTC 行为（Win32 播放器事件缺失时 1.5s 轮询兜底、封面流读取）、真实多盘/离线盘与网络盘的枚举健壮性、引擎跨widget长时间运行的内存观察仍需人工验收。`scripts/verify.ps1`、`tests/README.md` 与 `src/DeskCanvas.sln` 的登记由集成方统一完成。
