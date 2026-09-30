using System;
using Avalonia;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Styling;
using uWidgets.Core.Interfaces;
using uWidgets.Core.Models.Settings;

namespace uWidgets.Services;

public class ThemeService : IThemeService
{
    private readonly WallpaperThemeService wallpaperThemeService;

    public ThemeService(IAppSettingsProvider appSettingsProvider, WallpaperThemeService wallpaperThemeService)
    {
        this.wallpaperThemeService = wallpaperThemeService;
        appSettingsProvider.DataChanging += (_, _, newSettings) => 
            Apply(newSettings.Theme);
        // Wallpaper changed while in auto mode: re-resolve the variant.
        this.wallpaperThemeService.DarkFlagChanged += _ =>
        {
            var settings = appSettingsProvider.Get();
            if (settings.Theme.AutoTheme) Apply(settings.Theme);
        };
    }
    
    private readonly StyleInclude transparentStyle = new(new Uri("avares://uWidgets/"))
    {
        Source = new Uri("avares://uWidgets/Styles/Transparent.axaml")
    };
    
    private readonly StyleInclude solidStyle = new(new Uri("avares://uWidgets/"))
    {
        Source = new Uri("avares://uWidgets/Styles/Solid.axaml")
    };

    private readonly StyleInclude liquidGlassStyle = new(new Uri("avares://uWidgets/"))
    {
        Source = new Uri("avares://uWidgets/Styles/LiquidGlass.axaml")
    };

    private readonly StyleInclude colorfulStyle = new(new Uri("avares://uWidgets/"))
    {
        Source = new Uri("avares://uWidgets/Styles/Colorful.axaml")
    };
    
    private readonly StyleInclude monochromeStyle = new(new Uri("avares://uWidgets/"))
    {
        Source = new Uri("avares://uWidgets/Styles/Monochrome.axaml")
    };

    private readonly StyleInclude monochromeBlackWhiteStyle = new(new Uri("avares://uWidgets/"))
    {
        Source = new Uri("avares://uWidgets/Styles/MonochromeBlackWhite.axaml")
    };

    /// <summary>
    /// 强调色 accent foreground (widget titles / accent icons), always loaded.
    /// The monochrome styles are appended AFTER it, so they can unify this
    /// accent with the text color when monochrome is enabled.
    /// </summary>
    private readonly StyleInclude accentStyle = new(new Uri("avares://uWidgets/"))
    {
        Source = new Uri("avares://uWidgets/Styles/Accent.axaml")
    };
    
