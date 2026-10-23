using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using AudioVisualizer.Models;
using AudioVisualizer.Services;

namespace AudioVisualizer.Views.Controls;

/// <summary>
/// 交互式创作画板：等比自适应布局、外围振幅防裁切安全边距、直观可视节点与极速编辑操作
/// </summary>
public class DrawingBoardControl : Control
{
    private AudioVisualizerModel model = new();
    private readonly List<PathPoint> currentPoints = [];
    private bool isClosedShape;
    private DrawToolMode drawMode = DrawToolMode.Curve;

    // 外围预留 28px 律动震荡安全边距，确保柱状图与线条律动在画板内 100% 完整显示，永不被裁切！
    private const double MarginPadding = 28.0;

    private int draggingPointIndex = -1;
    private int hoveredPointIndex = -1;
    private Point currentPointerPos;
    private bool isFreehandDrawing;

    private readonly float[] previewSpectrum = new float[48];
    private DispatcherTimer? previewTimer;

    public event Action<List<PathPoint>, bool, DrawToolMode>? PathUpdated;

    public bool LivePreviewEnabled { get; set; } = true;

    public AudioVisualizerModel Model
    {
        get => model;
        set
        {
            model = value;
            currentPoints.Clear();
            if (model.Points != null && model.Points.Count > 0)
            {
                currentPoints.AddRange(model.Points);
            }
            isClosedShape = model.IsClosedShape;
            drawMode = model.DrawMode;
            InvalidateVisual();
        }
    }

    public DrawingBoardControl()
    {
        // 关键：不裁切边界，让律动线条和光晕完整延伸显示
        ClipToBounds = false;
        Focusable = true;
    }

    /// <summary>
    /// 设置画板显示尺寸：依据桌面小组件的实际宽高比自适应调整，保证在设置窗口中舒适完整展示
    /// </summary>
    public void SetWidgetProportions(double actualWidth, double actualHeight)
    {
        double targetDisplayWidth = 460.0;
        double aspect = actualHeight / Math.Max(1.0, actualWidth);
        // 高度按比例自适应，并约束在舒适的可视编辑高度 [160, 300]
        double targetDisplayHeight = Math.Clamp(targetDisplayWidth * aspect, 160.0, 300.0);

        Width = targetDisplayWidth;
        Height = targetDisplayHeight;
        InvalidateVisual();
    }

    public void SetDrawMode(DrawToolMode mode)
    {
        drawMode = mode;
        InvalidateVisual();
    }

    public void SetClosedShape(bool closed)
    {
        isClosedShape = closed;
        NotifyPathUpdated();
        InvalidateVisual();
    }

    public void ApplyPreset(List<PathPoint> points, bool isClosed, DrawToolMode mode)
    {
        currentPoints.Clear();
        currentPoints.AddRange(points);
        isClosedShape = isClosed;
        drawMode = mode;
        NotifyPathUpdated();
        InvalidateVisual();
    }

    public void Undo()
    {
        if (currentPoints.Count > 0)
        {
            currentPoints.RemoveAt(currentPoints.Count - 1);
            NotifyPathUpdated();
            InvalidateVisual();
        }
    }

    public void Clear()
    {
        currentPoints.Clear();
        NotifyPathUpdated();
        InvalidateVisual();
    }

    /// <summary>
    /// 一键将当前图形整体水平与垂直居中
    /// </summary>
    public void CenterShape()
    {
        if (currentPoints.Count == 0) return;

        double minX = double.MaxValue, maxX = double.MinValue;
        double minY = double.MaxValue, maxY = double.MinValue;

        foreach (var p in currentPoints)
        {
            if (p.X < minX) minX = p.X;
            if (p.X > maxX) maxX = p.X;
            if (p.Y < minY) minY = p.Y;
            if (p.Y > maxY) maxY = p.Y;
        }

        double cx = (minX + maxX) * 0.5;
        double cy = (minY + maxY) * 0.5;

        double shiftX = 0.5 - cx;
        double shiftY = 0.5 - cy;

        for (int i = 0; i < currentPoints.Count; i++)
        {
            var p = currentPoints[i];
            currentPoints[i] = new PathPoint(
                Math.Clamp(p.X + shiftX, 0.05, 0.95),
                Math.Clamp(p.Y + shiftY, 0.05, 0.95));
        }

        NotifyPathUpdated();
        InvalidateVisual();
    }

