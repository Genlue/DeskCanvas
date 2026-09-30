using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Media;
using ReactiveUI;
using uWidgets.Core.Interfaces;
using uWidgets.Core.Models.Settings;
using uWidgets.Locales;
using uWidgets.Services;
using uWidgets.Views.Controls;
// Alias: the TitleBarStyle property below would otherwise shadow the enum type name.
using TitleBarStyleEnum = uWidgets.Core.Models.Settings.TitleBarStyle;

namespace uWidgets.ViewModels;

public class AppearanceViewModel : ReactiveObject
{
    private readonly IAppSettingsProvider appSettingsProvider;

    public AppearanceViewModel(IAppSettingsProvider appSettingsProvider)
    {
        this.appSettingsProvider = appSettingsProvider;
        // Surface-dependent rows (glass outline) and the monochrome color picker
        // appear/disappear when the settings change.
        appSettingsProvider.DataChanged += (_, oldData, newData) =>
        {
            this.RaisePropertyChanged(nameof(ShowGlassSettings));
            this.RaisePropertyChanged(nameof(ShowGlassOpticsSettings));
            this.RaisePropertyChanged(nameof(ShowLegacyOptics));
            this.RaisePropertyChanged(nameof(ShowV2Settings));
            this.RaisePropertyChanged(nameof(ShowSoftGlowSettings));
            this.RaisePropertyChanged(nameof(GlassOpticsTitle));
            this.RaisePropertyChanged(nameof(OpacityLevel));
            this.RaisePropertyChanged(nameof(GlassBlur));
            this.RaisePropertyChanged(nameof(GlassRefraction));
            this.RaisePropertyChanged(nameof(GlassEdgeWidth));
            this.RaisePropertyChanged(nameof(GlassHighlight));
            this.RaisePropertyChanged(nameof(GlassDispersion));
            this.RaisePropertyChanged(nameof(GlassLightAngle));
            this.RaisePropertyChanged(nameof(GlassEdgeTint));
            this.RaisePropertyChanged(nameof(GlassGlow));
            this.RaisePropertyChanged(nameof(GlassSpectrum));
            this.RaisePropertyChanged(nameof(GlassDyeSpread));
            this.RaisePropertyChanged(nameof(GlassBackdropClarity));
            this.RaisePropertyChanged(nameof(LiveSampling));
            this.RaisePropertyChanged(nameof(LiveSamplingInterval));
            this.RaisePropertyChanged(nameof(V2Blur));
            this.RaisePropertyChanged(nameof(V2Refraction));
            this.RaisePropertyChanged(nameof(V2Highlight));
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
            this.RaisePropertyChanged(nameof(SolidBackgroundDark));
            this.RaisePropertyChanged(nameof(SolidBackgroundLight));
            this.RaisePropertyChanged(nameof(OutlineColor));
            this.RaisePropertyChanged(nameof(OutlineWidth));
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
            this.RaisePropertyChanged(nameof(ShowSolidBackgroundSetting));
            this.RaisePropertyChanged(nameof(AccentColor));
            // Only re-resolve the combo when the manual/system choice itself changed: a plain
            // colour change must not touch the selection, or the closed combo would flicker on
            // every pointer move of the palette.
            if ((oldData?.Theme.AccentColor is null) != (newData.Theme.AccentColor is null))
                this.RaisePropertyChanged(nameof(AccentMode));
        };
        // Fixed material presets; other appearance preferences are preserved.
        Themes = SurfaceTemplates.Select(theme => new ThemeButton(appSettingsProvider, theme)).ToArray();
    }

