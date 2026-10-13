using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Styling;

namespace DeskCanvas.Services;

/// <summary>
/// Centralizes application-level theme resource updates so ThemeService stays focused on
/// resolving the active theme. The resource keys intentionally mirror Avalonia Fluent keys.
/// </summary>
internal static class ThemeResourceHelper
{
    private static readonly string[] DarkAccentKeys =
        ["SystemAccentColorDark1", "SystemAccentColorDark2", "SystemAccentColorDark3"];

    private static readonly string[] LightAccentKeys =
        ["SystemAccentColorLight1", "SystemAccentColorLight2", "SystemAccentColorLight3"];

    private static readonly string[] AccentOverrideKeys =
        ["SystemAccentColor", .. DarkAccentKeys, .. LightAccentKeys];

    public static Color ParseColor(string value, string fallback)
        => Color.TryParse(value, out var color) ? color : Color.Parse(fallback);

    public static void ApplyAccent(Color accent, Color? light = null, Color? dark = null)
    {
        var resources = Application.Current!.Resources;
        var lightShade = light ?? accent;
        var darkShade = dark ?? accent;

        resources["SystemAccentColor"] = accent;
        foreach (var key in DarkAccentKeys)
            resources[key] = darkShade;
        foreach (var key in LightAccentKeys)
            resources[key] = lightShade;
    }

    public static void ClearAccentOverrides()
    {
        var resources = Application.Current!.Resources;
        foreach (var key in AccentOverrideKeys)
            resources.Remove(key);
    }

    public static void SwitchStyle(StyleInclude style, bool enable)
    {
        var styles = Application.Current!.Styles;
        if (enable && !styles.Contains(style))
            styles.Add(style);
        else if (!enable && styles.Contains(style))
            styles.Remove(style);
    }

    // ---- text selection pair (W1) --------------------------------------------------------

    public const string TextSelectionBackgroundKey = "TextSelectionBackgroundBrush";
    public const string TextSelectionForegroundKey = "TextSelectionForegroundBrush";

    /// <summary>
    /// Writes the computed selection pair into the ThemeDictionaries of the merged
    /// InteractionResources dictionary — Default (light) and Dark separately, so a dark window
    /// never inherits the light result. Only the two paired keys are touched; the current
    /// variant dictionary is never bulk-replaced. Writes happen only when a colour actually
    /// changed, so a refresh triggered by ResourcesChanged can not loop back into itself.
    /// </summary>
    public static void UpdateTextSelectionResources(TextSelectionPalette light, TextSelectionPalette dark)
    {
        var dictionary = FindInteractionDictionary();
        if (dictionary is null) return;
        WriteSelectionPair(dictionary, ThemeVariant.Default, light);
        WriteSelectionPair(dictionary, ThemeVariant.Dark, dark);
    }

    private static void WriteSelectionPair(ResourceDictionary dictionary, ThemeVariant variant, TextSelectionPalette palette)
    {
        if (!dictionary.ThemeDictionaries.TryGetValue(variant, out var provider)) return;
        if (provider is not ResourceDictionary themed) return;
        UpdateBrush(themed, TextSelectionBackgroundKey, palette.Background);
        UpdateBrush(themed, TextSelectionForegroundKey, palette.Foreground);
    }

    private static void UpdateBrush(ResourceDictionary themed, string key, Color color)
    {
        if (themed.TryGetValue(key, out var existing)
            && existing is ISolidColorBrush brush
            && brush.Color == color
            && Math.Abs(brush.Opacity - 1.0) < 0.001)
        {
            return;
        }

        themed[key] = new SolidColorBrush(color);
    }

    /// <summary>
    /// Locates the InteractionResources dictionary among the application's merged dictionaries
    /// by probing for the selection key in its theme dictionaries (the include may be either a
    /// raw dictionary or a ResourceInclude).
    /// </summary>
    private static ResourceDictionary? FindInteractionDictionary()
    {
        var app = Application.Current;
        if (app is null) return null;

        foreach (var provider in app.Resources.MergedDictionaries)
        {
            var dictionary = provider as ResourceDictionary
                ?? (provider as ResourceInclude)?.Loaded as ResourceDictionary;
            if (dictionary is null) continue;

            foreach (var themed in dictionary.ThemeDictionaries.Values)
            {
                if (themed is ResourceDictionary themedDictionary
                    && themedDictionary.ContainsKey(TextSelectionBackgroundKey))
                {
                    return dictionary;
                }
            }
        }

        return null;
    }
}
