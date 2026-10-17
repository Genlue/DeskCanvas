using System;
using System.Runtime.CompilerServices;
using Avalonia.Controls;

namespace DeskCanvas.Services;

/// <summary>The role of a window in the DeskCanvas shell — the single registration point that
/// declares what kind of window it is and therefore which platform capability profile applies
/// to it (P6, ADR-P6b).</summary>
public enum WindowRole
{
    /// <summary>A desktop widget card (the <c>Widget</c> window): bottom-band desktop furniture.</summary>
    DesktopWidget,

    /// <summary>A widget's secondary panel (<c>SecondaryPanelWindow</c> and its popups): pinned
    /// just above the widget band, follows the 失焦自动切换 theme, closed on display changes.</summary>
    SecondaryPanel,

    /// <summary>The shared settings window singleton: an ordinary application window that is
    /// hidden instead of closed and re-shown through <c>ShowAndActivate</c>.</summary>
    Settings,

    /// <summary>A transient dialog (confirm/input/pickers, the update popup, the widget and
    /// shortcut editors, the grid editor): shown over its owner, never desktop furniture.</summary>
    Dialog,

    /// <summary>Reserved for a future preview window. No implementation exists yet; the profile
    /// is an all-inert placeholder so the role is already addressable.</summary>
    Preview,

    /// <summary>The invisible 1×1 anchor window that keeps an Avalonia TopLevel alive for
    /// screen enumeration and watcher services for the whole app lifetime.</summary>
    Anchor
}

/// <summary>The z-order band a role's windows are pinned to by <see cref="WidgetZOrder"/>;
/// <see cref="WindowZBand.None"/> means the window is an ordinary z-order citizen.</summary>
public enum WindowZBand
{
    /// <summary>No band membership — dialogs, the settings window and previews.</summary>
    None,

    /// <summary>The bottom band: widget cards and the anchor (<see cref="WidgetZOrder.PinWidgetToBottom"/>).</summary>
    BottomBand,

    /// <summary>Just above the widget band: secondary panels (<see cref="WidgetZOrder.PinPanelAboveWidgets"/>).</summary>
    AboveWidgets
}

/// <summary>The platform capability profile of a window role. Every field records the role's
/// <b>current</b> behaviour as implemented by the scattered call sites this slice is registering
/// (see <see cref="WindowRoles"/> for the per-role evidence). Nothing here is enforced yet —
/// later P6 slices migrate the imperative behaviour (taskbar visibility, z-order pinning,
/// activation, theme tracking, display-change closing) to read from these profiles.</summary>
/// <param name="ShowInTaskbar">Whether the window occupies a taskbar / Alt-Tab slot.</param>
/// <param name="ZBand">The z-order band the window is pinned to (or <see cref="WindowZBand.None"/>).</param>
/// <param name="ActivatesOnShow">Whether showing the window brings it to the front and activates it.</param>
/// <param name="TracksFocusTheme">Whether the window is handed to <see cref="FocusThemeCoordinator"/>
/// (via <see cref="DesktopFurniture.Attach"/>) for the 失焦自动切换 per-screen light/dark variant.</param>
/// <param name="CloseOnDisplayChange">Whether the window is closed (not hidden) when the display
/// topology changes and recreated on demand.</param>
public sealed record WindowRoleProfile(
    bool ShowInTaskbar,
    WindowZBand ZBand,
    bool ActivatesOnShow,
    bool TracksFocusTheme,
    bool CloseOnDisplayChange);

