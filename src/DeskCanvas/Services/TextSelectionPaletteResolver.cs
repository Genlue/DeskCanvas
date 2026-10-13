using System;
using Avalonia;
using Avalonia.Media;
using Avalonia.Styling;

namespace DeskCanvas.Services;

/// <summary>The paired text-selection colours: an opaque background plus the readable foreground.</summary>
public readonly record struct TextSelectionPalette(Color Background, Color Foreground);

/// <summary>
/// Resolves the text-selection pair from the accent actually rendered behind selected text.
/// The old contract painted the raw accent under a fixed white foreground, which collides as
/// soon as the accent is bright (yellow, light cyan) — or pure white in 黑白's dark mode.
/// Instead of guessing from the theme depth or inverting RGB, the real brush is resolved with
/// the target variant, turned into an opaque solid, and the WCAG relative-luminance contrast
/// picks whichever of black/white is more readable (at the crossover both sit at ≈4.6:1, so
/// the choice is always ≥ 4.5:1).
/// </summary>
public static class TextSelectionPaletteResolver
{
    /// <summary>The brush the selection background is derived from (same source the old rule used).</summary>
    public const string AccentBrushKey = "SystemControlHighlightAccentBrush";

    /// <summary>Fallback source when the brush itself cannot be resolved.</summary>
    public const string AccentColorKey = "SystemAccentColor";

    /// <summary>Predictable fallback pairs for unresolvable/gradient accents (never a guess).</summary>
    public static TextSelectionPalette LightFallback { get; } = new(Color.Parse("#0F6CBD"), Colors.White);

    /// <inheritdoc cref="LightFallback" />
    public static TextSelectionPalette DarkFallback { get; } = new(Color.Parse("#4CC2FF"), Colors.Black);

    /// <summary>
    /// Resolves the pair for one theme variant. Uses a variant-aware lookup on purpose: a
    /// variant-less lookup always reads the Default (= light) dictionaries, so a dark window
    /// would get the light result even though its accent may differ (黑白, 背景色, system accent).
    /// </summary>
    public static TextSelectionPalette Resolve(ThemeVariant variant, TextSelectionPalette fallback)
    {
        var accent = TryResolveAccentColor(variant);
        return accent.HasValue ? Compute(accent.Value) : fallback;
    }

    /// <summary>
    /// The accent colour in effect for the variant: the solid color behind selected text when
    /// resolvable, otherwise the accent color itself. Returns null when neither is available
    /// (or the accent is not a solid colour, e.g. a gradient) — callers use the fallback pair.
    /// </summary>
    public static Color? TryResolveAccentColor(ThemeVariant variant)
    {
        var app = Application.Current;
        if (app is null) return null;

        if (app.TryGetResource(AccentBrushKey, variant, out var value))
        {
            switch (value)
            {
                case ISolidColorBrush brush:
                    return brush.Color;
                case Color color:
                    return color;
            }
        }

        if (app.TryGetResource(AccentColorKey, variant, out var raw) && raw is Color accent)
            return accent;

        return null;
    }

    /// <summary>
    /// Computes the pair for one accent colour. The background stays an opaque solid — glass or
    /// wallpapers behind a translucent accent would make the contrast basis unknowable — so the
    /// RGB is kept and alpha normalised to 255.
    /// </summary>
    public static TextSelectionPalette Compute(Color accent)
    {
        var background = Color.FromRgb(accent.R, accent.G, accent.B);
        return new TextSelectionPalette(background, PickForeground(background));
    }

    /// <summary>Black or white, whichever contrasts more with the background.</summary>
    public static Color PickForeground(Color background)
        => ContrastRatio(background, Colors.Black) >= ContrastRatio(background, Colors.White)
            ? Colors.Black
            : Colors.White;

    /// <summary>WCAG 2.x contrast ratio between two opaque colours.</summary>
    public static double ContrastRatio(Color a, Color b)
    {
        var lumA = RelativeLuminance(a);
        var lumB = RelativeLuminance(b);
        var (hi, lo) = lumA >= lumB ? (lumA, lumB) : (lumB, lumA);
        return (hi + 0.05) / (lo + 0.05);
    }

    /// <summary>WCAG relative luminance (sRGB channels linearised first).</summary>
    public static double RelativeLuminance(Color color)
        => 0.2126 * LinearChannel(color.R)
         + 0.7152 * LinearChannel(color.G)
         + 0.0722 * LinearChannel(color.B);

    private static double LinearChannel(byte channel)
    {
        var c = channel / 255.0;
        return c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
    }
}
