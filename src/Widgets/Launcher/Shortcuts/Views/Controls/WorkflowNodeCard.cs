using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Shortcuts.Models;
using Shortcuts.Services;

namespace Shortcuts.Views.Controls;

/// <summary>
/// 节点卡片的几何度量常量。<b>单一事实来源</b>：卡片自身、画布的端口命中测试、连线锚点
/// 全部引用这里，杜绝旧版"卡片常量 + 画布魔数"两处不同步导致端口错位的问题。
/// </summary>
public static class NodeMetrics
{
    public const double Width = 240;
    public const double HeaderHeight = 44;
    public const double RowHeight = 24;
    public const double BottomPadding = 8;
    public const double DotSize = 10;
    public const double IconChipSize = 26;

    /// <summary>整个卡片的高度：头部 + 参数行 + 输出行 + 底部留白。</summary>
    public static double ComputeHeight(int paramCount, int outputCount) =>
        HeaderHeight + (paramCount + outputCount) * RowHeight + BottomPadding;

    /// <summary>按节点可见参数与输出口数估算高度（与卡片实际布局一致）。</summary>
    public static double ComputeHeight(WorkflowNode node, ShortcutDefinition? owner) =>
        ComputeHeight(WorkflowNodeTypes.VisibleParams(node).Count, WorkflowNodeTypes.Outputs(node, owner).Count);

    /// <summary>参数行 i 的中心 Y（卡片本地坐标）。</summary>
    public static double ParamRowCenterY(int index) => HeaderHeight + index * RowHeight + RowHeight / 2;

    /// <summary>输出口行 i 的中心 Y（卡片本地坐标）。</summary>
    public static double OutputRowCenterY(int paramCount, int index) =>
        HeaderHeight + (paramCount + index) * RowHeight + RowHeight / 2;
}

/// <summary>一个端口圆点的完整描述：标识、位置（卡片本地）、种类与类型色。</summary>
public sealed record PortAnchor(
    PortRef Port,
    Point Center,
    bool IsData,
    bool IsError,
    DataType DataType,
    string Label);

/// <summary>
/// 画布上的一个节点：类别色头部 + 图标徽章、参数行、输出端口行。
/// <para>
/// 端口锚点在构造时一次性算好缓存起来（旧版每帧遍历目录 + LINQ，拖拽时是持续的 GC 压力）。
/// 节点内容变化时由画布调用 <c>RefreshNode</c> 重建单张卡片，而不是全画布重来。
/// </para>
/// </summary>
public sealed class WorkflowNodeCard : Border
{
    private readonly WorkflowCanvas canvas;
    private readonly Canvas dotLayer = new();
    private readonly Dictionary<string, PortAnchor> anchorsById = new(StringComparer.Ordinal);
    private readonly List<PortAnchor> anchors = [];
    private readonly Border badge;

    public WorkflowNode Node { get; }

    /// <summary>当前运行态（供画布 RefreshNode 重建时恢复）。</summary>
    public NodeRunState CurrentRunState { get; private set; } = NodeRunState.Idle;

    /// <summary>错误徽标计数（0 = 不显示）。</summary>
    public int ErrorBadgeCount { get; private set; }

