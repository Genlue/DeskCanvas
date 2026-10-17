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
/// <param name="HttpProxy">HTTP proxy for network requests: <c>null</c>/empty/<c>"auto"</c> = auto-detect active proxy and fall back to direct connection, <c>"direct"</c> = direct connection (bypass all proxies), <c>"system"</c> = use the system proxy, otherwise a custom proxy URL. <para>This in-memory token is persisted in a legacy-safe form — see <see cref="ProxyAutoDetect"/> and <c>ProxySettings.ToPersistedProxy</c>.</para></param>
/// <param name="ProxyAutoDetect">Persistence companion of <see cref="HttpProxy"/>'s auto mode: <c>true</c> on disk means the in-memory token is <c>"auto"</c>. The file itself only ever carries legacy-safe values (<c>null</c>/empty = direct, <c>"system"</c> = system proxy, otherwise a custom URL) so pre-3.7.0 builds — which read this same file and treat any unknown token as a custom proxy URL — keep working; <c>null</c> means a file written by an older build (interpret <see cref="HttpProxy"/> as-is). Not a user-facing setting.</param>
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
/// <param name="LastUpdateCheckStatus">Summary of the most recent update check outcome (connected/up-to-date, new version, or GitHub connection failure).</param>
/// <param name="FocusLink">
/// 失焦自动切换: auto light/dark switching when the desktop loses focus. <c>null</c> (old
/// configurations, feature never enabled) means the feature is off — see
/// <see cref="FocusLinkSettings"/>.
/// </param>
/// <param name="FullscreenBehavior">
/// 最大化或全屏时: what the host does with the widgets while every attached screen is covered
/// by a fullscreen or maximized application. <c>null</c> (old configurations) means
/// <see cref="FullscreenBehavior.Hide"/> — the historical behaviour.
/// </param>
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
    string? HttpProxy = "auto",
    bool? ProxyAutoDetect = null,
    TitleBarStyle? TitleBarStyle = null,
    double? TitleBarSize = null,
    string? ActiveProfile = null,
    bool ShowTrayIcon = true,
    UpdateCheckInterval UpdateInterval = UpdateCheckInterval.Daily,
    DateTime? LastUpdateCheckTime = null,
    Dictionary<string, Theme>? SurfaceThemes = null,
    string? LastUpdateCheckStatus = null,
    FocusLinkSettings? FocusLink = null,
    FullscreenBehavior? FullscreenBehavior = null)
{
    /// <summary>
    /// Gets the saved theme configuration for the specified surface, falling back to default presets.
    /// </summary>
    public Theme GetThemeForSurface(SurfaceStyle surface)
    {
        var key = surface.ToString();
        if (SurfaceThemes != null && SurfaceThemes.TryGetValue(key, out var savedTheme))
        {
            var withSurface = savedTheme.Surface == null ? savedTheme with { Surface = surface } : savedTheme;
            return withSurface.NormalizeForSurface();
        }

        return surface switch
        {
            SurfaceStyle.LiquidGlass => new Theme(
                DarkMode: Theme.DarkMode, AccentColor: null, OpacityLevel: 0.18, Monochrome: true,
                UseNativeFrame: Theme.UseNativeFrame, FontFamily: Theme.FontFamily,
                Surface: SurfaceStyle.LiquidGlass, MonochromeVariant: MonochromeStyle.BlackWhite,
                AutoTheme: Theme.AutoTheme, LiquidGlass: new LiquidGlassSettings()),

            SurfaceStyle.FullColor => new Theme(
                DarkMode: Theme.DarkMode, AccentColor: null, OpacityLevel: 1.0, Monochrome: false,
                UseNativeFrame: Theme.UseNativeFrame, FontFamily: Theme.FontFamily,
                Surface: SurfaceStyle.FullColor,
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
        theme = (theme with { Surface = surface }).NormalizeForSurface();
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
}
