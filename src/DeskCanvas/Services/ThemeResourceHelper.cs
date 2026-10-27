using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Styling;
using DeskCanvas.Core.Models.Settings;

namespace DeskCanvas.Services;

/// <summary>
/// Centralizes application-level and window-level theme resource updates so ThemeService and FocusThemeCoordinator
/// stay focused on resolving the active theme. The resource keys intentionally mirror Avalonia Fluent keys.
/// </summary>
public static class ThemeResourceHelper
{
    public static readonly Uri BaseAppUri = new("avares://DeskCanvas/");
    public const string ColorfulResourcesUri = "avares://DeskCanvas/Styles/ColorfulResources.axaml";
    public const string MonochromeBlackWhiteUri = "avares://DeskCanvas/Styles/MonochromeBlackWhite.axaml";
    public const string MonochromeBackgroundUri = "avares://DeskCanvas/Styles/MonochromeBackground.axaml";
    public const string MonochromeAccentUri = "avares://DeskCanvas/Styles/Monochrome.axaml";

    private static readonly string[] DarkAccentKeys =
        ["SystemAccentColorDark1", "SystemAccentColorDark2", "SystemAccentColorDark3"];

    private static readonly string[] LightAccentKeys =
        ["SystemAccentColorLight1", "SystemAccentColorLight2", "SystemAccentColorLight3"];

    public static readonly string[] AccentOverrideKeys =
        ["SystemAccentColor", .. DarkAccentKeys, .. LightAccentKeys];

    private static readonly string[] ComponentSemanticKeys =
    [
        "WidgetBackground",
        "WidgetForeground",
        "WidgetCardBorderBrush",
        "CalendarHeaderBrush",
        "CalendarTodayBrush",
        "CalendarCardBackground",
        "ClockHandBrush",
        "ClockSecondHandBrush",
        "ClockCenterPinBrush",
        "ClockCardBackground",
        "ClockAnalogIIICardBackground",
        "ClockAnalogIVCardBackground",
        "FlipClockCardBackground",
        "ClockDigitalCardBackground",
        "ClockDigitalTimeBrush",
        "ClockDigitalDateBrush",
        "ClockDialBackgroundBrush",
        "ClockOpaqueDialBackgroundBrush",
        "ClockStrokesBrush",
        "FlipClockTopFlapBrush",
        "FlipClockBottomFlapBrush",
        "FlipClockFlapBorderBrush",
        "FlipClockCreaseBrush",
        "FlipClockHingeBrush",
        "RemindersCardBackground",
        "RemindersBadgeBrush",
        "RemindersCheckBrush",
        "RemindersDotOrange",
        "RemindersDotRed",
        "RemindersDotBlue",
        "RemindersDotPurple",
        "NotesCardBackground",
        "NotesAccentBrush",
        "NotesPaperBackground",
        "NotesTextBrush",
        "NotesTitleForegroundBrush",
        "NotesDividerBrush",
        "NotesUpdatedBrush",
        "MusicCardBackground",
        "MusicAccentBrush",
        "MusicScrubberTrackBrush",
        "MusicPlayButtonBackgroundBrush",
        "MusicPlayButtonHoverBrush",
        "MusicCoverPlaceholderBrush",
        "MusicCoverBorderBrush",
        "PomodoroCardBackground",
        "PomodoroPillBackgroundBrush",
        "PomodoroPillBorderBrush",
        "PomodoroButtonBackgroundBrush",
        "PomodoroButtonHoverBrush",
        "HitokotoCardBackground",
        "HitokotoWatermarkBrush",
        "HitokotoQuoteBrush",
        "HitokotoAuthorBrush",
        "HitokotoPillBackgroundBrush",
        "HitokotoPillBorderBrush",
        "HitokotoPillForegroundBrush",
        "HitokotoButtonHoverBrush",
        "HitokotoButtonPressedBrush",
        "BatteriesCardBackground",
        "BatteriesTrackBrush",
        "BatteriesIconBrush",
        "BatteriesTextBrush",
        "BatteriesNormalBrush",
        "BatteriesChargingBrush",
        "BatteriesWarningBrush",
        "BatteriesCriticalBrush",
        "MonitorCardBackground",
        "MonitorTrackBrush",
        "MonitorCpuBrush",
        "MonitorRamBrush",
        "MonitorDiskBrush",
        "MonitorNetBrush",
        "MonitorBatteryBrush",
        "StorageCardBackground",
        "StorageSubCardBackground",
        "StorageSubCardBorderBrush",
        "StorageTrackBackground",
        "StorageBadgeBackground",
        "StorageSystemBrush",
        "StorageAppsBrush",
        "StorageDocsBrush",
        "StorageMediaBrush",
        "StorageRemovableBrush",
        "StorageWarningBrush",
        "StorageCriticalBrush",
        "FoldersCardBackground",
        "FolderAccentBrush",
        "TranslatorCardBackground",
        "TranslateCardBackground",
        "TranslateAccentBrush",
        "TranslatorInputBackground",
        "TranslatorOutputBackground",
        "TranslatorOutputBorderBrush",
        "CalculatorCardBackground",
        "CalculatorDisplayBackground",
        "CalculatorAccentBrush",
        "CalculatorNumButtonBackground",
        "CalculatorFuncButtonBackground",
        "CalculatorOpButtonBackground",
        "ClipboardCardBackground",
        "ClipboardItemCardBackground",
        "ClipboardItemCardHoverBackground",
        "ClipboardBadgeTextBgBrush",
        "ClipboardBadgeTextBrush",
        "ClipboardBadgeCodeBgBrush",
        "ClipboardBadgeCodeBrush",
        "ClipboardBadgeLinkBgBrush",
        "ClipboardBadgeLinkBrush",
        "ClipboardBadgeFileBgBrush",
        "ClipboardBadgeFileBrush",
        "ControlCenterCardBackground",
        "ControlCenterCapsuleBackground",
        "ControlCenterBadgeInactiveBackground",
        "MapCardBackground",
        "MapFloatingButtonBackground",
        "ProgressCardBackground",
        "ProgressMoveBrush",
        "ProgressExerciseBrush",
        "ProgressStandBrush",
        "SearchCardBackground",
        "SearchMagnifierBrush",
        "SearchPillBackground",
        "SearchPillBorderBrush",
        "SearchEngineButtonBackground",
        "SearchDockButtonBackground",
        "SearchButtonBrush",
        "WeatherCardBackground",
        "WeatherForegroundBrush",
        "WeatherIconBrush",
        "WeatherGaugeBrush",
        "WeatherTempBarBrush",
        "TimetableCardBackground",
        "HabitsCardBackground",
        "AccentContrastColor",
        "AccentContrastForegroundBrush",
        "SystemControlForegroundAccentBrush",
        "SystemControlHighlightAccentBrush"
    ];

