using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using DeskCanvas.Views.Controls;

namespace UiFoundationChecks;

/// <summary>
/// W9a（文案治理 + 重点设置页）：Music / Storage / Hitokoto / Map 四个设置页。
/// <list type="bullet">
/// <item><b>文案资源化</b>：逐属性解析 XAML（不是整文件 grep），用户可见
/// Text/Content/Header/Watermark/Title/Subtitle/OnContent/OffContent/ToolTip.Tip 必须是
/// x:Static 资源或登记了原因的例外（图标符号、示例 URL）。</item>
/// <item><b>双资源齐备 + zh 逐字节冻结</b>：页面用到的每个键都必须同时存在于 Locale.resx 与
/// Locale.zh-Hans.resx，且 zh-Hans 值与下方冻结对照表（迁移前原文，逐字节捕获）完全一致——
/// 任何人改动中文渲染都会在这里被拦下。</item>
/// <item><b>code-behind 治理</b>：四个页面的 .axaml.cs 不再允许 IsZh/IsChinese 手工语言判断，
/// C# 引用的 Locale 键也必须在双 resx 中存在。</item>
/// <item><b>共享层契约</b>：页面声明 ScrollOwnership=Host 且无自身 ScrollViewer；分组标题是
/// dc-section-header、设置行是共享 Setting、滑块行走 LabeledSlider；本地不得再定义
/// icon-btn/icon-button/group-header/section-header 基础样式；固定 Width 只允许登记过的例外。</item>
/// </list>
/// Usage: dotnet run --project tests/UiFoundationChecks -c Release
/// </summary>
internal static partial class Program
{
    // ---- 配置表 ---------------------------------------------------------------------------

    /// <summary>用户可见属性：这些 XML 特性上的字面量字符串都是界面文案。</summary>
    private static readonly HashSet<string> UserVisibleAttributes = new(StringComparer.Ordinal)
    {
        "Text", "Content", "Header", "Watermark", "Title", "Subtitle", "OnContent", "OffContent", "ToolTip.Tip",
    };

    /// <summary>
    /// 带原因的字面量例外清单（页面, 特性, 值）。新增例外必须在这里登记原因，否则检查失败。
    /// </summary>
    private static readonly HashSet<(string Page, string Attribute, string Value)> LiteralExceptions =
        new()
        {
            // 图标符号（箭头/关闭叉），不是文案，也无需翻译
            ("Music", "Content", "↑"),
            ("Music", "Content", "↓"),
            ("Music", "Content", "✕"),
            // 官方接口地址作为 Watermark 示例（示例数据，§10.1 允许）
            ("Hitokoto", "Watermark", "https://v1.hitokoto.cn/"),
        };

    /// <summary>登记过的固定 Width 例外（页面, 值, 原因）。</summary>
    private static readonly HashSet<(string Page, string Value)> WidthExceptions =
        new()
        {
            // Map 缩放行：LabeledSlider 内部是“滑块 + 读数”横排，需要有限宽度才能排布
            //（与 FramelessClock 样板的 Width=180 同理）；它不是被宽度假设截断的文本行。
            ("Map", "220"),
        };

    private static readonly string[] GovernedPages = ["Music", "Storage", "Hitokoto", "Map"];

    /// <summary>页面 → (分组标题数, Setting 行数)。数量写进断言消息。</summary>
    private static readonly Dictionary<string, (int Headers, int Rows)> ExpectedStructure = new()
    {
        ["Music"] = (2, 2),      // 列表/工具栏/表单是 §10.2 允许的专属业务区域
        ["Storage"] = (3, 7),
        ["Hitokoto"] = (2, 13),
        ["Map"] = (0, 14),       // 无分组标题，全部 Setting 行
    };

    /// <summary>页面 → 预期 LabeledSlider 行数（本批页面均无 ColorInput 行）。</summary>
    private static readonly Dictionary<string, int> ExpectedSliders = new()
    {
        ["Map"] = 1,             // 缩放级别 Z3~Z18
        ["Music"] = 0, ["Storage"] = 0, ["Hitokoto"] = 0,
    };

