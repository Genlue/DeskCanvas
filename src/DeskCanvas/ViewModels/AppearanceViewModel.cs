using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Runtime.CompilerServices;
using Avalonia.Media;
using Avalonia.Threading;
using ReactiveUI;
using DeskCanvas.Core.Interfaces;
using DeskCanvas.Core.Models.Settings;
using DeskCanvas.Locales;
using DeskCanvas.Services;
using DeskCanvas.Views.Controls;
// Alias: the TitleBarStyle property below would otherwise shadow the enum type name.
using TitleBarStyleEnum = DeskCanvas.Core.Models.Settings.TitleBarStyle;

namespace DeskCanvas.ViewModels;

public class AppearanceViewModel : ReactiveObject
{
    private readonly IAppSettingsProvider appSettingsProvider;
    private readonly FocusWatchService? focusWatch;
    private readonly AppearanceSettingsSession themeSettings;
    private readonly AppearanceSettingsSession sliderSettings;
    private bool pendingThemeSave;
    private bool pendingSliderSave;
    private string? themeSaveError;
    private string? sliderSaveError;
    private Theme SliderTheme => pendingSliderSave ? sliderSettings.Draft : appSettingsProvider.Get().Theme;
    private string? settingsError;
    public string? SettingsError
    {
        get => settingsError;
        private set
        {
            this.RaiseAndSetIfChanged(ref settingsError, value);
            this.RaisePropertyChanged(nameof(HasSettingsError));
        }
    }
    public bool HasSettingsError => !string.IsNullOrEmpty(SettingsError);
    public Theme? PendingTheme => pendingThemeSave ? themeSettings.Draft : null;

    public AppearanceViewModel(IAppSettingsProvider appSettingsProvider, FocusWatchService? focusWatch = null)
    {
        this.appSettingsProvider = appSettingsProvider;
        themeSettings = new AppearanceSettingsSession(appSettingsProvider);
        sliderSettings = new AppearanceSettingsSession(appSettingsProvider);
        this.focusWatch = focusWatch;
        if (focusWatch != null)
        {
            // The status line follows the live activation state (the watcher raises on the UI
            // thread; the marshal is only a safety net for a shutdown-time raise).
            focusWatch.StateChanged += (_, _) =>
            {
                if (Dispatcher.UIThread.CheckAccess())
                    this.RaisePropertyChanged(nameof(FocusStatusText));
                else
                    Dispatcher.UIThread.Post(() => this.RaisePropertyChanged(nameof(FocusStatusText)));
            };
        }
        // Surface-dependent rows (高光) and the monochrome color picker
        // appear/disappear when the settings change.
        appSettingsProvider.DataChanged += (_, oldData, newData) =>
        {
            this.RaisePropertyChanged(nameof(ShowHighlightSettings));
            this.RaisePropertyChanged(nameof(ShowGlassOpticsSettings));
            this.RaisePropertyChanged(nameof(ShowLegacyOptics));
            this.RaisePropertyChanged(nameof(ShowV2Settings));
            this.RaisePropertyChanged(nameof(ShowSoftGlowSettings));
            this.RaisePropertyChanged(nameof(GlassOpticsTitle));
            this.RaisePropertyChanged(nameof(OpacityLevel));
            this.RaisePropertyChanged(nameof(Font));
            this.RaisePropertyChanged(nameof(GlassBlur));
            this.RaisePropertyChanged(nameof(GlassRefraction));
            this.RaisePropertyChanged(nameof(GlassHighlight));
            this.RaisePropertyChanged(nameof(GlassHighlightAngle));
            this.RaisePropertyChanged(nameof(GlassDispersion));
            this.RaisePropertyChanged(nameof(GlassVibrancy));
            this.RaisePropertyChanged(nameof(GlassBackdropClarity));
            this.RaisePropertyChanged(nameof(LiveSampling));
            this.RaisePropertyChanged(nameof(LiveSamplingInterval));
            this.RaisePropertyChanged(nameof(V2Blur));
            this.RaisePropertyChanged(nameof(V2Refraction));
            this.RaisePropertyChanged(nameof(V2Highlight));
            this.RaisePropertyChanged(nameof(V2HighlightAngle));
            this.RaisePropertyChanged(nameof(V2Vibrancy));
            this.RaisePropertyChanged(nameof(V2Dispersion));
            this.RaisePropertyChanged(nameof(V2LiveSampling));
            this.RaisePropertyChanged(nameof(V2LiveSamplingInterval));
            this.RaisePropertyChanged(nameof(V2BackdropClarity));
            this.RaisePropertyChanged(nameof(Monochrome));
            this.RaisePropertyChanged(nameof(MonochromeVariant));
            this.RaisePropertyChanged(nameof(ShowMonochromeVariant));
            this.RaisePropertyChanged(nameof(ShowTitleBarSize));
            this.RaisePropertyChanged(nameof(TitleBarSize));
            this.RaisePropertyChanged(nameof(DarkMode));
            // The shadow row has a slider *and* a numeric box side by side, so both have to be
            // told about a change: the slider moves this property two-way, and without the
            // announcement below the box kept showing the value it was built with.
            this.RaisePropertyChanged(nameof(TextShadowEnabled));
            this.RaisePropertyChanged(nameof(ShowTextShadowStrength));
            this.RaisePropertyChanged(nameof(TextShadowStrength));
            // The accent controls read the settings directly, so they have to announce changes made
            // elsewhere (another settings window, a profile switch, …) instead of keeping a stale
            // colour on screen.
            this.RaisePropertyChanged(nameof(ShowColorPalette));
            this.RaisePropertyChanged(nameof(ShowAccentSetting));
            this.RaisePropertyChanged(nameof(ShowOpacitySetting));
            this.RaisePropertyChanged(nameof(ShowMonochromeSetting));
            this.RaisePropertyChanged(nameof(SolidBackgroundDarkHex));
            this.RaisePropertyChanged(nameof(SolidBackgroundLightHex));
            this.RaisePropertyChanged(nameof(ShowSolidBackgroundSetting));
            this.RaisePropertyChanged(nameof(AccentColorHex));
            this.RaisePropertyChanged(nameof(AccentShift));
            this.RaisePropertyChanged(nameof(FollowAccentBackground));
            this.RaisePropertyChanged(nameof(SolidBackgroundEditable));
            // Only re-resolve the combo when the manual/system choice itself changed: a plain
            // colour change must not touch the selection, or the closed combo would flicker on
            // every pointer move of the palette.
            if ((oldData?.Theme.AccentColor is null) != (newData.Theme.AccentColor is null))
                this.RaisePropertyChanged(nameof(AccentMode));
            // 失焦自动切换 rows: the switch, the two state pairs and the allowlist all live
            // in the settings file, so they have to announce external changes too.
            this.RaisePropertyChanged(nameof(FocusLinkEnabled));
            this.RaisePropertyChanged(nameof(ShowDarkModeRow));
            this.RaisePropertyChanged(nameof(FocusModeFocused));
            this.RaisePropertyChanged(nameof(FocusModeUnfocused));
            this.RaisePropertyChanged(nameof(FocusSurfaceFocused));
            this.RaisePropertyChanged(nameof(FocusSurfaceUnfocused));
            this.RaisePropertyChanged(nameof(FocusSwitchDelayMs));
            this.RaisePropertyChanged(nameof(FocusStatusText));
            RefreshFocusWhitelist();
        };
        // Fixed material presets; other appearance preferences are preserved.
        Themes = SurfaceTemplates.Select(theme => new ThemeButton(appSettingsProvider, theme)).ToArray();
        RefreshFocusWhitelist();
    }

