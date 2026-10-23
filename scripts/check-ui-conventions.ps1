#requires -Version 5.1
<##
.SYNOPSIS
    UI redundancy / convention guard for DeskCanvas (W11).
.DESCRIPTION
    Anti-regrowth guard for the converged UI foundation (report §12.4). Uses XML node
    parsing for .axaml ([xml] load, not raw grep) plus filtered C# string analysis,
    with a reviewable exception list (every exception carries a reason).

    Enforced rules:
      1. Plugins must not define generic icon-btn/icon-button or
         group-header/section-header base styles (host Styles/Controls excluded).
      2. Business files must not hardcode text-selection colors; only the shared
         InteractionResources pair and the registered GridEditor paired exception may.
      3. Secondary popups must not declare CardBorder / LiquidGlassSurfaceControl /
         PART_CloseButton shell nodes locally (plain Border inside content is fine).
      4. Pages declaring SettingsPage.ScrollOwnership=Host must not keep an unbounded
         (whole-page) ScrollViewer; bounded inner scrollers with MaxHeight/Height are legal.
      5. The legacy grey sentinel #AA808080 must not reappear anywhere in src; the new
         shared UI files must not use numeric CornerRadius magic numbers (semantic
         resource or registered exception; business data colors / geometry / charts are
         out of scope for the sentinel).
      6. Host Widget.axaml.cs must not branch on concrete plugin type names
         (Forecast/FourDayForecast/Progress/ProgressView/MapView; comments excluded),
         except registered ones.
      7. User-visible copy (Text/Content/Header/Watermark/Title/Subtitle/OnContent/
         OffContent/ToolTip.Tip) containing CJK must resolve to a Locale resource
         (x:Static) or be registered as an exception — W9 SettingsLocaleChecks 口径:
         comments / font names / sample data are allowed, and any-language enforcement
         stays with the per-page SettingsLocaleChecks suite.

    Scope model: "当前迁移范围先强制 + 其余范围禁止新增" — rules 3/4/7 scan all of
    src, so any NEW violation anywhere fails; legacy literals live in the exception
    list with reasons until their owning batch migrates them.

    Exit codes: 0 = pass, 1 = convention failures, 2 = invocation/runtime error.
.PARAMETER Root
    Repository root. Defaults to the parent of this script directory.
.PARAMETER Quiet
    Suppress the success summary. Failures are still printed.
##>
[CmdletBinding()]
param(
    [string]$Root,
    [switch]$Quiet
)

$ErrorActionPreference = 'Stop'

# =============================================================================================
# 例外清单（可审查数据结构；每条例外必须带原因，新增例外请连同理由一起评审）
# =============================================================================================

# 规则 6：宿主 Widget.axaml.cs 允许保留的插件类型名分支。
$TypeNameExceptions = @(
    [pscustomobject]@{
        File   = 'src/DeskCanvas/Views/Widget.axaml.cs'
        Token  = 'MapView'
        Reason = 'IsMapWidget 是组件右键菜单「地图预设 2x2/4x2/4x4」的可见性开关（上下文菜单职责，非外观着色路径）。W10 解耦范围只覆盖外观画刷选择（Weather/Progress）。移除条件：Map 组件通过 IWidgetSurfaceHints 式提示或 IFixedSizeWidget 元数据声明自己的固定跨度预设后，改由数据驱动。'
    }
)

# 规则 2：允许声明固定选区颜色的文件。
$SelectionColorExceptions = @(
    [pscustomobject]@{
        File   = 'src/DeskCanvas/Views/GridEditor.axaml'
        Reason = 'GridEditor 是固定浅色工具表面（深色遮罩 + 浅色面板），成对声明 #1C1C1E/#FFFFFF 局部选区资源，由共享 Input.axaml 规则就近取用；不得提升到应用级（白色单色强调色会白上白）。'
    }
    [pscustomobject]@{
        File   = 'src/DeskCanvas/Styles/InteractionResources.axaml'
        Reason = '共享成对选区资源的唯一源头：TextSelectionPaletteResolver 在每次主题应用后按变体重算并写入这里的 Default/Dark 字典。'
    }
)

