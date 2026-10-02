namespace DeskCanvas.Core.Models.Settings;

/// <summary>
/// Global右侧小组件侧栏 settings, stored in <see cref="AppSettings.Sidebar"/>.
/// Per-screen state (width, widget list) lives in <see cref="ScreenLayout.Sidebar"/>.
/// </summary>
/// <param name="HotKey">Global hotkey that toggles the sidebar on the cursor's screen. Normalized form
/// (e.g. <c>"Ctrl+Alt+Space"</c>).</param>
/// <param name="DefaultWidthDip">Width used for screens that never had one set.</param>
/// <param name="MinWidthDip">Lower clamp for any sidebar width.</param>
/// <param name="MaxWidthDip">Upper clamp for any sidebar width.</param>
/// <param name="BlockOnFullscreen">When <c>true</c>, the sidebar is pulled back as soon as a true fullscreen
/// app (exclusive fullscreen or borderless fullscreen) becomes foreground; the hotkey can still summon it.</param>
/// <param name="BlockedProcessNames">Process names (case-insensitive, without extension) whose foreground presence
/// currently blocks the sidebar overlay.</param>
/// <param name="Shadow">The sidebar's diffuse shadow; <c>null</c> uses the defaults.</param>
/// <param name="WidgetMarginDip">Inset between a card and its grid cell, <b>per side</b>, in DIPs —
/// the same knob the desktop grid calls 组件内边距 (<see cref="Dimensions.Margin"/>). Two
/// neighbouring cards therefore end up <c>2 ×</c> this apart. <c>null</c> uses
/// <see cref="DefaultWidgetMarginDip"/>.</param>
/// <param name="WidgetRadiusDip">Corner radius of the sidebar's cards, in DIPs. <c>null</c> keeps
/// following the desktop (the screen's own <see cref="ScreenLayout.Radius"/>, then the global
/// <see cref="Dimensions.Radius"/>), which is what every sidebar did before this setting existed.</param>
public record SidebarSettings(
    string HotKey = "Ctrl+Alt+Space",
    double DefaultWidthDip = 360,
    double MinWidthDip = 280,
    double MaxWidthDip = 640,
    bool BlockOnFullscreen = true,
    List<string>? BlockedProcessNames = null,
    SidebarShadowSettings? Shadow = null,
    int? WidgetMarginDip = null,
    int? WidgetRadiusDip = null)
{
    /// <summary>Inset per side used when nothing is configured (the sidebar's historic 4 DIP,
    /// i.e. the 8 DIP gutter between two neighbouring cards).</summary>
    public const int DefaultWidgetMarginDip = 4;

    /// <summary>Smallest inset accepted.</summary>
    public const int MinWidgetMarginDip = 0;

    /// <summary>
    /// Largest inset accepted. The grid additionally caps the drawn inset at a quarter of a cell
    /// (see <c>SidebarRules.EffectiveGutter</c>) so a narrow sidebar cannot squeeze its cards away.
    /// </summary>
    public const int MaxWidgetMarginDip = 24;

    /// <summary>Smallest corner radius accepted.</summary>
    public const int MinWidgetRadiusDip = 0;

    /// <summary>Largest corner radius accepted (same ceiling as the desktop's).</summary>
    public const int MaxWidgetRadiusDip = 48;

    /// <summary>The shadow settings with defaults and clamped ranges applied.</summary>
    public SidebarShadowSettings EffectiveShadow => Shadow ?? new SidebarShadowSettings();

    /// <summary>The per-side inset actually used (clamped; the default when nothing is configured).</summary>
    public int EffectiveWidgetMarginDip =>
        Math.Clamp(WidgetMarginDip ?? DefaultWidgetMarginDip, MinWidgetMarginDip, MaxWidgetMarginDip);

    /// <summary>
    /// The card radius actually used: the sidebar's own override when it has one, otherwise
    /// <paramref name="desktopRadiusDip"/> — the desktop chain the host resolves for this screen.
    /// </summary>
    public int EffectiveWidgetRadiusDip(int desktopRadiusDip) =>
        Math.Clamp(WidgetRadiusDip ?? desktopRadiusDip, MinWidgetRadiusDip, MaxWidgetRadiusDip);
}

