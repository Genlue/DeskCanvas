using System;
using Avalonia.Media;

namespace Shortcuts.Services;

/// <summary>Small color helpers shared by the card tiles and the editor blocks.</summary>
public static class Visuals
{
    // Tile/row colors come from a small closed set (the palette, the category colors and the
    // default accent), so caching the brushes is bounded in practice; the cap is only a safety
    // net against a hand-edited document introducing unbounded distinct colors.
    private const int CacheCap = 128;

    private static readonly Dictionary<string, Color> ColorCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<Color, SolidColorBrush> BrushCache = new();
    private static readonly Dictionary<(Color Base, byte Alpha), SolidColorBrush> TintCache = new();
    private static readonly Dictionary<Color, SolidColorBrush> ShadeCache = new();
    private static readonly Dictionary<(Color Base, double Factor), SolidColorBrush> LightenCache = new();

    /// <summary>Parse "#RRGGBB" / "#AARRGGBB"; falls back to the accent purple.</summary>
    public static Color ParseColor(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) return Color.Parse("#534AB7");
        if (ColorCache.TryGetValue(hex, out var cached)) return cached;

        var color = Color.Parse("#534AB7");
        try
        {
            color = Color.Parse(hex.Trim());
        }
        catch
        {
            // Keep the fallback.
        }

        ColorCache[hex] = color;
        return color;
    }

    /// <summary>Opaque brush for the color; instances are cached and safe to share (never mutated).</summary>
    public static IBrush Brush(string? hex) => GetBrush(ParseColor(hex));

    /// <summary>A translucent tint of the color (tile backgrounds).</summary>
    public static IBrush Tint(string? hex, byte alpha = 0x2E)
    {
        var c = ParseColor(hex);
        var key = (c, alpha);
        if (TintCache.TryGetValue(key, out var cached)) return cached;

        if (TintCache.Count >= CacheCap) TintCache.Clear();
        var brush = new SolidColorBrush(Color.FromArgb(alpha, c.R, c.G, c.B));
        TintCache[key] = brush;
        return brush;
    }

    /// <summary>Darker variant for text on a light tint.</summary>
    public static IBrush Shade(string? hex)
    {
        var c = ParseColor(hex);
        if (ShadeCache.TryGetValue(c, out var cached)) return cached;

        const double f = 0.72;
        var brush = new SolidColorBrush(Color.FromRgb((byte)(c.R * f), (byte)(c.G * f), (byte)(c.B * f)));
        if (ShadeCache.Count >= CacheCap) ShadeCache.Clear();
        ShadeCache[c] = brush;
        return brush;
    }

    /// <summary>
    /// Lighter variant for text on a dark tint — the dark-theme counterpart of <see cref="Shade"/>.
    /// Channels clamp at 255; the factor is a constant per caller so the cache stays bounded.
    /// </summary>
    public static IBrush Lighten(string? hex, double factor = 1.5)
    {
        var c = ParseColor(hex);
        var key = (c, factor);
        if (LightenCache.TryGetValue(key, out var cached)) return cached;

        if (LightenCache.Count >= CacheCap) LightenCache.Clear();
        var brush = new SolidColorBrush(Color.FromRgb(
            (byte)Math.Min(255, c.R * factor),
            (byte)Math.Min(255, c.G * factor),
            (byte)Math.Min(255, c.B * factor)));
        LightenCache[key] = brush;
        return brush;
    }

    private static SolidColorBrush GetBrush(Color color)
    {
        if (BrushCache.TryGetValue(color, out var cached)) return cached;

        if (BrushCache.Count >= CacheCap) BrushCache.Clear();
        var brush = new SolidColorBrush(color);
        BrushCache[color] = brush;
        return brush;
    }
}