    /// <summary>
    /// The surface presets, in the order they are shown: frosted glass, solid, liquid glass,
    /// new liquid glass and colorful (macOS).
    /// <para>
    /// 柔光玻璃 is deliberately <b>not</b> a preset any more: it was merged into 液态玻璃, which is
    /// one surface and one pipeline, and the soft look is reached through the 柔光晕 / 光谱弥散
    /// knobs. Its recipe survives as <see cref="LiquidGlassSettings.SoftGlowPreset"/> for stored
    /// configurations, and the liquid-glass preset below keeps the factory optics so the
    /// default material is unchanged by the merge.
    /// </para>
    /// </summary>
    public static readonly Theme[] SurfaceTemplates =
    [
        new(DarkMode: null, AccentColor: null, OpacityLevel: 0.4, Monochrome: true, UseNativeFrame: false, FontFamily: "Inter", Surface: SurfaceStyle.Acrylic),
        new(DarkMode: null, AccentColor: null, OpacityLevel: 1.0, Monochrome: true, UseNativeFrame: false, FontFamily: "Inter", Surface: SurfaceStyle.Solid),
        new(DarkMode: null, AccentColor: null, OpacityLevel: 0.18, Monochrome: true, UseNativeFrame: false, FontFamily: "Inter", Surface: SurfaceStyle.LiquidGlass,
            LiquidGlass: new LiquidGlassSettings()),
        new(DarkMode: null, AccentColor: null, OpacityLevel: 0.20, Monochrome: true, UseNativeFrame: false, FontFamily: "Inter", Surface: SurfaceStyle.LiquidGlassV2,
            LiquidGlassV2: new LiquidGlassV2Settings()),
        new(DarkMode: null, AccentColor: null, OpacityLevel: 1.0, Monochrome: false, UseNativeFrame: false, FontFamily: "Inter", Surface: SurfaceStyle.Colorful)
    ];

    public ThemeButton[] Themes { get; }

    /// <summary>
    /// True when the theme is a glass surface — the blur-strength and outline
    /// settings are hidden (and ignored) for the solid preset.
    /// </summary>
    public bool ShowGlassSettings => appSettingsProvider.Get().Theme.IsGlass;

    /// <summary>
    /// True for the wallpaper-sampled materials: 液态玻璃 and 新液态玻璃 expose an optics section;
    /// it is hidden for 毛玻璃 and 纯色.
    /// </summary>
    public bool ShowGlassOpticsSettings => appSettingsProvider.Get().Theme.UsesRenderedGlass;

    /// <summary>True while the older lens material (液态玻璃, incl. its 柔光 recipe) is active:
    /// the optics rows below bind to <see cref="LiquidGlassSettings"/>.</summary>
    public bool ShowLegacyOptics => appSettingsProvider.Get().Theme.UsesRenderedGlass && !appSettingsProvider.Get().Theme.IsLiquidGlassV2;

    /// <summary>True while 新液态玻璃 is active: its own spec-aligned rows are shown instead.</summary>
    public bool ShowV2Settings => appSettingsProvider.Get().Theme.IsLiquidGlassV2;

    /// <summary>
    /// The soft-recipe rows (halo / spectrum / dye spread). They belong to the merged theme now,
    /// so they are always offered alongside the other optics.
    /// </summary>
    public bool ShowSoftGlowSettings => ShowLegacyOptics;

    /// <summary>
    /// Section title of the optics block, named after the active material: 液态玻璃 (the
    /// LiquidGlassV2 recipe) or 柔光玻璃 (the older lens recipe).
    /// </summary>
    public string GlassOpticsTitle => appSettingsProvider.Get().Theme.IsLiquidGlassV2
        ? Locale.Settings_Appearance_Surface_LiquidGlassV2
        : Locale.Settings_Appearance_Surface_LiquidGlass;

    /// <summary>
    /// Whether the optics rows (实时采样 → 染色扩散) are unfolded. They are collapsed by
    /// default — expert knobs behind a working out-of-the-box theme — and the state is
    /// session-local on purpose: it is a reading aid, not a preference.
    /// </summary>
    public bool OpticsExpanded
    {
        get => opticsExpanded;
        set { if (opticsExpanded == value) return; opticsExpanded = value; this.RaisePropertyChanged(); }
    }
    private bool opticsExpanded;