    public void Apply(Theme theme)
    {
        // Auto mode resolves light/dark from the wallpaper brightness; otherwise
        // the explicit flag decides (null = follow the system).
        var darkMode = theme.AutoTheme ? wallpaperThemeService.IsWallpaperDark() : theme.DarkMode;
        Application.Current!.RequestedThemeVariant = darkMode switch
        {
            null => ThemeVariant.Default,
            false => ThemeVariant.Light,
            _ => ThemeVariant.Dark,
        };
        
        Application.Current.Resources["FontFamily"] = theme.FontFamily == "Inter"
            ? new FontFamily("avares://Avalonia.Fonts.Inter#Inter")
            : new FontFamily(theme.FontFamily);

        // 全局文字阴影 (设置 → 外观): a soft drop shadow behind every TextBlock inside a
        // desktop widget window, so text stays readable over busy wallpapers. The effect is
        // pushed to the live widget windows by WidgetTextShadow (see its remarks for why the
        // application happens in code instead of a style).
        WidgetTextShadow.Update(theme);

        // OS-level acrylic (the Transparent style sets the AcrylicBlur hint on the
        // windows): the desktop composer samples the live desktop every frame, so
        // dynamic wallpapers stay live behind the widgets. OpacityLevel is the
        // coating alpha. In Colorful mode, opacity is strictly 1.0 (opaque cards).
        Application.Current.Resources["BackgroundOpacity"] = theme.IsColorful ? 1.0 : theme.OpacityLevel;

        // Animated wallpapers are sampled periodically. Turning this off keeps the
        // last captured frame and makes liquid glass deterministic/static. Both
        // rendered materials carry their own sampling settings.
        if (theme.UsesRenderedGlass)
        {
            var glass = theme.EffectiveGlass;
            LiquidGlassWallpaper.ConfigureLiveSampling(glass.LiveSampling, glass.LiveSamplingInterval);
        }

        if (theme.IsColorful)
        {
            // Apple systemBlue: #007AFF on light, #0A84FF on dark (the ramp the
            // Colorful palettes were designed against).
            ApplyAccent(Color.Parse("#007AFF"), light: Color.Parse("#0A84FF"));
        }
        else if (theme.AccentColor != null && Color.TryParse(theme.AccentColor, out var color))
        {
            ApplyAccent(color);
        }

        // 纯色 surface: the card color (per dark/light variant) and the coating
        // opacity — Solid.axaml's WidgetBackground brush picks these up.
        // In Colorful mode, fixed authentic Apple card backgrounds are strictly enforced.
        Application.Current.Resources["SolidBackgroundDark"] = theme.IsColorful
            ? Color.Parse("#1C1C1E")
            : ParseColor(theme.EffectiveSolidBackgroundDark, Theme.DefaultSolidBackgroundDark);
        Application.Current.Resources["SolidBackgroundLight"] = theme.IsColorful
            ? Color.Parse("#FFFFFF")
            : ParseColor(theme.EffectiveSolidBackgroundLight, Theme.DefaultSolidBackgroundLight);
        
        // Surface material drives both the background style and the transparency
        // hint: Acrylic/OutlinedAcrylic → OS-level live blur, Solid → per-pixel
        // transparency so the opacity slider actually blends with the desktop.
        // Colorful (macOS) uses live OS acrylic blur with rich system semantic colors.
        // Always on base accent fallback (generic icons / title fallback)
        SwitchStyle(accentStyle, true);

        // Monochrome color source: 黑白 = black in light / white in dark mode
        // (both text AND accent colors), 强调色 = accent-based (the historic
        // Monochrome.axaml dictionaries — accent stays accent, text becomes accent).
        // In Colorful mode, monochrome is strictly disabled.
        var monochrome = !theme.IsColorful && theme.Monochrome && theme.EffectiveMonochromeVariant == MonochromeStyle.BlackWhite;
        SwitchStyle(monochromeBlackWhiteStyle, monochrome);
        SwitchStyle(monochromeStyle, !theme.IsColorful && theme.Monochrome && !monochrome);

        // Surface material background styles
        SwitchStyle(transparentStyle, theme.UsesNativeBlur && !theme.IsColorful);
        SwitchStyle(solidStyle, !theme.IsGlass && !theme.IsColorful);
        SwitchStyle(liquidGlassStyle, theme.UsesRenderedGlass);

        // Colorful (macOS) uses live OS acrylic blur with rich Apple HIG system semantic colors.
        // Loaded after accentStyle so its vibrant palette (Red calendar, Orange clock second hand,
        // Blue/Purple/Orange monitor rings, etc.) takes precedence over single-color accent fallbacks.
        SwitchStyle(colorfulStyle, theme.IsColorful);
    }

    /// <summary>
    /// The accent ramp the theme dictionaries read, minus the base <c>SystemAccentColor</c>.
    /// Avalonia's Fluent theme pre-defines every one of these as a fixed shade of its own blue,
    /// so <b>any key left unwritten keeps that blue</b> — which is exactly how a hand-picked
    /// accent used to survive only in the light variant: <c>Styles/Accent.axaml</c> reads
    /// <c>SystemAccentColorLight2</c> from its Dark dictionary and nothing ever wrote it, so dark
    /// mode stayed Fluent blue no matter what the user picked (same for <c>Monochrome.axaml</c>,
    /// <c>ThemeButton</c> and the 配置方案 badge).
    /// </summary>
    private static readonly string[] DarkAccentKeys =
        ["SystemAccentColorDark1", "SystemAccentColorDark2", "SystemAccentColorDark3"];

    /// <inheritdoc cref="DarkAccentKeys"/>
    private static readonly string[] LightAccentKeys =
        ["SystemAccentColorLight1", "SystemAccentColorLight2", "SystemAccentColorLight3"];

    /// <summary>
    /// Overwrite the whole accent ramp so the resolved accent is authoritative in both theme
    /// variants. <paramref name="light"/>/<paramref name="dark"/> default to the accent itself:
    /// the app has no shade hierarchy of its own, and a colour the user picked should render as
    /// that colour — not as Fluent's tint of it.
    /// </summary>
    private static void ApplyAccent(Color accent, Color? light = null, Color? dark = null)
    {
        var lightShade = light ?? accent;
        var darkShade = dark ?? accent;

        Application.Current!.Resources["SystemAccentColor"] = accent;

        foreach (var key in DarkAccentKeys)
            Application.Current.Resources[key] = darkShade;

        foreach (var key in LightAccentKeys)
            Application.Current.Resources[key] = lightShade;
    }

    private static Color ParseColor(string hex, string fallbackHex) =>
        Color.TryParse(hex, out var color) ? color : Color.Parse(fallbackHex);

    private static void SwitchStyle(StyleInclude style, bool enable)
    {
        if (enable && !Application.Current!.Styles.Contains(style))
            Application.Current.Styles.Add(style);
        if (!enable && Application.Current!.Styles.Contains(style))
            Application.Current.Styles.Remove(style);
    }
}