    /// <summary>
    /// 一键应用平滑样条滤波，消除手绘拐角生硬折点
    /// </summary>
    public void SmoothPath()
    {
        if (currentPoints.Count < 3) return;

        var smoothed = new List<PathPoint>(currentPoints.Count);
        int n = currentPoints.Count;

        for (int i = 0; i < n; i++)
        {
            if (!isClosedShape && (i == 0 || i == n - 1))
            {
                smoothed.Add(currentPoints[i]);
                continue;
            }

            int prev = (i - 1 + n) % n;
            int next = (i + 1) % n;

            double avgX = currentPoints[prev].X * 0.25 + currentPoints[i].X * 0.5 + currentPoints[next].X * 0.25;
            double avgY = currentPoints[prev].Y * 0.25 + currentPoints[i].Y * 0.5 + currentPoints[next].Y * 0.25;
            smoothed.Add(new PathPoint(Math.Round(avgX, 4), Math.Round(avgY, 4)));
        }

        currentPoints.Clear();
        currentPoints.AddRange(smoothed);
        drawMode = DrawToolMode.Curve;
        NotifyPathUpdated();
        InvalidateVisual();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        StartLivePreviewTimer();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        StopLivePreviewTimer();
    }

    private void StartLivePreviewTimer()
    {
        if (previewTimer != null) return;
        previewTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(16), DispatcherPriority.Render, (_, _) =>
        {
            if (!LivePreviewEnabled || currentPoints.Count < 2) return;
            AudioCaptureService.Instance.ReadSpectrum(previewSpectrum, model.Sensitivity, model.Smoothing);
            InvalidateVisual();
        });
        previewTimer.Start();
    }

    private void StopLivePreviewTimer()
    {
        if (previewTimer != null)
        {
            previewTimer.Stop();
            previewTimer = null;
        }
    }

    private Rect GetInnerDraftingRect()
    {
        double w = Math.Max(80.0, Bounds.Width - 2 * MarginPadding);
        double h = Math.Max(60.0, Bounds.Height - 2 * MarginPadding);
        return new Rect(MarginPadding, MarginPadding, w, h);
    }

    private Point NormalizedToCanvas(PathPoint p, Rect draftRect)
    {
        return new Point(draftRect.X + p.X * draftRect.Width, draftRect.Y + p.Y * draftRect.Height);
    }

    private PathPoint CanvasToNormalized(Point p, Rect draftRect)
    {
        double nx = Math.Clamp((p.X - draftRect.X) / draftRect.Width, 0.0, 1.0);
        double ny = Math.Clamp((p.Y - draftRect.Y) / draftRect.Height, 0.0, 1.0);
        return new PathPoint(Math.Round(nx, 4), Math.Round(ny, 4));
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        hoveredPointIndex = -1;
        InvalidateVisual();
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var pt = e.GetCurrentPoint(this);
        var pos = pt.Position;
        var draftRect = GetInnerDraftingRect();

        if (pt.Properties.IsLeftButtonPressed)
        {
            // 1. 优先检测是否点中已有节点（用于拖拽调整）
            draggingPointIndex = FindNearestPointIndex(pos, draftRect, thresholdPx: 14.0);

            if (draggingPointIndex >= 0)
            {
                Cursor = new Cursor(StandardCursorType.Hand);
                InvalidateVisual();
                e.Handled = true;
                return;
            }

            // 2. 点击空白区域：根据模式进行绘制
            var normPt = CanvasToNormalized(pos, draftRect);

            if (drawMode == DrawToolMode.Freehand)
            {
                isFreehandDrawing = true;
                currentPoints.Clear();
                currentPoints.Add(normPt);
            }
            else
            {
                // 折线或平滑曲线模式：点击添加新节点
                currentPoints.Add(normPt);
                draggingPointIndex = currentPoints.Count - 1;
            }

            NotifyPathUpdated();
            InvalidateVisual();
            e.Handled = true;
        }
        else if (pt.Properties.IsRightButtonPressed)
        {
            // 右键直接删除点击的节点
            int delIdx = FindNearestPointIndex(pos, draftRect, thresholdPx: 14.0);
            if (delIdx >= 0 && currentPoints.Count > 2)
            {
                currentPoints.RemoveAt(delIdx);
                hoveredPointIndex = -1;
                NotifyPathUpdated();
                InvalidateVisual();
                e.Handled = true;
            }
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var pt = e.GetCurrentPoint(this);
        currentPointerPos = pt.Position;
        var draftRect = GetInnerDraftingRect();

        // 检查悬停节点，给与手型光标与高亮光圈反馈
        int nearIdx = FindNearestPointIndex(currentPointerPos, draftRect, thresholdPx: 14.0);
        if (nearIdx != hoveredPointIndex)
        {
            hoveredPointIndex = nearIdx;
            Cursor = hoveredPointIndex >= 0 ? new Cursor(StandardCursorType.Hand) : new Cursor(StandardCursorType.Cross);
            InvalidateVisual();
        }

        if (isFreehandDrawing)
        {
            var normPt = CanvasToNormalized(currentPointerPos, draftRect);
            if (currentPoints.Count > 0)
            {
                var last = currentPoints[^1];
                double dx = (normPt.X - last.X) * draftRect.Width;
                double dy = (normPt.Y - last.Y) * draftRect.Height;
                if (Math.Sqrt(dx * dx + dy * dy) >= 8.0)
                {
                    currentPoints.Add(normPt);
                    NotifyPathUpdated();
                    InvalidateVisual();
                }
            }
        }
        else if (draggingPointIndex >= 0 && draggingPointIndex < currentPoints.Count)
        {
            currentPoints[draggingPointIndex] = CanvasToNormalized(currentPointerPos, draftRect);
            NotifyPathUpdated();
            InvalidateVisual();
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        var draftRect = GetInnerDraftingRect();

        if (isFreehandDrawing)
        {
            isFreehandDrawing = false;
            // 随手画释放后自动平滑一次
            if (currentPoints.Count >= 6)
            {
                var first = currentPoints[0];
                var last = currentPoints[^1];
                double dx = (first.X - last.X) * draftRect.Width;
                double dy = (first.Y - last.Y) * draftRect.Height;
                if (Math.Sqrt(dx * dx + dy * dy) < 26.0)
                {
                    isClosedShape = true;
                }
                SmoothPath();
            }
            NotifyPathUpdated();
            InvalidateVisual();
        }

        draggingPointIndex = -1;
        Cursor = hoveredPointIndex >= 0 ? new Cursor(StandardCursorType.Hand) : new Cursor(StandardCursorType.Arrow);
    }

    private int FindNearestPointIndex(Point pixelPos, Rect draftRect, double thresholdPx)
    {
        for (int i = 0; i < currentPoints.Count; i++)
        {
            Point p = NormalizedToCanvas(currentPoints[i], draftRect);
            double dist = Math.Sqrt((p.X - pixelPos.X) * (p.X - pixelPos.X) + (p.Y - pixelPos.Y) * (p.Y - pixelPos.Y));
            if (dist <= thresholdPx)
            {
                return i;
            }
        }
        return -1;
    }

    private void NotifyPathUpdated()
    {
        PathUpdated?.Invoke([.. currentPoints], isClosedShape, drawMode);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        var bounds = Bounds;
        if (bounds.Width < 50 || bounds.Height < 50) return;

        var draftRect = GetInnerDraftingRect();

        // 1. 画板外围安全透气底板
        var outerBg = new SolidColorBrush(Color.FromArgb(180, 10, 14, 20));
        var outerBorder = new Pen(new SolidColorBrush(Color.FromArgb(50, 0, 229, 255)), 1.0);
        context.DrawRectangle(outerBg, outerBorder, new Rect(0, 0, bounds.Width, bounds.Height), 10, 10);

        // 2. 内部核心绘图工作区底板 (Active Drafting Canvas)
        var draftBg = new SolidColorBrush(Color.FromArgb(240, 16, 22, 32));
        var draftBorder = new Pen(new SolidColorBrush(Color.FromArgb(100, 0, 229, 255)), 1.5);
        context.DrawRectangle(draftBg, draftBorder, draftRect, 6, 6);

        // 绘制微妙的辅助坐标网格
        var gridPen = new Pen(new SolidColorBrush(Color.FromArgb(20, 255, 255, 255)), 1.0);
        for (double x = draftRect.X + 20; x < draftRect.Right; x += 20)
        {
            context.DrawLine(gridPen, new Point(x, draftRect.Y), new Point(x, draftRect.Bottom));
        }
        for (double y = draftRect.Y + 20; y < draftRect.Bottom; y += 20)
        {
            context.DrawLine(gridPen, new Point(draftRect.X, y), new Point(draftRect.Right, y));
        }

        // 十字中心线
        var centerPen = new Pen(new SolidColorBrush(Color.FromArgb(40, 0, 240, 255)), 1.0, dashStyle: DashStyle.Dash);
        context.DrawLine(centerPen, new Point(draftRect.Center.X, draftRect.Y), new Point(draftRect.Center.X, draftRect.Bottom));
        context.DrawLine(centerPen, new Point(draftRect.X, draftRect.Center.Y), new Point(draftRect.Right, draftRect.Center.Y));

        // 3. 采样并渲染跳动的实时律动预览 (在安全边距内 100% 完整延伸展示！)
        if (currentPoints.Count >= 2)
        {
            var samples = PathGeometryHelper.SamplePath(
                currentPoints,
                isClosedShape,
                drawMode,
                draftRect.Size,
                Math.Min(36, model.BarCount));

            // 将采样的相对坐标平移到 draftRect.Position
            var offsetSamples = new List<SampledPoint>(samples.Count);
            foreach (var s in samples)
            {
                offsetSamples.Add(new SampledPoint(
                    new Point(draftRect.X + s.Position.X, draftRect.Y + s.Position.Y),
                    s.Tangent,
                    s.Normal));
            }

            if (offsetSamples.Count >= 2)
            {
                if (LivePreviewEnabled)
                {
                    var activeMags = previewSpectrum.AsSpan(0, Math.Min(offsetSamples.Count, previewSpectrum.Length));
                    double previewAmp = Math.Clamp(model.MaxAmplitude * 0.75, 12.0, 48.0);

                    // 绘制跳动的律动柱状条或波浪
                    if (model.VisualizerStyle == VisualizerStyle.Bars || model.VisualizerStyle == VisualizerStyle.Dots)
                    {
                        var bars = PathGeometryHelper.BuildBars(
                            offsetSamples,
                            activeMags,
                            model.RhythmDirection,
                            previewAmp,
                            minBarHeight: 3.5);

                        var rhythmPen = new Pen(new SolidColorBrush(Color.FromArgb(180, 0, 240, 255)), 3.0, lineCap: PenLineCap.Round);
                        var glowPen = new Pen(new SolidColorBrush(Color.FromArgb(50, 0, 240, 255)), 7.0, lineCap: PenLineCap.Round);

                        foreach (var (start, end) in bars)
                        {
                            context.DrawLine(glowPen, start, end);
                            context.DrawLine(rhythmPen, start, end);
                        }
                    }
                    else
                    {
                        var waveGeom = PathGeometryHelper.BuildLineWaveGeometry(
                            offsetSamples,
                            activeMags,
                            model.RhythmDirection,
                            previewAmp,
                            isClosedShape);

                        var glowPen = new Pen(new SolidColorBrush(Color.FromArgb(60, 0, 240, 255)), 7.0, lineCap: PenLineCap.Round);
                        var wavePen = new Pen(new SolidColorBrush(Color.FromArgb(200, 0, 240, 255)), 2.5, lineCap: PenLineCap.Round);
                        context.DrawGeometry(null, glowPen, waveGeom);
                        context.DrawGeometry(null, wavePen, waveGeom);
                    }
                }

                // 绘制当前基准骨干路径（高对比度荧光粉红）
                var baseGeom = PathGeometryHelper.BuildBaseGeometry(offsetSamples, isClosedShape);
                var baseGlowPen = new Pen(new SolidColorBrush(Color.FromArgb(60, 255, 0, 128)), 5.0);
                var basePathPen = new Pen(new SolidColorBrush(Color.Parse("#FF007F")), 2.2);
                context.DrawGeometry(null, baseGlowPen, baseGeom);
                context.DrawGeometry(null, basePathPen, baseGeom);
            }

            // 4. 绘制清晰大号可交互控制点 handles
            var handleFill = new SolidColorBrush(Color.Parse("#00F0FF"));
            var handleHoverFill = new SolidColorBrush(Colors.White);
            var handleBorder = new Pen(new SolidColorBrush(Colors.Black), 2.0);
            var haloPen = new Pen(new SolidColorBrush(Color.FromArgb(120, 0, 240, 255)), 3.0);

            for (int i = 0; i < currentPoints.Count; i++)
            {
                Point p = NormalizedToCanvas(currentPoints[i], draftRect);
                bool isHover = (i == hoveredPointIndex || i == draggingPointIndex);

                if (isHover)
                {
                    context.DrawEllipse(null, haloPen, p, 11.0, 11.0);
                    context.DrawEllipse(handleHoverFill, handleBorder, p, 7.0, 7.0);
                }
                else
                {
                    context.DrawEllipse(handleFill, handleBorder, p, 5.5, 5.5);
                }
            }
        }
        else
        {
            var hintBrush = new SolidColorBrush(Color.FromArgb(140, 255, 255, 255));
            var ft = new FormattedText(
                "请在此处点击添加节点或按住鼠标自由手绘线条",
                System.Globalization.CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight,
                Typeface.Default,
                12,
                hintBrush);
            context.DrawText(ft, new Point(draftRect.Center.X - ft.Width * 0.5, draftRect.Center.Y - ft.Height * 0.5));
        }

        // 5. 顶部尺寸与状态标签
        var tagBrush = new SolidColorBrush(Color.FromArgb(150, 0, 229, 255));
        var sizeTag = new FormattedText(
            $"工作区比例: {(int)Math.Round(draftRect.Width)} × {(int)Math.Round(draftRect.Height)} px (外围律动延展安全区已就绪)",
            System.Globalization.CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            Typeface.Default,
            10,
            tagBrush);
        context.DrawText(sizeTag, new Point(draftRect.X + 4, draftRect.Y - 18));
    }
}
