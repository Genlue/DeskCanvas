# DeskCanvas v3.5.0 更新日志

> 一个功能版本：新增**计算器**与**天气·四日预报**两个组件；**液态玻璃 V2** 的折射系数收敛到 GPU 着色器与 CPU 渲染器共享的单一求解器，并给折射带加「圆角封顶」，修掉大卡片四角的折痕与 90° 尖点；桌面抓取补上**屏幕 BitBlt 三级回退**，`PrintWindow` 被 UIPI 拒绝时不再直接掉到静态壁纸文件；二级面板把「点击外部关闭」统一收进基类；修掉全局滚动条**悬停时突然冒出的「大黑框」**；网格模式全链路收敛到自定义网格，移除自由摆放与锁定尺寸。主程序版本升至 3.5.0，全解决方案 19 个项目编译 0 警告 0 错误。

---

### 🧮 1. 计算器组件（全新）

* **三套面板一屏切换**：科学 / 程序员 / 单位换算三个页签 + 历史与内存侧栏同住在二级面板（560×560，从组件锚点缩放展开）。
* **自研递归下降求值器**：支持隐式乘法、**上下文百分号**（`100+20%` → `120`）、`√` `²` `³` `!` `^`、科学记数法，常数 `π` / `e` / `ans`，函数 `sin/cos/tan/asin/acos/atan/sqrt/cbrt/ln/log/abs/exp/fact`，DEG / RAD 可切。
* **程序员模式**：独立的 64 位整数求值器，`|` `^` `&` `<<` `>>` `~` 与四则取余，按 2 / 8 / 10 / 16 进制解析与格式化。
* **单位换算**：内置数据存储、长度、重量、温度、面积、时间六类；温度用自定义基准映射处理零点偏移。
* **跨实例共享状态**：`CalculatorStore` 单例把历史 / 内存 / 当前结果在桌面组件与二级面板之间同步，落盘到 `%LocalAppData%\DeskCanvas\Calculator\state.json`；历史默认 30 条（5–200 可调）、内存槽 20 个，含 `MC / MR / M+ / M- / MS` 语义，`StateChanged` 统一经 UI 线程派发。
* **按尺寸自适应的小组件**：2x2 / 4x2 / 4x4 三套布局；最小区间按钮按宽度 155 / 130 / 115 渐进显隐，二级面板入口最后让位。
* **主题适配**：新增 5 个 `Calculator*` 资源键（强调色 / 卡片底 / 数字键 / 功能键 / 运算符键），四个主题字典各自给出取值——Colorful 档直接采用 Apple 计算器配色（亮色 `#FF9500` / 暗色 `#FF9F0A`），单色与背景色档沿用系统强调色 + 半透明灰底。

---

### 🌤️ 2. 天气·四日预报组件（全新）

* **156×156 定尺布局**：上半城市 / 当前温度 / 今日高低温，下半 `UniformGrid` 四行列出未来四天（星期 / 图标 / 低温 / 高温）。
* **方格跨距契约**：按 `dailyList.Skip(1).Take(4)` 取未来四天；`AllowedBaseSpans` 仅 `(2,2)`，预设 `2x2 / 3x3 / 4x4`；`IsAllowedSpanOf` 要求横竖相等，`SnapSpanOf` 取两轴均值四舍五入成 `(k,k)` —— 只允许方格子。
* **宿主新增方格晶格判定**：`Widget.IsFixed2x2SquareWidget`，`OnFixedSpan` 据此决定按对角还是按行吸附；`Widget.axaml` 为 2x2 基准的固定方格组件补上三档右键尺寸菜单。
* **中文星期名独立处理**：中文环境直接输出「周日…周六」，不走 `AbbreviatedDayNames`，避免本地化缩写不一致；初始占位填 `--°`。
* **接入既有设施**：组件栈可选清单纳入该组件，彩色模式卡片背景判定与 `Forecast` 共用 `WeatherCardBackground`。

---

### 💧 3. 液态玻璃 V2：折射系数单一真相源 + 圆角封顶

* **消除双份系数**：新增 `LiquidGlassV2Lens.Compute(refraction, minSide, radius)`，GPU 着色器与 CPU 渲染器从此共用同一个求解器（此前两处重复块会、且已经发生漂移）。
* **圆角封顶**：新增常量 `CornerBandCapFactor = 0.8`（折射带以 `0.8 × 圆角` 封顶）与 `CornerBandCapTransitionFrac = 0.1`（C1 平滑过渡半宽）。库原始关系 `band = amount / 2` 按短边缩放，大卡片上折射带会吞掉圆角，导致四角出现折痕与 90° 尖点；封顶把折射带与位移一起收窄，使「膨胀弧心」（唯一无极限点，深度 1.5×半径）恒落在零位移区。只作用于派生渲染系数，**不回写** `Refraction`。
* **着色器与渲染器同步**：新增 `gradRadius` uniform 由求解器下发（不再本地 `min(radius * 1.5, …)`）；圆角扇形判定由 `>` 改 `>=`、新增 `sgnCoord` 替代 SkSL `sign()`（轴线上不再二次跳变）；归一化补 `1e-5` 兜底防方卡中线 NaN；零长度梯度直接置 0 而非除零。
* **代价已量化**：352px 卡片、折射 100 时边缘位移由 140.8px 降到 38.4px。

---

### 🖥️ 4. 桌面抓取：三级采样回退

