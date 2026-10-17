namespace DeskCanvas.Core.Models.Settings;

/// <summary>
/// Application theme settings.
/// </summary>
/// <param name="DarkMode">
/// Should the application use the dark mode
/// <para><c>null</c> to use system settings</para>
/// </param>
/// <param name="AccentColor">
/// Accent color in HEX format
/// <para><c>null</c> to use system accent color</para>
/// </param>
/// <param name="OpacityLevel">
/// Widget's background opacity level
/// </param>
/// <param name="Monochrome">
/// Should the application use monochrome theme
/// </param>
/// <param name="UseNativeFrame">
/// Should the application use native window frame
/// </param>
/// <param name="FontFamily">
/// Font family to use
/// </param>
/// <param name="Surface">
/// Widget surface material. Null means "not explicitly set"; the effective
/// material is then derived from <see cref="OpacityLevel"/>. Preferred over the
/// raw <see cref="OpacityLevel"/>&gt;1 heuristic once liquid glass is added.
/// </param>
/// <param name="AutoTheme">
/// Should the light/dark mode be chosen automatically from the desktop wallpaper
/// (dark wallpaper → dark mode, light wallpaper → light mode). When enabled,
/// <see cref="DarkMode"/> is ignored.
/// </param>
/// <param name="SolidBackgroundDark">
/// 纯色 surface background color in dark mode, HEX format. <c>null</c> uses
/// <see cref="DefaultSolidBackgroundDark"/>.
/// </param>
/// <param name="SolidBackgroundLight">
/// 纯色 surface background color in light mode, HEX format. <c>null</c> uses
/// <see cref="DefaultSolidBackgroundLight"/>.
/// </param>
/// <param name="MonochromeVariant">
/// Monochrome color source when <see cref="Monochrome"/> is enabled.
/// <c>null</c> uses <see cref="DefaultMonochromeVariant"/> (强调色, the historic
/// behavior, so old configurations keep their look).
/// </param>
    /// <param name="LiquidGlass">Static glass optics of 液态玻璃; null uses the default parameters.</param>
    /// <param name="LiquidGlassV2">Static optics of 新液态玻璃 (<see cref="SurfaceStyle.LiquidGlassV2"/>); null uses the default parameters.</param>
    /// <param name="TextShadowEnabled">
    /// 全局文字阴影: when on, every <c>TextBlock</c> inside a desktop widget window gets a soft
    /// drop shadow so text stays readable over busy wallpapers. Default off.
    /// </param>
    /// <param name="TextShadowStrength">
    /// 全局文字阴影 strength, 0-100. Scales the shadow's blur radius, depth and opacity; the
    /// whole effect is off while <see cref="TextShadowEnabled"/> is false.
    /// </param>
    public record Theme(
        bool? DarkMode, 
        string? AccentColor, 
        double OpacityLevel, 
        bool Monochrome, 
        bool UseNativeFrame, 
        string FontFamily,
        SurfaceStyle? Surface = null,
        string? SolidBackgroundDark = null,
        string? SolidBackgroundLight = null,
        MonochromeStyle? MonochromeVariant = null,
        bool AutoTheme = false,
        LiquidGlassSettings? LiquidGlass = null,
        LiquidGlassV2Settings? LiquidGlassV2 = null,
        bool TextShadowEnabled = false,
        double TextShadowStrength = 50)
{
    /// <summary>Default 纯色 background in dark mode (dark gray, the historic look).</summary>
    public const string DefaultSolidBackgroundDark = "#2E2E2E";

    /// <summary>Default 纯色 background in light mode (white, the historic look).</summary>
    public const string DefaultSolidBackgroundLight = "#FFFFFF";

    /// <summary>Default monochrome color source (强调色, the historic look).</summary>
    public const MonochromeStyle DefaultMonochromeVariant = MonochromeStyle.Accent;

    /// <summary>Default 柔光玻璃 coating opacity (a touch lighter than 液态玻璃, whose glow adds its own light).</summary>
    public const double DefaultSoftGlowOpacity = 0.14;

    /// <summary>
    /// The effective surface material.
    /// </summary>
    public SurfaceStyle EffectiveSurface => Surface ?? SurfaceStyle.Acrylic;

    /// <summary>
    /// True for surfaces that use a translucent, blur-capable backdrop
    /// (acrylic or liquid glass); false for <see cref="SurfaceStyle.FullColor"/>.
    /// </summary>
    public bool IsGlass => EffectiveSurface is SurfaceStyle.Acrylic or SurfaceStyle.LiquidGlass;

    /// <summary>Static refractive wallpaper material (Kyant0 optical model).</summary>
    public bool IsLiquidGlass => EffectiveSurface == SurfaceStyle.LiquidGlass;

    /// <summary>Legacy alias for IsLiquidGlass.</summary>
    public bool IsLiquidGlassV2 => IsLiquidGlass;

    /// <summary>SoftGlow legacy flag, always false after P11.</summary>
    public bool IsSoftGlow => false;

    /// <summary>
    /// True for the materials drawn by the app from a desktop snapshot (<see cref="SurfaceStyle.LiquidGlass"/>).
    /// </summary>
    public bool UsesRenderedGlass => EffectiveSurface == SurfaceStyle.LiquidGlass;

    /// <summary>Only acrylic uses the native, fixed-radius acrylic backdrop.</summary>
    public bool UsesNativeBlur => EffectiveSurface == SurfaceStyle.Acrylic;

    /// <summary>True for the 全彩 / FullColor theme.</summary>
    public bool IsFullColor => EffectiveSurface is SurfaceStyle.FullColor or SurfaceStyle.Colorful;

    /// <summary>Alias for IsFullColor.</summary>
    public bool IsColorful => IsFullColor;

    /// <summary>Defaults and validated ranges of the liquid glass optics.</summary>
    public LiquidGlassSettings EffectiveLiquidGlass =>
        (LiquidGlass ?? (LiquidGlassSettings?)LiquidGlassV2 ?? new LiquidGlassSettings()).Normalize();

    /// <summary>Alias for backward compatibility.</summary>
    public LiquidGlassV2Settings EffectiveLiquidGlassV2 =>
        new(EffectiveLiquidGlass.Blur, EffectiveLiquidGlass.Refraction, EffectiveLiquidGlass.Highlight,
            EffectiveLiquidGlass.HighlightAngle, EffectiveLiquidGlass.Vibrancy, EffectiveLiquidGlass.Dispersion,
            EffectiveLiquidGlass.WallpaperOffsetX, EffectiveLiquidGlass.WallpaperOffsetY,
            EffectiveLiquidGlass.LiveSampling, EffectiveLiquidGlass.LiveSamplingInterval,
            EffectiveLiquidGlass.BackdropClarity);

    /// <summary>
    /// The sampling-pipeline settings of the active wallpaper-sampled material.
    /// </summary>
    public IRenderedGlassSettings EffectiveGlass => EffectiveLiquidGlass;

    /// <summary>Normalize material.</summary>
    public Theme NormalizeMaterial() => this;

    /// <summary>
    /// Normalizes theme settings for its effective surface material.
    /// FullColor enforces fixed colors (AccentColor=null, Monochrome=false, MonochromeVariant=null, OpacityLevel=1.0).
    /// Other surfaces preserve accent color and monochrome customizations.
    /// </summary>
    public Theme NormalizeForSurface()
    {
        var theme = NormalizeMaterial();
        return theme.IsFullColor
            ? theme with
            {
                AccentColor = null,
                Monochrome = false,
                MonochromeVariant = null,
                OpacityLevel = 1.0
            }
            : theme;
    }

    /// <summary>
    /// The 纯色 background color in dark mode; falls back to
    /// <see cref="DefaultSolidBackgroundDark"/> when <see cref="SolidBackgroundDark"/>
    /// is not set.
    /// </summary>
    public string EffectiveSolidBackgroundDark => SolidBackgroundDark ?? DefaultSolidBackgroundDark;

    /// <summary>
    /// The 纯色 background color in light mode; falls back to
    /// <see cref="DefaultSolidBackgroundLight"/> when <see cref="SolidBackgroundLight"/>
    /// is not set.
    /// </summary>
    public string EffectiveSolidBackgroundLight => SolidBackgroundLight ?? DefaultSolidBackgroundLight;

    /// <summary>
    /// The monochrome color source; falls back to <see cref="DefaultMonochromeVariant"/>
    /// when <see cref="MonochromeVariant"/> is not set.
    /// </summary>
    public MonochromeStyle EffectiveMonochromeVariant => MonochromeVariant ?? DefaultMonochromeVariant;

    /// <summary>Clamped 0-1 strength of the global widget text shadow (0 = faintest, 1 = strongest).</summary>
    public double EffectiveTextShadowStrength => Math.Clamp(TextShadowStrength, 0, 100) / 100.0;
}
