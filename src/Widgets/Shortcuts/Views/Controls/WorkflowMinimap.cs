using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Styling;
using Shortcuts.Models;
using Shortcuts.Services;

namespace Shortcuts.Views.Controls;

/// <summary>
/// 画布小地图：右上角缩略显示全部节点（按类别着色）与当前视口框，点击/拖动可直接跳转。
/// 数据全部来自 <see cref="WorkflowCanvas"/> 的公开视图状态，自身零状态。
/// </summary>
public sealed class WorkflowMinimap : Control
{
    private WorkflowCanvas? source;
    private bool dragging;
    private Rect contentRect;
    private double mapScale;

    private static readonly IBrush NodeFillLight = new SolidColorBrush(Color.Parse("#7F000000"));
    private static readonly IBrush NodeFillDark = new SolidColorBrush(Color.Parse("#7FFFFFFF"));
    private static readonly IBrush ViewportFill = new SolidColorBrush(Color.Parse("#10534AB7"));
    private static readonly IBrush ViewportStroke = new SolidColorBrush(Color.Parse("#99534AB7"));

    public WorkflowCanvas? Source
    {
        get => source;
        set
        {
            if (ReferenceEquals(source, value)) return;
            if (source != null) source.ViewChanged -= OnSourceViewChanged;
            source = value;
            if (source != null) source.ViewChanged += OnSourceViewChanged;
            InvalidateVisual();
        }
    }

    private void OnSourceViewChanged() => InvalidateVisual();

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (source != null) source.ViewChanged -= OnSourceViewChanged;
        base.OnDetachedFromVisualTree(e);
    }

    public WorkflowMinimap()
    {
        ClipToBounds = true;
        Cursor = new Cursor(StandardCursorType.Hand);
    }

    public override void Render(DrawingContext context)
    {
        if (source?.Graph == null || Bounds.Width < 8 || Bounds.Height < 8) return;
        var content = source.ContentBounds();
        if (content == null) return;

        // 内容外围留 40 图空间像素的呼吸边距。
        contentRect = content.Value.Inflate(40);
        mapScale = Math.Min(Bounds.Width / contentRect.Width, Bounds.Height / contentRect.Height);
        mapScale = Math.Min(mapScale, 1.0);

        var isDark = ActualThemeVariant == ThemeVariant.Dark;
        var nodeFill = isDark ? NodeFillDark : NodeFillLight;
        var accent = TryAccent();

        using (context.PushClip(new Rect(Bounds.Size)))
        {
            foreach (var node in source.Graph.Nodes)
            {
                if (source.CardOf(node.Id) == null) continue;

                var width = NodeMetrics.Width;
                var height = NodeMetrics.ComputeHeight(node, source.Owner);
                var rect = MapRect(new Rect(node.X, node.Y, width, height));

                var selected = source.SelectedNodeIds.Contains(node.Id);
                var fill = selected ? accent ?? nodeFill : Visuals.Tint(NodeVisuals.NodeColor(node), isDark ? (byte)0x99 : (byte)0x8C);
                context.FillRectangle(fill, rect, 1.5f);

                if (selected)
                {
                    context.DrawRectangle(new Pen(accent ?? nodeFill, 1.4), rect, 1.5f);
                }
            }

            // 视口框。
            var viewport = MapRect(source.ViewportGraphRect());
            context.FillRectangle(ViewportFill, viewport);
            context.DrawRectangle(new Pen(ViewportStroke, 1.2), viewport);
        }
    }

    private Rect MapRect(Rect graphRect)
    {
        var x = (graphRect.X - contentRect.X) * mapScale;
        var y = (graphRect.Y - contentRect.Y) * mapScale;
        return new Rect(x, y, Math.Max(1, graphRect.Width * mapScale), Math.Max(1, graphRect.Height * mapScale));
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        if (source == null) return;
        dragging = true;
        e.Pointer.Capture(this);
        Navigate(e);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        if (!dragging || source == null) return;
        Navigate(e);
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        dragging = false;
        e.Pointer.Capture(null);
        e.Handled = true;
    }

    private void Navigate(PointerEventArgs e)
    {
        if (source == null) return;
        var point = e.GetPosition(this);
        var graph = new Point(
            contentRect.X + point.X / mapScale,
            contentRect.Y + point.Y / mapScale);
        source.CenterOnGraphPoint(graph);
    }

    private IBrush? TryAccent() =>
        this.TryFindResource("SystemControlHighlightAccentBrush", ActualThemeVariant, out var value) && value is IBrush brush
            ? brush
            : null;
}
