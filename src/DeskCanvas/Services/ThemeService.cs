using System;
using Avalonia;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Styling;
using DeskCanvas.Core.Interfaces;
using DeskCanvas.Core.Models.Settings;

namespace DeskCanvas.Services;

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
        // 跟随系统亮暗（DarkMode == null）时，系统切换深浅色既不走 DataChanging 也没有
        // 壁纸事件，但 ApplyAccent 写入的 SystemAccentColor 及整个 ramp 是按"当时的
        // ActualThemeVariant"算极性的——不重写，SystemControlHighlightAccentBrush（Fluent
        // 开关/复选/单选的选中底色取色源）等命令式键就停在旧变体的颜色上，切换后全部
        // 错乱。变体真正翻转时重跑一遍 Apply（幂等），ramp 按新变体重写。
        Application.Current!.ActualThemeVariantChanged += (_, _) =>
        {
            var settings = appSettingsProvider.Get();
            // 显式亮/暗模式的 RequestedThemeVariant 恒定，不会触发本事件；AutoTheme 的
            // 变体翻转由上面的壁纸监听处理（避免双重 Apply）。这里只接管跟随系统。
            if (settings.Theme.AutoTheme || settings.Theme.DarkMode != null) return;
            Apply(settings.Theme);
        };
    }
    
    private readonly StyleInclude transparentStyle = new(new Uri("avares://DeskCanvas/"))
    {
        Source = new Uri("avares://DeskCanvas/Styles/Transparent.axaml")
    };
    
    private readonly StyleInclude solidStyle = new(new Uri("avares://DeskCanvas/"))
    {
        Source = new Uri("avares://DeskCanvas/Styles/Solid.axaml")
    };

    private readonly StyleInclude liquidGlassStyle = new(new Uri("avares://DeskCanvas/"))
    {
        Source = new Uri("avares://DeskCanvas/Styles/LiquidGlass.axaml")
    };

    private readonly StyleInclude colorfulStyle = new(new Uri("avares://DeskCanvas/"))
    {
        Source = new Uri("avares://DeskCanvas/Styles/Colorful.axaml")
    };
    
    private readonly StyleInclude monochromeStyle = new(new Uri("avares://DeskCanvas/"))
    {
        Source = new Uri("avares://DeskCanvas/Styles/Monochrome.axaml")
    };

    private readonly StyleInclude monochromeBlackWhiteStyle = new(new Uri("avares://DeskCanvas/"))
    {
        Source = new Uri("avares://DeskCanvas/Styles/MonochromeBlackWhite.axaml")
    };

    private readonly StyleInclude monochromeBackgroundStyle = new(new Uri("avares://DeskCanvas/"))
    {
        Source = new Uri("avares://DeskCanvas/Styles/MonochromeBackground.axaml")
    };

    /// <summary>
    /// 强调色 accent foreground (widget titles / accent icons), always loaded.
    /// The monochrome styles are appended AFTER it, so they can unify this
    /// accent with the text color when monochrome is enabled.
    /// </summary>
    private readonly StyleInclude accentStyle = new(new Uri("avares://DeskCanvas/"))
    {
        Source = new Uri("avares://DeskCanvas/Styles/Accent.axaml")
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

        // 全局元素阴影 (设置 → 外观): a soft drop shadow behind widget text and elem-shadow-marked
        // template parts (progress pills, metric rings), so content stays readable over busy
        // wallpapers. The effect is pushed to the live widget windows by WidgetTextShadow (see its
        // remarks for why the application happens in code instead of a style).
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

        // Accent source resolution happens further down, in ONE switch, right after the
        // monochrome variant is known — see the ramp contract there for why.

        // 纯色 surface: the card color (per dark/light variant) and the coating
        // opacity — Solid.axaml's WidgetBackground brush picks these up.
        // In Colorful mode, fixed authentic Apple card backgrounds are strictly enforced.
        var solidBackgroundDark = theme.IsColorful
            ? Color.Parse("#1C1C1E")
            : ParseColor(theme.EffectiveSolidBackgroundDark, Theme.DefaultSolidBackgroundDark);
        var solidBackgroundLight = theme.IsColorful
            ? Color.Parse("#FFFFFF")
            : ParseColor(theme.EffectiveSolidBackgroundLight, Theme.DefaultSolidBackgroundLight);
        Application.Current.Resources["SolidBackgroundDark"] = solidBackgroundDark;
        Application.Current.Resources["SolidBackgroundLight"] = solidBackgroundLight;

        // Accent ramp — exactly ONE source wins per Apply, and every branch either rewrites the
        // whole ramp or hands it back to Fluent. Preference order: 彩色 > 单色档 > 手选强调色 >
        // 系统强调色.
        //
        // The "every branch" part is the fix, not decoration: the ramp lives in
        // Application.Resources, so it OUTLIVES a single Apply. The old code only wrote it for
        // 彩色 / 手选 / 单色 and did NOTHING otherwise, so leaving 单色 behind left the previous
        // Apply's achromatic values in place — SystemAccentColor stayed pure white (or black) and
        // every accent-driven surface kept rendering as it did in 单色. The visible fallout: with
        // 黑白 turned off, an ON ToggleSwitch drew a white track (`SystemControlHighlightAccentBrush`
        // = SystemAccentColor) under a white knob, and hovering it read the equally stale
        // SystemAccentColorLight1 — the control simply vanished.
        //
        // - 黑白: the accent is strictly achromatic — BLACK in light mode, WHITE in dark mode —
        //   so accent-following text (aggregate city name, battery rings) never picks up the
        //   background colors. The ramp mapping is inverted accordingly: `dark:` feeds
        //   SystemAccentColorDark1 which the LIGHT accent dictionaries read, `light:` feeds
        //   SystemAccentColorLight2 for the DARK ones.
        // - 背景色: text AND accent lock to the inverted background — light mode renders the
        //   dark-mode background color, dark mode the light-mode one (the historic 黑白 ramp).
        // - 强调色 keeps whatever accent the user (or the system) provides.
        var monochromeVariant = theme.IsColorful || !theme.Monochrome
            ? (MonochromeStyle?)null
            : theme.EffectiveMonochromeVariant;
        var isDarkVariant = darkMode ?? Application.Current.ActualThemeVariant == ThemeVariant.Dark;
        switch (monochromeVariant)
        {
            case MonochromeStyle.BlackWhite:
                ApplyAccent(isDarkVariant ? Colors.White : Colors.Black, dark: Colors.Black, light: Colors.White);
                break;
            case MonochromeStyle.BackgroundColor:
                ApplyAccent(
                    isDarkVariant ? solidBackgroundLight : solidBackgroundDark,
                    dark: solidBackgroundDark,
                    light: solidBackgroundLight);
                break;
            default:
                if (theme.IsColorful)
                {
                    // Apple systemBlue: #007AFF on light, #0A84FF on dark (the ramp the
                    // Colorful palettes were designed against).
                    ApplyAccent(Color.Parse("#007AFF"), light: Color.Parse("#0A84FF"));
                }
                else if (theme.AccentColor != null && Color.TryParse(theme.AccentColor, out var accentColor))
                {
                    ApplyAccent(accentColor);
                }
                else
                {
                    // No accent picked means "use the system accent", which is exactly the
                    // resource the Fluent theme already publishes: drop our overrides so its
                    // value shines through again. Merely skipping the write is NOT equivalent —
                    // it would leave the previous Apply's ramp (e.g. 黑白's pure white) behind.
                    ClearAccentOverrides();
                }

                break;
        }

        // Surface material drives both the background style and the transparency
        // hint: Acrylic/OutlinedAcrylic → OS-level live blur, Solid → per-pixel
        // transparency so the opacity slider actually blends with the desktop.
        // Colorful (macOS) uses live OS acrylic blur with rich system semantic colors.
        // Always on base accent fallback (generic icons / title fallback)
        SwitchStyle(accentStyle, true);

        // Monochrome style dictionaries (appended after accentStyle so they win):
        // 黑白 keeps the text family pure black/white and resolves the accent family through
        // the achromatic ramp written above; 背景色 additionally locks the text family to the
        // ramp (everything renders in the inverted background color); 强调色 keeps the accent
        // as-is and only re-tints text (the historic Monochrome.axaml dictionaries).
        // In Colorful mode, monochrome is strictly disabled.
        SwitchStyle(monochromeBlackWhiteStyle, monochromeVariant == MonochromeStyle.BlackWhite);
        SwitchStyle(monochromeBackgroundStyle, monochromeVariant == MonochromeStyle.BackgroundColor);
        SwitchStyle(monochromeStyle, monochromeVariant == MonochromeStyle.Accent);

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

    /// <summary>
    /// Every key <see cref="ApplyAccent"/> owns. Dropping them lets the Fluent theme's own
    /// (platform) accent resolve again.
    /// </summary>
    private static readonly string[] AccentOverrideKeys =
        ["SystemAccentColor", .. DarkAccentKeys, .. LightAccentKeys];

    /// <summary>
    /// Hand the accent ramp back to the Fluent theme. Used when the resolved accent IS the
    /// system accent, where writing nothing is not good enough: a previous 单色/手选 Apply would
    /// otherwise stay in <c>Application.Resources</c> and keep painting the accent-driven surfaces
    /// (e.g. the ToggleSwitch ON track) in its own color.
    /// </summary>
    private static void ClearAccentOverrides()
    {
        foreach (var key in AccentOverrideKeys)
            Application.Current!.Resources.Remove(key);
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
