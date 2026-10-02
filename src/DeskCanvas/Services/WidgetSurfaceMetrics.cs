using System;
using Avalonia;
using Avalonia.Media;
using DeskCanvas.Core.Models.Settings;

namespace DeskCanvas.Services;

/// <summary>
/// Pure geometry/optics helpers shared by the desktop widget window and the sidebar widget host,
/// so both resolve the same adaptive corner radius for a given card.
/// </summary>
public static class WidgetSurfaceMetrics
{
    /// <summary>
    /// Adaptively scale corner radius for widgets of different sizes.
    /// 1x1 small widgets scale to ~55% (iOS app icon style, balanced curvature);
    /// 1xN or Nx1 strip widgets scale to ~72% (prevents excessive rounding on the short edge);
    /// standard and large widgets (2x2, 4x2, …) keep the full configured radius.
    /// </summary>
    /// <param name="baseRadius">Configured radius (theme/dimensions).</param>
    /// <param name="columns">Current column span.</param>
    /// <param name="rows">Current row span.</param>
    /// <param name="width">Card cell width (DIPs).</param>
    /// <param name="height">Card cell height (DIPs).</param>
    /// <param name="margin">Grid margin subtracted from each side (0 outside the manual grid).</param>
    public static double ResolveEffectiveRadius(double baseRadius, int columns, int rows,
        double width, double height, double margin)
    {
        if (baseRadius <= 0) return 0;

        var cardW = Math.Max(1, width - 2 * margin);
        var cardH = Math.Max(1, height - 2 * margin);
        var minSide = Math.Min(cardW, cardH);

        // 1x1 small widget (single file, icon tile, or <= 90px square)
        if ((columns <= 1 && rows <= 1) || minSide <= 90)
            return Math.Max(4, Math.Round(baseRadius * 0.55));

        // 1xN or Nx1 strip widget (e.g. 2x1, 4x1, 1x2, 1x4, or short edge <= 125px)
        if (columns <= 1 || rows <= 1 || minSide <= 125)
            return Math.Max(6, Math.Round(baseRadius * 0.72));

        return baseRadius;
    }

    /// <summary>
    /// Approximate the grid span a pixel size represents, for the adaptive radius heuristic
    /// outside the manual grid (unit = cell size + margin).
    /// </summary>
    public static (int Columns, int Rows) SpanFromSize(double width, double height, double unit)
    {
        if (unit <= 0) return (1, 1);
        return (
            Math.Max(1, (int)Math.Round(width / unit)),
            Math.Max(1, (int)Math.Round(height / unit)));
    }

    /// <summary>
    /// Whether the card should draw the outline highlight ring: glass with outline width &gt; 0
    /// (or the colorful card rim), no native frame, not frameless, not a stack container.
    /// </summary>
    public static bool IsOutlined(Theme theme, bool frameless, bool isStack) =>
        !frameless
        && ((theme.IsGlass && theme.OutlineWidth > 0) || theme.IsColorful)
        && !theme.UseNativeFrame
        && !isStack;

    /// <summary>Highlight ring thickness (DIPs), 0 when the surface is not outlined glass.</summary>
    public static Avalonia.Thickness OutlineThickness(Theme theme, bool frameless, bool isStack, bool isSelfFraming)
    {
        if (!IsOutlined(theme, frameless, isStack)) return new Avalonia.Thickness(0);
        if (theme.IsColorful && isSelfFraming) return new Avalonia.Thickness(0);
        var width = Math.Clamp(theme.OutlineWidth, 0, 6);
        if (width <= 0 && theme.IsColorful) return new Avalonia.Thickness(1);
        return new Avalonia.Thickness(width);
    }

    /// <summary>
    /// Highlight ring brush for outlined glass: strongest at the top-left and bottom-right corners,
    /// fading linearly along every edge to nothing at the other two corners. Built as a conic
    /// gradient whose sweep starts at the actual top-left corner, so the fades follow the real
    /// corners for any aspect ratio.
    /// </summary>
    /// <param name="theme">Active theme.</param>
    /// <param name="width">Card width (DIPs).</param>
    /// <param name="height">Card height (DIPs).</param>
    /// <param name="frameless">Frameless widgets draw their own material.</param>
    /// <param name="isStack">Stack containers have no ring.</param>
    /// <param name="isSelfFraming">Edge-to-edge views (Note/Map) keep no ring in colorful mode.</param>
    public static IBrush? OutlineBrush(Theme theme, double width, double height, bool frameless, bool isStack, bool isSelfFraming)
    {
        if (!IsOutlined(theme, frameless, isStack)) return null;
        if (theme.IsColorful && isSelfFraming) return null;
        if (theme.OutlineWidth <= 0) return null; // the colorful rim falls back to a resource brush

        width = Math.Max(1, width);
        height = Math.Max(1, height);
        var cornerAngle = Math.Atan2(width / 2.0, height / 2.0) * 180.0 / Math.PI;
        var startAngle = 360.0 - cornerAngle;
        var color = Color.TryParse(theme.EffectiveOutlineColor, out var parsed)
            ? parsed
            : Color.Parse(Theme.DefaultOutlineColor);
        var clear = Colors.Transparent;

        return new ConicGradientBrush
        {
            Angle = startAngle,
            Center = RelativePoint.Center,
            GradientStops =
            {
                new GradientStop(color, 0),
                new GradientStop(clear, 2 * cornerAngle / 360.0),
                new GradientStop(color, 0.5),
                new GradientStop(clear, 0.5 + 2 * cornerAngle / 360.0),
                new GradientStop(color, 1.0)
            }
        };
    }
}
