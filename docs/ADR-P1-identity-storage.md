# ADR-P1：稳定实例身份与单文档原子保存

状态：已实施最小切片（2026-10-16）

`WidgetLayout` 增加持久 `InstanceId` 和会话 `Generation`。旧布局缺少 ID 时，`ScreensLayout.EnsureWidgetIdentities` 为每条记录生成一次 GUID；`LayoutProvider.Get` 将迁移结果立即写回，因此重复加载不会重新分配。已有实例跨屏或几何变化仍由 ID 定位，复制实例由工厂生成新 ID；旧调用仍可使用原有构造参数。

`JsonParser<T>.Save` 采用单写者锁，在目标文件同目录创建临时文件，写入并 flush 后替换目标。只有替换成功才更新缓存并发布 `DataChanged`；序列化或提交失败会保留旧缓存、旧文件和可重试状态。`DataChanging` 仍在提交前触发，现有 `IDataProvider<T>` API 不变。

本切片覆盖单文档提交。布局、配置档与指针等多文件一致性仍需后续操作日志/提交指针方案；文件系统断电持久性和旧程序与新程序同时写同一数据目录也未在本 ADR 中承诺。
