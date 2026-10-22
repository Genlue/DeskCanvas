using Avalonia.Controls;
using Avalonia.Media;

namespace DeskCanvas.Services;

/// <summary>
/// Applies the text antialiasing <see cref="PanelAppearanceResolver.ResolveTextRenderingMode"/>
/// picks for a card material to a window's whole text pipeline.
/// <para>
/// Both attached properties are written because Avalonia resolves the effective mode through
/// them in a layered fallback (RenderOptions ↔ TextOptions, Unspecified inherits the other
/// instance) — setting both pins the decision regardless of which one a given draw path
/// consults first. The trailing repaint matters for runtime material swaps: attached-property
/// change alone does not guarantee the compositor re-rasterizes the glyphs, and a 毛玻璃 →
/// 多彩 flip must not leave the old mode's raster on screen.
/// </para>
/// </summary>
public static class SurfaceTextRendering
{
    /// <summary>Pin <paramref name="mode"/> onto <paramref name="window"/>'s text pipeline.</summary>
    public static void Apply(Window window, TextRenderingMode mode)
    {
        TextOptions.SetTextRenderingMode(window, mode);
        RenderOptions.SetTextRenderingMode(window, mode);
        window.InvalidateVisual();
    }
}
