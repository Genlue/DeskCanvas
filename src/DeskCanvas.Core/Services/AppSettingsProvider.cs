using System.Text.Json;
using DeskCanvas.Core.Interfaces;
using DeskCanvas.Core.Models.Settings;

namespace DeskCanvas.Core.Services;

/// <inheritdoc cref="IAppSettingsProvider"/>
public class AppSettingsProvider() : JsonParser<AppSettings>(Const.AppSettingsFile), IAppSettingsProvider
{
    /// <inheritdoc />
    protected override AppSettings Normalize(AppSettings settings)
    {
        // Proxy persistence round-trip: the file stores the legacy-safe form ("system"/null/URL +
        // ProxyAutoDetect — see ProxySettings.ToPersistedProxy); reconstruct the in-memory mode
        // token ("auto"/"direct") so every consumer keeps seeing the semantic string.
        settings = settings with
        {
            HttpProxy = ProxySettings.FromPersistedProxy(settings.ProxyAutoDetect, settings.HttpProxy)
        };

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
        if (layout.GridMode != GridMode.Manual || layout.LockSize)
        {
            layout = layout with { GridMode = GridMode.Manual, LockSize = false };
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

    /// <summary>
    /// Persist the proxy mode in the legacy-safe form: the in-memory token ("auto"/"direct"/
    /// "system"/custom URL) becomes (ProxyAutoDetect, file value) — see
    /// <see cref="ProxySettings.ToPersistedProxy"/> for why the file must never carry the
    /// 3.7.0+ tokens ("auto"/"direct") that pre-3.7.0 builds sharing this file misparse as a
    /// custom proxy URL. The cached in-memory value stays the semantic one.
    /// </summary>
    protected override string SerializeForFile(AppSettings value)
    {
        var (autoDetect, fileValue) = ProxySettings.ToPersistedProxy(value.HttpProxy);
        return JsonSerializer.Serialize(value with { ProxyAutoDetect = autoDetect, HttpProxy = fileValue });
    }
}
