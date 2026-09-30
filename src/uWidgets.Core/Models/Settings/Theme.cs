namespace uWidgets.Core.Models.Settings;

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
/// <param name="OutlineColor">
/// Highlight ring color of the glass outline, in HEX format (RGBA, alpha is kept).
/// <c>null</c> uses <see cref="DefaultOutlineColor"/>.
/// </param>
/// <param name="OutlineWidth">
/// Highlight ring thickness in DIPs for the frosted glass; <c>0</c> (default)
/// hides the ring. The outline is part of the 毛玻璃 theme — any glass surface
/// draws it when this is greater than 0.
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
/// <param name="AutoTheme">
/// Should the light/dark mode be chosen automatically from the desktop wallpaper
/// (dark wallpaper → dark mode, light wallpaper → light mode). When enabled,
/// <see cref="DarkMode"/> is ignored.
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
        string? OutlineColor = null,
        double OutlineWidth = 0,
        string? SolidBackgroundDark = null,
        string? SolidBackgroundLight = null,
        MonochromeStyle? MonochromeVariant = null,
        bool AutoTheme = false,
        LiquidGlassSettings? LiquidGlass = null,
        LiquidGlassV2Settings? LiquidGlassV2 = null,
        bool TextShadowEnabled = false,
        double TextShadowStrength = 50)
{
    /// <summary>Default highlight-ring color when <see cref="OutlineColor"/> is not set
    /// (soft gray-white, less stark than pure white).</summary>
    public const string DefaultOutlineColor = "#B3FFFFFF";

    /// <summary>Default highlight-ring thickness (DIPs): 0 = outline hidden.</summary>
    public const double DefaultOutlineWidth = 0;

    /// <summary>Default 纯色 background in dark mode (dark gray, the historic look).</summary>
    public const string DefaultSolidBackgroundDark = "#2E2E2E";

    /// <summary>Default 纯色 background in light mode (white, the historic look).</summary>
    public const string DefaultSolidBackgroundLight = "#FFFFFF";

    /// <summary>Default monochrome color source (强调色, the historic look).</summary>
    public const MonochromeStyle DefaultMonochromeVariant = MonochromeStyle.Accent;

    /// <summary>Default 柔光玻璃 coating opacity (a touch lighter than 液态玻璃, whose glow adds its own light).</summary>
    public const double DefaultSoftGlowOpacity = 0.14;

    /// <summary>
    /// The effective surface material. Falls back to deriving it from
    /// <see cref="OpacityLevel"/> when <see cref="Surface"/> is not set
    /// (migrates old configurations that predate the <see cref="SurfaceStyle"/> field).
    /// </summary>
    public SurfaceStyle EffectiveSurface =>
        Surface == SurfaceStyle.SoftGlow ? SurfaceStyle.LiquidGlass :
        Surface ?? (OpacityLevel < 1 ? SurfaceStyle.Acrylic : SurfaceStyle.Solid);

    /// <summary>
    /// True for surfaces that use a translucent, blur-capable backdrop
    /// (frosted glass, its outlined variant and the two wallpaper-sampled
    /// liquid glass materials); false for <see cref="SurfaceStyle.Solid"/> and
    /// <see cref="SurfaceStyle.Colorful"/>.
    /// </summary>
    public bool IsGlass => EffectiveSurface is SurfaceStyle.Acrylic or SurfaceStyle.LiquidGlass or SurfaceStyle.LiquidGlassV2;

    /// <summary>Static refractive wallpaper material, with independently adjustable blur.</summary>
    public bool IsLiquidGlass => EffectiveSurface == SurfaceStyle.LiquidGlass;

    /// <summary>
    /// True for 新液态玻璃 (<see cref="SurfaceStyle.LiquidGlassV2"/>): the second, independent
    /// optical recipe drawn from the same wallpaper sampling pipeline.
    /// </summary>
    public bool IsLiquidGlassV2 => EffectiveSurface == SurfaceStyle.LiquidGlassV2;

    /// <summary>
    /// True while the <b>soft recipe</b> of the liquid-glass pipeline is active: the legacy
    /// <see cref="SurfaceStyle.SoftGlow"/> material, or its two signature ingredients turned up on
    /// the merged 液态玻璃 theme.
    /// <para>
    /// 柔光玻璃 was merged into 液态玻璃: both are the same surface and the same pipeline, and the
    /// soft look is now reached through the 柔光晕 (<see cref="LiquidGlassSettings.Glow"/>) and
    /// 光谱弥散 (<see cref="LiquidGlassSettings.Spectrum"/>) knobs instead of a separate theme.
    /// At the factory defaults both are 0, so the merged theme is the crisp, macOS-faithful
    /// material and stored configurations keep the optics they were saved with.
    /// </para>
    /// </summary>
    public bool IsSoftGlow => Surface == SurfaceStyle.SoftGlow || EffectiveLiquidGlass.IsSoftRecipe;

    /// <summary>
    /// True for the materials drawn by the app from a desktop snapshot
    /// (<see cref="SurfaceStyle.LiquidGlass"/>, which absorbed 柔光玻璃, and
    /// <see cref="SurfaceStyle.LiquidGlassV2"/>): widgets use this to know
    /// that the card itself must stay transparent so the rendered material shows through.
    /// </summary>
    public bool UsesRenderedGlass => EffectiveSurface is SurfaceStyle.LiquidGlass or SurfaceStyle.LiquidGlassV2;

    /// <summary>Only frosted glass (毛玻璃) uses the native, fixed-radius acrylic backdrop.</summary>
    public bool UsesNativeBlur => EffectiveSurface == SurfaceStyle.Acrylic;

    /// <summary>True for the 多彩 / macOS flat opaque widget theme.</summary>
    public bool IsColorful => EffectiveSurface == SurfaceStyle.Colorful;

    /// <summary>Defaults and validated ranges for old or manually edited configurations.</summary>
    public LiquidGlassSettings EffectiveLiquidGlass =>
        (LiquidGlass ?? (Surface == SurfaceStyle.SoftGlow ? LiquidGlassSettings.SoftGlowPreset : new())).Normalize();

    /// <summary>Defaults and validated ranges of the 新液态玻璃 optics.</summary>
    public LiquidGlassV2Settings EffectiveLiquidGlassV2 =>
        (LiquidGlassV2 ?? new LiquidGlassV2Settings()).Normalize();

    /// <summary>
    /// The sampling-pipeline settings of the active wallpaper-sampled material — the backdrop
    /// cache, the wallpaper alignment offsets and the live sampler all read through this so they
    /// never branch per surface. Only meaningful when <see cref="UsesRenderedGlass"/> is true.
    /// </summary>
    public IRenderedGlassSettings EffectiveGlass => IsLiquidGlassV2 ? EffectiveLiquidGlassV2 : EffectiveLiquidGlass;

    /// <summary>Merge the legacy soft material while preserving its optical settings.</summary>
    public Theme NormalizeMaterial() => Surface == SurfaceStyle.SoftGlow
        ? this with { Surface = SurfaceStyle.LiquidGlass, LiquidGlass = EffectiveLiquidGlass }
        : this;

    /// <summary>
    /// The highlighted glass ring color; falls back to <see cref="DefaultOutlineColor"/>
    /// when <see cref="OutlineColor"/> is not set.
    /// </summary>
    public string EffectiveOutlineColor => OutlineColor ?? DefaultOutlineColor;

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