    public static Color ParseColor(string value, string fallback)
        => Color.TryParse(value, out var color) ? color : Color.Parse(fallback);

    public static void ApplyAccent(Color accent, Color? light = null, Color? dark = null) =>
        ApplyAccent(Application.Current!.Resources, accent, light, dark);

    public static void ApplyAccent(IResourceDictionary resources, Color accent, Color? light = null, Color? dark = null)
    {
        var lightShade = light ?? accent;
        var darkShade = dark ?? accent;

        resources["SystemAccentColor"] = accent;
        foreach (var key in DarkAccentKeys)
            resources[key] = darkShade;
        foreach (var key in LightAccentKeys)
            resources[key] = lightShade;
    }

    public static void ClearAccentOverrides() =>
        ClearAccentOverrides(Application.Current!.Resources);

    public static void ClearAccentOverrides(IResourceDictionary resources)
    {
        foreach (var key in AccentOverrideKeys)
            resources.Remove(key);
    }

    public static void ClearDirectComponentColorOverrides(IResourceDictionary resources)
    {
        foreach (var key in ComponentSemanticKeys)
            resources.Remove(key);
    }

    // ---- scope card resources (single source of truth) --------------------------------------

    /// <summary>
    /// The card-material keys the material style dictionaries resolve through
    /// <c>DynamicResource</c>: <c>BackgroundOpacity</c> (the coating alpha) and
    /// <c>SolidBackgroundDark/Light</c> (the 纯色 card colours). Applied at application
    /// level for the live theme — the one theme the uniform 失焦自动切换 switch and the
    /// settings page's theme buttons both land on.
    /// <para>
    /// <paramref name="theme"/> is expected to be the theme already resolved for rendering
    /// (i.e. with 跟随强调色 baked in by <see cref="AccentPalette"/>); writing the stored pair
    /// here instead would make the 纯色 card disagree with the liquid-glass coating, which
    /// reads the same derived value off the material record.
    /// </para>
    /// </summary>
    public static void WriteScopeCardResources(IResourceDictionary resources, Theme theme)
    {
        resources["BackgroundOpacity"] = theme.IsColorful ? 1.0 : theme.OpacityLevel;

        resources["SolidBackgroundDark"] = theme.IsColorful
            ? Color.Parse("#1C1C1E")
            : ParseColor(theme.EffectiveSolidBackgroundDark, Theme.DefaultSolidBackgroundDark);
        resources["SolidBackgroundLight"] = theme.IsColorful
            ? Color.Parse("#FFFFFF")
            : ParseColor(theme.EffectiveSolidBackgroundLight, Theme.DefaultSolidBackgroundLight);
    }

