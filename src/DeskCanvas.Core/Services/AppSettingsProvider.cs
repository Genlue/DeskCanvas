using DeskCanvas.Core.Interfaces;
using DeskCanvas.Core.Models.Settings;

namespace DeskCanvas.Core.Services;

/// <inheritdoc cref="IAppSettingsProvider"/>
public class AppSettingsProvider() : JsonParser<AppSettings>(Const.AppSettingsFile), IAppSettingsProvider
{
    /// <inheritdoc />
    protected override AppSettings Normalize(AppSettings settings)
    {
        var surface = settings.Theme.EffectiveSurface;
        var theme = settings.Theme.NormalizeMaterial() with { Surface = surface };
        var surfaceThemes = settings.SurfaceThemes != null
            ? new Dictionary<string, Theme>(settings.SurfaceThemes, StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, Theme>(StringComparer.OrdinalIgnoreCase);

        if (surfaceThemes.Remove(nameof(SurfaceStyle.SoftGlow), out var legacy) &&
            !surfaceThemes.ContainsKey(nameof(SurfaceStyle.LiquidGlass)))
            surfaceThemes[nameof(SurfaceStyle.LiquidGlass)] = legacy.NormalizeMaterial();
        // The currently selected soft-glow customization wins over an older saved lens preset.
        if (settings.Theme.Surface == SurfaceStyle.SoftGlow)
            surfaceThemes[nameof(SurfaceStyle.LiquidGlass)] = theme;

        if (!surfaceThemes.ContainsKey(surface.ToString()))
        {
            surfaceThemes[surface.ToString()] = theme;
        }

        var layout = settings.Layout;
        if ((int)layout.GridMode == 1)
        {
            layout = layout with { GridMode = GridMode.Manual };
        }

        return settings with
        {
            Grid = settings.Grid ?? Grid.Default,
            Theme = theme,
            Templates = settings.Templates.Select(t => t.NormalizeMaterial()).Distinct().ToArray(),
            SurfaceThemes = surfaceThemes,
            Layout = layout
        };
    }
}
