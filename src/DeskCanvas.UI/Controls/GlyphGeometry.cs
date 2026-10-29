using System;
using Avalonia;
using Avalonia.Media;

namespace DeskCanvas.UI.Controls;

/// <summary>
/// Geometry helpers for hand-authored icon paths (开发规范 §4.4).
/// <para>
/// <b>Why this exists.</b> Avalonia's <see cref="Avalonia.Controls.Shapes.Shape.Stretch"/> scales a
/// geometry to its <see cref="Geometry.Bounds"/> and anchors that box at the control's <b>top-left
/// corner</b> — it has no centring alignment (unlike WPF's <c>Stretch.Uniform</c>). A glyph whose ink
/// is 14 × 20 inside an 11 × 11 box therefore scales to 7.7 × 11 and sits against the left edge:
/// 1.65 DIP of air on the right, none on the left, which reads as "图标不在按钮中心". This is the
/// reported 剪贴板面板图钉 defect, and it affects every non-square mark equally.
/// </para>
/// <para>
/// <b>The fix.</b> Frame the ink on a square canvas of the size it was authored for (24 × 24 for the
/// Material-derived marks). A square canvas fills a square box exactly on both axes, so nothing is
/// left over to be mis-anchored, and the glyph's designed position inside that canvas is preserved.
/// </para>
/// <para>
/// The frame is an <c>IsFilled = false</c> two-point figure, so it contributes its corners to
/// <see cref="Geometry.Bounds"/> and draws nothing for a filled path. Use it for <b>filled</b>
/// glyphs only: a stroked path would draw the frame's diagonal line. Stroked marks must be authored
/// with square bounds around their own centre instead (see <c>ToolGlyphs.Search</c>).
/// </para>
/// </summary>
public static class GlyphGeometry
{
    /// <summary>Default canvas the bundled icon data is authored on (Material's 24 × 24 grid).</summary>
    public const double DefaultCanvas = 24;

    /// <summary>
    /// Frame <paramref name="glyph"/> on a square canvas so a square <c>Path</c> box centres it.
    /// The canvas is anchored at the origin, which is where the icon data is authored.
    /// </summary>
    public static Geometry Square(Geometry glyph, double canvas = DefaultCanvas)
    {
        ArgumentNullException.ThrowIfNull(glyph);

        // The frame is centred on the INK, not on the authored origin: icon artwork is rarely centred
        // in its own coordinate box (a clipboard silhouette sits one unit high, a gear a unit right),
        // so framing from (0,0) would preserve that offset and the mark would still be off centre.
        var centre = glyph.Bounds.Center;
        var half = canvas / 2;

        var frame = new PathGeometry();
        var figure = new PathFigure
        {
            StartPoint = new Point(centre.X - half, centre.Y - half),
            IsFilled = false,
            IsClosed = false,
        };
        figure.Segments!.Add(new LineSegment
        {
            Point = new Point(centre.X + half, centre.Y + half),
        });
        frame.Figures!.Add(figure);

        var group = new GeometryGroup();
        group.Children!.Add(glyph);
        group.Children.Add(frame);
        return group;
    }

    /// <summary>
    /// True when <paramref name="geometry"/> has square bounds within
    /// <paramref name="tolerance"/> DIP — the invariant a square <c>Path</c> box relies on.
    /// Exposed for the icon-centring checks.
    /// </summary>
    public static bool IsSquare(Geometry geometry, double tolerance = 0.75)
    {
        var bounds = geometry.Bounds;
        if (bounds.Width <= 0 || bounds.Height <= 0) return false;
        return Math.Abs(bounds.Width - bounds.Height) <= tolerance;
    }
}
