using Avalonia;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;

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
}
