namespace DeskCanvas.Core.Models.Settings;

/// <summary>
/// Application settings, stored in <c>appsettings.json</c>.
/// </summary>
/// <param name="Theme">Theme settings.</param>
/// <param name="Templates">Theme templates shown in the Appearance page.</param>
/// <param name="Layout">Widget sizing and positioning settings.</param>
/// <param name="Dimensions">Widget dimensions (grid unit size, margin, radius).</param>
/// <param name="Region">Region settings (language).</param>
/// <param name="RunOnStartup">Start DeskCanvas with Windows.</param>
/// <param name="IgnoreUpdate">Version to ignore for update checks.</param>
/// <param name="UpdateUrl">Custom update source (releases/latest page or API URL); <c>null</c>/empty disables update checks.</param>
/// <param name="Grid">Custom manual grid settings (used when <see cref="Layout.GridMode"/> is <see cref="GridMode.Manual"/>).</param>
/// <param name="HttpProxy">HTTP proxy for network requests: <c>null</c>/empty = direct connection (bypass system proxy), <c>"system"</c> = use the system proxy, otherwise a proxy URL.</param>
/// <param name="TitleBarStyle">Title bar style of the settings window (macOS traffic lights or native system buttons); <c>null</c> uses <see cref="TitleBarStyle.Native"/> so old configurations keep their look.</param>
/// <param name="TitleBarSize">Traffic light diameter in DIPs; <c>null</c> uses <see cref="DefaultTitleBarSize"/> (14 — a bit larger than the 12px macOS standard for high-DPI screens).</param>
/// <param name="ActiveProfile">Active profile name; <c>null</c> or empty defaults to "默认配置".</param>
/// <param name="ShowTrayIcon">
/// Show the notification-area (tray) icon. <c>true</c> by default; the tray menu can hide it and
/// the widget context menu brings it back, so a hidden icon is never unreachable.
/// </param>
/// <param name="UpdateInterval">How often to check for updates.</param>
/// <param name="LastUpdateCheckTime">UTC timestamp of the last update check; <c>null</c> when never checked.</param>
/// <param name="SurfaceThemes">Per-surface theme overrides keyed by <see cref="SurfaceStyle"/> name; <c>null</c> falls back to the built-in presets.</param>
/// <param name="Sidebar">右侧小组件侧栏 global settings (hotkey, default width, fullscreen/process blocking);
/// <c>null</c> uses <see cref="SidebarSettings"/>'s defaults, so configurations written before sidebars existed keep working.</param>
public record AppSettings(
    Theme Theme,
    Theme[] Templates,
    Layout Layout,
    Dimensions Dimensions,
    Region Region,
    bool RunOnStartup,
    string? IgnoreUpdate,
    string? UpdateUrl = null,
    Grid? Grid = null,
    string? HttpProxy = null,
    TitleBarStyle? TitleBarStyle = null,
    double? TitleBarSize = null,
    string? ActiveProfile = null,
    bool ShowTrayIcon = true,
    UpdateCheckInterval UpdateInterval = UpdateCheckInterval.Daily,
    DateTime? LastUpdateCheckTime = null,
    Dictionary<string, Theme>? SurfaceThemes = null,
    SidebarSettings? Sidebar = null)
{
    /// <summary>
    /// Gets the saved theme configuration for the specified surface, falling back to default presets.
    /// </summary>
    public Theme GetThemeForSurface(SurfaceStyle surface)
    {
        if (surface == SurfaceStyle.SoftGlow) surface = SurfaceStyle.LiquidGlass;
        var key = surface.ToString();
        if (SurfaceThemes != null && SurfaceThemes.TryGetValue(key, out var savedTheme))
        {
            return savedTheme.NormalizeMaterial();
        }
        if (surface == SurfaceStyle.LiquidGlass && SurfaceThemes != null &&
            SurfaceThemes.TryGetValue(nameof(SurfaceStyle.SoftGlow), out var legacy))
            return legacy.NormalizeMaterial();

        return surface switch
        {
            SurfaceStyle.LiquidGlass => new Theme(
                DarkMode: Theme.DarkMode, AccentColor: null, OpacityLevel: 0.18, Monochrome: true,
                UseNativeFrame: Theme.UseNativeFrame, FontFamily: Theme.FontFamily,
                Surface: SurfaceStyle.LiquidGlass, MonochromeVariant: MonochromeStyle.BlackWhite,
                AutoTheme: Theme.AutoTheme, LiquidGlass: new LiquidGlassSettings()),

            // 新液态玻璃 factory recipe: a hair more neutral light than 液态玻璃 (the material's
            // subtle 中性漫射层), everything else lives in LiquidGlassV2Settings' spec-aligned defaults.
            SurfaceStyle.LiquidGlassV2 => new Theme(
                DarkMode: Theme.DarkMode, AccentColor: null, OpacityLevel: 0.20, Monochrome: true,
                UseNativeFrame: Theme.UseNativeFrame, FontFamily: Theme.FontFamily,
                Surface: SurfaceStyle.LiquidGlassV2, MonochromeVariant: MonochromeStyle.BlackWhite,
                AutoTheme: Theme.AutoTheme, LiquidGlassV2: new LiquidGlassV2Settings()),

            SurfaceStyle.Solid => new Theme(
                DarkMode: Theme.DarkMode, AccentColor: null, OpacityLevel: 1.0, Monochrome: true,
                UseNativeFrame: Theme.UseNativeFrame, FontFamily: Theme.FontFamily,
                Surface: SurfaceStyle.Solid, MonochromeVariant: MonochromeStyle.BlackWhite,
                AutoTheme: Theme.AutoTheme),

            SurfaceStyle.Colorful => new Theme(
                DarkMode: Theme.DarkMode, AccentColor: null, OpacityLevel: 1.0, Monochrome: false,
                UseNativeFrame: Theme.UseNativeFrame, FontFamily: Theme.FontFamily,
                Surface: SurfaceStyle.Colorful,
                AutoTheme: Theme.AutoTheme),

            _ => new Theme(
                DarkMode: Theme.DarkMode, AccentColor: null, OpacityLevel: 0.4, Monochrome: true,
                UseNativeFrame: Theme.UseNativeFrame, FontFamily: Theme.FontFamily,
                Surface: SurfaceStyle.Acrylic, MonochromeVariant: MonochromeStyle.BlackWhite,
                AutoTheme: Theme.AutoTheme)
        };
    }

    /// <summary>
    /// Updates the theme configuration for the specified surface, keeping surface-specific
    /// settings isolated in SurfaceThemes.
    /// </summary>
    public AppSettings WithThemeForSurface(SurfaceStyle surface, Theme theme)
    {
        if (surface == SurfaceStyle.SoftGlow) surface = SurfaceStyle.LiquidGlass;
        theme = theme.NormalizeMaterial();
        var dict = SurfaceThemes != null 
            ? new Dictionary<string, Theme>(SurfaceThemes, StringComparer.OrdinalIgnoreCase) 
            : new Dictionary<string, Theme>(StringComparer.OrdinalIgnoreCase);
        
        dict[surface.ToString()] = theme;
        
        var next = this with { SurfaceThemes = dict };
        if (Theme.EffectiveSurface == surface)
        {
            next = next with { Theme = theme };
        }
        return next;
    }

    /// <summary>Default profile name.</summary>
    public const string DefaultProfileName = "默认配置";

    /// <summary>Default GitHub release API endpoint for DeskCanvas.</summary>
    public const string DefaultReleaseApiUrl = "https://api.github.com/repos/Genlue/DeskCanvas/releases/latest";

    /// <summary>
    /// Effective update source URL; falls back to official repository releases when not configured.
    /// </summary>
    public string EffectiveUpdateUrl =>
        string.IsNullOrWhiteSpace(UpdateUrl) ? DefaultReleaseApiUrl : UpdateUrl.Trim();

    /// <summary>
    /// The effective active configuration profile name.
    /// </summary>
    public string EffectiveActiveProfile =>
        string.IsNullOrWhiteSpace(ActiveProfile) ? DefaultProfileName : ActiveProfile;

    /// <summary>The macOS-standard traffic light diameter (DIPs).</summary>
    public const double DefaultTitleBarSize = 12;

    /// <summary>
    /// The effective title bar style; falls back to <see cref="TitleBarStyle.Native"/>
    /// when <see cref="TitleBarStyle"/> is not set (migrates old configurations).
    /// </summary>
    public TitleBarStyle EffectiveTitleBarStyle =>
        TitleBarStyle ?? Settings.TitleBarStyle.Native;

    /// <summary>
    /// The traffic light diameter; falls back to <see cref="DefaultTitleBarSize"/>
    /// when <see cref="TitleBarSize"/> is not set.
    /// </summary>
    public double EffectiveTitleBarSize => TitleBarSize ?? DefaultTitleBarSize;

    /// <summary>Default global hotkey for toggling the sidebar.</summary>
    public const string DefaultSidebarHotKey = "Ctrl+Alt+Space";

    /// <summary>
    /// The effective sidebar settings; a configuration written before sidebars existed
    /// (<see cref="Sidebar"/> = <c>null</c>) yields the defaults.
    /// </summary>
    public SidebarSettings EffectiveSidebar => Sidebar ?? new SidebarSettings();
}
