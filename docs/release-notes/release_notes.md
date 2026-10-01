## uWidgetsPlus v1.9.3 更新日志

### 🐞 修复：高级设置修改网格数值导致崩溃（`InvalidOperationException`）
- **现象**：新建配置方案后，在「设置 → 高级 → 网格」调整列数 / 行数 / 格子边长 / 网格位置时，弹出 `System.InvalidOperationException: Cannot change source while update is in progress.`。
- **根因**：属性通知重入。`AdvancedViewModel.RaiseAllProperties()` 会在一次**尚未结束的双向绑定写入内部**被同步调用——数值框写入 → `SaveGrid()` → 保存设置 → 每个桌面组件 `AfterMove()` 回写 layout → `LayoutProvider.DataChanged` → 再次批量刷新属性；该批量刷新会把 `ScreenTargets`（每次调用都新建 `List`）抛给屏幕选择下拉框，Avalonia 在其选择项更新尚未结束时被要求替换 `ItemsSource`，触发 `SelectionModel` 的重入保护异常。
- **修复**：批量属性通知改为**合并 + 延迟投递**（`Dispatcher.UIThread.Post(..., DispatcherPriority.Background)`），确保永不在绑定/布局过程内部执行；并加入双重重入标志。附带收益：原本 N 个桌面组件触发 N 次的通知风暴被合并为一次。

### 🎨 修复：网格编辑器（GridEditor）参数面板与按钮看不清
- **面板亮色化**：参数面板由硬编码深色 `#EE202028` 改为亮色磨砂白 `#F7FFFFFF`（含描边与投影），标题、标签、分隔线全部显式配色，浅色主题下不再出现「深色画在深色上」。
- **按钮高对比度**：新增窗口级按钮样式，普通按钮白底 `#FFFFFF` + 近黑字 `#1C1C1E` + 灰描边，并定义 hover / pressed 三态；**「保存并关闭」改为亮蓝主按钮**（`#007AFF` 白字），不会再被误认为空白块。窗口固定 `Light` 主题变体，面板内 `NumericUpDown` 等模板部件配色统一。
- **补上丢失的按钮文字**：面板底部第二个按钮此前在 XAML 中**完全没有 `Content`**，一直渲染为空白色块，现补为「关闭 / Close」。
- **方向键优化**：← → ↑ ↓ 由按内容撑开的小方块改为 4 个等宽按钮，更好点击。
- **✕ 关闭按钮**：显式白色字 + 半透明白描边（此前继承主题前景色，浅色主题下不可见）。

### 🖥️ 修复：网格编辑器全屏定位与面板可达性
- **DPI 修正**：窗口尺寸改用窗口自身 `RenderScaling`（屏幕模型仅作回退），避免远程桌面 / 混合 DPI 场景下内容 DIP 宽度大于物理屏幕，导致右对齐面板与右上角 ✕ 被推出可见区域。
- **面板默认可见**：参数面板不再需要「精确右键点到网格区域」才出现，打开编辑器即显示，右键**任意位置**也能唤出。
