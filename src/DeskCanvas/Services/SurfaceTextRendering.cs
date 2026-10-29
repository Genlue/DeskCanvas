using Avalonia.Controls;
using Avalonia.Media;

namespace DeskCanvas.Services;

/// <summary>
/// Applies the text antialiasing <see cref="PanelAppearanceResolver.ResolveTextRenderingMode"/>
/// picks for a card material to a window's whole text pipeline.
/// <para>
/// Avalonia 12 retired the parallel <c>RenderOptions.TextRenderingMode</c> attached property —
/// <see cref="TextOptions"/> owns the setting now, and writing it there is what every draw path
/// reads. The trailing repaint matters for runtime material swaps: an attached-property change
/// alone does not guarantee the compositor re-rasterizes the glyphs, and a 毛玻璃 → 多彩 flip
/// must not leave the old mode's raster on screen.
/// </para>
/// </summary>
public static class SurfaceTextRendering
{
    /// <summary>Pin <paramref name="mode"/> onto <paramref name="window"/>'s text pipeline.</summary>
    public static void Apply(Window window, TextRenderingMode mode)
    {
        TextOptions.SetTextRenderingMode(window, mode);
        window.InvalidateVisual();
    }
}