    public double GlassBlur
    {
        get => appSettingsProvider.Get().Theme.EffectiveLiquidGlass.Blur;
        set => UpdateGlass(glass => glass with { Blur = value });
    }
    public double GlassRefraction
    {
        get => appSettingsProvider.Get().Theme.EffectiveLiquidGlass.Refraction;
        set => UpdateGlass(glass => glass with { Refraction = value });
    }
    public double GlassEdgeWidth
    {
        get => appSettingsProvider.Get().Theme.EffectiveLiquidGlass.EdgeWidth;
        set => UpdateGlass(glass => glass with { EdgeWidth = value });
    }
    public double GlassHighlight
    {
        get => appSettingsProvider.Get().Theme.EffectiveLiquidGlass.Highlight;
        set => UpdateGlass(glass => glass with { Highlight = value });
    }
    public double GlassDispersion
    {
        get => appSettingsProvider.Get().Theme.EffectiveLiquidGlass.Dispersion;
        set => UpdateGlass(glass => glass with { Dispersion = value });
    }
    public double GlassLightAngle
    {
        get => appSettingsProvider.Get().Theme.EffectiveLiquidGlass.LightAngle;
        set => UpdateGlass(glass => glass with { LightAngle = value });
    }

    /// <summary>边缘染色 strength (0-100%): the soft colored rim at the glass border.</summary>
    public double GlassEdgeTint
    {
        get => appSettingsProvider.Get().Theme.EffectiveLiquidGlass.EdgeTint;
        set => UpdateGlass(glass => glass with { EdgeTint = value });
    }

    /// <summary>柔光晕 strength (0-100%) of 柔光玻璃: the diffused luminous bloom around the rim.</summary>
    public double GlassGlow
    {
        get => appSettingsProvider.Get().Theme.EffectiveLiquidGlass.Glow;
        set => UpdateGlass(glass => glass with { Glow = value });
    }

    /// <summary>光谱弥散 (0-100%) of 柔光玻璃: how far the dispersion blends towards a full spectrum.</summary>
    public double GlassSpectrum
    {
        get => appSettingsProvider.Get().Theme.EffectiveLiquidGlass.Spectrum;
        set => UpdateGlass(glass => glass with { Spectrum = value });
    }

    /// <summary>染色扩散 (0-100%) of 柔光玻璃: how far the edge dye reaches into the card (0 = rim only).</summary>
    public double GlassDyeSpread
    {
        get => appSettingsProvider.Get().Theme.EffectiveLiquidGlass.DyeSpread;
        set => UpdateGlass(glass => glass with { DyeSpread = value });
    }

    /// <summary>
    /// 背景清晰度 (25-100%): the resolution the shared backdrop is built at, relative to the
    /// desktop's long side. This is the knob that actually moves the live-sampling rate — the
    /// backdrop's downscale + blur is the half of a sampling round that is not the desktop grab,
    /// and it costs roughly the square of this value. 100% = native pixels, no compression.
    /// </summary>
    public double GlassBackdropClarity
    {
        get => appSettingsProvider.Get().Theme.EffectiveLiquidGlass.BackdropClarity;
        set => UpdateGlass(glass => glass with { BackdropClarity = value });
    }

    /// <summary>
    /// Sample the desktop continuously so animated wallpapers stay live behind the glass.
    /// Switching this off freezes the last captured frame — the static material — which is what a
    /// still wallpaper or a battery-saving session wants.
    /// </summary>
    public bool LiveSampling
    {
        get => appSettingsProvider.Get().Theme.EffectiveLiquidGlass.LiveSampling;
        set => UpdateGlass(glass => glass with { LiveSampling = value });
    }

    /// <summary>Sampling interval in milliseconds; shorter is smoother and more expensive.</summary>
    public int LiveSamplingInterval
    {
        get => appSettingsProvider.Get().Theme.EffectiveLiquidGlass.LiveSamplingInterval;
        set => UpdateGlass(glass => glass with { LiveSamplingInterval = value });
    }

