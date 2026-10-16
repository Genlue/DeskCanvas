# ADR-P4：主题快照与设置提交切片

状态：生产设置页持久化接线与独立回归已完成；真机验收待执行（2026-10-16）

`ThemeSnapshot` 以 revision 和 `ThemeImpact` 表达不可变主题结果。`DraftSettingsStore<T>` 将 draft、expectedRevision 校验、提交成功后的 revision 发布与冲突结果集中到一个无头协议；相同值不增加 revision，也不重复通知，冲突保留当前快照供编辑器重载或合并。

Clock 与 Countdown 通过各自适配器使用同一提交协议，分别声明 Palette 和 Material 影响域。

`PersistentSettingsSession<T>` 将真实 provider 的读取、草稿、expectedRevision、外部基线比较和持久化提交接在一起：失败不推进 revision、不发送 Committed，保留草稿；错误使用稳定的 `save-failed` / `revision-conflict` 代码，设置页显示本地化文案。重试使用原草稿，重新加载才显式放弃草稿。

外观页全部 19 个 `LabeledSlider`（透明度、旧/新玻璃光学、文字阴影强度）使用真实 `ValueCommitted`：拖动和数值输入只 stage，拖动完成、Enter 或失焦才提交，同一次输入的重复完成事件不重复写盘。滑块共享独立于即时控件的 editor session，getter 返回草稿预览，因此另一个即时控件或另一窗口保存不会覆盖未提交的滑块值，而会在提交时报告冲突。重新加载显式丢弃全部滑块草稿并刷新显示。其他主题字段经 `AppearanceSettingsSession` 调用真实设置 provider，字体也进入同一保存与 per-surface 快照路径；失败主题草稿保留，可重试。字体列表筛选清空 `SelectedItem` 时忽略空值，避免中文输入触发写入空字体。

ThemeService 由 DataChanging 改为 DataChanged 后更新资源；LocaleService 同样在持久化完成后发布语言。FocusThemeCoordinator 原已监听 DataChanged。单文档 `JsonParser` 继续使用同卷临时文件、flush 和原子替换；替换失败时缓存和已发布资源仍为旧值。

验证：`ThemeSettingsChecks` 14 项检查覆盖 revision、重复值、持久化失败、重试、外部冲突与 reload；独立 `SettingsIntegrationChecks` 使用真实 AppSettings、真实 JsonParser、临时目录和真实外观页。36 项无头检查覆盖透明度、旧/新玻璃和阴影强度输入提交时一次保存、目标文件锁定导致的真实原子替换失败、磁盘/缓存/主题资源/语言均不发布失败结果、临时文件清理、保留草稿后重试、同页与跨编辑器冲突、字体保存、Avalonia IME client 预编辑/中文提交/退格，以及两个真实 Window 的局部透明度与强调色资源隔离。`dotnet run --project tests/SettingsIntegrationChecks -c Release --no-restore` 通过 36/36，`dotnet run --project tests/ThemeSettingsChecks -c Debug --no-restore` 通过 14/14。

边界：两个窗口的检查验证资源作用域 API 隔离，不代表当前 uniform focus-theme 策略变成每屏独立主题。物理输入法候选窗、真实多屏切换与焦点、GPU/性能/双 4K 压力、旧组件包兼容矩阵均未执行，不能宣称通过。
