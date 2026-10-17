using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using DeskCanvas.Core.Interfaces;
using DeskCanvas.Core.Models.Settings;

namespace DeskCanvas.Services;

public class ThemeService : IThemeService
{
    private readonly IAppSettingsProvider appSettingsProvider;
    private readonly WallpaperThemeService wallpaperThemeService;

    /// <summary>
    /// Latest 失焦自动切换 settings seen by a save, so the <see cref="Apply(Theme)"/> overload
    /// (interface-compatible path) resolves the same app-level mode the snapshot save did.
    /// </summary>
    private FocusLinkSettings? pendingFocusLink;

    public ThemeService(IAppSettingsProvider appSettingsProvider, WallpaperThemeService wallpaperThemeService)
    {
        this.appSettingsProvider = appSettingsProvider;
        this.wallpaperThemeService = wallpaperThemeService;
        appSettingsProvider.DataChanged += (_, _, newSettings) =>
            Apply(newSettings);
        // Wallpaper changed while in auto mode: re-resolve the variant. With 失焦自动切换
        // enabled the app-level pair is the "聚焦时" pair, so that is what decides here.
        this.wallpaperThemeService.DarkFlagChanged += _ =>
        {
            var settings = appSettingsProvider.Get();
            if (EffectiveAppMode(settings).AutoTheme) Apply(settings);
        };
        // 跟随系统亮暗（DarkMode == null）时，系统切换深浅色既不走 DataChanged 也没有
        // 壁纸事件，但 ApplyAccent 写入的 SystemAccentColor 及整个 ramp 是按"当时的
        // ActualThemeVariant"算极性的——不重写，SystemControlHighlightAccentBrush（Fluent
        // 开关/复选/单选的选中底色取色源）等命令式键就停在旧变体的颜色上，切换后全部
        // 错乱。变体真正翻转时重跑一遍 Apply（幂等），ramp 按新变体重写。
        Application.Current!.ActualThemeVariantChanged += (_, _) =>
        {
            var settings = appSettingsProvider.Get();
            // 显式亮/暗模式的 RequestedThemeVariant 恒定，不会触发本事件；AutoTheme 的
            // 变体翻转由上面的壁纸监听处理（避免双重 Apply）。这里只接管跟随系统——
            // 「失焦自动切换」启用时以「聚焦时」模式为准。
            var mode = EffectiveAppMode(settings);
            if (mode.AutoTheme || mode.DarkMode != null) return;
            Apply(settings);
        };
        // System accent changes while the variant stays put trigger neither DataChanged nor
        // ActualThemeVariantChanged, yet the accent brush behind selected text changes — the
        // computed selection pair must follow. Post to the next dispatcher pass so the Fluent
        // accent resources have settled before re-resolving.
        if (Application.Current.PlatformSettings is { } platformSettings)
        {
            platformSettings.ColorValuesChanged += (_, _) =>
                Dispatcher.UIThread.Post(RefreshTextSelectionPaletteCore, DispatcherPriority.Background);
        }
    }
    
