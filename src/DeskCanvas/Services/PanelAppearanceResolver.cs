using System;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;
using DeskCanvas.Core.Interfaces;
using DeskCanvas.Core.Models.Settings;
using DeskCanvas.Views.Controls;

namespace DeskCanvas.Services;

/// <summary>
/// The resolved shell appearance of a secondary panel: everything the nine popups computed
/// inside their own <c>ApplyTheme</c> before W7, computed once here instead.
/// <para>
/// The record is immutable so the host can compare-and-swap it and the shell can TemplateBind
/// straight to its members without a per-property notification contract.
/// </para>
/// </summary>
/// <param name="Surface">The effective material the appearance was resolved for.</param>
/// <param name="IsDark">Whether the panel paints its dark variant (follow-system resolved).</param>
/// <param name="TransparencyLevels">
/// Native transparency hint for the panel window: the historic single level first, then the
/// platform's last-resort <see cref="WindowTransparencyLevel.None"/> so an unsupported level can
/// never leave the window unopened. <see cref="PrimaryTransparencyLevel"/> is the historic value.
/// </param>
/// <param name="CardBackground">Card fill.</param>
/// <param name="CardBorderBrush">Card hairline.</param>
/// <param name="UseRenderedGlass">Whether <c>PART_GlassSurface</c> paints the card.</param>
/// <param name="GlassMaterial">The optics handed to the liquid-glass surface (null when off).</param>
/// <param name="TitleBrush">Header title foreground.</param>
/// <param name="SubtitleBrush">Subtitle / body secondary foreground.</param>
/// <param name="IconBrush">Icon-only chrome foreground (the shared close button).</param>
/// <param name="CornerRadius">The radius the card and the glass are drawn with.</param>
public sealed record PanelAppearance(
    SurfaceStyle Surface,
    bool IsDark,
    WindowTransparencyLevel[] TransparencyLevels,
    IBrush CardBackground,
    IBrush CardBorderBrush,
    bool UseRenderedGlass,
    Theme? GlassMaterial,
    IBrush TitleBrush,
    IBrush SubtitleBrush,
    IBrush IconBrush,
    double CornerRadius)
{
    /// <summary>The level every popup set on its own before the shared resolver existed.</summary>
    public WindowTransparencyLevel PrimaryTransparencyLevel =>
        TransparencyLevels.Length > 0 ? TransparencyLevels[0] : WindowTransparencyLevel.None;
}

/// <summary>
/// The single place that turns the current theme into a secondary panel's shell appearance.
/// <para>
/// The nine popups carried byte-identical shell branches (only Weather's 多彩 card colour and
/// BigFolder's missing 多彩 branch differed); this class pins that shared baseline — background
/// alphas, border alphas, glass settings, transparency hints and the light/dark text brushes —
/// and nothing else. Plugin-owned colours (Weather's gradient, list category colours, custom
/// title colours) stay in the plugin and are applied on top through
/// <c>OnPanelThemeApplied</c> / the panel's own semantic resources; the shared resolver never
/// writes them back.
/// </para>
/// The resolution itself is a pure static function (<see cref="Resolve"/>) so it can be asserted
/// without a window, a renderer or a settings file.
/// </summary>
public static class PanelAppearanceResolver
{
    /// <summary>The radius every popup declared locally in its AXAML (Weather's 20 excepted).</summary>
    public const double DefaultCornerRadius = 18.0;

    /// <summary>
    /// What a popup used when <c>new AppSettingsProvider().Get()</c> threw — kept verbatim so a
    /// panel opened before the settings file is readable looks exactly as it did.
    /// </summary>
    public static Theme FallbackTheme { get; } = new(
        DarkMode: true,
        AccentColor: null,
        OpacityLevel: 0.8,
        Monochrome: false,
        UseNativeFrame: false,
        FontFamily: "Inter");