    public static void SwitchStyle(StyleInclude style, bool enable)
    {
        var styles = Application.Current!.Styles;
        if (enable && !styles.Contains(style))
            styles.Add(style);
        else if (!enable && styles.Contains(style))
            styles.Remove(style);
    }

    /// <summary>
    /// Applies the computed accent ramp to the target resource dictionary under the Normal color policy.
    /// Colorful policy strictly forbids calling ApplyAccentRamp and will clear overrides immediately.
    /// <para>
    /// 强调色偏移 shifts the <b>effective</b> accent: the manual colour when one is stored,
    /// otherwise the live OS accent. The no-manual-colour path therefore can no longer just clear
    /// its overrides — with a shift &gt; 0 there is a colour to write, and Fluent's own unshifted
    /// accent is exactly what the user asked to move away from. At 0% (and with no manual colour)
    /// the clear is kept byte-for-byte, so existing settings files look untouched.
    /// </para>
    /// <para>
    /// 黑白 is deliberately exempt: its accent is pure white/black by definition, and shifting
    /// either endpoint is a no-op that would only muddy the "strictly achromatic" contract.
    /// 背景色 needs no special case — it consumes the theme's already-baked background pair.
    /// </para>
    /// </summary>
    public static void ApplyAccentRamp(IResourceDictionary resources, Theme theme, bool isDarkVariant)
    {
        if (theme.IsColorful)
        {
            ClearAccentOverrides(resources);
            return;
        }

        var solidBackgroundDark = ParseColor(theme.EffectiveSolidBackgroundDark, Theme.DefaultSolidBackgroundDark);
        var solidBackgroundLight = ParseColor(theme.EffectiveSolidBackgroundLight, Theme.DefaultSolidBackgroundLight);

        var monochromeVariant = !theme.Monochrome
            ? (MonochromeStyle?)null
            : theme.EffectiveMonochromeVariant;

        switch (monochromeVariant)
        {
            case MonochromeStyle.BlackWhite:
                ApplyAccent(resources, isDarkVariant ? Colors.White : Colors.Black, dark: Colors.Black, light: Colors.White);
                break;
            case MonochromeStyle.BackgroundColor:
                ApplyAccent(
                    resources,
                    isDarkVariant ? solidBackgroundLight : solidBackgroundDark,
                    dark: solidBackgroundDark,
                    light: solidBackgroundLight);
                break;
            default:
            {
                var shift = theme.EffectiveAccentShift;
                // Read the platform accent only when it can actually be used: a 0% shift with no
                // manual colour must keep the historic "let Fluent's accent show through" path
                // without touching PlatformSettings at all.
                var accent = AccentPalette.ResolveBaseAccent(
                    theme.AccentColor,
                    shift > 0 ? AccentPalette.TryGetSystemAccent() : null);

                if (accent is { } baseAccent)
                {
                    // Dark mode pushes toward white so the accent lifts off a dark card; light
                    // mode pushes toward black for the same contrast against a bright card.
                    ApplyAccent(resources, AccentPalette.Shift(baseAccent, towardWhite: isDarkVariant, shift));
                }
                else
                {
                    ClearAccentOverrides(resources);
                }

                break;
            }
        }
    }

    /// <summary>
    /// Applies the accent ramp according to a resolved theme profile.
    /// </summary>
    public static void ApplyAccentRamp(IResourceDictionary resources, ResolvedThemeProfile profile, bool isDarkVariant)
    {
        if (profile.ColorPolicy != ThemeColorPolicy.Normal)
        {
            ClearAccentOverrides(resources);
            return;
        }

        ApplyAccentRamp(resources, profile.Theme, isDarkVariant);
    }

    /// <summary>
    /// Applies the Normal theme color scope to a window:
    /// 1. Removes any ColorfulResources.axaml include.
    /// 2. Removes window-level ColorfulActive marker and any direct component overrides.
    /// 3. Clears old accent ramp overrides.
    /// 4. Applies the full accent ramp for the Normal policy.
    /// 5. Mounts/unmounts monochrome stylesheets according to profile.MonochromeVariant.
    /// 6. Sets diagnostic marker window.Resources["ThemeScopePolicy"] = ThemeColorPolicy.Normal.
    /// </summary>
    public static void ApplyNormalColorScope(Window window, ResolvedThemeProfile profile, bool isDarkVariant)
    {
        RemoveColorfulResourcesInclude(window);
        window.Resources.Remove("ColorfulActive");
        ClearDirectComponentColorOverrides(window.Resources);
        ClearAccentOverrides(window.Resources);

        ApplyAccentRamp(window.Resources, profile, isDarkVariant);
        SyncMonochromeStyle(window, profile.MonochromeVariant);

        window.Resources["ThemeScopePolicy"] = ThemeColorPolicy.Normal;
    }

