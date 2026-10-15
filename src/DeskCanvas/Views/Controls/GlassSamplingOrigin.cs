using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;

namespace DeskCanvas.Views.Controls;

/// <summary>
/// Shared wallpaper-sampling geometry for every rendered-glass consumer —
/// <see cref="LiquidGlassSurface.BuildFrame"/> and the frameless clock's glyph renderer.
/// <para>
/// Two invariants the whole glass theme hangs on, kept in ONE place so the two frame builders
/// cannot drift apart again:
/// </para>
/// <list type="bullet">
/// <item>The sampling origin is the surface's <b>layout</b> position, computed by walking
/// <see cref="Visual.Bounds.TopLeft"/> up to the window and mapping that offset through
/// <c>window.PointToScreen</c> once. Ancestor <c>RenderTransform</c>s (the panel shell's open/close
/// zoom, a widget's content scale) are deliberately NOT composed: Avalonia's per-visual
/// <c>PointToScreen</c> folds every transform on the way up, so a surface sampled mid-zoom would
/// bake the interpolated position into its material and keep sampling the wrong wallpaper region
/// for its whole lifetime. With no transform on the path the walk is exactly equal to the raw
/// <c>PointToScreen</c> — pure layout offsets — so making it the default shifts nothing that was
/// already correct.</item>
/// <item>The desktop extent (origin + size of the whole virtual desktop, and the owning screen's
/// offset inside it) is the same Min/Max over <see cref="Window.Screens"/> for every consumer, so
/// one shared backdrop stays aligned with every card's <c>DesktopX/DesktopY</c>.</item>
/// </list>
/// </summary>
public static class GlassSamplingOrigin
{
    /// <summary>
    /// The surface's top-left corner in screen (physical) pixels, computed from layout offsets
    /// (<see cref="Visual.Bounds.TopLeft"/>) only — every <c>RenderTransform</c> on the way up is
    /// deliberately skipped, so an ancestor's animation never shifts the sampled wallpaper region.
    /// Falls back to the regular <c>PointToScreen</c> when the walk does not reach the window
    /// (an unexpected tree shape must not break the material).
    /// </summary>
    public static PixelPoint Resolve(Visual surface, Window? window)
    {
        if (window == null) return default;
        var offset = default(Point);
        var visual = surface;
        while (visual != null && !ReferenceEquals(visual, window))
        {
            var topLeft = visual.Bounds.TopLeft;
            offset = new Point(offset.X + topLeft.X, offset.Y + topLeft.Y);
            visual = visual.GetVisualParent();
        }

        return ReferenceEquals(visual, window) ? window.PointToScreen(offset) : surface.PointToScreen(default);
    }

    /// <summary>The virtual desktop extent in physical pixels: origin and size across all screens.</summary>
    public static (PixelPoint Origin, PixelSize Size) DesktopExtent(Window? window)
    {
        var screens = window?.Screens.All;
        var left = screens?.Min(s => s.Bounds.X) ?? 0;
        var top = screens?.Min(s => s.Bounds.Y) ?? 0;
        var width = (screens?.Max(s => s.Bounds.Right) ?? 1920) - left;
        var height = (screens?.Max(s => s.Bounds.Bottom) ?? 1080) - top;
        return (new PixelPoint(left, top), new PixelSize(width, height));
    }

    /// <summary>
    /// The desktop-space values both frame builders feed into <see cref="LiquidGlassRenderer.Frame"/>
    /// — the card's origin inside the desktop and the desktop/screen extents — scaled by
    /// <paramref name="originScale"/>. The GPU-backed surface passes its per-card quality factor
    /// there; the frameless clock has no quality concept and passes 1.0. One function so the two
    /// conventions cannot drift (they silently did: the clock's copy grew without the quality
    /// multiplier and without the surface's later fixes).
    /// </summary>
    public static (float DesktopX, float DesktopY, float DesktopWidth, float DesktopHeight,
        float ScreenX, float ScreenY, float ScreenWidth, float ScreenHeight) FrameDesktopGeometry(
            Window? window, Visual origin, double originScale)
    {
        var (desktopOrigin, desktopSize) = DesktopExtent(window);
        var position = Resolve(origin, window);
        var screen = window?.Screens.ScreenFromWindow(window);
        return (
            (float)((position.X - desktopOrigin.X) * originScale),
            (float)((position.Y - desktopOrigin.Y) * originScale),
            (float)(desktopSize.Width * originScale),
            (float)(desktopSize.Height * originScale),
            (float)(((screen?.Bounds.X ?? 0) - desktopOrigin.X) * originScale),
            (float)(((screen?.Bounds.Y ?? 0) - desktopOrigin.Y) * originScale),
            (float)((screen?.Bounds.Width ?? 1920) * originScale),
            (float)((screen?.Bounds.Height ?? 1080) * originScale));
    }
}