    public WorkflowNodeCard(WorkflowNode node, WorkflowCanvas canvas, ShortcutDefinition? owner = null)
    {
        Node = node;
        this.canvas = canvas;

        Width = NodeMetrics.Width;
        MinHeight = NodeMetrics.HeaderHeight;
        CornerRadius = new CornerRadius(12);
        BorderThickness = new Thickness(1);
        ClipToBounds = false;
        Classes.Add("wf-node");
        Cursor = new Cursor(StandardCursorType.Hand);

        var definition = ActionCatalog.Get(node.Type);
        var categoryHex = NodeVisuals.NodeColor(node);

        // ---- 头部：类别色图标徽章 + 标题 -------------------------------------
        var headerContent = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Margin = new Thickness(11, 0, 10, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        headerContent.Children.Add(new Border
        {
            Width = NodeMetrics.IconChipSize,
            Height = NodeMetrics.IconChipSize,
            CornerRadius = new CornerRadius(8),
            Background = Visuals.Tint(categoryHex, 0x30),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new GlyphView
            {
                Data = GlyphLibrary.GetGeometry(definition?.Glyph ?? "bolt"),
                Stroke = Visuals.Brush(categoryHex),
                StrokeThickness = 1.7,
                Width = 15,
                Height = 15,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        });
        headerContent.Children.Add(new TextBlock
        {
            Text = DisplayTitle(node),
            FontSize = 12.5,
            FontWeight = FontWeight.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = NodeMetrics.Width - 72,
        });

        var header = new Border
        {
            Height = NodeMetrics.HeaderHeight,
            Background = Visuals.Tint(categoryHex, 0x16),
            CornerRadius = new CornerRadius(12, 12, 0, 0),
            Child = headerContent,
        };

        // ---- 主体：参数行 + 输出行 -------------------------------------------
        var body = new StackPanel { Spacing = 0 };
        body.Children.Add(header);

        var specs = WorkflowNodeTypes.VisibleParams(node);
        var outputs = WorkflowNodeTypes.Outputs(node, owner);

        foreach (var spec in specs)
            body.Children.Add(BuildParamRow(node, spec));

        foreach (var port in outputs)
            body.Children.Add(BuildPortRow(port));

        body.Children.Add(new Border { Height = NodeMetrics.BottomPadding });

        // ---- 错误徽标（右上角悬浮） ------------------------------------------
        badge = new Border
        {
            Width = 18,
            Height = 18,
            CornerRadius = new CornerRadius(9),
            Background = Visuals.Brush("#A32D2D"),
            IsVisible = false,
            IsHitTestVisible = false,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, -6, -6, 0),
            ZIndex = 10,
            Child = new TextBlock
            {
                FontSize = 10,
                FontWeight = FontWeight.Bold,
                Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };

        var root = new Grid();
        root.Children.Add(body);
        root.Children.Add(dotLayer);
        root.Children.Add(badge);
        Child = root;

        BuildAnchors(specs, outputs);
        BuildDots();
    }

    /// <summary>自定义标题，或动作名（switch 附加 case 数以便区分）。</summary>
    private static string DisplayTitle(WorkflowNode node)
    {
        if (!string.IsNullOrWhiteSpace(node.Title)) return node.Title!;

        var definition = ActionCatalog.Get(node.Type);
        if (definition == null) return node.Type;

        var title = Loc.Get(definition.TitleKey);
        return node.Type == WorkflowNodeTypes.Switch ? $"{title} · {node.EffectiveCases.Count}" : title;
    }

    // ---- 端口锚点 -----------------------------------------------------------------

    private void BuildAnchors(IReadOnlyList<ParamSpec> specs, IReadOnlyList<WorkflowPort> outputs)
    {
        anchors.Clear();
        anchorsById.Clear();

        if (WorkflowNodeTypes.HasInput(Node.Type))
        {
            Add(new PortRef(Node.Id, WorkflowNodeTypes.In, false),
                new Point(0, NodeMetrics.HeaderHeight / 2), isData: false, isError: false,
                DataType.Any, Loc.Get("Shortcuts_Port_In"));
        }

        for (var i = 0; i < specs.Count; i++)
        {
            var spec = specs[i];
            Add(new PortRef(Node.Id, spec.Key, false),
                new Point(0, NodeMetrics.ParamRowCenterY(i)), isData: true, isError: false,
                spec.DataType, Loc.Get(spec.LabelKey));
        }

        for (var i = 0; i < outputs.Count; i++)
        {
            var port = outputs[i];
            Add(new PortRef(Node.Id, port.Id, true),
                new Point(NodeMetrics.Width, NodeMetrics.OutputRowCenterY(specs.Count, i)),
                port.IsData, isError: port.Id == WorkflowNodeTypes.ErrorPort,
                port.DataType, port.Display ?? Loc.Get(port.LabelKey));
        }

        void Add(PortRef port, Point center, bool isData, bool isError, DataType dataType, string label)
        {
            var anchor = new PortAnchor(port, center, isData, isError, dataType, label);
            anchors.Add(anchor);
            anchorsById[$"{port.IsOutput}:{port.PortId}"] = anchor;
        }
    }

    /// <summary>卡片本地端口锚点（缓存，无分配）。</summary>
    public Point Anchor(string portId, bool output, ShortcutDefinition? owner = null) =>
        anchorsById.TryGetValue($"{output}:{portId}", out var anchor) ? anchor.Center : ComputeAnchor(portId, output, owner);

    /// <summary>兜底计算（缓存未命中的罕见端口，例如刚改过 case 列表的瞬间）。</summary>
    private Point ComputeAnchor(string portId, bool output, ShortcutDefinition? owner)
    {
        var specs = WorkflowNodeTypes.VisibleParams(Node);
        var outputs = WorkflowNodeTypes.Outputs(Node, owner);
        if (!output)
        {
            if (portId == WorkflowNodeTypes.In) return new Point(0, NodeMetrics.HeaderHeight / 2);
            var index = IndexOf(specs, portId);
            return new Point(0, NodeMetrics.ParamRowCenterY(index));
        }

        var outputIndex = IndexOfPort(outputs, portId);
        return new Point(NodeMetrics.Width, NodeMetrics.OutputRowCenterY(specs.Count, outputIndex));
    }

    /// <summary>全部端口锚点（兼容旧调用方）。</summary>
    public IEnumerable<(PortRef Port, Point Anchor)> PortAnchors(ShortcutDefinition? owner = null) =>
        anchors.Select(a => (a.Port, a.Center));

    /// <summary>缓存的完整端口信息（画布命中测试与着色使用）。</summary>
    public IReadOnlyList<PortAnchor> Anchors => anchors;

    private static int IndexOf(IReadOnlyList<ParamSpec> specs, string key)
    {
        for (var i = 0; i < specs.Count; i++)
        {
            if (string.Equals(specs[i].Key, key, StringComparison.Ordinal)) return i;
        }
        return 0;
    }

    private static int IndexOfPort(IReadOnlyList<WorkflowPort> ports, string key)
    {
        for (var i = 0; i < ports.Count; i++)
        {
            if (string.Equals(ports[i].Id, key, StringComparison.Ordinal)) return i;
        }
        return 0;
    }

    // ---- 行构建 -------------------------------------------------------------------

    private Control BuildParamRow(WorkflowNode node, ParamSpec spec)
    {
        var bound = BindingText(node, spec);
        var isBound = bound != null;

        var grid = new Grid
        {
            Height = NodeMetrics.RowHeight,
            ColumnDefinitions = new ColumnDefinitions("14,*"),
            Background = Brushes.Transparent,
            Cursor = new Cursor(StandardCursorType.Hand),
        };

        var text = new TextBlock
        {
            Text = isBound
                ? $"{Loc.Get(spec.LabelKey)} ← {bound}"
                : $"{Loc.Get(spec.LabelKey)}: {ParamPreview(spec, node.Params.GetValueOrDefault(spec.Key))}",
            FontSize = 10.5,
            Opacity = isBound ? 0.9 : 0.72,
            FontStyle = isBound ? FontStyle.Italic : FontStyle.Normal,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 0, 10, 0),
        };
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);

        if (spec.Required && !node.Params.ContainsKey(spec.Key) && !isBound)
            text.Foreground = Visuals.Brush("#A32D2D");

        // 画布在隧道阶段识别参数行并转发给编辑器；Tag 就是识别凭据。
        grid.Tag = new ParamRowRef(node, spec, grid);

        // 右键必须打开该行的绑定菜单，而不是卡片的节点菜单。
        grid.ContextRequested += (_, e) => e.Handled = true;

        // 兜底（正常路径由画布的隧道处理器接管）。
        grid.PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(grid).Properties.IsRightButtonPressed)
            {
                canvas.RequestParamMenu(node, spec, grid);
                e.Handled = true;
                return;
            }

