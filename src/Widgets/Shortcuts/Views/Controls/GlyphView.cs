using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Shortcuts.Views.Controls;

/// <summary>
/// Draws one stroke/fill glyph, fitted and centred inside the control's box.
/// <para>
/// A plain <c>Path</c> with <c>Stretch="Uniform"</c> leaves the leftover space of a non-square
/// glyph on the right/bottom edge (Avalonia 11 aligns the fitted result to the top-left), so
/// glyphs with a non-square bounding box sat visibly off-centre inside their badges — the lock
/// was 2.5px left of its square, the bolt 1.5px. This control computes the fit itself: the
/// geometry's visual bounds are scaled to fit the box and centred exactly on both axes. The
/// pen width is compensated for the fit scale, so the stroke renders at the requested
/// thickness whatever the scale — matching what the icon was designed to look like.
/// </para>
/// </summary>
public class GlyphView : Control
{
    /// <summary>Glyph outline (glyphs live in a 24x24 design space; the fit is automatic).</summary>
    public static readonly StyledProperty<Geometry?> DataProperty =
        AvaloniaProperty.Register<GlyphView, Geometry?>(nameof(Data));

    /// <summary>Stroke brush; null draws no outline.</summary>
    public static readonly StyledProperty<IBrush?> StrokeProperty =
        AvaloniaProperty.Register<GlyphView, IBrush?>(nameof(Stroke));

    /// <summary>Fill brush; null draws just the stroke.</summary>
    public static readonly StyledProperty<IBrush?> FillProperty =
        AvaloniaProperty.Register<GlyphView, IBrush?>(nameof(Fill));

    /// <summary>Visible stroke width, in the control's own pixels (not scaled by the fit).</summary>
    public static readonly StyledProperty<double> StrokeThicknessProperty =
        AvaloniaProperty.Register<GlyphView, double>(nameof(StrokeThickness), 1.7);

    public static readonly StyledProperty<PenLineCap> StrokeLineCapProperty =
        AvaloniaProperty.Register<GlyphView, PenLineCap>(nameof(StrokeLineCap), PenLineCap.Round);

    public static readonly StyledProperty<PenLineJoin> StrokeLineJoinProperty =
        AvaloniaProperty.Register<GlyphView, PenLineJoin>(nameof(StrokeLineJoin), PenLineJoin.Round);

    static GlyphView()
    {
        AffectsMeasure<GlyphView>(DataProperty, StrokeThicknessProperty);
        AffectsRender<GlyphView>(
            DataProperty, StrokeProperty, FillProperty, StrokeThicknessProperty,
            StrokeLineCapProperty, StrokeLineJoinProperty);
    }

    public Geometry? Data
    {
        get => GetValue(DataProperty);
        set => SetValue(DataProperty, value);
    }

    public IBrush? Stroke
    {
        get => GetValue(StrokeProperty);
        set => SetValue(StrokeProperty, value);
    }

    public IBrush? Fill
    {
        get => GetValue(FillProperty);
        set => SetValue(FillProperty, value);
    }

    public double StrokeThickness
    {
        get => GetValue(StrokeThicknessProperty);
        set => SetValue(StrokeThicknessProperty, value);
    }

    public PenLineCap StrokeLineCap
    {
        get => GetValue(StrokeLineCapProperty);
        set => SetValue(StrokeLineCapProperty, value);
    }

    public PenLineJoin StrokeLineJoin
    {
        get => GetValue(StrokeLineJoinProperty);
        set => SetValue(StrokeLineJoinProperty, value);
    }

    /// <summary>Natural size (glyph bounds inflated by the stroke) when no explicit size is set.</summary>
    protected override Size MeasureOverride(Size availableSize)
    {
        if (Data is not { } geometry) return default;

        var bounds = geometry.Bounds;
        if (bounds.Width <= 0 || bounds.Height <= 0) return default;

        var pad = Math.Max(0, StrokeThickness) / 2;
        return new Size(bounds.Width + (pad * 2), bounds.Height + (pad * 2));
    }

    public override void Render(DrawingContext context)
    {
        if (Data is not { } geometry) return;
        if (Stroke is null && Fill is null) return;

        var box = Bounds.Size;
        if (box.Width <= 0 || box.Height <= 0) return;

        var source = geometry.Bounds;
        if (source.Width <= 0 || source.Height <= 0) return;

        var scale = Math.Min(box.Width / source.Width, box.Height / source.Height);
        if (!double.IsFinite(scale) || scale <= 0) return;

        // Map the geometry's visual bounds centre onto the box centre.
        var dx = ((box.Width - (source.Width * scale)) / 2) - (source.X * scale);
        var dy = ((box.Height - (source.Height * scale)) / 2) - (source.Y * scale);

        using (context.PushTransform(new Matrix(scale, 0, 0, scale, dx, dy)))
        {
            // The pushed transform scales the pen too: divide it out so the visible stroke
            // width stays exactly StrokeThickness regardless of the fit scale.
            Pen? pen = null;
            if (Stroke is { } stroke && StrokeThickness > 0)
            {
                pen = new Pen(stroke, StrokeThickness / scale)
                {
                    LineCap = StrokeLineCap,
                    LineJoin = StrokeLineJoin,
                };
            }

            context.DrawGeometry(Fill, pen, geometry);
        }
    }
}
