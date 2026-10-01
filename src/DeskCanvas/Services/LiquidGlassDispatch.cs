using SkiaSharp;
using DeskCanvas.Core.Models.Settings;

namespace DeskCanvas.Services;

/// <summary>
/// Routes a wallpaper-sampled glass render to the optical model of the frame's active material:
/// <see cref="SurfaceStyle.LiquidGlassV2"/> goes to <see cref="LiquidGlassV2Renderer"/>, the
/// older 液态玻璃/柔光 recipes stay on <see cref="LiquidGlassRenderer"/>.
/// <para>
/// Every wallpaper-sampled glass render funnels through here — the CPU fallback of
/// <see cref="LiquidGlassSurface"/> and the panel/clock render paths — so a material switch
/// never leaves a card on the wrong recipe.
/// </para>
/// </summary>
public static class LiquidGlassDispatch
{
    /// <summary>Render one background to PNG bytes with the frame's material.</summary>
    public static byte[] Render(LiquidGlassRenderer.Frame frame, WallpaperSnapshot wallpaper) =>
        frame.Theme.EffectiveSurface == SurfaceStyle.LiquidGlassV2
            ? LiquidGlassV2Renderer.Render(frame, wallpaper)
            : LiquidGlassRenderer.Render(frame, wallpaper);

    /// <summary>Render one background bitmap with the frame's material; the caller owns the result.</summary>
    public static SKBitmap? RenderBitmap(LiquidGlassRenderer.Frame frame, WallpaperSnapshot wallpaper) =>
        frame.Theme.EffectiveSurface == SurfaceStyle.LiquidGlassV2
            ? LiquidGlassV2Renderer.RenderBitmap(frame, wallpaper)
            : LiquidGlassRenderer.RenderBitmap(frame, wallpaper);

    /// <summary>
    /// Cache-key fragment naming the active material and the optics that change what a render
    /// looks like — used by the material caches to tell recipes apart.
    /// </summary>
    public static string OpticsKey(Theme theme)
    {
        if (theme.EffectiveSurface == SurfaceStyle.LiquidGlassV2)
        {
            var v2 = theme.EffectiveLiquidGlassV2;
            return $"v2_{v2.Blur}_{v2.Refraction}_{v2.Highlight}_{v2.Vibrancy}_{v2.Dispersion}";
        }
        var legacy = theme.EffectiveLiquidGlass;
        return $"lg_{legacy.Blur}_{legacy.Refraction}_{legacy.LightAngle}_{legacy.Glow}_{legacy.Spectrum}";
    }
}