    // ---------- 新液态玻璃 optics (LiquidGlassV2Settings) ----------

    /// <summary>模糊 (0-100): the light backdrop blur.</summary>
    public double V2Blur
    {
        get => appSettingsProvider.Get().Theme.EffectiveLiquidGlassV2.Blur;
        set => UpdateV2(glass => glass with { Blur = value });
    }

    /// <summary>折射 (0-100): the rim lens strength — quarter-circle displacement peaking at the outline.</summary>
    public double V2Refraction
    {
        get => appSettingsProvider.Get().Theme.EffectiveLiquidGlassV2.Refraction;
        set => UpdateV2(glass => glass with { Refraction = value });
    }

    /// <summary>边缘高光 (0-100): the hairline rim stroke's alpha.</summary>
    public double V2Highlight
    {
        get => appSettingsProvider.Get().Theme.EffectiveLiquidGlassV2.Highlight;
        set => UpdateV2(glass => glass with { Highlight = value });
    }

    /// <summary>活力 (0-100): the iOS vibrancy colour boost (33 ≈ the library's saturation ×1.5).</summary>
    public double V2Vibrancy
    {
        get => appSettingsProvider.Get().Theme.EffectiveLiquidGlassV2.Vibrancy;
        set => UpdateV2(glass => glass with { Vibrancy = value });
    }

    /// <summary>色散 (0-100): the seven-tap spectral split along the rim displacement.</summary>
    public double V2Dispersion
    {
        get => appSettingsProvider.Get().Theme.EffectiveLiquidGlassV2.Dispersion;
        set => UpdateV2(glass => glass with { Dispersion = value });
    }

    /// <summary>Sample the desktop continuously so animated wallpapers stay live behind the glass.</summary>
    public bool V2LiveSampling
    {
        get => appSettingsProvider.Get().Theme.EffectiveLiquidGlassV2.LiveSampling;
        set => UpdateV2(glass => glass with { LiveSampling = value });
    }

    /// <summary>Sampling interval in milliseconds; shorter is smoother and more expensive.</summary>
    public int V2LiveSamplingInterval
    {
        get => appSettingsProvider.Get().Theme.EffectiveLiquidGlassV2.LiveSamplingInterval;
        set => UpdateV2(glass => glass with { LiveSamplingInterval = value });
    }

    /// <summary>
    /// 背景清晰度 (25-100%): the resolution the shared backdrop is built at. The material leans
    /// on the lens rather than a heavy pre-blur, so its factory default is richer than 液态玻璃's.
    /// </summary>
    public double V2BackdropClarity
    {
        get => appSettingsProvider.Get().Theme.EffectiveLiquidGlassV2.BackdropClarity;
        set => UpdateV2(glass => glass with { BackdropClarity = value });
    }

    private void UpdateV2(Func<LiquidGlassV2Settings, LiquidGlassV2Settings> update)
    {
        var settings = appSettingsProvider.Get();
        var glass = update(settings.Theme.EffectiveLiquidGlassV2).Normalize();
        if (glass == settings.Theme.EffectiveLiquidGlassV2) return;
        SaveTheme(settings.Theme with { LiquidGlassV2 = glass });
    }

    private void UpdateGlass(Func<LiquidGlassSettings, LiquidGlassSettings> update)
    {
        var settings = appSettingsProvider.Get();
        var glass = update(settings.Theme.EffectiveLiquidGlass).Normalize();
        if (glass == settings.Theme.EffectiveLiquidGlass) return;
        SaveTheme(settings.Theme with { LiquidGlass = glass });
    }

    private void SaveTheme(Theme newTheme)
    {
        var settings = appSettingsProvider.Get();
        var next = settings.WithThemeForSurface(newTheme.EffectiveSurface, newTheme);
        appSettingsProvider.Save(next with { Theme = newTheme });
    }

