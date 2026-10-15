using Avalonia.Media;
using Avalonia.Styling;
using DeskCanvas.Core.Models.Settings;

namespace DeskCanvas.Services;

/// <summary>
/// Pure resolver that derives an immutable <see cref="ResolvedThemeProfile"/> from a raw theme and variant.
/// Guaranteed single source of truth across application-level chrome, window-level focus coordination,
/// and unit tests.
/// </summary>
public static class ThemeProfileResolver
{
    /// <summary>
    /// Resolves the effective theme profile for the given theme and theme variant.
    /// Colorful surfaces enforce a fixed color policy that completely ignores user accents and monochrome settings.
    /// Other surfaces follow the standard accent and monochrome pipeline.
    /// </summary>
    public static ResolvedThemeProfile Resolve(Theme rawTheme, ThemeVariant variant, AppSettings? settings = null)
    {
        var theme = rawTheme.NormalizeForSurface();

        if (theme.IsColorful)
        {
            return new ResolvedThemeProfile(
                Theme: theme,
                Variant: variant,
                Surface: theme.EffectiveSurface,
                ColorPolicy: ThemeColorPolicy.ColorfulFixed,
                MonochromeVariant: null,
                AccentColor: null,
                ScopeSignature: $"ColorfulFixed:{variant.Key}:{theme.EffectiveSurface}:rev1");
        }

        var monochromeVariant = theme.Monochrome ? theme.EffectiveMonochromeVariant : (MonochromeStyle?)null;
        Color? accentColor = theme.AccentColor != null && Color.TryParse(theme.AccentColor, out var parsed)
            ? parsed
            : null;
        var scopeSignature = $"Normal:{variant.Key}:{theme.EffectiveSurface}:mono={monochromeVariant}:accent={theme.AccentColor ?? "system"}:rev1";

        return new ResolvedThemeProfile(
            Theme: theme,
            Variant: variant,
            Surface: theme.EffectiveSurface,
            ColorPolicy: ThemeColorPolicy.Normal,
            MonochromeVariant: monochromeVariant,
            AccentColor: accentColor,
            ScopeSignature: scopeSignature);
    }
}