* **屏幕 BitBlt 回退**：候选窗口的 `PrintWindow` 全部以 `ERROR_ACCESS_DENIED(5)` 失败时（shell / 壁纸引擎运行在更高完整性级别，UIPI 拒绝），自动切换为截取合成桌面，不再直接掉到「壁纸文件 + 注册表」静态路径。
* **自我排除**：进入回退时对本进程所有窗口设 `WDA_EXCLUDEFROMCAPTURE`，保证玻璃不会采样到自己；每 250ms 节流枚举一次，回退结束时恢复 `WDA_NONE`。
* **候选链补全**：新增对 `Progman` 子 `WorkerW` 的扫描（`EnumChildWindows`），修掉 Wallpaper Engine 之类把壁纸宿主挂在子 WorkerW 上时不可见、只能退到 `Progman` 的问题；候选优先级重排。
* **源切换不再丢首帧**：新增 `ResetAcceptanceOnSourceChange()`，切换采样源时清空时间验收网格，否则首帧会被当成叠加层跳变丢弃。
* **可观测性**：新增 `TraceCapture` 去重节流诊断（同 key 5s 内不重复，新 key 立即写），各 Win32 失败点记录 `GetLastWin32Error()` 与阶段 / HWND；多个 P/Invoke 补 `SetLastError = true`。
* **诊断开关**：新增环境变量 `DESKCANVAS_GLASS_CAPTURE`（`screen` 强制屏幕路径 / `window` 禁用自动回退）。
* **对外状态**：新增 `ScreenFallbackActive` 与 `CaptureSourceDescription`，可区分「窗口抓取 / 屏幕回退 / 壁纸文件」三级来源。

---

### 🪟 5. 二级面板：点击外部关闭统一收进基类

* `SecondaryPanelWindow` 新增全局低级鼠标钩子（`WH_MOUSE_LL`），面板加载即安装、`Closing / Closed / Close()` 即卸载：左 / 右 / 中键按下，且落点在窗口 `GetWindowRect` 之外、并且不在任何已打开 `Popup` 内时关闭面板；加载后 150ms 优雅期内忽略。
* **失焦不再立刻关闭**，而是抢回焦点，避免「点外部 → 先失焦 → 面板已关」的竞态；
* `BigFolderPopupWindow` 删掉自带的整套鼠标钩子（-133 行）改走基类，天气面板移除自写的拖动处理。剪贴板 / 翻译 / 笔记 / 清单 / 天气 / 日历 / 计算器等所有派生面板自动获得该行为。

---

### 🧹 6. 滚动条：修掉悬停时突然冒出的「大黑框」

* **根因**：鼠标进入滚动条区域会让 `ScrollBar.IsExpanded` 变成 true，而 Fluent 控制主题的展开态触发器会一次性把滑块 `RenderTransform` 清成 `none`（涨到 16px 全宽）、把轨道不透明度抬到 1 并填深色、根节点铺一层深色、上下翻页箭头按钮置为可见 —— 深色模式下叠起来就是那个突兀的大黑框。
* **修复**：新增 `Styles/ScrollBar.axaml`，以 `BasedOn` 叠加在 Fluent 原生主题之上，只覆盖展开态（根节点背景透明 / 轨道不透明度 0 / 翻页按钮保持不可见 / 滑块恢复收起态的缩放与配色，横竖各一条）。控制主题的样式帧优先级高于普通 App 级样式，必须走 ControlTheme 这条路才能压住。
* 收起态的细条观感、隐藏与翻页机制全部沿用原生；`App.axaml` 只增加一次资源合并与一条 `Theme` 绑定。

---

### 🧭 7. 网格收敛：移除自由摆放与锁定尺寸

* `GridMode.Free` 标记废弃，配置加载时若 `GridMode != Manual` 或 `LockSize` 为真，则强制迁移为 `GridMode.Manual` / `LockSize = false`。
* 删除自由模式的全部路径：`GridService` 的 Free 分支与不再使用的辅助方法、设置页的「网格模式」下拉与「锁定尺寸」开关、组件的自由模式缩放手柄与自定义尺寸对话框（`CustomSizeDialog` 整文件移除）、组件库的 `FindFreeSpot`，以及「内容驱动最小窗口尺寸」闭环。
* 尺寸恒为 `span × cell`、位置恒贴网格并 Clamp，拖拽 / 跨屏 / 恢复路径统一走 Manual 逻辑。

---

### 🧪 8. 验证与版本

* `tests/LiquidGlassChecks` 新增 `v2-glass` 独立入口，把「好不好看」变成可断言数字：方向场有限性与弧心线两侧极限一致、弧心掩码、系数关系（`amount = 2×band`、`band ≤ 0.8×radius`、随滑杆与短边单调、DPI 线性、退化几何有限）、`SmoothCap` 分段与切线斜率、着色器确实取自共享系数（含 `gradRadius`、不再含 `radius * 1.5`）、352px 4×4 实渲（中心不透明 + 角裁剪 + 折射 rimDelta > 1.5）。
* `tests/WidgetAdaptiveChecks` 新增计算器断言组：引擎数值（百分比 / 三角函数 / 幂 / 程序化位运算）与三尺寸布局档位，并断言最小档仍保留二级面板入口。
* `tests/ScrollBarHoverChecks` 新增：以 `AllowAutoHide=false` 强制展开态，断言轨道 / 根节点 / 箭头按钮 / 滑块全部与收起态一致，并用 SkiaSharp 做「悬停 vs 不悬停」逐像素比对 —— 明暗 × 横竖四组均为 **0 像素差异**，另用原生主题作对照组证明该检查确有灵敏度。
* **版本同步**：
  * 主程序：`3.4.3 → 3.5.0`（`src/DeskCanvas/AssemblyInfo.cs` + `installer/Package.wxs` 同步）；
  * Tools：`1.0.3 → 1.1.0`（新增计算器组件）；
  * Weather：`1.4.6 → 1.5.0`（新增四日预报组件）；
  * 全解决方案 19 个项目编译 0 警告 0 错误。