    /// <summary>
    /// The surface presets shown as the 应用主题 cards — the shared catalog
    /// <see cref="ThemePresets.SurfaceTemplates"/> (also consumed by the 失焦自动切换 state
    /// combos and the frameless clock's material list, so none of them can drift apart).
    /// </summary>
    public static readonly Theme[] SurfaceTemplates = ThemePresets.SurfaceTemplates;

    public ThemeButton[] Themes { get; }

    /// <summary>
    /// True when the 高光 rows apply: every material that is not drawn inside a native frame
    /// (液态玻璃 reads them in its shader; 毛玻璃 and 全彩 draw the same rim in the host). The
    /// rows write <see cref="Theme.EffectiveLiquidGlass"/>'s shared highlight optics, so the
    /// look cannot drift between materials.
    /// </summary>
    public bool ShowHighlightSettings => !appSettingsProvider.Get().Theme.UseNativeFrame;

    /// <summary>
    /// True for the wallpaper-sampled materials: 液态玻璃 and 新液态玻璃 expose an optics section;
    /// it is hidden for 毛玻璃 and 纯色.
    /// </summary>
    public bool ShowGlassOpticsSettings => appSettingsProvider.Get().Theme.UsesRenderedGlass;

    public bool ShowLegacyOptics => false;

    public bool ShowV2Settings => false;

    public bool ShowSoftGlowSettings => false;

    /// <summary>
    /// Section title of the optics block: 液态玻璃.
    /// </summary>
    public string GlassOpticsTitle => Locale.Settings_Appearance_Surface_LiquidGlass;

    /// <summary>
    /// Whether the optics rows are unfolded.
    /// </summary>
    public bool OpticsExpanded
    {
        get => opticsExpanded;
        set { if (opticsExpanded == value) return; opticsExpanded = value; this.RaisePropertyChanged(); }
    }
    private bool opticsExpanded;

    public double GlassBlur
    {
        get => SliderTheme.EffectiveLiquidGlass.Blur;
        set => UpdateGlass(glass => glass with { Blur = value });
    }
    public double GlassRefraction
    {
        get => SliderTheme.EffectiveLiquidGlass.Refraction;
        set => UpdateGlass(glass => glass with { Refraction = value });
    }
    public double GlassHighlight
    {
        get => SliderTheme.EffectiveLiquidGlass.Highlight;
        set => UpdateGlass(glass => glass with { Highlight = value });
    }
    public double GlassHighlightAngle
    {
        get => SliderTheme.EffectiveLiquidGlass.HighlightAngle;
        set => UpdateGlass(glass => glass with { HighlightAngle = value });
    }
    public double GlassDispersion
    {
        get => SliderTheme.EffectiveLiquidGlass.Dispersion;
        set => UpdateGlass(glass => glass with { Dispersion = value });
    }
    public double GlassVibrancy
    {
        get => SliderTheme.EffectiveLiquidGlass.Vibrancy;
        set => UpdateGlass(glass => glass with { Vibrancy = value });
    }
    public double GlassBackdropClarity
    {
        get => SliderTheme.EffectiveLiquidGlass.BackdropClarity;
        set => UpdateGlass(glass => glass with { BackdropClarity = value });
    }