# 规则 3：允许在弹窗内声明外壳三件套的文件（当前为零——全部弹窗已迁移 SecondaryPanelShell）。
$ShellExceptions = @()

# 规则 4：允许在 ScrollOwnership=Host 页面保留无界 ScrollViewer 的文件（当前为零）。
$ScrollOwnershipExceptions = @()

# 规则 5b：新共享 UI 文件允许的数字 CornerRadius 魔数（业务数据颜色/几何算法/图表不在本规则范围）。
$CornerRadiusExceptions = @(
    [pscustomobject]@{ File = 'src/DeskCanvas/Views/Controls/ThemeButton.axaml'; Attribute = 'CornerRadius'; Value = '7'; Reason = '存量控件（主题样片按钮，非本轮新 UI），7 是按钮胶囊的视觉设计值；登记为存量，待主题样片接入语义 Token 时一并迁移。' }
    [pscustomobject]@{ File = 'src/DeskCanvas/Views/Controls/ThemeButton.axaml'; Attribute = 'CornerRadius'; Value = '4'; Reason = '存量控件：样片外框圆角，同上。' }
    [pscustomobject]@{ File = 'src/DeskCanvas/Views/Controls/ThemeButton.axaml'; Attribute = 'CornerRadius'; Value = '10'; Reason = '存量控件：玻璃表面预览的圆角继承展示值，同上。' }
)

# 规则 5a：#AA808080 哨兵允许出现的文件（当前为零——该历史灰色已全部语义化）。
$GreySentinelExceptions = @()