    /// <summary>
    /// zh-Hans 冻结对照表：迁移前用户看到的中文原文，逐字节捕获（含 Music 预设规则名——它们
    /// 以中文落盘在既有设置文件里，zh 资源同时充当“模型存值 → 显示值”的映射源头）。
    /// 断言：页面用到的每个键都必须出现在对应页的表里，且 zh-Hans resx 值与其逐字节相同。
    /// </summary>
    private static readonly Dictionary<string, Dictionary<string, string>> FrozenZh = new()
    {
        ["Music"] = new()
        {
            ["Music_AddFromRunning"] = "从运行中添加",
            ["Music_AddManualRule"] = "手动添加规则",
            ["Music_AmbientGlow"] = "启用专辑封面流光氛围 (Ambient Glow)",
            ["Music_BrowseExe"] = "浏览程序 (.exe)",
            ["Music_BrowseExeFilter"] = "应用程序",
            ["Music_BrowseExeTitle"] = "选择音乐播放器可执行文件",
            ["Music_Cancel"] = "取消",
            ["Music_Close"] = "关闭",
            ["Music_ConfirmAdd"] = "确定添加",
            ["Music_DeleteRule"] = "删除规则",
            ["Music_ManualNameLabel"] = "软件名称：",
            ["Music_ManualNameWatermark"] = "例如：YesPlayMusic",
            ["Music_ManualPatternLabel"] = "匹配特征：",
            ["Music_ManualPatternWatermark"] = "例如：yesplaymusic (进程或AUMID关键字)",
            ["Music_ManualRuleTitle"] = "添加自定义软件规则",
            ["Music_MoveDown"] = "下移优先级",
            ["Music_MoveUp"] = "上移优先级",
            ["Music_NoSessionsHint"] = "请先在任意音乐软件中播放音乐",
            ["Music_NoSessionsTitle"] = "未检测到正在运行的媒体软件",
            ["Music_Player_Browser"] = "浏览器 (Edge/Chrome/Firefox)",
            ["Music_Player_Kugou"] = "酷狗音乐",
            ["Music_Player_Kuwo"] = "酷我音乐",
            ["Music_Player_Netease"] = "网易云音乐",
            ["Music_Player_QQMusic"] = "QQ音乐",
            ["Music_Player_Soda"] = "汽水音乐",
            ["Music_Player_Wmp"] = "Windows 媒体播放器",
            ["Music_PlayersHint"] = "当多个软件均处于活动状态时，列表上方的软件具有优先识别权。",
            ["Music_QuickAdd"] = "一键添加",
            ["Music_RestoreDefaults"] = "恢复默认预设",
            ["Music_RunningSessionsTitle"] = "系统当前运行的媒体会话（点击添加）：",
            ["Music_SectionGeneral"] = "常规与显示",
            ["Music_SectionPlayers"] = "音乐软件列表与智能识别",
            ["Music_ShowProgress"] = "显示时间进度条与跳转",
        },
        ["Storage"] = new()
        {
            ["Storage_DisplayMode"] = "显示样式",
            ["Storage_DoubleClickToOpen"] = "双击打开磁盘文件夹",
            ["Storage_DriveTypeLocal"] = "本地盘",
            ["Storage_DriveTypeNetwork"] = "网络盘",
            ["Storage_DriveTypeRemovable"] = "可移动盘",
            ["Storage_DriveTypeSystem"] = "系统盘",
            ["Storage_DrivesHint"] = "可单独勾选或隐藏不想在小组件中展示的分区；超出会按盘符顺序自动截取适配卡片尺寸。",
            ["Storage_EnableTypeFirstTooltip"] = "请先在上方开启对应驱动器类型",
            ["Storage_Format_FreeSpace"] = "剩余可用 (如 138G 可用)",
            ["Storage_Format_Percentage"] = "使用率百分比 (如 73%)",
            ["Storage_Format_UsedAndTotal"] = "已用 / 总计 (如 374G / 512G)",
            ["Storage_Mode_ProgressBarList"] = "📊 进度条列表",
            ["Storage_Mode_RingDashboard"] = "⭕ 圆环仪表盘",
            ["Storage_NoDrives"] = "未检测到可用的磁盘驱动器",
            ["Storage_Open"] = "打开",
            ["Storage_RefreshDrives"] = "刷新磁盘",
            ["Storage_SectionAppearance"] = "外观与交互",
            ["Storage_SectionDrives"] = "本机磁盘详情与显隐管理",
            ["Storage_SectionFilter"] = "驱动器过滤",
            ["Storage_ShowDriveTooltip"] = "勾选在小组件中显示该分区",
            ["Storage_ShowHeader"] = "显示顶部总览栏",
            ["Storage_ShowNetworkDrives"] = "显示网络映射驱动器",
            ["Storage_ShowRemovableDrives"] = "显示可移动磁盘 (U盘/移动硬盘)",
            ["Storage_TextFormat"] = "容量数值格式",
            ["Storage_UseWarningColors"] = "空间不足变色预警",
            ["Storage_UsedFreeFormat"] = "已用 {0} / {1} · 剩余 {2}",
        },
        ["Hitokoto"] = new()
        {
            ["Hitokoto_AutoHideActions"] = "常态隐藏操作按钮",
            ["Hitokoto_AutoHideActions_Subtitle"] = "仅在鼠标悬停到卡片上时浮现点赞、收藏、复制与展开按钮",
            ["Hitokoto_Categories"] = "句子分类筛选",
            ["Hitokoto_Categories_Subtitle"] = "全不选或全选均代表「全部分类随机」，可勾选特定风格",
            ["Hitokoto_CustomApi"] = "自定义语句接口地址 (可选)",
            ["Hitokoto_CustomApi_Subtitle"] = "留空默认使用官方 https://v1.hitokoto.cn/，支持自建实例",
            ["Hitokoto_FontDefault"] = "跟随排版默认",
            ["Hitokoto_FontFamily"] = "正文字体",
            ["Hitokoto_FontFamily_Subtitle"] = "选择一言展示字体（默认随排版风格自动优选）",
            ["Hitokoto_FontSize"] = "正文字号 (0 为自适应)",
            ["Hitokoto_FontSize_Subtitle"] = "设为 0 时随卡片大小自动缩放，也可固定为 12–28 px",
            ["Hitokoto_Frameless"] = "无边框桌面水印模式",
            ["Hitokoto_Frameless_Subtitle"] = "隐去卡片背景与边框，让文字带柔光阴影直接浮于桌面壁纸",
            ["Hitokoto_Interval"] = "自动切换间隔",
            ["Hitokoto_Interval_15m"] = "每 15 分钟",
            ["Hitokoto_Interval_1h"] = "每 1 小时",
            ["Hitokoto_Interval_2h"] = "每 2 小时",
            ["Hitokoto_Interval_30m"] = "每 30 分钟 (推荐)",
            ["Hitokoto_Interval_5m"] = "每 5 分钟",
            ["Hitokoto_Interval_Daily"] = "每日一句 (24 小时)",
            ["Hitokoto_Interval_Manual"] = "仅手动切换 (双击卡片/按钮)",
            ["Hitokoto_Interval_Subtitle"] = "除定时切换外，随时可双击卡片立即换一句",
            ["Hitokoto_MaxLength"] = "最大句子长度",
            ["Hitokoto_MaxLength_Subtitle"] = "避免超长句子在小卡片中拥挤（默认 45 字）",
            ["Hitokoto_MinLength"] = "最小句子长度",
            ["Hitokoto_MinLength_Subtitle"] = "过滤掉过短的句子（默认 0 字）",
            ["Hitokoto_ResetCategories"] = "重置为全部",
            ["Hitokoto_SectionAppearance"] = "外观与排版",
            ["Hitokoto_SectionFilter"] = "自动切换与句子过滤",
            ["Hitokoto_ShowAttribution"] = "显示出处与作者",
            ["Hitokoto_ShowAttribution_Subtitle"] = "在卡片底部展示句子的作品出处与人物署名",
            ["Hitokoto_ShowCategory"] = "显示句子分类角标",
            ["Hitokoto_ShowCategory_Subtitle"] = "在左上角展示「诗词」「文学」「动画」等分类胶囊",
            ["Hitokoto_ShowLikes"] = "显示社区获赞数 (Core API)",
            ["Hitokoto_ShowLikes_Subtitle"] = "通过一言核心接口实时读取获赞数并支持一键点赞",
            ["Hitokoto_StyleMode"] = "排版风格",
            ["Hitokoto_StyleMode_Subtitle"] = "切换现代引语、古典居中诗词或极简留白排版",
            ["Hitokoto_Style_MinimalCard"] = "极简纯粹 (无装饰水印)",
            ["Hitokoto_Style_ModernQuote"] = "现代引语 (左对齐 + 引号水印)",
            ["Hitokoto_Style_PoeticCenter"] = "古典诗词 (居中衬线 + 留白)",
        },
        ["Map"] = new()
        {
            ["Map_AllowDragSetting"] = "桌面拖拽平移",
            ["Map_AllowDragSetting_Subtitle"] = "开启后允许鼠标直接在桌面上拖拽平移地图视野",
            ["Map_AutoLocate"] = "网络自动定位",
            ["Map_AutoLocateOff"] = "手动预设",
            ["Map_AutoLocateOn"] = "启动时自动定位",
            ["Map_AutoLocate_Subtitle"] = "基于网络 IP 自动识别并定位到您当前所在的城市",
            ["Map_BaiduKey"] = "百度地图 AK (可选)",
            ["Map_BaiduKeyWatermark"] = "留空使用高德/开源备用源",
            ["Map_BaiduKey_Subtitle"] = "填入开发者 API Key 以获取百度官方高清切片",
            ["Map_LatitudeSetting"] = "纬度 (Latitude)",
            ["Map_LatitudeSetting_Subtitle"] = "地图中心点纬度 (-85.0 ~ 85.0)",
            ["Map_LocateNow"] = "立即定位当前位置",
            ["Map_LocationDesc_Subtitle"] = "显示在浮动卡片上的辅助副标题",
            ["Map_LocationDescription"] = "详细地址 / 描述",
            ["Map_LocationNameSetting"] = "地点标题",
            ["Map_LocationNameSetting_Subtitle"] = "显示在 iPad 风格卡片上的大字标题",
            ["Map_LongitudeSetting"] = "经度 (Longitude)",
            ["Map_LongitudeSetting_Subtitle"] = "地图中心点经度 (-180.0 ~ 180.0)",
            ["Map_PresetSetting"] = "预设知名地点",
            ["Map_PresetSetting_Subtitle"] = "快速定位到国内与全球标志性城市或商圈",
            ["Map_Provider"] = "地图图源",
            ["Map_Provider_Subtitle"] = "选择底图切片提供商与视觉风格",
            ["Map_ShowControlsSetting"] = "悬浮控制按钮组",
            ["Map_ShowControlsSetting_Subtitle"] = "在右侧展示复位、放大、缩小与图源切换按钮",
            ["Map_ShowInfoPillSetting"] = "浮动信息卡片",
            ["Map_ShowInfoPillSetting_Subtitle"] = "在地图角落展示当前地点、坐标与缩放胶囊",
            ["Map_ShowPinSetting"] = "中心大头针",
            ["Map_ShowPinSetting_Subtitle"] = "在地图中心显示 iPad 风格红色地标大头针",
            ["Map_ShowScaleBarSetting"] = "地图比例尺",
            ["Map_ShowScaleBarSetting_Subtitle"] = "在中大号尺寸下展示 Apple 风格米/公里比例尺",
            ["Map_ZoomSetting"] = "默认缩放等级",
            ["Map_ZoomSetting_Subtitle"] = "级别 3 (全球) ~ 18 (高精度街区)",
        },
    };