    /// <summary>Legacy compatibility aliases</summary>
    public double GlassEdgeWidth { get => 0; set { } }
    public double GlassLightAngle { get => GlassHighlightAngle; set => GlassHighlightAngle = value; }
    public double GlassEdgeTint { get => 0; set { } }
    public double GlassGlow { get => 0; set { } }
    public double GlassSpectrum { get => 0; set { } }
    public double GlassDyeSpread { get => 0; set { } }

    /// <summary>Sample the desktop continuously so animated wallpapers stay live behind the glass.</summary>
    public bool LiveSampling
    {
        get => appSettingsProvider.Get().Theme.EffectiveLiquidGlass.LiveSampling;
        set => UpdateGlass(glass => glass with { LiveSampling = value }, commitImmediately: true);
    }

    /// <summary>Sampling interval in milliseconds.</summary>
    public int LiveSamplingInterval
    {
        get => appSettingsProvider.Get().Theme.EffectiveLiquidGlass.LiveSamplingInterval;
        set => UpdateGlass(glass => glass with { LiveSamplingInterval = value }, commitImmediately: true);
    }

    // ---------- V2 forwarding aliases for backward compatibility ----------

    public double V2Blur { get => GlassBlur; set => GlassBlur = value; }
    public double V2Refraction { get => GlassRefraction; set => GlassRefraction = value; }
    public double V2Highlight { get => GlassHighlight; set => GlassHighlight = value; }
    public double V2HighlightAngle { get => GlassHighlightAngle; set => GlassHighlightAngle = value; }
    public double V2Vibrancy { get => GlassVibrancy; set => GlassVibrancy = value; }
    public double V2Dispersion { get => GlassDispersion; set => GlassDispersion = value; }
    public bool V2LiveSampling { get => LiveSampling; set => LiveSampling = value; }
    public int V2LiveSamplingInterval { get => LiveSamplingInterval; set => LiveSamplingInterval = value; }
    public double V2BackdropClarity { get => GlassBackdropClarity; set => GlassBackdropClarity = value; }

    private void UpdateV2(Func<LiquidGlassV2Settings, LiquidGlassV2Settings> update,
        bool commitImmediately = false, [CallerMemberName] string? propertyName = null)
    {
        UpdateGlass(glass => update(new LiquidGlassV2Settings(glass)), commitImmediately, propertyName);
    }

    private void UpdateGlass(Func<LiquidGlassSettings, LiquidGlassSettings> update,
        bool commitImmediately = false, [CallerMemberName] string? propertyName = null)
    {
        var theme = commitImmediately ? appSettingsProvider.Get().Theme : SliderTheme;
        var glass = update(theme.EffectiveLiquidGlass).Normalize();
        if (glass == theme.EffectiveLiquidGlass) return;
        var v2 = new LiquidGlassV2Settings(glass);
        if (commitImmediately) SaveTheme(theme with { LiquidGlass = glass, LiquidGlassV2 = v2 });
        else StageSlider(draft => draft with { LiquidGlass = glass, LiquidGlassV2 = v2 }, propertyName);
    }

    private void StageSlider(Func<Theme, Theme> update, string? propertyName)
    {
        var next = update(SliderTheme);
        if (next == SliderTheme) return;
        if (!pendingSliderSave) sliderSettings.Reload();
        sliderSettings.Stage(next);
        pendingSliderSave = true;
        this.RaisePropertyChanged(propertyName);
    }

    public bool CommitThemeSliders()
    {
        if (!pendingSliderSave) return true;
        var result = sliderSettings.Commit();
        pendingSliderSave = !result.Succeeded;
        sliderSaveError = result.Error;
        RefreshSettingsError();
        RaiseSliderProperties();
        return result.Succeeded;
    }

    private void RaiseSliderProperties()
    {
        foreach (var name in new[] { nameof(OpacityLevel), nameof(GlassBlur), nameof(GlassRefraction),
            nameof(GlassHighlight), nameof(GlassHighlightAngle), nameof(GlassDispersion), nameof(GlassVibrancy),
            nameof(GlassBackdropClarity), nameof(V2Blur), nameof(V2Refraction), nameof(V2Highlight),
            nameof(V2HighlightAngle), nameof(V2Vibrancy), nameof(V2Dispersion), nameof(V2BackdropClarity),
            nameof(TextShadowStrength), nameof(AccentShift) })
            this.RaisePropertyChanged(name);
    }

    private void SaveTheme(Theme newTheme)
    {
        // A failed durable write retains its complete draft until retry or explicit reload.
        if (pendingThemeSave) return;
        themeSettings.Reload();
        themeSettings.Stage(newTheme);
        var result = themeSettings.Commit();
        pendingThemeSave = !result.Succeeded;
        themeSaveError = result.Error;
        RefreshSettingsError();
    }

    private void RefreshSettingsError() => SettingsError = (themeSaveError ?? sliderSaveError) switch
    {
        null => null,
        "revision-conflict" => Locale.Settings_Appearance_SaveConflict,
        _ => Locale.Settings_Appearance_SaveFailed
    };

    public void RetrySettingsSave()
    {
        if (pendingThemeSave)
        {
            var result = themeSettings.Commit();
            pendingThemeSave = !result.Succeeded;
            themeSaveError = result.Error;
            RefreshSettingsError();
            this.RaisePropertyChanged(nameof(Font));
        }
        else CommitThemeSliders();
    }

    public void ReloadSettings()
    {
        pendingThemeSave = false;
        pendingSliderSave = false;
        themeSaveError = sliderSaveError = null;
        themeSettings.Reload();
        sliderSettings.Reload();
        SettingsError = null;
        RaiseSliderProperties();
        this.RaisePropertyChanged(nameof(Font));
    }