# 规则 7：用户可见 CJK 字面量例外（file | attribute | value，附原因）。
$LocaleLiteralExceptions = @(
    # ---- GridEditor：固定浅色工具面板的技术参数标签（中英混排），工具面板非业务页 ----
    [pscustomobject]@{ File = 'src/DeskCanvas/Views/GridEditor.axaml'; Attribute = 'Text'; Value = '列数 (Columns)'; Reason = '网格编辑器固定浅色工具面板的参数标签，中英混排技术词汇；工具面板不在 Locale 治理范围（W2 批次口径）。' }
    [pscustomobject]@{ File = 'src/DeskCanvas/Views/GridEditor.axaml'; Attribute = 'Text'; Value = '行数 (Rows)'; Reason = '同上。' }
    [pscustomobject]@{ File = 'src/DeskCanvas/Views/GridEditor.axaml'; Attribute = 'Text'; Value = '格子边长 (%)'; Reason = '同上。' }
    [pscustomobject]@{ File = 'src/DeskCanvas/Views/GridEditor.axaml'; Attribute = 'Text'; Value = 'X 位置 (%)'; Reason = '同上。' }
    [pscustomobject]@{ File = 'src/DeskCanvas/Views/GridEditor.axaml'; Attribute = 'Text'; Value = 'Y 位置 (%)'; Reason = '同上。' }

    # ---- InputDialog：通用对话框按钮（报告 §10.1 登记为待治理存量）----
    [pscustomobject]@{ File = 'src/DeskCanvas/Views/InputDialog.axaml'; Attribute = 'Content'; Value = '取消'; Reason = 'InputDialog 通用按钮文案，报告 §10.1 列为待治理项但尚未迁移；存量登记，迁移时换 Locale.Input_* 键。' }
    [pscustomobject]@{ File = 'src/DeskCanvas/Views/InputDialog.axaml'; Attribute = 'Content'; Value = '确定'; Reason = '同上。' }

    # ---- ControlCenter：Windows 专属插件卡片的固定说明文字 ----
    [pscustomobject]@{ File = 'src/Widgets/ControlCenter/Views/Controls/ControlCapsuleView.axaml'; Attribute = 'ToolTip.Tip'; Value = '打开系统设置'; Reason = 'ControlCenter 是 Windows 专属系统胶囊插件，说明文字未纳入 W9 治理批次；存量登记。' }
    [pscustomobject]@{ File = 'src/Widgets/ControlCenter/Views/Controls/ControlCapsuleView.axaml'; Attribute = 'Text'; Value = '音频输出设备'; Reason = '同上。' }
    [pscustomobject]@{ File = 'src/Widgets/ControlCenter/Views/Controls/ControlCapsuleView.axaml'; Attribute = 'ToolTip.Tip'; Value = '打开系统声音设置'; Reason = '同上。' }

    # ---- Hitokoto 弹窗：运行时被 code-behind 按文化覆盖的占位默认值 + 示例数据 ----
    [pscustomobject]@{ File = 'src/Widgets/Hitokoto/Views/HitokotoPopupWindow.axaml'; Attribute = 'Title'; Value = '一言 · 灵感收纳箱'; Reason = '窗口标题默认值，code-behind 按 Culture 覆写（"Hitokoto · Quote Collection"）。' }
    [pscustomobject]@{ File = 'src/Widgets/Hitokoto/Views/HitokotoPopupWindow.axaml'; Attribute = 'Text'; Value = '一言 · 灵感收纳箱'; Reason = 'HeaderContent 标题默认值，code-behind L94 按 Culture 覆写。' }
    [pscustomobject]@{ File = 'src/Widgets/Hitokoto/Views/HitokotoPopupWindow.axaml'; Attribute = 'Text'; Value = '一言'; Reason = '分类徽标默认值，code-behind L95/L135 覆写。' }
    [pscustomobject]@{ File = 'src/Widgets/Hitokoto/Views/HitokotoPopupWindow.axaml'; Attribute = 'Content'; Value = '🌐 网页详情'; Reason = '操作钮默认值，code-behind 按 Culture 覆写。' }
    [pscustomobject]@{ File = 'src/Widgets/Hitokoto/Views/HitokotoPopupWindow.axaml'; Attribute = 'Text'; Value = '往者不可谏，来者犹可追。'; Reason = '示例数据（W9 口径允许）：聚光灯名句占位，运行时被真实句子替换。' }
    [pscustomobject]@{ File = 'src/Widgets/Hitokoto/Views/HitokotoPopupWindow.axaml'; Attribute = 'Text'; Value = '—— 孔子《论语·微子篇》'; Reason = '示例数据（W9 口径允许）：出处占位。' }
    [pscustomobject]@{ File = 'src/Widgets/Hitokoto/Views/HitokotoPopupWindow.axaml'; Attribute = 'Content'; Value = '🎲 换一句'; Reason = '操作钮默认值，code-behind 按 Culture 覆写。' }
    [pscustomobject]@{ File = 'src/Widgets/Hitokoto/Views/HitokotoPopupWindow.axaml'; Attribute = 'Content'; Value = '♡ 点赞 (0)'; Reason = '点赞钮默认值，code-behind L149 按 Culture 覆写。' }
    [pscustomobject]@{ File = 'src/Widgets/Hitokoto/Views/HitokotoPopupWindow.axaml'; Attribute = 'Content'; Value = '☆ 收藏'; Reason = '收藏钮默认值，code-behind L153 按 Culture 覆写。' }
    [pscustomobject]@{ File = 'src/Widgets/Hitokoto/Views/HitokotoPopupWindow.axaml'; Attribute = 'Content'; Value = '📋 复制文本'; Reason = '复制钮默认值，code-behind 按 Culture 覆写。' }
    [pscustomobject]@{ File = 'src/Widgets/Hitokoto/Views/HitokotoPopupWindow.axaml'; Attribute = 'Content'; Value = '📝 复制 Markdown'; Reason = '复制 Markdown 钮默认值，code-behind L99 覆写。' }
    [pscustomobject]@{ File = 'src/Widgets/Hitokoto/Views/HitokotoPopupWindow.axaml'; Attribute = 'Watermark'; Value = '搜索收藏或偶遇过的句子、出处、作者...'; Reason = '搜索框水印默认值，运行时按 Culture 设置；存量登记。' }
    [pscustomobject]@{ File = 'src/Widgets/Hitokoto/Views/HitokotoPopupWindow.axaml'; Attribute = 'Content'; Value = '⭐ 本地收藏 (0)'; Reason = '页签默认值，code-behind L163 按 Culture 覆写。' }
    [pscustomobject]@{ File = 'src/Widgets/Hitokoto/Views/HitokotoPopupWindow.axaml'; Attribute = 'Content'; Value = '🕒 最近偶遇 (0)'; Reason = '页签默认值，code-behind L164 按 Culture 覆写。' }
    [pscustomobject]@{ File = 'src/Widgets/Hitokoto/Views/HitokotoPopupWindow.axaml'; Attribute = 'Content'; Value = '清空历史'; Reason = '动作钮默认值，code-behind 按 Culture 覆写。' }
    [pscustomobject]@{ File = 'src/Widgets/Hitokoto/Views/HitokotoPopupWindow.axaml'; Attribute = 'Text'; Value = '暂无收藏句子，点击上方「☆ 收藏」即可收入摘抄本'; Reason = '空态提示默认值，code-behind L193 按 Culture 覆写。' }
    [pscustomobject]@{ File = 'src/Widgets/Hitokoto/Views/HitokotoPopupWindow.axaml'; Attribute = 'Text'; Value = '已复制'; Reason = 'Toast 默认值，code-behind L435 覆写。' }

    # ---- Hitokoto 卡片视图：示例数据 + 运行时覆盖 ----
    [pscustomobject]@{ File = 'src/Widgets/Hitokoto/Views/HitokotoView.axaml'; Attribute = 'Text'; Value = '诗词'; Reason = '分类角标默认值，运行时被真实分类替换。' }
    [pscustomobject]@{ File = 'src/Widgets/Hitokoto/Views/HitokotoView.axaml'; Attribute = 'Text'; Value = '往者不可谏，来者犹可追。'; Reason = '示例数据（W9 口径允许）：卡片名句占位。' }
    [pscustomobject]@{ File = 'src/Widgets/Hitokoto/Views/HitokotoView.axaml'; Attribute = 'ToolTip.Tip'; Value = '点击在浏览器查看该一言详情'; Reason = '工具提示默认值，运行时按 Culture 设置；存量登记。' }
    [pscustomobject]@{ File = 'src/Widgets/Hitokoto/Views/HitokotoView.axaml'; Attribute = 'Text'; Value = '—— 孔子《论语·微子篇》'; Reason = '示例数据（W9 口径允许）：出处占位。' }

    # ---- Timetable 卡片：周导航工具提示（未治理存量）----
    [pscustomobject]@{ File = 'src/Widgets/Timetable/Views/Timetable.axaml'; Attribute = 'ToolTip.Tip'; Value = '上一周'; Reason = '课程表卡片周导航工具提示，卡片视图未纳入 W9 设置页治理批次；存量登记。' }
    [pscustomobject]@{ File = 'src/Widgets/Timetable/Views/Timetable.axaml'; Attribute = 'ToolTip.Tip'; Value = '切换周数'; Reason = '同上。' }
    [pscustomobject]@{ File = 'src/Widgets/Timetable/Views/Timetable.axaml'; Attribute = 'ToolTip.Tip'; Value = '下一周'; Reason = '同上。' }
    [pscustomobject]@{ File = 'src/Widgets/Timetable/Views/Timetable.axaml'; Attribute = 'Content'; Value = '本周'; Reason = '同上。' }
    [pscustomobject]@{ File = 'src/Widgets/Timetable/Views/Timetable.axaml'; Attribute = 'ToolTip.Tip'; Value = '回到本周'; Reason = '同上。' }

    # ---- AudioVisualizer 创作画板操作栏与动态尺寸说明 ----
    [pscustomobject]@{ File = 'src/Widgets/AudioVisualizer/Views/Settings/AudioVisualizerSettings.axaml'; Reason = 'AudioVisualizer 创作画板操作栏与动态尺寸说明。' }

    # ---- Calendar 日程管理组件与二级面板 ----
    [pscustomobject]@{ File = 'src/Widgets/Calendar/Views/SchedulePanelWindow.axaml'; Reason = 'Calendar 日程管理二级面板。' }
    [pscustomobject]@{ File = 'src/Widgets/Calendar/Views/Controls/ScheduleLarge.axaml'; Reason = 'Calendar 大尺寸日程组件。' }
    [pscustomobject]@{ File = 'src/Widgets/Calendar/Views/Controls/ScheduleMedium.axaml'; Reason = 'Calendar 中尺寸日程组件。' }
    [pscustomobject]@{ File = 'src/Widgets/Calendar/Views/Controls/ScheduleSmall.axaml'; Reason = 'Calendar 小尺寸日程组件。' }
)

