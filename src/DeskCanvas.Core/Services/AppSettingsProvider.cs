using System.Text.Json;
using DeskCanvas.Core.Interfaces;
using DeskCanvas.Core.Models.Settings;

namespace DeskCanvas.Core.Services;

/// <inheritdoc cref="IAppSettingsProvider"/>
public class AppSettingsProvider() : JsonParser<AppSettings>(() => Const.AppSettingsFile), IAppSettingsProvider
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
        var theme = (settings.Theme with { Surface = surface }).NormalizeForSurface();
        var surfaceThemes = settings.SurfaceThemes != null
            ? new Dictionary<string, Theme>(settings.SurfaceThemes, StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, Theme>(StringComparer.OrdinalIgnoreCase);


        if (!surfaceThemes.ContainsKey(surface.ToString()))
        {
            surfaceThemes[surface.ToString()] = theme;
        }

        foreach (var (k, v) in surfaceThemes.ToList())
        {
            var entryTheme = Enum.TryParse<SurfaceStyle>(k, ignoreCase: true, out var parsedSurface) && v.Surface == null
                ? v with { Surface = parsedSurface }
                : v;
            surfaceThemes[k] = entryTheme.NormalizeForSurface();
        }

        var layout = settings.Layout;
        if (layout.GridMode != GridMode.Manual || layout.LockSize)
        {
            layout = layout with { GridMode = GridMode.Manual, LockSize = false };
        }

        // 失焦自动切换: tolerate hand-edited files — trim / de-duplicate the allowlist
        // (process names, case-insensitive), drop blank entries, fold 柔光玻璃 into
        // 液态玻璃 and clamp the anti-flicker dwell to its 0–1000 ms range. Null modes
        // need no fixup: the record's Effective* accessors fall back to "follow the system".
        var focusLink = settings.FocusLink;
        if (focusLink != null)
        {
            var whitelist = (focusLink.Whitelist ?? [])
                .Select(entry => entry?.Trim() ?? string.Empty)
                .Where(entry => entry.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            focusLink = focusLink with
            {
                Whitelist = whitelist,
                FocusedSurface = FocusLinkSettings.NormalizeSurface(focusLink.FocusedSurface),
                UnfocusedSurface = FocusLinkSettings.NormalizeSurface(focusLink.UnfocusedSurface),
                SwitchDelayMs = Math.Clamp(focusLink.SwitchDelayMs,
                    FocusLinkSettings.SwitchDelayMinMs, FocusLinkSettings.SwitchDelayMaxMs)
            };
        }

        // 最大化或全屏时: tolerate hand-edited files — an undefined token falls back to
        // "not configured" (the consumers then apply the historical default, 隐藏).
        FullscreenBehavior? fullscreenBehavior = settings.FullscreenBehavior is { } behavior && Enum.IsDefined(behavior)
            ? behavior
            : null;

        return settings with
        {
            Grid = settings.Grid ?? Grid.Default,
            Theme = theme,
            Templates = settings.Templates.Select(t => t.NormalizeForSurface()).Distinct().ToArray(),
            SurfaceThemes = surfaceThemes,
            Layout = layout,
            FocusLink = focusLink,
            FullscreenBehavior = fullscreenBehavior
        };
    }

    /// <summary>
    /// Serialize settings in the on-disk (legacy-safe) form: the in-memory proxy token
    /// ("auto"/"direct"/"system"/custom URL) becomes (ProxyAutoDetect, file value) — see
    /// <see cref="ProxySettings.ToPersistedProxy"/> for why the file must never carry the
    /// 3.7.0+ tokens ("auto"/"direct") that pre-3.7.0 builds sharing this file misparse as a
    /// custom proxy URL. The in-memory value itself stays the semantic one.
    /// <para>
    /// Public so other writers of an <c>appSettings.json</c> (seeding a brand-new profile)
    /// persist through the same mapping instead of copying the in-memory token into the file.
    /// </para>
    /// </summary>
    public static string ToFileJson(AppSettings value)
    {
        var (autoDetect, fileValue) = ProxySettings.ToPersistedProxy(value.HttpProxy);
        return JsonSerializer.Serialize(value with { ProxyAutoDetect = autoDetect, HttpProxy = fileValue });
    }

    /// <inheritdoc />
    protected override string SerializeForFile(AppSettings value) => ToFileJson(value);
}