/// <summary>
/// P6's window-role registry: the single declarative place where each DeskCanvas window declares
/// its role, so the role → capability profile mapping lives in one table instead of being
/// re-derived from scattered imperative code.
/// <para>
/// This slice is behaviour-preserving: <see cref="Register"/> only records the role. The profiles
/// below faithfully document each role's platform behaviour <i>as it exists today</i>, citing the
/// call sites that implement it. Later slices replace those call sites with profile reads —
/// taskbar visibility, z-order pinning (<see cref="WidgetZOrder"/>), focus-theme tracking
/// (<see cref="DesktopFurniture"/>/<see cref="FocusThemeCoordinator"/>), activation and
/// display-change closing.
/// </para>
/// <para>
/// Registration is keyed per window instance (<see cref="ConditionalWeakTable"/>, no root keeps
/// a window alive) and is idempotent; re-registering a window with a different role silently
/// overwrites — the last registration wins, matching the "creator knows best" rule.
/// </para>
/// <para>
/// Profile values and their evidence (all as of this slice, behaviour-preserving):
/// <list type="bullet">
/// <item><b>DesktopWidget</b> — ShowInTaskbar=false (Widget.axaml:8), ZBand=BottomBand
/// (Widget.axaml.cs:254 <c>WidgetZOrder.PinWidgetToBottom</c>), ActivatesOnShow=false,
/// TracksFocusTheme=true (Widget.axaml.cs:245 <c>DesktopFurniture.Attach</c> →
/// FocusThemeCoordinator.Track), CloseOnDisplayChange=false (display changes suspend/hide
/// widgets via WidgetFactory, they are not closed).</item>
/// <item><b>SecondaryPanel</b> — ShowInTaskbar=false, ZBand=AboveWidgets
/// (SecondaryPanelWindow.cs:402 <c>WidgetZOrder.PinPanelAboveWidgets</c>), ActivatesOnShow=true,
/// TracksFocusTheme=true (SecondaryPanelWindow.cs:450 <c>DesktopFurniture.Attach</c>),
/// CloseOnDisplayChange=true (SecondaryPanelWindow.cs:102 <c>CloseForDisplayChange</c>,
/// invoked from WidgetFactory.cs:85).</item>
/// <item><b>Settings</b> — ShowInTaskbar=true (the Avalonia default; neither Settings.axaml nor
/// Settings.axaml.cs touches the property), ZBand=None, ActivatesOnShow=true
/// (Settings.axaml.cs:114 <c>Activate</c> + :118 <c>InteropService.BringToFront</c>),
/// TracksFocusTheme=false (Settings never calls <c>DesktopFurniture.Attach</c>),
/// CloseOnDisplayChange=false (the window hides, never closes — Settings.axaml.cs:290 OnClosing).</item>
/// <item><b>Dialog</b> — ZBand=None, TracksFocusTheme=false (dialogs are never attached to the
/// focus-theme coordinator), CloseOnDisplayChange=false. ShowInTaskbar records the modal
/// baseline false (ConfirmDialog.axaml:9, InputDialog.axaml:9, ScaleDialog, ProcessPickerDialog.axaml:12,
/// GuideWindow.axaml:11, ImportReviewWindow.axaml:11), shown modally via <c>ShowDialog</c>
/// (ConfirmDialog.axaml.cs:40/50, InputDialog.axaml.cs:40, Widget.axaml.cs:1029,
/// TimetableSettings.axaml.cs:170/234). Per-window exceptions that keep their own explicit
/// values: EditWidget (ShowInTaskbar=true, EditWidget.axaml:13), ShortcutsEditorWindow
/// (ShowInTaskbar=true, ShortcutsEditorWindow.axaml:14), and the non-modal windows shown with
/// plain <c>Show()</c> — GridEditor (ShowInTaskbar=false, GridEditor.axaml:6),
/// WallpaperAlignDialog (Appearance.axaml.cs:57) and UpdatePopup (About.axaml.cs:55,
/// GeneralViewModel.cs:249, UpdateService.cs:125). The registration point, not the profile,
/// carries those nuances today.</item>
/// <item><b>Preview</b> — reserved placeholder, all-inert (no implementation exists yet).</item>
/// <item><b>Anchor</b> — ShowInTaskbar=false (WidgetAnchorWindow.cs:25), ZBand=BottomBand
/// (App.axaml.cs:199 the anchor is pinned to the widget band so the band predicate never sees a
/// visible non-member below a widget), ActivatesOnShow=false (1×1, off-screen, WindowDecorations.None,
/// removed from Alt-Tab — WidgetAnchorWindow.cs:21-55), TracksFocusTheme=false,
/// CloseOnDisplayChange=false (lives for the whole app lifetime).</item>
/// </list>
/// </para>
/// </summary>
public static class WindowRoles
{
    /// <summary>Registration per window instance. A ConditionalWeakTable keeps the mapping alive
    /// exactly as long as the window itself (same pattern as DesktopFurniture.attached) and is
    /// individually thread-safe, so no extra gate is needed. The role is stored boxed — the
    /// table only accepts reference-type values.</summary>
    private static readonly ConditionalWeakTable<Window, object> registered = [];