    /// <summary>
    /// Reset the active rendered material's optics to its factory recipe: 新液态玻璃 goes back
    /// to <see cref="LiquidGlassV2Settings"/>' spec-aligned defaults, the merged 液态玻璃 theme
    /// to <see cref="LiquidGlassSettings"/>. 柔光玻璃 has no separate defaults any more: its look
    /// is a set of knob positions on the lens theme, and 柔光晕 / 光谱弥散 are what switch it
    /// back on.
    /// </summary>
    public void ResetLiquidGlass()
    {
        if (appSettingsProvider.Get().Theme.IsLiquidGlassV2) UpdateV2(_ => new LiquidGlassV2Settings());
        else UpdateGlass(_ => new LiquidGlassSettings());
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
    /// Accent color setting is hidden in Colorful mode, which uses authentic fixed macOS palettes.
    /// </summary>
    public bool ShowAccentSetting => !appSettingsProvider.Get().Theme.IsColorful;

    /// <summary>
    /// Opacity slider is hidden in Colorful mode, which uses 100% opaque cards.
    /// </summary>
    public bool ShowOpacitySetting => !appSettingsProvider.Get().Theme.IsColorful;

    /// <summary>
    /// Monochrome toggle is hidden in Colorful mode, which strictly uses Apple HIG vibrant semantic palettes.
    /// </summary>
    public bool ShowMonochromeSetting => !appSettingsProvider.Get().Theme.IsColorful;

    /// <summary>
    /// Custom background colors are hidden in Colorful mode, which strictly uses Apple HIG light/dark backgrounds.
    /// </summary>
    public bool ShowSolidBackgroundSetting => !appSettingsProvider.Get().Theme.IsColorful;

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
    /// The manual accent colour. Stored via <see cref="ToHex"/> ("#AARRGGBB") so it
    /// round-trips through the settings JSON in a stable way and can be compared.
    /// </summary>
    public Color AccentColor
    {
        get => Color.TryParse(appSettingsProvider.Get().Theme.AccentColor, out var color) ? color : Colors.DodgerBlue;
        set
        {
            var settings = appSettingsProvider.Get();
            var hex = ToHex(value);
            if (settings.Theme.AccentColor == hex) return;
            SaveTheme(settings.Theme with { AccentColor = hex });
        }
    }

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
            var newSettings = settings with { Theme = newTheme };
            appSettingsProvider.Save(newSettings);
        }
    }
    
    public double OpacityLevel
    {
        get => appSettingsProvider.Get().Theme.OpacityLevel;
        set
        {
            var settings = appSettingsProvider.Get();
            // Controls push their rendered value back on attach; only a real change is saved
            // so that merely opening the settings window never touches the settings file.
            if (settings.Theme.OpacityLevel.Equals(value)) return;
            SaveTheme(settings.Theme with { OpacityLevel = value });
        }
    }

    /// <summary>
    /// Highlight-ring color of the outlined-glass theme (RGBA HEX, alpha preserved).
    /// </summary>
    public Color OutlineColor
    {
        get => Color.TryParse(
            appSettingsProvider.Get().Theme.EffectiveOutlineColor, out var color) ? color : Colors.White;
        set
        {
            var settings = appSettingsProvider.Get();
            // Compare against the *effective* value: the picker renders the default while
            // nothing is stored, and writing that back would materialize it.
            var hex = ToHex(value);
            if (settings.Theme.EffectiveOutlineColor == hex) return;
            SaveTheme(settings.Theme with { OutlineColor = hex });
        }
    }

    /// <summary>
    /// Highlight-ring thickness of the outlined-glass theme in DIPs; 0 hides the ring.
    /// </summary>
    public double OutlineWidth
    {
        get => appSettingsProvider.Get().Theme.OutlineWidth;
        set
        {
            var settings = appSettingsProvider.Get();
            var width = Math.Clamp(value, 0, 6);
            if (Math.Abs(settings.Theme.OutlineWidth - width) < 0.001) return;
            SaveTheme(settings.Theme with { OutlineWidth = width });
        }
    }
    
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
    /// 全局文字阴影 (default off): when on, every TextBlock inside a desktop widget window
    /// gets a soft drop shadow for readability over busy wallpapers.
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
        get => appSettingsProvider.Get().Theme.TextShadowStrength;
        set
        {
            var settings = appSettingsProvider.Get();
            var strength = Math.Clamp(value, 0, 100);
            if (Math.Abs(settings.Theme.TextShadowStrength - strength) < 0.001) return;
            SaveTheme(settings.Theme with { TextShadowStrength = strength });
        }
    }

    /// <summary>
    /// The monochrome color source options (黑白 / 强调色).
    /// </summary>
    public MonochromeVariantViewModel[] MonochromeVariants { get; } =
    [
        new(Locale.Settings_Appearance_Monochrome_Variant_BlackWhite, MonochromeStyle.BlackWhite),
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
    public Color SolidBackgroundDark
    {
        get => ParseColor(appSettingsProvider.Get().Theme.EffectiveSolidBackgroundDark, Theme.DefaultSolidBackgroundDark);
        set => SaveSolidBackground(SolidBackgroundDark: ToHex(value));
    }

    /// <summary>
    /// Widget card background color in light mode (HEX, alpha is the global opacity slider's job).
    /// Applies to both 毛玻璃 and 纯色 surfaces.
    /// </summary>
    public Color SolidBackgroundLight
    {
        get => ParseColor(appSettingsProvider.Get().Theme.EffectiveSolidBackgroundLight, Theme.DefaultSolidBackgroundLight);
        set => SaveSolidBackground(SolidBackgroundLight: ToHex(value));
    }

    /// <summary>
    /// Apply the currently selected dark-mode color to the light-mode color.
    /// </summary>
    public void ApplySolidToLight()
    {
        SaveSolidBackground(SolidBackgroundLight: ToHex(SolidBackgroundDark));
        this.RaisePropertyChanged(nameof(SolidBackgroundLight));
    }

    /// <summary>
    /// Apply the currently selected light-mode color to the dark-mode color.
    /// </summary>
    public void ApplySolidToDark()
    {
        SaveSolidBackground(SolidBackgroundDark: ToHex(SolidBackgroundLight));
        this.RaisePropertyChanged(nameof(SolidBackgroundDark));
    }

    /// <summary>
    /// Store colors as a plain "#AARRGGBB" string — <see cref="Color.ToString"/>
    /// may return a named color (e.g. "White") that doesn't round-trip through
    /// the settings JSON in a stable way.
    /// </summary>
    private static string ToHex(Color color) =>
        $"#{color.A:X2}{color.R:X2}{color.G:X2}{color.B:X2}";

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

    private static Color ParseColor(string hex, string fallbackHex) =>
        Color.TryParse(hex, out var color) ? color : Color.Parse(fallbackHex);
    
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
        get => appSettingsProvider.Get().Theme.FontFamily;
        set
        {
            var settings = appSettingsProvider.Get();
            // Idempotent: the font ComboBox writes its resolved item back on attach.
            if (settings.Theme.FontFamily == value) return;
            var theme = settings.Theme with { FontFamily = value };
            var newSettings = settings with { Theme = theme };
            appSettingsProvider.Save(newSettings);
        }
    }

    public bool UseNativeFrame
    {
        get => appSettingsProvider.Get().Theme.UseNativeFrame;
        set
        {
            var settings = appSettingsProvider.Get();
            if (settings.Theme.UseNativeFrame == value) return;
            var theme = settings.Theme with { UseNativeFrame = value };
            var newSettings = settings with { Theme = theme };
            appSettingsProvider.Save(newSettings);
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