    /// <summary>
    /// Reset the active rendered material's optics to its factory recipe.
    /// </summary>
    public void ResetLiquidGlass()
    {
        UpdateGlass(_ => new LiquidGlassSettings(), commitImmediately: true);
    }

    public void RefreshLiquidGlassWallpaper()
    {
        // Force a fresh desktop capture (the normal path reuses a short-lived cache).
        LiquidGlassWallpaper.Invalidate();
        LiquidGlassSurface.RefreshAll();
    }
    
    public DarkModeViewModel[] DarkModes =>
    [
        new DarkModeViewModel(Locale.Settings_Appearance_DarkMode_False, false),
        new DarkModeViewModel(Locale.Settings_Appearance_DarkMode_True, true),
        new DarkModeViewModel(Locale.Settings_Appearance_DarkMode_Null, null),
        new DarkModeViewModel(Locale.Settings_Appearance_DarkMode_Auto, null, Auto: true)
    ];

    /// <summary>
    /// Colour the 手动 entry offers while no manual colour has been stored yet.
    /// </summary>
    private const string ManualAccentDefault = "#3376CD";

    private AccentColorViewModel[]? accentComboboxItems;

    /// <summary>
    /// The accent source options (跟随系统 / 手动).
    /// <para>
    /// Built once and never replaced. Re-creating the items makes the closed ComboBox throw away
    /// and rebuild its containers, which visibly flickers its text — and picking a colour updates
    /// the settings on every pointer move, so that used to flicker constantly while the palette was
    /// open. What protects a stored colour is the mode check in <see cref="AccentMode"/>'s setter,
    /// not the value carried here.
    /// </para>
    /// </summary>
    public AccentColorViewModel[] AccentComboboxItems =>
        accentComboboxItems ??=
        [
            new AccentColorViewModel(Locale.Settings_Appearance_AccentColor_Null, null),
            new AccentColorViewModel(Locale.Settings_Appearance_AccentColor_Manual, ManualAccentDefault)
        ];

    /// <summary>
    /// Current color policy resolved from the shared Theme profile.
    /// Under ColorfulFixed policy, accent and monochrome controls are hidden.
    /// </summary>
    public ThemeColorPolicy CurrentColorPolicy =>
        ThemeProfileResolver.Resolve(appSettingsProvider.Get().Theme, Avalonia.Styling.ThemeVariant.Default, appSettingsProvider.Get()).ColorPolicy;

    /// <summary>
    /// Accent color setting is hidden in Colorful mode, which uses authentic fixed macOS palettes.
    /// </summary>
    public bool ShowAccentSetting => CurrentColorPolicy == ThemeColorPolicy.Normal;

    /// <summary>
    /// Opacity slider is hidden in Colorful mode, which uses 100% opaque cards.
    /// </summary>
    public bool ShowOpacitySetting => CurrentColorPolicy == ThemeColorPolicy.Normal;

    /// <summary>
    /// Monochrome toggle is hidden in Colorful mode, which strictly uses Apple HIG vibrant semantic palettes.
    /// </summary>
    public bool ShowMonochromeSetting => CurrentColorPolicy == ThemeColorPolicy.Normal;

    /// <summary>
    /// 背景颜色 rows are hidden in Colorful mode, which pins the fixed macOS card colours.
    /// </summary>
    public bool ShowSolidBackgroundSetting => CurrentColorPolicy == ThemeColorPolicy.Normal;


    public bool ShowColorPalette => appSettingsProvider.Get().Theme.AccentColor != null;
    
    public AccentColorViewModel AccentMode
    {
        get => appSettingsProvider.Get().Theme.AccentColor == null ? AccentComboboxItems[0] : AccentComboboxItems[1];
        set
        {
            if (value == null) return;

            // Selecting the entry that is already active is a no-op. The ComboBox re-resolves
            // SelectedItem whenever it (re)attaches — and its 手动 entry carries a default colour,
            // so acting on that would overwrite whatever the user picked.
            var settings = appSettingsProvider.Get();
            if ((value.Value != null) == (settings.Theme.AccentColor != null)) return;

            var newTheme = settings.Theme with { AccentColor = value.Value };
            SaveTheme(newTheme);
        }
    }

    /// <summary>
    /// The manual accent colour, exactly as it is stored in the settings JSON ("#AARRGGBB").
    /// <para>
    /// The string is the model's own shape, and the editor owns parsing, alpha policy and
    /// validation instead of this row: see <c>ColorInput</c>. <c>null</c> is a real state here
    /// (跟随系统), not a parsing failure.
    /// </para>
    /// </summary>
    public string? AccentColorHex
    {
        get => appSettingsProvider.Get().Theme.AccentColor;
        set
        {
            var settings = appSettingsProvider.Get();
            if (settings.Theme.AccentColor == value) return;
            SaveTheme(settings.Theme with { AccentColor = value });
        }
    }

    /// <summary>
    /// 强调色偏移, 0-100 (%): how far the effective accent is pushed toward white (dark mode) or
    /// black (light mode).
    /// <para>
    /// Saved on every tick instead of being staged until release: the accent ramp is what the
    /// whole UI paints with, and the accent colour picker above already commits on every pointer
    /// move. Staging would leave the slider visibly dead while it is dragged, then snap once.
    /// </para>
    /// </summary>
    public double AccentShift
    {
        // A slider, so it follows the page's slider contract: the drag stages a draft (no file
        // write per step) and ValueCommitted persists it. Saving from the setter instead would
        // rewrite appSettings.json and re-run the whole theme apply on every pixel of the drag.
        get => SliderTheme.EffectiveAccentShift;
        set
        {
            var shift = AccentPalette.NormalizeShift(value);
            StageSlider(draft => draft with { AccentShift = shift }, nameof(AccentShift));
        }
    }

