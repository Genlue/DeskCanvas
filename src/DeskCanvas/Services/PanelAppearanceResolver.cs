using System;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform;
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
    public const double DefaultCornerRadius = 36.0;

    // ---- 集中的历史观感颜色常量（P2 挂账，待语义键收敛后统一） --------------------------------
    //
    // 多彩（全彩）卡片的固定 macOS 底色。两个原因决定它们不能直接换成语义令牌：
    // 1. SolidBackgroundDark/Light（ThemeResourceHelper.WriteScopeCardResources 写入的
    //    "纯色卡片底色"键）是用户可调的纯色主题底色，默认 #2E2E2E/#FFFFFF——与本处的
    //    多彩固定卡片色 #1C1C1E/#FFFFFF 语义不同且暗色值不同，直接消费会观感漂移；
    // 2. ColorfulResources.WidgetBackground 虽然同值，但本类是文档化的纯静态函数
    //    （headless 可断言，不触碰 Avalonia Application 资源）。
    private static readonly Color ColorfulCardBackgroundDark = Color.Parse("#1C1C1E");
    private static readonly Color ColorfulCardBackgroundLight = Colors.White;

    // 面板主文字色（历史观感：暗白 / 亮近黑）。InteractionResources 目前没有主文字
    // 令牌（只有 SecondaryTextBrush），待语义键收敛（P2）后统一。
    private static readonly IBrush TextPrimaryDark = Brushes.White;
    private static readonly IBrush TextPrimaryLight = new SolidColorBrush(Color.FromRgb(30, 30, 30));

    /// <summary>
    /// What a popup used when <c>new AppSettingsProvider().Get()</c> threw — kept verbatim so a
    /// panel opened before the settings file is readable looks exactly as it did.
    /// </summary>
    /// <remarks>
    /// W10 修正：OpacityLevel 从 0.8 改为 1.0。旧插件弹窗 catch 分支在拿不到设置时按
    /// “非玻璃”处理（从不启用原生模糊）；而 0.8 会让 <see cref="Theme.EffectiveSurface"/>
    /// 推导出 Acrylic → UsesNativeBlur=true，语义正好相反。1.0（非玻璃基线，推导出
    /// Solid）与旧 catch 分支对齐：无 provider 时不再给出毛玻璃观感。
    /// </remarks>
    public static Theme FallbackTheme { get; } = new(
        DarkMode: true,
        AccentColor: null,
        OpacityLevel: 1.0,
        Monochrome: false,
        UseNativeFrame: false,
        FontFamily: "Microsoft YaHei UI",
        Surface: SurfaceStyle.FullColor);

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
    /// The panel's effective theme — the single rule the appearance binder and every plugin-owned
    /// content theming share: while 失焦自动切换 is live, every layer of the panel follows the
    /// state theme of the screen the panel's <b>own window</b> sits on (the same rule its
    /// opening widget uses); otherwise the shared theme. Without a provider (headless checks,
    /// very early start-up) or on a read failure this falls back to <see cref="FallbackTheme"/>.
    /// Never throws.
    /// </summary>
    /// <remarks>
    /// Content theming MUST read the theme through here (or off the shell's resolved
    /// <see cref="PanelAppearance"/>) instead of the shared theme directly: with 失焦自动切换 on,
    /// the state material and the shared material differ by design, and a panel whose shell paints
    /// one state while its content paints the other is exactly the reported mixed style.
    /// </remarks>
    public static Theme ResolveEffectiveTheme(IAppSettingsProvider? settings) =>
        ResolveEffectiveTheme(settings, window: null);

    /// <summary>The per-screen entry: the panel window maps to its screen and follows that
    /// screen's state. A null window (or one without a resolvable screen) falls back to the
    /// uniform entry — the behaviour before per-screen resolution existed.
    /// <para>
    /// 跟随强调色 is baked before the theme leaves this method, so the panel's glass material,
    /// its acrylic fill and the launcher that asks this same resolver all agree on the derived
    /// background pair. The coordinator path already returns a baked theme; baking again is a
    /// no-op by construction (the pair is always re-derived from the accent, never from the
    /// pair already in the record).
    /// </para>
    /// </summary>
    public static Theme ResolveEffectiveTheme(IAppSettingsProvider? settings, Window? window)
    {
        if (settings is null) return FallbackTheme;
        try
        {
            var appSettings = settings.Get();
            if (appSettings.FocusLink is not { Enabled: true })
                return AccentPalette.ApplyDerivedBackgrounds(appSettings.Theme, AccentPalette.TryGetSystemAccent());
            var coordinator = App.Services?.GetService(typeof(FocusThemeCoordinator)) as FocusThemeCoordinator;
            if (coordinator is null)
                return AccentPalette.ApplyDerivedBackgrounds(appSettings.Theme, AccentPalette.TryGetSystemAccent());
            return window is { } w
                ? coordinator.ResolveWindowTheme(appSettings, w)
                : coordinator.ResolveWindowTheme(appSettings);
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

    /// <summary>
    /// Whether <b>Windows</b> is currently dark, independent of the app's own variant.
    /// <para>
    /// 跟随系统 must answer to the OS even while the app is pinned light or dark — the window's
    /// <c>ActualThemeVariant</c> is the app's answer, so it cannot stand in for the system's.
    /// </para>
    /// </summary>
    /// <param name="fallback">Used when the platform has no opinion (headless checks, start-up).</param>
    public static bool IsSystemDark(bool fallback)
    {
        try
        {
            var variant = Application.Current?.PlatformSettings?.GetColorValues().ThemeVariant;
            if (variant == PlatformThemeVariant.Dark) return true;
            if (variant == PlatformThemeVariant.Light) return false;
        }
        catch
        {
            // Platform settings are optional; a failure must not break the open path.
        }

        return fallback;
    }

    /// <summary>Pure resolution of the shell appearance. No Avalonia object is touched.</summary>
    public static PanelAppearance Resolve(Theme theme, bool isDark, double cornerRadius)
    {
        var surface = theme.EffectiveSurface;

        if (theme.UsesRenderedGlass)
            return Glass(theme, isDark, cornerRadius, surface);
        if (theme.IsColorful)
            return Colorful(theme, isDark, cornerRadius);

        return Acrylic(theme, isDark, cornerRadius);
    }

    // ---- the material branches -------------------

    /// <summary>
    /// 液态玻璃: the card itself stays transparent and the live surface paints it.
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

    /// <summary>全彩: the flat opaque macOS-style card.</summary>
    private static PanelAppearance Colorful(Theme theme, bool isDark, double cornerRadius)
        => new(
            Surface: SurfaceStyle.Colorful,
            IsDark: isDark,
            TransparencyLevels: [WindowTransparencyLevel.Transparent, WindowTransparencyLevel.None],
            CardBackground: new SolidColorBrush(isDark ? ColorfulCardBackgroundDark : ColorfulCardBackgroundLight),
            CardBorderBrush: new SolidColorBrush(isDark ? Color.FromArgb(60, 255, 255, 255) : Color.FromArgb(40, 0, 0, 0)),
            UseRenderedGlass: false,
            GlassMaterial: null,
            TitleBrush: TextBrush(isDark),
            SubtitleBrush: SubTextBrush(isDark),
            IconBrush: SubTextBrush(isDark),
            CornerRadius: cornerRadius);

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
        => isDark ? TextPrimaryDark : TextPrimaryLight;

    /// <summary>Secondary text and icon chrome: what every popup assigned to its close button.</summary>
    private static IBrush SubTextBrush(bool isDark)
        => isDark
            ? new SolidColorBrush(Color.FromArgb(200, 255, 255, 255))
            : new SolidColorBrush(Color.FromArgb(180, 0, 0, 0));

    // ---- text antialiasing ------------------------------------------------------------------

    /// <summary>
    /// The text antialiasing a card material may use. The ClearType-class subpixel mask blends
    /// each channel separately assuming the pixels under the glyphs are opaque; on a translucent
    /// card the window surface is then alpha-composited with the live desktop, and the mask's
    /// leftover per-channel coverage survives that composite as red/green fringing (色散) —
    /// the same reason Windows itself forces grayscale AA over acrylic. The opaque 多彩 card
    /// keeps subpixel: blending is correct there, and it is what keeps 11–12 px CJK stems one
    /// pixel wide (the historic "grey mush" complaint under plain grayscale).
    /// <para>
    /// 毛玻璃 resolves translucent by construction (the acrylic branch clamps the card alpha to
    /// ≤ 240), 液态玻璃 because the glass layer itself carries partial alpha.
    /// </para>
    /// </summary>
    public static TextRenderingMode ResolveTextRenderingMode(Theme theme)
        => theme.IsFullColor ? TextRenderingMode.SubpixelAntialias : TextRenderingMode.Antialias;

    /// <summary>The resolved-shell entry: same rule keyed on the <see cref="PanelAppearance"/>.</summary>
    public static TextRenderingMode ResolveTextRenderingMode(PanelAppearance appearance)
        => !appearance.UseRenderedGlass && appearance.Surface == SurfaceStyle.Colorful
            ? TextRenderingMode.SubpixelAntialias
            : TextRenderingMode.Antialias;
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

    /// <summary>Recompute the appearance and push it into the shell and the panel window.
    /// The panel's own window is passed to the resolver so a panel follows the state theme
    /// of the screen it was opened on.</summary>
    public void Refresh()
    {
        var theme = PanelAppearanceResolver.ResolveEffectiveTheme(settings, window);
        var radius = shell.CornerRadius.TopLeft;
        var appearance = PanelAppearanceResolver.Resolve(theme, PanelAppearanceResolver.IsDark(theme, window.ActualThemeVariant), radius);

        Current = appearance;
        RefreshCount++;

        // The shell owns the card; the window owns the native backdrop level. Nothing here
        // touches business content, so plugin-specific brushes applied afterwards survive.
        shell.SurfaceAppearance = appearance;
        window.TransparencyLevelHint = appearance.TransparencyLevels;

        // Text AA follows the material (same physics as the spotlight overlay): the
        // ClearType-class subpixel mask is only correct over opaque pixels, so the translucent
        // 毛玻璃/液态玻璃 panels render grayscale — see
        // PanelAppearanceResolver.ResolveTextRenderingMode(PanelAppearance).
        SurfaceTextRendering.Apply(window, PanelAppearanceResolver.ResolveTextRenderingMode(appearance));

        if (window is Views.SecondaryPanelWindow spw)
        {
            spw.ApplyWindowRegion();
        }
    }

    private void OnSettingsChanged(object? sender, AppSettings? oldData, AppSettings newData)
        => Refresh();

    private void OnThemeVariantChanged(object? sender, EventArgs e)
        => Refresh();
}