    private static readonly WindowRoleProfile[] profiles =
    [
        // DesktopWidget: see the class comment for every field's evidence.
        new WindowRoleProfile(ShowInTaskbar: false, WindowZBand.BottomBand,
            ActivatesOnShow: false, TracksFocusTheme: true, CloseOnDisplayChange: false),
        // SecondaryPanel: pinned above the widget band, theme-tracked, closed on display changes.
        new WindowRoleProfile(ShowInTaskbar: false, WindowZBand.AboveWidgets,
            ActivatesOnShow: true, TracksFocusTheme: true, CloseOnDisplayChange: true),
        // Settings: ordinary taskbar window, hide/re-show reuse, never theme-tracked.
        new WindowRoleProfile(ShowInTaskbar: true, WindowZBand.None,
            ActivatesOnShow: true, TracksFocusTheme: false, CloseOnDisplayChange: false),
        // Dialog: modal baseline (taskbar-less, owner-relative, z-order unmanaged, not theme-tracked).
        new WindowRoleProfile(ShowInTaskbar: false, WindowZBand.None,
            ActivatesOnShow: true, TracksFocusTheme: false, CloseOnDisplayChange: false),
        // Preview: reserved placeholder — nothing is implemented for it yet.
        new WindowRoleProfile(ShowInTaskbar: false, WindowZBand.None,
            ActivatesOnShow: false, TracksFocusTheme: false, CloseOnDisplayChange: false),
        // Anchor: invisible lifetime anchor, member of the widget bottom band.
        new WindowRoleProfile(ShowInTaskbar: false, WindowZBand.BottomBand,
            ActivatesOnShow: false, TracksFocusTheme: false, CloseOnDisplayChange: false),
    ];

    /// <summary>Declare <paramref name="window"/>'s role. Idempotent per window; re-registering
    /// with a different role silently overwrites (last registration wins) — creators that can
    /// run twice for one window (re-show paths) must not stack state, mirroring the reasoning
    /// behind DesktopFurniture's attached table.</summary>
    public static void Register(Window window, WindowRole role)
    {
        ArgumentNullException.ThrowIfNull(window);
        registered.Remove(window);
        registered.Add(window, role);
    }

    /// <summary>The role declared for <paramref name="window"/>, or <c>null</c> when the window
    /// never went through <see cref="Register"/>.</summary>
    public static WindowRole? GetRole(Window window) =>
        window != null && registered.TryGetValue(window, out var boxed) ? (WindowRole)boxed : null;

    /// <summary>The capability profile of <paramref name="role"/> — the declarative record of
    /// what the role's windows currently do on this platform.</summary>
    public static WindowRoleProfile GetProfile(WindowRole role) => profiles[(int)role];

    /// <summary>The profile of the role <paramref name="window"/> was registered with, or
    /// <c>null</c> when the window is unregistered.</summary>
    public static WindowRoleProfile? ProfileOf(Window window) =>
        GetRole(window) is { } role ? GetProfile(role) : null;

    /// <summary>Check-fixture escape hatch: clears every registration so a probe can start from
    /// a pristine table. Not for production use.</summary>
    internal static void ResetForChecks() => registered.Clear();
}
