# ADR-P5：Weather/Fixed 网络接线与共享系统指标

状态：已实施共享 provider 接线切片（2026-10-16）

## 决策与生产路径

Runtime 的 `SharedDataProvider<TKey,TValue>` 统一提供请求单飞、从成功完成时间起算的 TTL、独立 owner lease 和请求/命中/合并/失败/活动 owner 统计。快照的时间与版本在共享任务内只生成一次。过期且无 owner 的历史键在下一次请求时清理；过期后仍被租用的旧快照可与刷新快照同时存在，释放旧 lease 不会减少新 lease 的计数。失败不缓存为成功快照，后续调用可立即重试。

`OpenMeteoSharedProvider` 分享不可变 JSON 文本，并按四位坐标、规范化温度单位及资源种类生成键。预报请求包含 current、hourly、daily 的完整字段，Weather 反序列化为原有丰富模型，Fixed 保留简要 `WeatherInfo` 投影；相同位置/单位只执行一次请求。AQI 使用独立资源键并忽略温度单位。地理编码同样经过 provider，键包含查询文本、语言和可选 count：Weather 保留原默认数量与语言语义，Fixed 保留 trim、zh/en、count=10 和 format=json；查询值统一 URL 转义。不同 count 仍是不同请求，不将不等价搜索错误合并。

宿主 `SharedDataServices.Weather` 是每进程的 provider 单例。实际网络轮次才通过 `ProxySettings.CreateHttpClient` 读取当前代理并创建、释放传输客户端；缓存命中或并发等待不会各自创建客户端。Weather 的 `OpenMeteoWeatherProvider` 与 Fixed 的 `OpenMeteoWeatherService` 原有公开方法、默认构造函数及失败返回 null 的语义保留，内部所有 forecast、AQI、geocoding 网络请求均改由这个单例执行，不再持有 HTTP 客户端。另提供 provider 注入构造函数，供冻结响应夹具独立验证。Weather ViewModel 的旧私有预报缓存已移除，统一使用 provider 的五分钟 TTL。

消费者取消或 Dispose 只取消自己的等待，并释放自己的 owner 计数；共享的网络/WMI 轮次继续服务其他消费者，全部消费者离开后也允许当前轮次结束并生成可复用快照。Weather 城市搜索新增带 token 的兼容重载，设置页转交 AutoComplete 的搜索 token；原单参数 API 保留。每个返回模型由调用者独立反序列化，因此修改一个插件模型不会污染其他 owner 的数据。原 `WeatherSharedDataProvider` 与两个 compact `SharedWeatherAdapter` API 保留；compact provider 改为复用同一通用引擎。

这些最小 DTO API 只作为此前切片的兼容入口，不创建或提供另一套生产网络实现。现有组件的全部生产 Open-Meteo 访问均走丰富响应的 `SharedDataServices.Weather` 单例；兼容 DTO 路径和丰富响应路径共用 `SharedDataProvider` 核心。

## 系统资源指标

`SystemMetricsSharedProvider` 在 Runtime 中声明 CPU/RAM/磁盘/网络/电池资源指标与可注入采集函数，默认 TTL 为 900ms。WMI 实现移至宿主 `NativeSystemMetrics` 平台边界，继续在后台采集并释放每个 WMI collection/object，失败或不支持的平台返回无值。宿主 `SharedDataServices.SystemMetrics` 提供唯一生产实例。

Monitor 的 SingleMetric 与 MultiDashboard 是两个真实消费路径，经 `MetricService` 保留原公开 API、图标及单位约定。同一个资源指标的并发采集和 TTL 内结果跨两种视图共享；每个视图有独立 owner 与生命周期取消源，Dispose 释放自己的在途等待，MultiDashboard 停止向已释放的模型发布结果。截图几何不是资源指标，本切片没有改变 Shortcuts 的系统操作行为。

## 验证与边界

`WeatherSharedChecks` 是纯 Runtime 夹具：覆盖规范化键/单位隔离、请求单飞、TTL 与稳定完成元数据、过期旧 lease 和新 lease 共存、取消隔离、失败重试、系统指标共享及活动 owner 归零。

`ProviderIntegrationChecks` 直接调用真实 Weather/Fixed 兼容入口及 Monitor adapter，使用冻结 JSON 与可控任务验证完整预报/AQI/地理编码委托、查询转义/count、无重复网络轮次、可变模型隔离、城市搜索取消、组件 Dispose 隔离和资源指标共享。它不访问外网、不采集真实 WMI，也不创建系统截图；真机代理/离线恢复、原生 WMI 性能和长期运行内存观测仍需人工验收。本切片不改变现有小时/秒级调度策略。

2026-10-16 验证结果：`WeatherSharedChecks` 21 项通过，`ProviderIntegrationChecks` 15 项通过；两者均以 Release、`--no-restore` 纳入统一门禁，完整 `scripts/verify.ps1 -NoRestore` 18/18 通过、退出码 0。详见 [P3–P5 接线验收报告](P3-P5接线验收报告_20261016.md)。