    private readonly StyleInclude transparentStyle = new(new Uri("avares://DeskCanvas/"))
    {
        Source = new Uri("avares://DeskCanvas/Styles/Transparent.axaml")
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
    
    /// <summary>
    /// Resolves the effective application-level theme (representing Settings window, dialogs,
    /// and chrome). When focus link is disabled, returns normalized settings.Theme.
    /// When focus link is enabled, returns the normalized "focused" state theme.
    /// </summary>
    public static Theme ResolveApplicationTheme(AppSettings settings)
    {
        if (settings.FocusLink is not { Enabled: true } link)
        {
            return settings.Theme.NormalizeForSurface();
        }

        return FocusThemeCoordinator.ResolveStateTheme(link, focused: true, settings).NormalizeForSurface();
    }

    /// <summary>
    /// The light/dark pair behind the app-level variant. While 失焦自动切换 is enabled the
    /// app's own chrome (settings window, dialogs — windows without a per-window override)
    /// follows the "聚焦时" pair, the home state; the per-screen switching of the widget
    /// windows is <see cref="FocusThemeCoordinator"/>'s job.
    /// </summary>
    private static FocusMode EffectiveAppMode(AppSettings settings)
    {
        var appTheme = ResolveApplicationTheme(settings);
        return new FocusMode(appTheme.DarkMode, appTheme.AutoTheme);
    }

    /// <summary>
    /// The distinct surface materials in play: the shared theme's own surface, plus — while
    /// 失焦自动切换 is enabled — each state's override. With the feature off this is exactly
    /// the single active surface, i.e. the historic behaviour.
    /// <para>
    /// 多彩 is part of the union like any other material; it is the one surface whose
    /// <b>dictionary loading</b> is not driven by this set (the app-wide 多彩 style is only
    /// loaded while the shared theme is 多彩 — a 多彩 focus state gets the palette merged into
    /// its own window by <see cref="FocusThemeCoordinator"/> instead).
    /// </para>
    /// </summary>
    private readonly ResourceInclude colorfulResourcesInclude = new(ThemeResourceHelper.BaseAppUri)
    {
        Source = new Uri(ThemeResourceHelper.ColorfulResourcesUri)
    };

    private void AttachApplicationColorfulResources()
    {
        if (Application.Current?.Resources == null) return;
        var merged = Application.Current.Resources.MergedDictionaries;
        for (int i = 0; i < merged.Count; i++)
        {
            if (merged[i] is ResourceInclude inc
                && inc.Source?.OriginalString?.Contains("ColorfulResources.axaml", StringComparison.OrdinalIgnoreCase) == true)
            {
                return;
            }
        }
        merged.Add(colorfulResourcesInclude);
    }

    private void RemoveApplicationColorfulResources()
    {
        if (Application.Current?.Resources == null) return;
        var merged = Application.Current.Resources.MergedDictionaries;
        for (int i = merged.Count - 1; i >= 0; i--)
        {
            if (merged[i] is ResourceInclude inc
                && inc.Source?.OriginalString?.Contains("ColorfulResources.axaml", StringComparison.OrdinalIgnoreCase) == true)
            {
                merged.RemoveAt(i);
            }
        }
    }

    /// <inheritdoc />
    public void Apply(Theme theme)
    {
        var current = appSettingsProvider.Get();
        var settings = current with
        {
            Theme = theme,
            FocusLink = pendingFocusLink ?? current.FocusLink
        };
        Apply(settings);
    }

    /// <inheritdoc />
    public void Apply(AppSettings settings)
    {
        pendingFocusLink = settings.FocusLink;
        ApplyCore(settings);
    }

    /// <summary>Raised on the UI thread after a global scope application actually ran (not
    /// <summary>
    /// The focus lens the 失焦自动切换 coordinator installs: given the settings it answers
    /// <c>true</c> (desktop focused), <c>false</c> (unfocused) or <c>null</c> (feature off —
    /// apply the shared theme). Every global application — settings saves, the variant and
    /// wallpaper listeners, the coordinator's own flips — resolves the live state's theme
    /// <b>once</b> through this lens; there is no second, corrective pass, so a colour drag
    /// (which saves on every pointer move) costs exactly one global application.
    /// </summary>
    internal Func<AppSettings, bool?>? FocusLensResolver { get; set; }

    /// <summary>
    /// The global scope the 失焦自动切换 flips: resolves the state's theme (its light/dark
    /// pair + optional surface material over the shared parameters) and runs the very same
    /// application-level switch the settings page's theme buttons run — one code path, so a
    /// focus flip is a real theme switch, not a per-window imitation. Returns whether the
    /// application actually ran (an unchanged state short-circuits to <c>false</c>).
    /// </summary>
    /// <param name="force">
    /// Run the application even when the last pass already applied the very same tuple. The
    /// 失焦自动切换 master switch needs it: flipping the switch changes nothing in the tuple the
    /// application is keyed on (every widget window keeps resolving its own material), so without
    /// a forced pass the windows are never told to re-resolve and stay on the previous theme.
    /// </param>
    public bool ApplyFocusState(AppSettings settings, bool focused, bool force = false)
    {
        var scopeTheme = settings.FocusLink is { Enabled: true } link
            ? FocusThemeCoordinator.ResolveStateTheme(link, focused, settings)
            : null;
        return ApplyCoreWithTheme(settings, scopeTheme, force);
    }

    /// <summary>What the last <see cref="ApplyCoreWithTheme"/> actually applied — an unchanged
    /// tuple skips the run entirely (focus flips, settings saves and wallpaper events all land here).</summary>
    private (AppSettings? Settings, ThemeVariant Variant, Theme? ScopeTheme) lastGlobalScope;

    private void ApplyCore(AppSettings settings)
    {
        var lens = FocusLensResolver?.Invoke(settings);
        var scopeTheme = lens is { } focused && settings.FocusLink is { Enabled: true } link
            ? FocusThemeCoordinator.ResolveStateTheme(link, focused, settings)
            : null;
        ApplyCoreWithTheme(settings, scopeTheme);
    }

    private bool ApplyCoreWithTheme(AppSettings settings, Theme? scopeTheme, bool force = false)
    {
        var appTheme = (scopeTheme ?? ResolveApplicationTheme(settings)).NormalizeForSurface();
        var mode = new FocusMode(appTheme.DarkMode, appTheme.AutoTheme);
        var darkMode = mode.AutoTheme ? wallpaperThemeService.IsWallpaperDark() : mode.DarkMode;
        var appVariant = darkMode switch
        {
            null => ThemeVariant.Default,
            false => ThemeVariant.Light,
            _ => ThemeVariant.Dark,
        };

        if (!force
            && lastGlobalScope.Settings?.Equals(settings) == true
            && lastGlobalScope.Variant == appVariant
            && lastGlobalScope.ScopeTheme == appTheme)
        {
            return false;
        }
        lastGlobalScope = (settings, appVariant, appTheme);

        Application.Current!.RequestedThemeVariant = appVariant;

        var appProfile = ThemeProfileResolver.Resolve(appTheme, appVariant, settings);

        // Uniform switching: one state is live at a time, so exactly one surface material
        // is in play — the live theme's own. (Before the uniform switch this was the union
        // of the two states' materials, mounted to serve per-screen windows.)
        var surfaces = new HashSet<SurfaceStyle> { appTheme.EffectiveSurface };
        
        Application.Current.Resources["FontFamily"] = settings.Theme.FontFamily == "Inter"
            ? new FontFamily("avares://Avalonia.Fonts.Inter#Inter")
            : new FontFamily(settings.Theme.FontFamily);

        // 全局元素阴影 (设置 → 外观): a soft drop shadow behind widget text and elem-shadow-marked
        // template parts (progress pills, metric rings), so content stays readable over busy
        // wallpapers. The effect is pushed to the live widget windows by WidgetTextShadow (see its
        // remarks for why the application happens in code instead of a style).
        WidgetTextShadow.Update(settings.Theme);

        // OS-level acrylic (the Transparent style sets the AcrylicBlur hint on the
        // windows): the desktop composer samples the live desktop every frame, so
        // dynamic wallpapers stay live behind the widgets. OpacityLevel is the
        // coating alpha. In Colorful mode, opacity is strictly 1.0 (opaque cards).
        Application.Current.Resources["BackgroundOpacity"] = appTheme.IsColorful ? 1.0 : appTheme.OpacityLevel;

        // Animated wallpapers are sampled periodically. Turning this off keeps the
        // last captured frame and makes liquid glass deterministic/static. Both
        // rendered materials carry their own sampling settings — the live theme's
        // own optics drive the pipeline, exactly like a manual theme switch.
        if (appTheme.UsesRenderedGlass)
        {
            var glass = appTheme.EffectiveGlass;
            LiquidGlassWallpaper.ConfigureLiveSampling(glass.LiveSampling, glass.LiveSamplingInterval);
        }
        else
        {
            LiquidGlassWallpaper.ConfigureLiveSampling(false, LiquidGlassWallpaper.DefaultIntervalMs);
            LiquidGlassSourceCache.Clear();
        }

        // 纯色 surface: the card color (per dark/light variant) and the coating
        // opacity — Solid.axaml's WidgetBackground brush picks these up. In Colorful
        // mode, fixed authentic Apple card backgrounds are strictly enforced.
        // Shared with the focus path's window-scope writes (single source of truth).
        ThemeResourceHelper.WriteScopeCardResources(Application.Current.Resources, appTheme);

        // Base accent fallback (generic icons / title fallback) is always on
        ThemeResourceHelper.SwitchStyle(accentStyle, true);

        var isDarkVariant = darkMode ?? Application.Current.ActualThemeVariant == ThemeVariant.Dark;

        if (appProfile.ColorPolicy == ThemeColorPolicy.ColorfulFixed)
        {
            // 卸载普通单色样式
            ThemeResourceHelper.SwitchStyle(monochromeBlackWhiteStyle, false);
            ThemeResourceHelper.SwitchStyle(monochromeBackgroundStyle, false);
            ThemeResourceHelper.SwitchStyle(monochromeStyle, false);

            // 清理普通强调色 ramp 和直接覆盖键
            ThemeResourceHelper.ClearAccentOverrides(Application.Current.Resources);
            ThemeResourceHelper.ClearDirectComponentColorOverrides(Application.Current.Resources);

            // 挂载多彩样式与资源字典
            ThemeResourceHelper.SwitchStyle(colorfulStyle, true);
            AttachApplicationColorfulResources();

            Application.Current.Resources["ThemeScopePolicy"] = ThemeColorPolicy.ColorfulFixed;
            Application.Current.Resources["ColorfulActive"] = true;
        }
        else
        {
            // 卸载多彩样式与资源字典
            ThemeResourceHelper.SwitchStyle(colorfulStyle, false);
            RemoveApplicationColorfulResources();
            Application.Current.Resources.Remove("ColorfulActive");
            ThemeResourceHelper.ClearDirectComponentColorOverrides(Application.Current.Resources);
            ThemeResourceHelper.ClearAccentOverrides(Application.Current.Resources);

            // 应用普通强调色 ramp
            ThemeResourceHelper.ApplyAccentRamp(Application.Current.Resources, appProfile, isDarkVariant);

            // 挂载对应单色样式
            ThemeResourceHelper.SwitchStyle(monochromeBlackWhiteStyle, appProfile.MonochromeVariant == MonochromeStyle.BlackWhite);
            ThemeResourceHelper.SwitchStyle(monochromeBackgroundStyle, appProfile.MonochromeVariant == MonochromeStyle.BackgroundColor);
            ThemeResourceHelper.SwitchStyle(monochromeStyle, appProfile.MonochromeVariant == MonochromeStyle.Accent);

            Application.Current.Resources["ThemeScopePolicy"] = ThemeColorPolicy.Normal;
        }

        // Surface material background styles.
        ThemeResourceHelper.SwitchStyle(transparentStyle, surfaces.Contains(SurfaceStyle.Acrylic) && !appTheme.IsColorful);
        ThemeResourceHelper.SwitchStyle(liquidGlassStyle, surfaces.Contains(SurfaceStyle.LiquidGlass));

        // Text selection pair (W1): refresh only after every style switch above has landed, so
        // the resolver reads the FINAL accent brush of each variant rather than a mid-switch one.
        RefreshTextSelectionPalette();
        return true;
    }

    /// <summary>
    /// Recomputes the paired text-selection brushes (background + readable foreground) for
    /// both variants. Must run on the UI thread; off-thread callers are coalesced onto the
    /// next dispatcher pass so pending resource updates settle first.
    /// </summary>
    private static void RefreshTextSelectionPalette()
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            RefreshTextSelectionPaletteCore();
        }
        else
        {
            Dispatcher.UIThread.Post(RefreshTextSelectionPaletteCore, DispatcherPriority.Background);
        }
    }

    private static void RefreshTextSelectionPaletteCore()
    {
        var light = TextSelectionPaletteResolver.Resolve(ThemeVariant.Light, TextSelectionPaletteResolver.LightFallback);
        var dark = TextSelectionPaletteResolver.Resolve(ThemeVariant.Dark, TextSelectionPaletteResolver.DarkFallback);
        ThemeResourceHelper.UpdateTextSelectionResources(light, dark);
    }
    /// <summary>
    /// Applies the computed accent ramp to the target resource dictionary (either Application.Resources
    /// or a specific Window.Resources for focus-linked scoped styling).
    /// </summary>
    public static void ApplyAccentRamp(Avalonia.Controls.IResourceDictionary resources, Theme theme, bool isDarkVariant)
        => ThemeResourceHelper.ApplyAccentRamp(resources, theme, isDarkVariant);

    public static void ApplyAccentRamp(Avalonia.Controls.IResourceDictionary resources, ResolvedThemeProfile profile, bool isDarkVariant)
        => ThemeResourceHelper.ApplyAccentRamp(resources, profile, isDarkVariant);
}