    /// <summary>
    /// The shared settings provider, when the host has wired one up. Optional by design: a plugin
    /// panel keeps its parameterless constructor and its static <c>ShowPopup</c> signature, and
    /// must never <c>new AppSettingsProvider()</c> of its own. Null (headless checks, very early
    /// start-up) simply means "use <see cref="FallbackTheme"/>".
    /// </summary>
    public static IAppSettingsProvider? TryResolveSettingsProvider()
    {
        try
        {
            return App.Services?.GetService(typeof(IAppSettingsProvider)) as IAppSettingsProvider;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Read the theme through the shared provider; falls back to <see cref="FallbackTheme"/>
    /// when there is no provider (headless checks, panels opened before the host finished
    /// wiring) or the read throws. Never throws.
    /// </summary>
    public static Theme ReadTheme(IAppSettingsProvider? settings)
    {
        try
        {
            return settings?.Get().Theme ?? FallbackTheme;
        }
        catch
        {
            return FallbackTheme;
        }
    }

    /// <summary>
    /// The shared light/dark rule: "follow system" is <c>DarkMode == null</c> and must resolve to
    /// the LIVE variant — treating null as dark painted every panel dark on a light system.
    /// </summary>
    public static bool IsDark(Theme theme, ThemeVariant actualVariant)
        => theme.DarkMode ?? actualVariant == ThemeVariant.Dark;

    /// <summary>Pure resolution of the shell appearance. No Avalonia object is touched.</summary>
    public static PanelAppearance Resolve(Theme theme, bool isDark, double cornerRadius)
    {
        var surface = theme.EffectiveSurface;

        if (theme.UsesRenderedGlass)
            return Glass(theme, isDark, cornerRadius, surface);
        if (theme.IsColorful)
            return Colorful(theme, isDark, cornerRadius);
        if (surface == SurfaceStyle.Solid)
            return Solid(theme, isDark, cornerRadius);

        return Acrylic(theme, isDark, cornerRadius);
    }

    // ---- the four material branches, in the order the popups tested them -------------------

    /// <summary>
    /// 液态玻璃 / 新液态玻璃: the card itself stays transparent and the live surface paints it.
    /// The surface is fed the zoom's interpolated scale by SecondaryPanelWindow, so the animation
    /// shows real glass per tick.
    /// </summary>
    private static PanelAppearance Glass(Theme theme, bool isDark, double cornerRadius, SurfaceStyle surface)
        => new(
            Surface: surface,
            IsDark: isDark,
            TransparencyLevels: [WindowTransparencyLevel.Transparent, WindowTransparencyLevel.None],
            CardBackground: Brushes.Transparent,
            CardBorderBrush: new SolidColorBrush(Color.FromArgb(90, 255, 255, 255)),
            UseRenderedGlass: true,
            GlassMaterial: theme,
            TitleBrush: TextBrush(isDark),
            SubtitleBrush: SubTextBrush(isDark),
            IconBrush: SubTextBrush(isDark),
            CornerRadius: cornerRadius);

    /// <summary>多彩: the flat opaque macOS-style card.</summary>
    private static PanelAppearance Colorful(Theme theme, bool isDark, double cornerRadius)
        => new(
            Surface: SurfaceStyle.Colorful,
            IsDark: isDark,
            TransparencyLevels: [WindowTransparencyLevel.Transparent, WindowTransparencyLevel.None],
            CardBackground: new SolidColorBrush(isDark ? Color.Parse("#1C1C1E") : Color.Parse("#FFFFFF")),
            CardBorderBrush: new SolidColorBrush(isDark ? Color.FromArgb(60, 255, 255, 255) : Color.FromArgb(40, 0, 0, 0)),
            UseRenderedGlass: false,
            GlassMaterial: null,
            TitleBrush: TextBrush(isDark),
            SubtitleBrush: SubTextBrush(isDark),
            IconBrush: SubTextBrush(isDark),
            CornerRadius: cornerRadius);

    /// <summary>纯色: the user's solid colour, coated by the opacity slider (alpha 40–255).</summary>
    private static PanelAppearance Solid(Theme theme, bool isDark, double cornerRadius)
    {
        var hex = isDark ? theme.EffectiveSolidBackgroundDark : theme.EffectiveSolidBackgroundLight;
        var baseColor = Color.TryParse(hex, out var parsed)
            ? parsed
            : (isDark ? Color.FromRgb(46, 46, 46) : Colors.White);
        byte alpha = (byte)Math.Clamp(Math.Round(theme.OpacityLevel * 255), 40, 255);

        return new PanelAppearance(
            Surface: SurfaceStyle.Solid,
            IsDark: isDark,
            TransparencyLevels: [WindowTransparencyLevel.Transparent, WindowTransparencyLevel.None],
            CardBackground: new SolidColorBrush(Color.FromArgb(alpha, baseColor.R, baseColor.G, baseColor.B)),
            CardBorderBrush: new SolidColorBrush(isDark ? Color.FromArgb(40, 255, 255, 255) : Color.FromArgb(30, 0, 0, 0)),
            UseRenderedGlass: false,
            GlassMaterial: null,
            TitleBrush: TextBrush(isDark),
            SubtitleBrush: SubTextBrush(isDark),
            IconBrush: SubTextBrush(isDark),
            CornerRadius: cornerRadius);
    }

    /// <summary>毛玻璃: the native acrylic backdrop under a translucent fill (alpha 40–240).</summary>
    private static PanelAppearance Acrylic(Theme theme, bool isDark, double cornerRadius)
    {
        byte alpha = (byte)Math.Clamp(Math.Round(theme.OpacityLevel * 220), 40, 240);

        return new PanelAppearance(
            Surface: SurfaceStyle.Acrylic,
            IsDark: isDark,
            TransparencyLevels: [WindowTransparencyLevel.AcrylicBlur, WindowTransparencyLevel.None],
            CardBackground: new SolidColorBrush(isDark
                ? Color.FromArgb(alpha, 28, 28, 32)
                : Color.FromArgb(alpha, 245, 245, 248)),
            CardBorderBrush: new SolidColorBrush(isDark ? Color.FromArgb(55, 255, 255, 255) : Color.FromArgb(35, 0, 0, 0)),
            UseRenderedGlass: false,
            GlassMaterial: null,
            TitleBrush: TextBrush(isDark),
            SubtitleBrush: SubTextBrush(isDark),
            IconBrush: SubTextBrush(isDark),
            CornerRadius: cornerRadius);
    }

    /// <summary>Header/body text: what every popup assigned to its title boxes.</summary>
    private static IBrush TextBrush(bool isDark)
        => isDark ? Brushes.White : new SolidColorBrush(Color.FromRgb(30, 30, 30));

    /// <summary>Secondary text and icon chrome: what every popup assigned to its close button.</summary>
    private static IBrush SubTextBrush(bool isDark)
        => isDark
            ? new SolidColorBrush(Color.FromArgb(200, 255, 255, 255))
            : new SolidColorBrush(Color.FromArgb(180, 0, 0, 0));
}

/// <summary>
/// Keeps one panel's <see cref="SecondaryPanelShell"/> in sync with the settings and the live
/// theme variant while the panel is open, and lets go of everything when it closes.
/// <para>
/// The panel's own window is the target of both subscriptions, so a closed panel is only
/// reachable from here while <see cref="IsAttached"/> is true — <see cref="ActiveCount"/>
/// exists for the foundation checks to prove that.
/// </para>
/// </summary>
public sealed class PanelAppearanceBinder
{
    private static int activeCount;

    /// <summary>Binders that are currently subscribed (must return to 0 after every panel closes).</summary>
    public static int ActiveCount => Volatile.Read(ref activeCount);

    private readonly Window window;
    private readonly SecondaryPanelShell shell;
    private readonly IAppSettingsProvider? settings;
    private bool attached;

    public PanelAppearanceBinder(Window window, SecondaryPanelShell shell, IAppSettingsProvider? settings = null)
    {
        this.window = window;
        this.shell = shell;
        this.settings = settings;
    }

    public bool IsAttached => attached;

    /// <summary>The last applied appearance (null until the first <see cref="Refresh"/>).</summary>
    public PanelAppearance? Current { get; private set; }

    /// <summary>How often the appearance was recomputed — a live settings/theme change must bump it.</summary>
    public int RefreshCount { get; private set; }

    public void Attach()
    {
        if (attached) return;
        attached = true;

        if (settings != null)
            settings.DataChanged += OnSettingsChanged;
        window.ActualThemeVariantChanged += OnThemeVariantChanged;
        Interlocked.Increment(ref activeCount);

        Refresh();
    }

    public void Detach()
    {
        if (!attached) return;
        attached = false;

        if (settings != null)
            settings.DataChanged -= OnSettingsChanged;
        window.ActualThemeVariantChanged -= OnThemeVariantChanged;
        Interlocked.Decrement(ref activeCount);
    }

    /// <summary>Recompute the appearance and push it into the shell and the panel window.</summary>
    public void Refresh()
    {
        var theme = PanelAppearanceResolver.ReadTheme(settings);
        var radius = shell.CornerRadius.TopLeft;
        var appearance = PanelAppearanceResolver.Resolve(theme, PanelAppearanceResolver.IsDark(theme, window.ActualThemeVariant), radius);

        Current = appearance;
        RefreshCount++;

        // The shell owns the card; the window owns the native backdrop level. Nothing here
        // touches business content, so plugin-specific brushes applied afterwards survive.
        shell.SurfaceAppearance = appearance;
        window.TransparencyLevelHint = appearance.TransparencyLevels;
    }

    private void OnSettingsChanged(object? sender, AppSettings? oldData, AppSettings newData)
        => Refresh();

    private void OnThemeVariantChanged(object? sender, EventArgs e)
        => Refresh();
}