    public static void ApplyNormalColorScope(Window window, Theme theme, bool isDarkVariant)
    {
        var variant = isDarkVariant ? ThemeVariant.Dark : ThemeVariant.Light;
        var profile = ThemeProfileResolver.Resolve(theme, variant);
        ApplyNormalColorScope(window, profile, isDarkVariant);
    }

    /// <summary>
    /// Applies the Colorful theme color scope to a window:
    /// 1. Unmounts normal theme monochrome style include.
    /// 2. Clears window-level accent ramp (strictly prohibited from overriding colorful dictionary).
    /// 3. Cleans normal theme ThemeScopePolicy and direct component color overrides.
    /// 4. Mounts ColorfulResources.axaml include.
    /// 5. Sets diagnostic markers window.Resources["ThemeScopePolicy"] = ThemeColorPolicy.ColorfulFixed, window.Resources["ColorfulActive"] = true.
    /// 6. Strictly avoids calling ApplyAccentRamp.
    /// </summary>
    public static void ApplyColorfulColorScope(Window window, ResolvedThemeProfile profile)
    {
        SyncMonochromeStyle(window, null);
        ClearAccentOverrides(window.Resources);
        ClearDirectComponentColorOverrides(window.Resources);

        AttachColorfulResourcesInclude(window);

        window.Resources["ThemeScopePolicy"] = ThemeColorPolicy.ColorfulFixed;
        window.Resources["ColorfulActive"] = true;
    }

    public static void ApplyColorfulColorScope(Window window, Theme theme)
    {
        var profile = ThemeProfileResolver.Resolve(theme, ThemeVariant.Default);
        ApplyColorfulColorScope(window, profile);
    }

    /// <summary>
    /// Completely clears all color scope overrides and attached style/resource includes from a window.
    /// </summary>
    public static void ClearWindowColorScope(Window window)
    {
        RemoveColorfulResourcesInclude(window);
        SyncMonochromeStyle(window, null);
        ClearAccentOverrides(window.Resources);
        ClearDirectComponentColorOverrides(window.Resources);
        window.Resources.Remove("ColorfulActive");
        window.Resources.Remove("ThemeScopePolicy");
    }

    public static void AttachColorfulResourcesInclude(Window window)
    {
        var merged = window.Resources.MergedDictionaries;
        for (int i = 0; i < merged.Count; i++)
        {
            if (merged[i] is ResourceInclude inc
                && inc.Source?.OriginalString?.Contains("ColorfulResources.axaml", StringComparison.OrdinalIgnoreCase) == true)
            {
                return;
            }
        }

        var include = new ResourceInclude(BaseAppUri)
        {
            Source = new Uri(ColorfulResourcesUri)
        };
        merged.Add(include);
    }

    public static void RemoveColorfulResourcesInclude(Window window)
    {
        var merged = window.Resources.MergedDictionaries;
        for (int i = merged.Count - 1; i >= 0; i--)
        {
            if (merged[i] is ResourceInclude inc
                && inc.Source?.OriginalString?.Contains("ColorfulResources.axaml", StringComparison.OrdinalIgnoreCase) == true)
            {
                merged.RemoveAt(i);
            }
        }
    }

    public static void SyncMonochromeStyle(Window window, MonochromeStyle? desiredVariant)
    {
        string? targetUri = desiredVariant switch
        {
            MonochromeStyle.BlackWhite => MonochromeBlackWhiteUri,
            MonochromeStyle.BackgroundColor => MonochromeBackgroundUri,
            MonochromeStyle.Accent => MonochromeAccentUri,
            _ => null
        };

        bool alreadyAttached = false;
        for (int i = window.Styles.Count - 1; i >= 0; i--)
        {
            if (window.Styles[i] is StyleInclude styleInc
                && styleInc.Source?.OriginalString?.Contains("Monochrome", StringComparison.OrdinalIgnoreCase) == true)
            {
                if (targetUri != null
                    && string.Equals(styleInc.Source?.OriginalString, targetUri, StringComparison.OrdinalIgnoreCase))
                {
                    alreadyAttached = true;
                }
                else
                {
                    window.Styles.RemoveAt(i);
                }
            }
        }

        if (targetUri != null && !alreadyAttached)
        {
            var newInclude = new StyleInclude(BaseAppUri)
            {
                Source = new Uri(targetUri)
            };
            window.Styles.Add(newInclude);
        }
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