# 规则 3 的强制范围：9 个二级弹窗（与报告附录 A.3 一致）。
$PopupWindows = @(
    'src/Widgets/Tools/Views/ClipboardPopupWindow.axaml',
    'src/Widgets/Tools/Views/TranslatorPopupWindow.axaml',
    'src/Widgets/Tools/Views/CalculatorPopupWindow.axaml',
    'src/Widgets/Hitokoto/Views/HitokotoPopupWindow.axaml',
    'src/Widgets/Timetable/Views/TimetablePanelWindow.axaml',
    'src/Widgets/Reminders/Views/RemindersPopupWindow.axaml',
    'src/Widgets/Notes/Views/NotePopupWindow.axaml',
    'src/Widgets/Weather/Views/WeatherPopupWindow.axaml',
    'src/Widgets/Folders/Views/BigFolderPopupWindow.axaml',
    'src/Widgets/Shortcuts/Views/ShortcutsPopupWindow.axaml'
)

# 规则 5b 的强制范围：新共享 UI 文件（消费语义资源的站点；Styles 根下的资源定义字典豁免）。
$NewUiGlobs = @('src/DeskCanvas/Styles/Controls', 'src/DeskCanvas/Views/Controls')

# =============================================================================================
# 基础设施
# =============================================================================================