    /// <summary>
    /// 跟随强调色: the 纯色 background pair follows the accent, shifted the opposite way (light
    /// mode → toward white, dark mode → toward black) so text stays readable.
    /// <para>
    /// Only the flag is stored. The derived pair is computed at render time, so the user's own
    /// hex values stay in the settings file and switching this back off restores them exactly.
    /// </para>
    /// </summary>
    public bool FollowAccentBackground
    {
        get => appSettingsProvider.Get().Theme.FollowAccentBackground;
        set
        {
            var settings = appSettingsProvider.Get();
            if (settings.Theme.FollowAccentBackground == value) return;
            SaveTheme(settings.Theme with { FollowAccentBackground = value });
        }
    }

    /// <summary>
    /// Whether the two 背景颜色 inputs still own the card colour. While 跟随强调色 is on they are
    /// disabled rather than fed the derived colours: the inputs edit the <b>stored</b> pair, and
    /// showing a computed value there would let the next edit silently overwrite the colour the
    /// user is keeping for when they turn the switch back off.
    /// </summary>
    public bool SolidBackgroundEditable => !FollowAccentBackground;

    public DarkModeViewModel? DarkMode
    {
        get
        {
            var theme = appSettingsProvider.Get().Theme;
            return DarkModes.FirstOrDefault(mode => mode.Auto == theme.AutoTheme
                && (theme.AutoTheme || mode.Value == theme.DarkMode));
        }
        set
        {
            var settings = appSettingsProvider.Get();
            var darkMode = value?.Value;
            var auto = value?.Auto ?? false;
            // Idempotent: the ComboBox writes its resolved item back on attach.
            if (settings.Theme.DarkMode == darkMode && settings.Theme.AutoTheme == auto) return;
            var newTheme = settings.Theme with
            {
                DarkMode = darkMode,
                AutoTheme = auto
            };
            // SaveTheme (not a bare settings save): the light/dark mode belongs to the
            // material preset like every colour setting, so each 应用主题 theme keeps its
            // own mode and switching themes switches modes with them.
            SaveTheme(newTheme);
        }
    }

    // ---------- 失焦自动切换 (auto light/dark switching when the desktop loses focus) ----------
    //
    // Both states share every appearance parameter above; the only per-state setting is the
    // light/dark mode pair. The pairs live in FocusLinkSettings (not in Theme) — Theme's own
    // pair stays untouched and drives everything again once the feature is switched off.

    /// <summary>
    /// Master switch of 失焦自动切换; <c>false</c> by default (and for configurations that
    /// never enabled it). Enabling seeds both states from the theme's current light/dark pair,
    /// so flipping the switch alone changes nothing visually.
    /// </summary>
    public bool FocusLinkEnabled
    {
        get => appSettingsProvider.Get().FocusLink?.Enabled ?? false;
        set
        {
            var settings = appSettingsProvider.Get();
            if ((settings.FocusLink?.Enabled ?? false) == value) return;
            SaveFocusLink(link => link with { Enabled = value });
        }
    }

    /// <summary>
    /// While the feature is off the original single 亮暗模式 row is shown; while it is on that
    /// row is replaced by the two per-state rows below it.
    /// </summary>
    public bool ShowDarkModeRow => !FocusLinkEnabled;

    /// <summary>Light/dark pair used while the desktop is focused.</summary>
    public DarkModeViewModel FocusModeFocused
    {
        get => ResolveModeViewModel(EffectiveFocusedMode);
        set
        {
            if (value == null) return;
            var mode = new FocusMode(value.Value, value.Auto);
            SaveFocusLink(link => link.Focused == mode ? link : link with { Focused = mode });
        }
    }

    /// <summary>Light/dark pair used while an application window is in the foreground.</summary>
    public DarkModeViewModel FocusModeUnfocused
    {
        get => ResolveModeViewModel(EffectiveUnfocusedMode);
        set
        {
            if (value == null) return;
            var mode = new FocusMode(value.Value, value.Auto);
            SaveFocusLink(link => link.Unfocused == mode ? link : link with { Unfocused = mode });
        }
    }

    /// <summary>
    /// The per-state 主题 options, derived from the same preset list the 应用主题 cards use
    /// (<see cref="SurfaceTemplates"/>) so the two can never drift apart. The first entry
    /// (<c>null</c>) means "follow the shared theme"; the rest are the five materials —
    /// including 多彩, which is isolated per window by the coordinator.
    /// </summary>
    public FocusSurfaceViewModel[] FocusSurfaceOptions { get; } =
    [
        new(Locale.Settings_Appearance_FocusLink_Theme_Shared, null),
        .. SurfaceTemplates.Select(template =>
            new FocusSurfaceViewModel(SurfaceNames.Of(template.EffectiveSurface), template.EffectiveSurface))
    ];

