using Avalonia.Media;
using Shortcuts.Models;

namespace Shortcuts.Services;

/// <summary>
/// 颜色系统：节点、端口与连线按「类别」与「数据类型」双维度着色，让画布一眼可读。
/// <para>
/// - 执行连线继承源节点的类别色（节点是什么类别，它发起的执行流就是什么颜色）；
/// - 数据连线继承源端口的数据类型色（文本是蓝、数字是绿……），与端口圆点一致；
/// - 所有画刷都经过 <see cref="Visuals"/> 的缓存，逐帧渲染零分配。
/// </para>
/// </summary>
public static class NodeVisuals
{
    /// <summary>数据类型 → 主题无关的标识色（在浅色与深色背景上都有足够对比度）。</summary>
    private static readonly Dictionary<DataType, string> DataTypeColors = new()
    {
        [DataType.Any] = "#8E8C86",
        [DataType.Text] = "#3B82C4",
        [DataType.Number] = "#2FA463",
        [DataType.Boolean] = "#D9782D",
        [DataType.DateTime] = "#9A5BD0",
        [DataType.Path] = "#C4634F",
        [DataType.List] = "#2B9EB3",
        [DataType.Object] = "#B3922E",
        [DataType.Null] = "#8E8C86",
    };

    private const string FallbackTypeColor = "#8E8C86";

    /// <summary>色值 of a data type; 与端口圆点、数据连线共用同一套色，保证「同色即同型」。</summary>
    public static string DataTypeColor(DataType type) =>
        DataTypeColors.TryGetValue(type, out var hex) ? hex : FallbackTypeColor;

    /// <summary>类别色（直接透传 ActionCatalog 的定义，便于调用方少一层间接）。</summary>
    public static string CategoryColor(ActionCategory category) => ActionCatalog.CategoryColor(category);

    /// <summary>节点的类别色；未知类型按流程控制处理。</summary>
    public static string NodeColor(WorkflowNode node)
    {
        var definition = ActionCatalog.Get(node.Type);
        return ActionCatalog.CategoryColor(definition?.Category ?? ActionCategory.Flow);
    }

    /// <summary>执行连线的颜色：源节点的类别色，略降不透明度避免喧宾夺主。</summary>
    public static IBrush ExecLinkBrush(WorkflowNode sourceNode) =>
        Visuals.Tint(NodeColor(sourceNode), 0xD9);

    /// <summary>数据连线的颜色：源端口的数据类型色。</summary>
    public static IBrush DataLinkBrush(DataType sourceType) =>
        Visuals.Tint(DataTypeColor(sourceType), 0xCC);

    /// <summary>端口圆点颜色：数据口用类型色，执行口用类别色，错误口固定红。</summary>
    public static IBrush PortBrush(bool isData, DataType dataType, string nodeHex, bool isError)
    {
        if (isError) return Visuals.Brush("#A32D2D");
        return isData ? Visuals.Brush(DataTypeColor(dataType)) : Visuals.Brush(nodeHex);
    }

    /// <summary>画笔缓存：渲染每帧都要取笔，按（画刷、宽度、虚线）缓存，宽度量化到 0.05 防止爆表。</summary>
    private static readonly Dictionary<(IBrush Brush, double Width, bool Dashed), Pen> PenCache = new();

    /// <summary>取一支缓存的画笔。宽度会被按当前缩放换算（外层传入已除以 scale 的值）。</summary>
    public static Pen Pen(IBrush brush, double width, bool dashed = false)
    {
        var quantized = Math.Round(width * 20) / 20;
        var key = (brush, quantized, dashed);
        if (PenCache.TryGetValue(key, out var pen)) return pen;

        if (PenCache.Count >= 256) PenCache.Clear();
        pen = new Pen(brush, quantized)
        {
            LineCap = PenLineCap.Round,
            DashStyle = dashed ? DashStyle.Dash : null,
        };
        PenCache[key] = pen;
        return pen;
    }
}