    // ---- 检查主体 -------------------------------------------------------------------------

    private static void CheckSettingsPagesAndLocale()
    {
        Console.WriteLine("--- 其余设置页迁移与文案资源治理(W9: Music/Storage/Hitokoto/Map) ---");

        var root = FindRepoRoot();

        foreach (var page in GovernedPages)
        {
            Console.WriteLine($"--- {page}Settings ---");

            var xamlPath = Path.Combine(root, "src", "Widgets", page, "Views", "Settings", $"{page}Settings.axaml");
            var csPath = Path.Combine(root, "src", "Widgets", page, "Views", "Settings", $"{page}Settings.axaml.cs");
            var localeDir = Path.Combine(root, "src", "Widgets", page, "Locales");
            var xaml = File.ReadAllText(xamlPath);
            var code = File.ReadAllText(csPath);

            // 1) 用户可见文案全部来自 x:Static 资源（带原因的例外清单机制）
            var literalViolations = CollectLiteralStrings(page, xaml);
            Check(literalViolations.Count == 0,
                literalViolations.Count == 0
                    ? $"{page}: 用户可见属性零字面量（例外清单 {LiteralExceptions.Count} 条已单独校验）"
                    : $"{page}: 未登记的用户可见字面量 {literalViolations.Count} 处: {string.Join(" | ", literalViolations.Take(5))}");

            // 2) 收集本页用到的资源键（XAML x:Static + code-behind Locale.*）
            var usedKeys = new SortedSet<string>(StringComparer.Ordinal);
            foreach (Match m in Regex.Matches(xaml, @"\{x:Static \w+:Locale\.([A-Za-z0-9_]+)\}"))
                usedKeys.Add(m.Groups[1].Value);
            foreach (Match m in Regex.Matches(code, @"Locale\.([A-Z][A-Za-z0-9_]*)"))
            {
                var key = m.Groups[1].Value;
                if (key is not ("Culture" or "ResourceManager")) usedKeys.Add(key);
            }
            Check(usedKeys.Count >= 10, $"{page}: 解析到 {usedKeys.Count} 个资源键");

            // 3) 双 resx 齐备 + zh 逐字节冻结
            var en = ReadResx(Path.Combine(localeDir, "Locale.resx"));
            var zh = ReadResx(Path.Combine(localeDir, "Locale.zh-Hans.resx"));
            var missingEn = usedKeys.Where(k => !en.ContainsKey(k)).ToArray();
            var missingZh = usedKeys.Where(k => !zh.ContainsKey(k)).ToArray();
            Check(missingEn.Length == 0 && missingZh.Length == 0,
                missingEn.Length == 0 && missingZh.Length == 0
                    ? $"{page}: {usedKeys.Count} 个键在 Locale.resx 与 Locale.zh-Hans.resx 中均存在"
                    : $"{page}: 缺键 en=[{string.Join(",", missingEn)}] zh=[{string.Join(",", missingZh)}]");

            var unmapped = usedKeys.Where(k => !FrozenZh[page].ContainsKey(k)).ToArray();
            Check(unmapped.Length == 0,
                unmapped.Length == 0
                    ? $"{page}: {usedKeys.Count} 个键全部在 zh 冻结对照表中登记"
                    : $"{page}: 对照表缺项 [{string.Join(",", unmapped)}]");

            var drifted = usedKeys
                .Where(FrozenZh[page].ContainsKey)
                .Where(k => zh[k] != FrozenZh[page][k])
                .Select(k => $"{k}: '{zh[k]}' != '{FrozenZh[page][k]}'")
                .ToArray();
            Check(drifted.Length == 0,
                drifted.Length == 0
                    ? $"{page}: zh-Hans 值与迁移前中文逐字节一致（{usedKeys.Count} 键）"
                    : $"{page}: zh 值漂移 {drifted.Length} 处: {string.Join(" | ", drifted.Take(3))}");

            var emptyEn = usedKeys.Where(k => en.TryGetValue(k, out var v) && v.Trim().Length == 0).ToArray();
            Check(emptyEn.Length == 0,
                emptyEn.Length == 0
                    ? $"{page}: 英文资源无空值（长标签不会被截断的前提是键确实有英文内容）"
                    : $"{page}: 英文空值键 [{string.Join(",", emptyEn)}]");

            // 4) code-behind 治理：不再有手工语言分支
            Check(!Regex.IsMatch(code, @"\bIsZh\b|\bIsChinese\b"),
                $"{page}: code-behind 已无 IsZh/IsChinese 手工语言判断");
        }

        // 5) 实例化四个页面：滚动契约 + 共享层结构 + 无本地通用基础样式
        var pages = new List<(string Name, UserControl Control)>
        {
            ("Music", new Music.Views.Settings.MusicSettings()),
            ("Storage", new Storage.Views.Settings.StorageSettings(new StubWidgetLayoutProvider())),
            ("Hitokoto", new Hitokoto.Views.Settings.HitokotoSettings(new StubWidgetLayoutProvider())),
            ("Map", new Map.Views.Settings.MapSettings(new StubWidgetLayoutProvider())),
        };

        foreach (var (name, control) in pages)
        {
            var (expectedHeaders, expectedRows) = ExpectedStructure[name];

            Check(SettingsPage.GetScrollOwnership(control) == SettingsPageScrollOwnership.Host,
                $"{name}Settings 声明 ScrollOwnership=Host");
            Check(!control.GetLogicalDescendants().OfType<ScrollViewer>().Any(),
                $"{name}Settings 自身无根 ScrollViewer（唯一滚动所有者是宿重视口）");

            var headers = control.GetLogicalDescendants().OfType<TextBlock>()
                .Count(t => t.Classes.Contains("dc-section-header"));
            Check(headers == expectedHeaders,
                $"{name}Settings 分组标题全部走 dc-section-header（{headers}/{expectedHeaders}）");

            var rows = control.GetLogicalDescendants().OfType<Setting>().Count();
            Check(rows == expectedRows,
                $"{name}Settings 设置行全部是共享 Setting（{rows}/{expectedRows}）");

            foreach (var fragment in new[] { "icon-btn", "icon-button", "group-header", "section-header" })
            {
                Check(!HasLocalSelector(control.Styles, fragment),
                    $"{name}Settings 无本地 {fragment} 基础样式");
            }

            var sliders = control.GetLogicalDescendants().OfType<LabeledSlider>().Count();
            Check(sliders == ExpectedSliders[name],
                $"{name}Settings 滑块行走共享 LabeledSlider（{sliders}/{ExpectedSliders[name]}）");
            if (ExpectedSliders[name] == 0 && sliders == 0)
            {
                Check(CountStray<Slider, LabeledSlider>(control) == 0,
                    $"{name}Settings 无游离的裸 Slider");
            }

            // 固定 Width 只允许登记过的例外（XAML 源级断言）
            var widthViolations = CollectUnregisteredWidths(name, File.ReadAllText(
                Path.Combine(FindRepoRoot(), "src", "Widgets", name, "Views", "Settings", $"{name}Settings.axaml")));
            Check(widthViolations.Count == 0,
                widthViolations.Count == 0
                    ? $"{name}Settings 固定 Width 均为登记过的例外（无业务理由的宽度已改 Stretch/MinWidth）"
                    : $"{name}Settings 未登记的固定 Width: {string.Join(" | ", widthViolations)}");
        }

        // 6) Map 缩放行细节：Z 前缀读数由 DisplayFormat 复现；缩放行已不是
        //    Slider+TextBlock 的本地拼装，而经纬度两行是业务上的独立单值行（各自带
        //    -85~85 / -180~180 的专用范围），保留裸 NumericUpDown 是正确的。
        var map = pages.Single(p => p.Name == "Map").Control;
        var zoomSlider = map.GetLogicalDescendants().OfType<LabeledSlider>().Single();
        Check(zoomSlider.Minimum == 3 && zoomSlider.Maximum == 18 && zoomSlider.Step == 1,
            $"Map 缩放行范围 3~18 步长 1（与 MapModel.MinZoom/MaxZoom 一致，实际 {zoomSlider.Minimum}~{zoomSlider.Maximum} 步长 {zoomSlider.Step}）");
        var strayNumerics = map.GetLogicalDescendants().OfType<NumericUpDown>()
            .Count(n => n.GetLogicalAncestors().OfType<LabeledSlider>().Any() == false);
        Check(strayNumerics == 2,
            $"Map 仅剩经纬度两个独立的单值 NumericUpDown（{strayNumerics}/2）");
    }