    /// <summary>Theme (surface material) of the focused state; the null entry = follow the app theme.</summary>
    public FocusSurfaceViewModel FocusSurfaceFocused
    {
        get => FocusSurfaceOptions.First(option =>
            option.Value == FocusLinkSettings.NormalizeSurface(appSettingsProvider.Get().FocusLink?.FocusedSurface));
        set
        {
            if (value == null) return;
            var surface = FocusLinkSettings.NormalizeSurface(value.Value);
            SaveFocusLink(link => link.FocusedSurface == surface ? link : link with { FocusedSurface = surface });
        }
    }

    /// <summary>Theme (surface material) of the unfocused state; the null entry = follow the app theme.</summary>
    public FocusSurfaceViewModel FocusSurfaceUnfocused
    {
        get => FocusSurfaceOptions.First(option =>
            option.Value == FocusLinkSettings.NormalizeSurface(appSettingsProvider.Get().FocusLink?.UnfocusedSurface));
        set
        {
            if (value == null) return;
            var surface = FocusLinkSettings.NormalizeSurface(value.Value);
            SaveFocusLink(link => link.UnfocusedSurface == surface ? link : link with { UnfocusedSurface = surface });
        }
    }

    /// <summary>
    /// Anti-flicker dwell (检测时长): how long a new focus state must persist before the switch
    /// is applied, 0–1000 ms with 0 switching immediately; typed freely in the numeric box.
    /// </summary>
    public int FocusSwitchDelayMs
    {
        get => appSettingsProvider.Get().FocusLink?.EffectiveSwitchDelayMs ?? FocusLinkSettings.DefaultSwitchDelayMs;
        set
        {
            var clamped = Math.Clamp(value, FocusLinkSettings.SwitchDelayMinMs, FocusLinkSettings.SwitchDelayMaxMs);
            // Guard inside the update so reading the box never materializes a FocusLink block.
            SaveFocusLink(link => link.SwitchDelayMs == clamped ? link : link with { SwitchDelayMs = clamped });
        }
    }

    /// <summary>
    /// Read-only status line — the current state plus the subject that explains it, the
    /// debugging aid for “why did it not switch?”. While an application keeps the state
    /// unfocused it names that application (the leading one — stable across clicks, unlike the
    /// momentary foreground); with the desktop clear it names the momentary foreground
    /// (desktop / a whitelisted app / the widgets themselves …). It always reflects the
    /// watcher's live state, even while the feature is switched off.
    /// </summary>
    public string FocusStatusText
    {
        get
        {
            if (appSettingsProvider.Get().FocusLink is not { Enabled: true })
                return Locale.Settings_Appearance_FocusLink_Status_Disabled;

            var state = focusWatch?.Current ?? FocusStateSnapshot.Focused;
            var status = state.AnyApplicationFocused
                ? Locale.Settings_Appearance_FocusLink_Status_Unfocused
                : Locale.Settings_Appearance_FocusLink_Status_Focused;

            if (state.AnyApplicationFocused)
            {
                // The window that keeps the state unfocused — “close/minimize it to go back”.
                var application = state.ProcessName ?? Locale.Settings_Appearance_FocusLink_Foreground_None;
                return $"{status} · {string.Format(Locale.Settings_Appearance_FocusLink_Status_Application, application)}";
            }

            var foreground = state.Kind switch
            {
                ForegroundKind.None => Locale.Settings_Appearance_FocusLink_Foreground_None,
                ForegroundKind.Shell => Locale.Settings_Appearance_FocusLink_Foreground_Desktop,
                ForegroundKind.OwnFurniture => Locale.Settings_Appearance_FocusLink_Foreground_Widget,
                _ => state.ProcessName ?? Locale.Settings_Appearance_FocusLink_Foreground_None
            };

            return $"{status} · {string.Format(Locale.Settings_Appearance_FocusLink_Status_Foreground, foreground)}";
        }
    }

    /// <summary>The process allowlist, shown as removable chips.</summary>
    public ObservableCollection<FocusWhitelistItemViewModel> FocusWhitelist { get; } = [];

    /// <summary>True while the allowlist is empty (the hint line is shown instead).</summary>
    public bool ShowFocusWhitelistEmpty => FocusWhitelist.Count == 0;

    /// <summary>Append processes picked in the process dialog (case-insensitive de-duplication).</summary>
    public void AddFocusProcesses(IEnumerable<string> names)
    {
        var settings = appSettingsProvider.Get();
        var link = settings.FocusLink ?? FocusLinkSettings.CreateDefault(settings.Theme.DarkMode, settings.Theme.AutoTheme);
        var list = link.EffectiveWhitelist.ToList();
        foreach (var name in names)
        {
            var trimmed = name?.Trim();
            if (string.IsNullOrEmpty(trimmed)) continue;
            if (!list.Contains(trimmed, StringComparer.OrdinalIgnoreCase)) list.Add(trimmed);
        }

        if (list.Count == link.EffectiveWhitelist.Length) return;
        appSettingsProvider.Save(settings with { FocusLink = link with { Whitelist = list.ToArray() } });
    }