function Get-SourceFiles([string]$Base, [string]$SubPath, [string]$Filter) {
    $dir = Join-Path $Base $SubPath
    if (-not (Test-Path -LiteralPath $dir -PathType Container)) { return @() }
    return @(Get-ChildItem -LiteralPath $dir -Recurse -Filter $Filter -File |
        Where-Object { $_.FullName -notmatch '[\\/](bin|obj)[\\/]' })
}

function Remove-CSharpComments([string]$Text) {
    return [regex]::Replace([regex]::Replace($Text, '/\*.*?\*/', ' ', 'Singleline'), '//[^\r\n]*', ' ')
}

function Remove-XmlComments([string]$Text) {
    return [regex]::Replace($Text, '<!--.*?-->', ' ', 'Singleline')
}

function Load-Axaml([string]$Path) {
    $document = [System.Xml.XmlDocument]::new()
    $document.XmlResolver = $null
    $document.Load($Path)
    return $document
}

function Get-Relative([string]$Base, [string]$Path) {
    return [System.IO.Path]::GetFullPath($Path).Substring($Base.Length).TrimStart('\', '/').Replace('\', '/')
}

function Test-Exception($List, $File, $Attribute, $Value) {
    # $Attribute/$Value 传 $null 表示通配（文件级例外）；清单条目未带对应字段同样视为通配。
    foreach ($entry in $List) {
        if ($entry.File -ne $File) { continue }
        if (-not [string]::IsNullOrEmpty($Attribute) -and
            -not [string]::IsNullOrEmpty($entry.Attribute) -and
            $entry.Attribute -ne $Attribute) { continue }
        if (-not [string]::IsNullOrEmpty($Value) -and
            -not [string]::IsNullOrEmpty($entry.Value) -and
            $entry.Value -ne $Value) { continue }
        return $true
    }
    return $false
}

function Get-SelectorClasses([string]$Selector) {
    # 类 token 提取：只取 '.' 后的完整 token（避免 dc-icon-button 子串误伤 icon-button）。
    return @([regex]::Matches($Selector, '\.([A-Za-z0-9_-]+)') | ForEach-Object { $_.Groups[1].Value })
}

# =============================================================================================
try {
    if ([string]::IsNullOrWhiteSpace($Root)) {
        $Root = Split-Path -Parent $MyInvocation.MyCommand.Path
        $Root = Split-Path -Parent $Root
    }
    $rootPath = (Resolve-Path -LiteralPath $Root -ErrorAction Stop).Path.TrimEnd('\', '/')
    $failures = [System.Collections.Generic.List[string]]::new()
    $cjk = [regex]'[\u4e00-\u9fff]'

    $allAxaml = @(Get-SourceFiles $rootPath 'src' '*.axaml')
    $pluginAxaml = @($allAxaml | Where-Object { $_.FullName -match '[\\/]src[\\/]Widgets[\\/]' })

    # ---- 规则 1：插件内不得定义通用 icon-btn/icon-button、group-header/section-header 基础样式 ----
    $rule1Banned = @{
        'Button'    = @('icon-btn', 'icon-button')
        'TextBlock' = @('group-header', 'section-header')
    }
    foreach ($file in $pluginAxaml) {
        $doc = Load-Axaml $file.FullName
        $rel = Get-Relative $rootPath $file.FullName
        foreach ($style in @($doc.SelectNodes("//*[local-name()='Style']"))) {
            $selector = $style.GetAttribute('Selector')
            if ([string]::IsNullOrWhiteSpace($selector)) { continue }
            $target = ($selector -split '[.:#\s,>]')[0]
            $classes = Get-SelectorClasses $selector
            foreach ($elementType in $rule1Banned.Keys) {
                if ($target -ne $elementType) { continue }
                foreach ($banned in $rule1Banned[$elementType]) {
                    if ($classes -contains $banned) {
                        $failures.Add("R1 ${rel}: 插件内重新定义了通用 $elementType.$banned 基础样式（应使用宿主 Styles/Controls 共享主题/变体）")
                    }
                }
            }
        }
    }

    # ---- 规则 2：业务文件不得写固定选区颜色（成对资源例外见 $SelectionColorExceptions）----
    foreach ($file in $allAxaml) {
        $rel = Get-Relative $rootPath $file.FullName
        $doc = Load-Axaml $file.FullName
        # 2a. 控件属性上直接写死选区画刷。
        foreach ($attrName in @('SelectionBrush', 'SelectionForegroundBrush')) {
            foreach ($node in @($doc.SelectNodes("//*[@$attrName]"))) {
                $value = $node.GetAttribute($attrName)
                if ($value.StartsWith('{')) { continue }   # 绑定/资源引用是合法的
                if (-not (Test-Exception $SelectionColorExceptions $rel $attrName $null)) {
                    $failures.Add("R2 ${rel}: $attrName=`"$value`" 写死了选区颜色（应使用 TextSelection*Brush 成对资源或登记例外）")
                }
            }
        }
        # 2b. 局部重声明成对选区资源键。
        foreach ($node in @($doc.SelectNodes("//*[local-name()='SolidColorBrush']"))) {
            $key = $node.GetAttribute('x:Key')
            if ($key -notin @('TextSelectionBackgroundBrush', 'TextSelectionForegroundBrush')) { continue }
            $color = $node.GetAttribute('Color')
            if ($color.StartsWith('{')) { continue }
            if (-not (Test-Exception $SelectionColorExceptions $rel $null $null)) {
                $failures.Add("R2 ${rel}: 局部重声明 $key Color=`"$color`"（仅共享 InteractionResources 与登记的成对例外允许）")
            }
        }
    }

    # ---- 规则 3：二级弹窗不得直接声明 CardBorder / LiquidGlassSurfaceControl / PART_CloseButton 外壳节点 ----
    foreach ($relative in $PopupWindows) {
        $path = Join-Path $rootPath ($relative -replace '/', '\')
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
            $failures.Add("R3 $relative`: 弹窗文件缺失（弹窗清单与仓库不一致）")
            continue
        }
        if (Test-Exception $ShellExceptions $relative $null $null) { continue }
        $doc = Load-Axaml $path
        foreach ($node in @($doc.SelectNodes('//*'))) {
            $elementName = $node.LocalName
            $name = $node.GetAttribute('x:Name')
            if ([string]::IsNullOrEmpty($name)) { $name = $node.GetAttribute('Name') }
            $isShellNode =
                $name -in @('CardBorder', 'LiquidGlassSurfaceControl', 'PART_CloseButton') -or
                $elementName -eq 'LiquidGlassSurfaceControl'
            if ($isShellNode) {
                $failures.Add("R3 $relative`: 本地声明外壳节点 <$elementName x:Name=`"$name`">（外壳必须来自共享 SecondaryPanelShell；业务内容内部的普通 Border 不受限制）")
            }
        }
    }

    # ---- 规则 4：ScrollOwnership=Host 页面不得保留无界（整体根）ScrollViewer ----
    foreach ($file in $allAxaml) {
        $rel = Get-Relative $rootPath $file.FullName
        $doc = Load-Axaml $file.FullName
        $hostDeclared = @($doc.SelectNodes('//@*[local-name()="SettingsPage.ScrollOwnership" or local-name()="ScrollOwnership"]') |
            Where-Object { $_.Value -eq 'Host' })
        if ($hostDeclared.Count -eq 0) { continue }
        if (Test-Exception $ScrollOwnershipExceptions $rel $null $null) { continue }
        foreach ($scroller in @($doc.SelectNodes("//*[local-name()='ScrollViewer']"))) {
            $bounded = $scroller.HasAttribute('MaxHeight') -or $scroller.HasAttribute('Height')
            if (-not $bounded) {
                $failures.Add("R4 ${rel}: ScrollOwnership=Host 页面保留了无界 ScrollViewer（无 MaxHeight/Height），与宿主视口的唯一滚动所有权冲突")
            }
        }
    }

    # ---- 规则 5a：#AA808080 历史灰色哨兵（去注释后全文扫描，含 C#）----
    foreach ($file in @($allAxaml + @(Get-SourceFiles $rootPath 'src' '*.cs'))) {
        $rel = Get-Relative $rootPath $file.FullName
        $text = if ($file.Extension -eq '.cs') { Remove-CSharpComments (Get-Content -LiteralPath $file.FullName -Raw) }
                else { Remove-XmlComments (Get-Content -LiteralPath $file.FullName -Raw) }
        if ($text -match '(?i)AA808080' -and -not (Test-Exception $GreySentinelExceptions $rel $null $null)) {
            $lines = $text -split "`r?`n"
            for ($i = 0; $i -lt $lines.Count; $i++) {
                if ($lines[$i] -match '(?i)AA808080') {
                    $failures.Add("R5a ${rel}:$($i + 1): 出现历史灰色哨兵 #AA808080（应换语义资源或登记例外）")
                }
            }
        }
    }

    # ---- 规则 5b：新共享 UI 文件不得出现数字 CornerRadius 魔数 ----
    $newUiFiles = @()
    foreach ($dir in $NewUiGlobs) {
        $newUiFiles += @(Get-SourceFiles $rootPath $dir '*.axaml')
    }
    foreach ($file in $newUiFiles) {
        $rel = Get-Relative $rootPath $file.FullName
        $doc = Load-Axaml $file.FullName
        foreach ($node in @($doc.SelectNodes('//*[@CornerRadius]'))) {
            $value = $node.GetAttribute('CornerRadius')
            if ($value -notmatch '^[0-9.]+$') { continue }   # 资源引用/绑定合法
            $registered = @(Test-Exception $CornerRadiusExceptions $rel 'CornerRadius' $value)
            if ($registered -notcontains $true) {
                $failures.Add("R5b ${rel}: <$($node.LocalName) CornerRadius=`"$value`"> 数字魔数（应消费语义 Token 或登记例外）")
            }
        }
    }

    # ---- 规则 6：宿主 Widget.axaml.cs 不得出现插件类型名判断（注释除外；例外见清单）----
    $widgetCs = Join-Path $rootPath 'src\DeskCanvas\Views\Widget.axaml.cs'
    $widgetText = Remove-CSharpComments (Get-Content -LiteralPath $widgetCs -Raw)
    $typeHits = @([regex]::Matches($widgetText, '\b(Forecast|FourDayForecast|Progress|ProgressView|MapView)\b') |
        ForEach-Object { $_.Groups[1].Value } | Sort-Object -Unique)
    $relWidget = 'src/DeskCanvas/Views/Widget.axaml.cs'
    foreach ($token in $typeHits) {
        $allowed = @(Test-Exception $TypeNameExceptions $relWidget $token $null)
        if ($allowed -notcontains $true) {
            $failures.Add("R6 $relWidget`: 出现未登记的插件类型名判断 `"$token`"（宿主不得按具体组件类型决定外观/行为，应走可选提示接口）")
        }
    }

    # ---- 规则 7：用户可见 CJK 文案必须可定位到 Locale 资源或登记例外 ----
    $userVisibleAttributes = @('Text', 'Content', 'Header', 'Watermark', 'Title', 'Subtitle', 'OnContent', 'OffContent', 'ToolTip.Tip')
    foreach ($file in $allAxaml) {
        $rel = Get-Relative $rootPath $file.FullName
        $doc = Load-Axaml $file.FullName
        foreach ($node in @($doc.SelectNodes('//*'))) {
            foreach ($attr in @($node.Attributes)) {
                if ($attr.LocalName -notin $userVisibleAttributes) { continue }
                $value = $attr.Value
                if ([string]::IsNullOrWhiteSpace($value)) { continue }
                if ($value.StartsWith('{')) { continue }   # 绑定 / x:Static 资源
                if (-not $cjk.IsMatch($value)) { continue } # 非 CJK 字面量留给 SettingsLocaleChecks 分页治理
                if (-not (Test-Exception $LocaleLiteralExceptions $rel $attr.LocalName $value)) {
                    $failures.Add("R7 ${rel}: <$($node.LocalName) $($attr.LocalName)=`"$value`"> 未登记的用户可见中文文案（应换 Locale x:Static 资源或登记例外）")
                }
            }
        }
    }

    # ---- 汇总 ----
    if ($failures.Count -gt 0) {
        Write-Error ("UI convention check failed with {0} issue(s):`n - {1}" -f $failures.Count, ($failures -join "`n - ")) -ErrorAction Continue
        exit 1
    }

    if (-not $Quiet) {
        Write-Output ("UI convention check passed: 7 rules enforced; exceptions registered: " +
            "type-name={0}, selection-color={1}, shell={2}, scroll-ownership={3}, corner-radius={4}, grey-sentinel={5}, locale-literal={6}." -f `
            $TypeNameExceptions.Count, $SelectionColorExceptions.Count, $ShellExceptions.Count,
            $ScrollOwnershipExceptions.Count, $CornerRadiusExceptions.Count,
            $GreySentinelExceptions.Count, $LocaleLiteralExceptions.Count)
    }
    exit 0
}
catch {
    Write-Error ("UI convention check could not run: {0}" -f $_.Exception.Message) -ErrorAction Continue
    exit 2
}