    // ---- 助手 -----------------------------------------------------------------------------

    /// <summary>解析 XAML，返回未在例外清单登记的用户可见字面量（含定位信息）。</summary>
    private static List<string> CollectLiteralStrings(string page, string xaml)
    {
        var violations = new List<string>();
        var doc = XDocument.Parse(xaml);

        foreach (var element in doc.Descendants())
        {
            foreach (var attribute in element.Attributes())
            {
                if (!UserVisibleAttributes.Contains(attribute.Name.LocalName)) continue;
                var value = attribute.Value;
                if (value.Length == 0 || value.StartsWith('{')) continue;    // 空串 / 绑定 / x:Static
                if (LiteralExceptions.Contains((page, attribute.Name.LocalName, value))) continue;
                violations.Add($"{element.Name.LocalName}.{attribute.Name.LocalName}=\"{value}\"");
            }
        }

        return violations;
    }

    /// <summary>返回 XAML 中未登记的固定 Width（MinWidth/MaxWidth 不受限制）。</summary>
    private static List<string> CollectUnregisteredWidths(string page, string xaml)
    {
        var violations = new List<string>();
        var doc = XDocument.Parse(xaml);
        foreach (var element in doc.Descendants())
        {
            var width = element.Attribute("Width")?.Value;
            if (width is null) continue;
            if (width.StartsWith('{')) continue;   // 绑定/资源
            if (WidthExceptions.Contains((page, width))) continue;
            violations.Add($"{element.Name.LocalName} Width=\"{width}\"");
        }
        return violations;
    }

    private static Dictionary<string, string> ReadResx(string path)
    {
        var doc = XDocument.Load(path);
        return doc.Descendants("data")
            .ToDictionary(
                d => d.Attribute("name")!.Value,
                d => d.Element("value")?.Value ?? string.Empty,
                StringComparer.Ordinal);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null
               && !(Directory.Exists(Path.Combine(dir.FullName, "src"))
                    && Directory.Exists(Path.Combine(dir.FullName, "tests"))))
        {
            dir = dir.Parent;
        }
        return dir?.FullName
            ?? throw new InvalidOperationException("repo root (containing src/ and tests/) not found");
    }
}