    /// <summary>Remove one allowlist entry.</summary>
    public void RemoveFocusProcess(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        var settings = appSettingsProvider.Get();
        var link = settings.FocusLink;
        if (link is null) return;

        var list = link.EffectiveWhitelist
            .Where(entry => !string.Equals(entry, name, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (list.Length == link.EffectiveWhitelist.Length) return;
        appSettingsProvider.Save(settings with { FocusLink = link with { Whitelist = list } });
    }

    private FocusMode EffectiveFocusedMode
    {
        get
        {
            var settings = appSettingsProvider.Get();
            return settings.FocusLink?.EffectiveFocused ?? new FocusMode(settings.Theme.DarkMode, settings.Theme.AutoTheme);
        }
    }

    private FocusMode EffectiveUnfocusedMode
    {
        get
        {
            var settings = appSettingsProvider.Get();
            return settings.FocusLink?.EffectiveUnfocused ?? new FocusMode(settings.Theme.DarkMode, settings.Theme.AutoTheme);
        }
    }

    private DarkModeViewModel ResolveModeViewModel(FocusMode mode) =>
        DarkModes.FirstOrDefault(item => item.Auto == mode.AutoTheme && (mode.AutoTheme || item.Value == mode.DarkMode))
        ?? DarkModes[0];

    /// <summary>Update the focus-link block, seeded from the current theme on first use so a
    /// never-enabled configuration can be edited without a separate migration step.</summary>
    private void SaveFocusLink(Func<FocusLinkSettings, FocusLinkSettings> update)
    {
        var settings = appSettingsProvider.Get();
        var link = settings.FocusLink ?? FocusLinkSettings.CreateDefault(settings.Theme.DarkMode, settings.Theme.AutoTheme);
        var next = update(link);
        if (next == link) return;
        appSettingsProvider.Save(settings with { FocusLink = next });
    }

    private void RefreshFocusWhitelist()
    {
        var names = appSettingsProvider.Get().FocusLink?.EffectiveWhitelist ?? [];
        FocusWhitelist.Clear();
        foreach (var name in names) FocusWhitelist.Add(new FocusWhitelistItemViewModel(name));
        this.RaisePropertyChanged(nameof(ShowFocusWhitelistEmpty));
    }

    public double OpacityLevel
    {
        get => SliderTheme.OpacityLevel;
        set
        {
            if (OpacityLevel.Equals(value)) return;
            if (!double.IsFinite(value)) return;
            StageSlider(theme => theme with { OpacityLevel = Math.Clamp(value, 0, 1) }, nameof(OpacityLevel));
        }
    }

    public bool CommitOpacityLevel() => CommitThemeSliders();

    public bool Monochrome
    {
        get => appSettingsProvider.Get().Theme.Monochrome;
        set
        {
            var settings = appSettingsProvider.Get();
            if (settings.Theme.Monochrome == value) return;
            SaveTheme(settings.Theme with { Monochrome = value });
            this.RaisePropertyChanged(nameof(ShowMonochromeVariant));
        }
    }

    /// <summary>
    /// 全局元素阴影 (default off): when on, every TextBlock inside a desktop widget window
    /// gets a soft drop shadow for readability over busy wallpapers, and template parts marked
    /// with the elem-shadow class (progress pills, metric rings) float the same way.
    /// </summary>
    public bool TextShadowEnabled
    {
        get => appSettingsProvider.Get().Theme.TextShadowEnabled;
        set
        {
            var settings = appSettingsProvider.Get();
            if (settings.Theme.TextShadowEnabled == value) return;
            SaveTheme(settings.Theme with { TextShadowEnabled = value });
            this.RaisePropertyChanged(nameof(ShowTextShadowStrength));
        }
    }

    /// <summary>True while the 全局文字阴影 strength slider is shown (shadow enabled).</summary>
    public bool ShowTextShadowStrength => TextShadowEnabled;

    /// <summary>全局文字阴影 strength, 0-100 (scales the shadow's blur, depth and opacity).</summary>
    public double TextShadowStrength
    {
        get => SliderTheme.TextShadowStrength;
        set
        {
            var strength = Math.Clamp(value, 0, 100);
            if (Math.Abs(SliderTheme.TextShadowStrength - strength) < 0.001) return;
            StageSlider(theme => theme with { TextShadowStrength = strength }, nameof(TextShadowStrength));
        }
    }

    /// <summary>
    /// The monochrome color source options (黑白 / 背景色 / 强调色).
    /// </summary>
    public MonochromeVariantViewModel[] MonochromeVariants { get; } =
    [
        new(Locale.Settings_Appearance_Monochrome_Variant_BlackWhite, MonochromeStyle.BlackWhite),
        new(Locale.Settings_Appearance_Monochrome_Variant_Background, MonochromeStyle.BackgroundColor),
        new(Locale.Settings_Appearance_Monochrome_Variant_Accent, MonochromeStyle.Accent)
    ];

    /// <summary>True when the monochrome color source combo is shown (monochrome enabled).</summary>
    public bool ShowMonochromeVariant => Monochrome;

    public MonochromeVariantViewModel MonochromeVariant
    {
        get => MonochromeVariants.First(variant =>
            variant.Value == appSettingsProvider.Get().Theme.EffectiveMonochromeVariant);
        set
        {
            if (value == null) return;
            var settings = appSettingsProvider.Get();
            if (settings.Theme.EffectiveMonochromeVariant == value.Value) return;
            SaveTheme(settings.Theme with { MonochromeVariant = value.Value });
        }
    }

    /// <summary>
    /// Widget card background color in dark mode (HEX, alpha is the global opacity slider's job).
    /// Applies to both 毛玻璃 and 纯色 surfaces.
    /// </summary>
    public string? SolidBackgroundDarkHex
    {
        get => appSettingsProvider.Get().Theme.EffectiveSolidBackgroundDark;
        set
        {
            if (appSettingsProvider.Get().Theme.EffectiveSolidBackgroundDark == value) return;
            SaveSolidBackground(SolidBackgroundDark: value);
        }
    }

    /// <summary>
    /// Widget card background color in light mode (HEX, alpha is the global opacity slider's job).
    /// Applies to both 毛玻璃 and 纯色 surfaces.
    /// </summary>
    public string? SolidBackgroundLightHex
    {
        get => appSettingsProvider.Get().Theme.EffectiveSolidBackgroundLight;
        set
        {
            if (appSettingsProvider.Get().Theme.EffectiveSolidBackgroundLight == value) return;
            SaveSolidBackground(SolidBackgroundLight: value);
        }
    }

    /// <summary>
    /// Apply the currently selected dark-mode color to the light-mode color.
    /// </summary>
    public void ApplySolidToLight()
    {
        SaveSolidBackground(SolidBackgroundLight: SolidBackgroundDarkHex);
        this.RaisePropertyChanged(nameof(SolidBackgroundLightHex));
    }

    /// <summary>
    /// Apply the currently selected light-mode color to the dark-mode color.
    /// </summary>
    public void ApplySolidToDark()
    {
        SaveSolidBackground(SolidBackgroundDark: SolidBackgroundLightHex);
        this.RaisePropertyChanged(nameof(SolidBackgroundDarkHex));
    }

    private void SaveSolidBackground(string? SolidBackgroundDark = null, string? SolidBackgroundLight = null)
    {
        var settings = appSettingsProvider.Get();
        var theme = settings.Theme with
        {
            SolidBackgroundDark = SolidBackgroundDark ?? settings.Theme.SolidBackgroundDark,
            SolidBackgroundLight = SolidBackgroundLight ?? settings.Theme.SolidBackgroundLight
        };
        SaveTheme(theme);
    }


    /// <summary>
    /// Every font available: all fonts installed on the system, plus the bundled Inter font.
    /// </summary>
    public IReadOnlyList<string> AllFonts { get; } = SystemFonts.GetFonts().ToArray();

    private string fontSearchText = "";
    public string FontSearchText
    {
        get => fontSearchText;
        set
        {
            if (fontSearchText == value) return;
            fontSearchText = value;
            this.RaisePropertyChanged();
            this.RaisePropertyChanged(nameof(FilteredFonts));
            this.RaisePropertyChanged(nameof(NoFontResults));
        }
    }

    /// <summary>
    /// Fonts filtered by <see cref="FontSearchText"/>.
    /// </summary>
    public IReadOnlyList<string> FilteredFonts =>
        string.IsNullOrWhiteSpace(FontSearchText)
            ? AllFonts
            : AllFonts
                .Where(font => font.Contains(FontSearchText.Trim(), System.StringComparison.OrdinalIgnoreCase))
                .ToArray();

    /// <summary>
    /// True when the font search matched nothing.
    /// </summary>
    public bool NoFontResults => FilteredFonts.Count == 0;

    public string Font
    {
        get => PendingTheme?.FontFamily ?? appSettingsProvider.Get().Theme.FontFamily;
        set
        {
            // Filtering the font list temporarily clears SelectedItem; this is not a user choice.
            if (string.IsNullOrWhiteSpace(value)) return;
            var settings = appSettingsProvider.Get();
            // Idempotent: the font ComboBox writes its resolved item back on attach.
            if (settings.Theme.FontFamily == value) return;
            SaveTheme(settings.Theme with { FontFamily = value });
        }
    }

    /// <summary>
    /// Title bar style options of the settings window (native system buttons
    /// or macOS traffic lights).
    /// </summary>
    public TitleBarStyleViewModel[] TitleBarStyles =>
    [
        new(Locale.Settings_Advanced_TitleBarStyle_Native, TitleBarStyleEnum.Native),
        new(Locale.Settings_Advanced_TitleBarStyle_TrafficLights, TitleBarStyleEnum.TrafficLights)
    ];

    /// <summary>
    /// The selected title bar style. Takes effect immediately (the window
    /// chrome is reconfigured on save).
    /// </summary>
    public TitleBarStyleViewModel TitleBarStyle
    {
        get => TitleBarStyles.First(style =>
            style.Value == appSettingsProvider.Get().EffectiveTitleBarStyle);
        set
        {
            if (value == null) return;
            var settings = appSettingsProvider.Get();
            // Idempotent: the ComboBox writes its resolved item back on attach.
            if (settings.EffectiveTitleBarStyle == value.Value) return;
            appSettingsProvider.Save(settings with { TitleBarStyle = value.Value });
        }
    }

    /// <summary>True when the traffic-light title bar is active (its size row is shown).</summary>
    public bool ShowTitleBarSize =>
        appSettingsProvider.Get().EffectiveTitleBarStyle == TitleBarStyleEnum.TrafficLights;

    /// <summary>
    /// Traffic light size options (diameter in DIPs).
    /// </summary>
    public TitleBarSizeViewModel[] TitleBarSizes => TitleBarSizeViewModel.Options;

    /// <summary>
    /// The traffic light diameter in DIPs (proportional scaling, 12px = macOS standard).
    /// </summary>
    public TitleBarSizeViewModel TitleBarSize
    {
        get => TitleBarSizes.First(option => Math.Abs(option.Value - appSettingsProvider.Get().EffectiveTitleBarSize) < 0.001);
        set
        {
            if (value == null) return;
            var settings = appSettingsProvider.Get();
            // Idempotent: the ComboBox writes its resolved item back on attach.
            if (Math.Abs(settings.EffectiveTitleBarSize - value.Value) < 0.001) return;
            appSettingsProvider.Save(settings with { TitleBarSize = value.Value });
        }
    }

}