/// <summary>
/// The sidebar's diffuse shadow — the soft dark wash that spreads from the sidebar's right edge
/// leftwards across the strip, so the panel reads as floating above the desktop instead of as a
/// strip of loose cards (the macOS Notification Center shadow).
/// <para>
/// The wash is <b>flat</b> behind the cards and only starts to weaken at the leftmost card, then
/// fades out over <see cref="FeatherDip"/> and reaches a little further left than that card does
/// (<see cref="ExtendDip"/>) — which is also why the sidebar window is widened by that amount
/// while the cards keep their own width: the tail has to have somewhere to go.
/// </para>
/// </summary>
/// <param name="Enabled">Whether the shadow is drawn at all.</param>
/// <param name="Opacity">Peak opacity of the wash, 0-1 (0.30 is a macOS-like panel shadow).</param>
/// <param name="Color">Wash colour in HEX; near-black reads best over any wallpaper.</param>
/// <param name="ExtendDip">How far the shadow reaches past the leftmost card, in DIPs. This is the
/// width the sidebar window is widened by while the cards keep their configured width.</param>
/// <param name="FeatherDip">Length of the fade from full opacity down to nothing, in DIPs.</param>
/// <param name="FadeStartOffsetDip">Where the fade starts, relative to the leftmost card's left
/// edge. <c>0</c> = the weakening begins exactly at that card; a positive value starts it further
/// right (a softer, wider shadow), a negative one pushes it left.</param>
public record SidebarShadowSettings(
    bool Enabled = true,
    double Opacity = 0.30,
    string Color = "#000000",
    double ExtendDip = 36,
    double FeatherDip = 56,
    double FadeStartOffsetDip = 0)
{
    /// <summary>Largest reach past the leftmost card accepted (the window grows by this much).</summary>
    public const double MaxExtendDip = 160;

    /// <summary>Shortest fade accepted — 1 rather than 0, so the gradient always has a falloff.</summary>
    public const double MinFeatherDip = 1;

    /// <summary>Longest fade accepted.</summary>
    public const double MaxFeatherDip = 400;

    /// <summary>Peak opacity actually used.</summary>
    public double EffectiveOpacity => Math.Clamp(Opacity, 0, 1);

    /// <summary>Reach past the leftmost card actually used (and therefore the window's extra width).</summary>
    public double EffectiveExtendDip => Math.Clamp(ExtendDip, 0, MaxExtendDip);

    /// <summary>Fade length actually used.</summary>
    public double EffectiveFeatherDip => Math.Clamp(FeatherDip, MinFeatherDip, MaxFeatherDip);

    /// <summary>Fade start offset actually used.</summary>
    public double EffectiveFadeStartOffsetDip => Math.Clamp(FadeStartOffsetDip, -MaxFeatherDip, MaxFeatherDip);

    /// <summary>The wash colour, falling back to black for an unparsable value.</summary>
    public string EffectiveColor => string.IsNullOrWhiteSpace(Color) ? "#000000" : Color;

    /// <summary>Whether a shadow is actually drawn (enabled, visible, and reaching somewhere).</summary>
    public bool IsVisible => Enabled && EffectiveOpacity > 0.001
        && (EffectiveExtendDip > 0 || EffectiveFeatherDip > 0);
}

/// <summary>
/// Runtime overlay policy derived from <see cref="SidebarSettings"/> — kept as a small
/// value object so the fullscreen/process-blocking rule can be exercised in tests.
/// </summary>
/// <param name="BlockOnFullscreen">Block the overlay while a true fullscreen app is foreground.</param>
/// <param name="BlockedProcessNames">Foreground process names that block the overlay.</param>
public record SidebarOverlayPolicy(
    bool BlockOnFullscreen,
    IReadOnlyList<string> BlockedProcessNames)
{
    /// <summary>Build the policy from persisted settings (never null; a missing name list reads as empty).</summary>
    public static SidebarOverlayPolicy From(SidebarSettings settings) =>
        new(settings.BlockOnFullscreen, settings.BlockedProcessNames ?? []);

    /// <summary>
    /// Whether the sidebar may stay visible given the foreground observation.
    /// <paramref name="isTrueFullscreen"/> must already exclude ordinary maximized windows —
    /// a maximized window never blocks the sidebar.
    /// </summary>
    public bool Allows(bool isTrueFullscreen, string? foregroundProcessName)
    {
        if (BlockOnFullscreen && isTrueFullscreen) return false;
        if (foregroundProcessName is { Length: > 0 } name &&
            BlockedProcessNames.Any(blocked => string.Equals(blocked, name, StringComparison.OrdinalIgnoreCase)))
            return false;
        return true;
    }
}