            canvas.RequestParamEdit(node, spec, grid);
            e.Handled = true;
        };

        var tip = isBound
            ? Loc.Format("Shortcuts_Tip_Bound", Loc.Get(spec.LabelKey), bound)
            : $"{Loc.Get(spec.LabelKey)}: {ParamPreview(spec, node.Params.GetValueOrDefault(spec.Key))}";
        if (spec.HelpKey != null) tip += "\n" + Loc.Get(spec.HelpKey);
        ToolTip.SetTip(grid, tip);
        return grid;
    }

    /// <summary>参数的值来自哪里；null = 字面量。</summary>
    private string? BindingText(WorkflowNode node, ParamSpec spec)
    {
        if (canvas.Graph?.IncomingData(node.Id, spec.Key) is { } link)
        {
            var portLabel = canvas.PortLabelOf(link.From, link.FromPort);
            return $"{canvas.ResolveNodeTitle(link.From) ?? link.From}.{portLabel}";
        }

        var value = node.Params.GetValueOrDefault(spec.Key);
        if (value is not { IsVariable: true }) return null;

        if (value.SourceActionId == ValueKinds.LastResult) return Loc.Get("Shortcuts_Param_LastResult");
        return canvas.ResolveNodeTitle(value.SourceActionId!) ?? value.SourceActionId;
    }

    private Control BuildPortRow(WorkflowPort port)
    {
        var grid = new Grid
        {
            Height = NodeMetrics.RowHeight,
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
        };

        var isError = port.Id == WorkflowNodeTypes.ErrorPort;
        var label = new TextBlock
        {
            Text = PortText(port),
            FontSize = 9.5,
            Opacity = isError ? 0.85 : 0.55,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 14, 0),
        };
        if (isError)
            label.Foreground = Visuals.Brush("#A32D2D");

        Grid.SetColumn(label, 0);
        grid.Children.Add(label);
        ToolTip.SetTip(grid, PortText(port));
        return grid;
    }

    /// <summary>"→ out" 表示执行口；"text: 文本" 表示数据口。</summary>
    private static string PortText(WorkflowPort port)
    {
        var name = port.Display ?? Loc.Get(port.LabelKey);
        if (!port.IsData) return "→ " + name;
        return $"{name}: {Loc.Get(ActionCatalog.TypeLabelKey(port.DataType))}";
    }

    private void BuildDots()
    {
        dotLayer.Children.Clear();

        foreach (var anchor in anchors)
        {
            var nodeHex = NodeVisuals.NodeColor(Node);
            var dot = new Border
            {
                Width = NodeMetrics.DotSize,
                Height = NodeMetrics.DotSize,
                CornerRadius = new CornerRadius(NodeMetrics.DotSize / 2),
                Background = NodeVisuals.PortBrush(anchor.IsData, anchor.DataType, nodeHex, anchor.IsError),
                BorderThickness = new Thickness(1.5),
                BorderBrush = Brushes.White,
                Cursor = new Cursor(StandardCursorType.Cross),
                Tag = anchor.Port,
            };
            ToolTip.SetTip(dot, anchor.Label);

            Canvas.SetLeft(dot, anchor.Center.X - NodeMetrics.DotSize / 2);
            Canvas.SetTop(dot, anchor.Center.Y - NodeMetrics.DotSize / 2);
            dotLayer.Children.Add(dot);
        }
    }

    // ---- 状态 ---------------------------------------------------------------------

    public void SetSelected(bool selected)
    {
        Classes.Set("selected", selected);
    }

    public void SetRunState(NodeRunState state)
    {
        CurrentRunState = state;
        Classes.Set("running", state == NodeRunState.Running);
        Classes.Set("failed", state == NodeRunState.Failed);
    }

    /// <summary>校验诊断徽标：count>0 显示红色数字，tooltip 列出全部消息。</summary>
    public void SetErrorBadge(int count, string? tooltip)
    {
        ErrorBadgeCount = count;
        badge.IsVisible = count > 0;
        if (badge.Child is TextBlock text)
            text.Text = count > 9 ? "9+" : count.ToString();
        ToolTip.SetTip(badge, tooltip);
    }

    /// <summary>紧凑的一行参数值预览。</summary>
    private string ParamPreview(ParamSpec spec, ShortcutValue? value)
    {
        if (value == null) return Loc.Get("Shortcuts_Param_Missing");

        if (value.IsVariable)
        {
            return value.SourceActionId == ValueKinds.LastResult
                ? Loc.Get("Shortcuts_Param_LastResult")
                : Loc.Get("Shortcuts_Param_Variable");
        }

        var raw = value.Text ?? string.Empty;
        if (raw.Length == 0) return Loc.Get("Shortcuts_Param_Missing");

        if (spec.Kind == ParamKind.Choice)
        {
            var labelKey = ActionCatalog.OptionLabelKey(spec, raw);
            return string.IsNullOrEmpty(labelKey) ? raw : Loc.Get(labelKey);
        }

        if (spec.Kind == ParamKind.Hotkey &&
            HotkeyGesture.TryParse(raw, requireModifier: false, out var hotkey, out _))
        {
            return hotkey.Text;
        }

        // 恰好是一个模板 token（{{nodeId}}）的字面量按"引用"解读。
        if (raw.Length > 4 && raw.StartsWith("{{", StringComparison.Ordinal) && raw.EndsWith("}}", StringComparison.Ordinal))
        {
            var token = raw[2..^2].Trim();
            if (token is "last" or "$last") return Loc.Get("Shortcuts_Param_LastResult");
            if (token is "index" or "item") return "{{" + token + "}}";

            var referenced = canvas.ResolveNodeTitle(token);
            if (!string.IsNullOrEmpty(referenced))
                return Loc.Format("Shortcuts_Param_ResultOf", referenced);
        }

        var text = spec.UnitKey != null ? Loc.Format(spec.UnitKey, raw) : raw;
        return text.Length <= 22 ? text : text[..21] + "…";
    }
}
