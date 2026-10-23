using System;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DeskCanvas.Services;
using DeskCanvas.UI.Animation;
using DeskCanvas.Views.Controls;
// The panel drives the glass through the shared port; the alias disambiguates it from the host
// shell namespace, which also comes in via using above.
using GlassSurfaceBase = global::DeskCanvas.UI.Controls.GlassSurfaceBase;

namespace DeskCanvas.Views;

/// <summary>
/// Shared template for the widgets' secondary panel windows (weather forecast, reminders,
/// big folder, clipboard). Everything a panel needs to behave identically lives here:
///   - spawn position: centered on the opening widget (clamped to the screen's work area),
///   - open transition: the card scales up straight out of the opening widget (render
///     transform origin pinned to the widget's center) while fading in, on the mod's damped
///     spring (a restrained overshoot, then settle),
///   - close transition: the card folds back into the widget on the critically-damped spring,
///   - dismissal: Escape, focus loss (after a grace period), outside click, or an explicit Close(),
///   - glass: the card is drawn by the same <see cref="LiquidGlassSurface"/> the widget card
///     uses, and the surface is fed the zoom's interpolated scale so its optics animate with
///     the visible transition (with or without live wallpaper sampling),
///   - per-panel-type rules: one instance at a time and no immediate re-open after a close.
/// Derived windows keep their own AXAML, theming and content; they expose the animatable
/// card via <see cref="PanelCard"/> and call <see cref="InitializePanel"/> right after
/// InitializeComponent. <see cref="Close"/> runs the exit animation before really closing.
/// </summary>
public abstract class SecondaryPanelWindow : Window, IFocusThemedWindow
{
    private delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSLLHOOKSTRUCT
    {
        public POINT pt;
        public uint mouseData;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    private const int WH_MOUSE_LL = 14;
    private const int WM_LBUTTONDOWN = 0x0201;
    private const int WM_RBUTTONDOWN = 0x0204;
    private const int WM_MBUTTONDOWN = 0x0207;
    private const int WM_NCLBUTTONDOWN = 0x00A1;
    private const int WM_NCRBUTTONDOWN = 0x00A4;
    private const int WM_NCMBUTTONDOWN = 0x00A7;

    /// <summary>A panel belongs to its opening display; dismiss it before Windows relocates it.</summary>
    /// <remarks>State and identity live in <see cref="PanelService"/>; this forwards so the
    /// public entry point the widget host calls stays unchanged.</remarks>
    public static void CloseForDisplayChange() => PanelService.CloseForDisplayChange();

    // Motion ported from the Windhawk "Mac Island & Dock" mod (DeskCanvas.UI.Animation):
    // the card's zoom is a damped spring rather than a fixed easing curve, so it overshoots
    // by ~3.9% and settles instead of arriving dead on a spline. The overshoot is what makes
    // the panel read as physical; the mod's island opens exactly this way (kBounceDamping).
    // Opacity and the closing zoom keep critical damping: an overshooting alpha would clip at
    // 1 and dip back, and a shrinking card must not undershoot past its target.
    private static readonly SpringEasing OpenEasing = SpringMotion.CreateEasing(SpringMotion.Bounce);
    private static readonly SpringEasing SmoothEasing = SpringMotion.CreateEasing(SpringMotion.Smooth);

    private static readonly TimeSpan OpenDuration = TimeSpan.FromMilliseconds(320);
    private static readonly TimeSpan CloseDuration = TimeSpan.FromMilliseconds(180);
    // Transitions start on the first rendered frame after the value change; pad the close
    // timer so base.Close() never fires while the exit transition is still running.
    private static readonly TimeSpan CloseTimerDuration = CloseDuration + TimeSpan.FromMilliseconds(40);

    // The card grows out of the widget from 60% of its final size — small enough that the
    // anchored origin reads clearly, large enough that text never looks scrambled mid-flight.
    private const double OpenScaleFrom = 0.60;
    private const double CloseScaleTo = 0.60;
    private const int ReopenCooldownMs = 250;

    private ScaleTransform? cardScale;
    private bool panelLoaded;
    private bool openAnimationPlayed;
    private bool closeAnimationRunning;
    private bool wasActivated;
    private DateTime activatedAtUtc;
    private LowLevelMouseProc? mouseHookProc;
    private IntPtr hookHandle = IntPtr.Zero;

    // ---- Glass transition during the open/close zoom ----
    // The card's glass must follow the zoom: the liquid-glass surface re-parameterises its
    // shader for every interpolated scale (SetAnimationFrameScale), driven by the scale
    // transform's per-tick property changes so it stays exactly in phase with the visible zoom.
    // This is the *same* path the primary widget card uses, with or without live wallpaper
    // sampling — with sampling off the shared wallpaper frame is simply frozen, which is still
    // a full-quality backdrop. (Panels used to switch to a strip of pre-rendered bitmaps when
    // sampling was off; that strip lagged behind the surface, masked the animating glass with a
    // static bitmap, and fell back to a flat translucent scrim until it was ready.)
    private RelativePoint? spawnAnchor;
    // The panel drives the glass through the shared port (DeskCanvas.UI.GlassSurfaceBase) —
    // render requests and animation-rate re-parameterisation — so the shell's slot surface and
    // any legacy in-content surface are driven identically without casting to the host type.
    private GlassSurfaceBase? glassSurface;

    // ---- W7: the shared shell -----------------------------------------------------------
    // A migrated panel declares no card/glass/title/close-button of its own: its AXAML root is a
    // SecondaryPanelShell and this window drives that shell's parts. Old panels keep overriding
    // PanelCard, so the default below only kicks in when a shell is actually present.
    private SecondaryPanelShell? panelShell;
    private SecondaryPanelShell? connectedShell;
    private PanelAppearanceBinder? appearanceBinder;
    private bool panelInitialised;
    private bool visualCardConnected;

    /// <summary>The connected shell, or null for a panel that still declares its own chrome.</summary>
    protected SecondaryPanelShell? PanelShell => panelShell ??= FindPanelShell();

    /// <summary>The card visual that carries the open/close transition: the connected shell's card
    /// border, or whatever a not-yet-migrated panel overrides it with.</summary>
    protected virtual Visual? PanelCard => connectedShell?.CardBorder;

    /// <summary>Physical-pixel center of the widget the panel spawns from; null centers on the primary work area.</summary>
    protected abstract Point? SpawnScreenCenter { get; }

    /// <summary>
    /// Corner radius (DIP) propagated from the opening widget's card so the panel's corners
    /// match it exactly; 0 keeps the panel's own AXAML radius. Must be handed to
    /// <see cref="InitializePanel"/> before derived theming runs, so every glass path
    /// (glass surface, native window region) picks it up.
    /// </summary>
    protected double SpawnCornerRadius { get; private set; }

    /// <summary>Whether this panel's own screen's state theme uses native blur. Instance on
    /// purpose: with 失焦自动切换 live the panel follows its own screen, not the uniform state.</summary>
    protected bool UsesNativeBlurTheme()
    {
        return PanelAppearanceResolver.ResolveEffectiveTheme(PanelAppearanceResolver.TryResolveSettingsProvider(), this).UsesNativeBlur;
    }

    /// <summary>
    /// Whether the card may scale and rise. Must stay off when the panel clips itself with a
    /// native window region (SetWindowRgn): the region cannot follow a render transform, so
    /// the transition falls back to opacity only.
    /// </summary>
    protected virtual bool TransformAnimationEnabled => !UsesNativeBlurTheme();

    /// <summary>How long the panel tolerates losing focus right after taking it before auto-closing.</summary>
    protected virtual int DeactivateCloseGraceMs => 300;

    /// <summary>When the panel's window finished loading; starts at MinValue until then.</summary>
    protected DateTime LoadedAtUtc { get; private set; } = DateTime.MinValue;

    /// <summary>True while the exit animation is playing (all close paths are no-ops then).</summary>
    protected bool CloseAnimationRunning => closeAnimationRunning;

    /// <summary>Extra load work (focus, glass surface, window region, hooks); positioning and the open animation are handled by the base.</summary>
    protected virtual void OnPanelLoaded() { }

    /// <summary>Return true to consume Escape (e.g. close an inner drawer instead of the panel).</summary>
    protected virtual bool OnPanelEscape() => false;

    /// <summary>
    /// Called by derived constructors right after InitializeComponent. Two steps, because a
    /// ControlTheme's parts do not exist yet at this point:
    /// <list type="number">
    /// <item>registration (idempotent): spawn radius, window-role and PanelService registration
    /// (legacy process/type key — <see cref="PanelService.Open"/> rebinds it), window events;</item>
    /// <item>connecting the visual shell — deferred to the shell's template application for a
    /// migrated panel, immediate for one that still declares its own card.</item>
    /// </list>
    /// </summary>
    protected void InitializePanel(double? cornerRadius = null)
    {
        InitializePanelCore(cornerRadius);

        // A shell's parts only exist after its template is applied, which happens when the window
        // is realized — hence the re-connection on TemplateApplied (and once more from Loaded as
        // a safety net, which is why connecting is idempotent).
        if (PanelShell is { } shell)
        {
            shell.TemplateConnected -= OnShellTemplateApplied;
            shell.TemplateConnected += OnShellTemplateApplied;
        }

        ConnectPanelShell();
    }

    private void InitializePanelCore(double? cornerRadius)
    {
        if (panelInitialised) return;
        panelInitialised = true;

        SpawnCornerRadius = cornerRadius ?? 0;

        WindowRoles.Register(this, WindowRole.SecondaryPanel);

        // Registration goes through the shared service: the panel starts under its legacy
        // process/type key (bit-identical with the pre-service consumers) and
        // PanelService.Open rebinds it to the requesting identity before it is shown.
        PanelService.RegisterPanel(this);

        Loaded += OnPanelWindowLoaded;
        Resized += (_, _) => ApplyWindowRegion();
        Activated += OnPanelWindowActivated;
        Deactivated += OnPanelWindowDeactivated;
        Closing += OnPanelWindowClosing;
        Closed += OnPanelWindowClosed;
        KeyDown += OnPanelKeyDown;
    }

    private SecondaryPanelShell? FindPanelShell()
        => Content as SecondaryPanelShell
           ?? this.GetLogicalDescendants().OfType<SecondaryPanelShell>().FirstOrDefault();

    private void OnShellTemplateApplied(object? sender, EventArgs e)
    {
        ConnectPanelShell();

        // Late template application: the shell's card did not exist yet when Loaded ran the
        // open transition (PlayOpenAnimation early-returned with no card to animate). The card
        // is connected hidden at the spawn scale now — play the transition for real. The call
        // is idempotent, so the normal ordering (template applied before Loaded, where Loaded
        // plays the transition itself) is unaffected.
        if (panelLoaded)
        {
            RequestGlassRender();
            PlayOpenAnimation();
        }
    }

    /// <summary>
    /// Take the card/glass/close plumbing of the shell that is currently realized. Runs again
    /// on every template application: a re-applied template rebuilds the card visual, so the
    /// transform and the reveal state must be (re)connected. Idempotency comes from the
    /// <c>-=/+=</c> event pairs and <see cref="DisconnectCardScale"/> — subscriptions are
    /// never stacked.
    /// </summary>
    private void ConnectPanelShell()
    {
        if (PanelShell is { IsTemplateApplied: true } shell)
        {
            connectedShell = shell;
            shell.CloseRequested -= OnShellCloseRequested;
            shell.CloseRequested += OnShellCloseRequested;

            // The card must inherit the opening widget's radius before the glass reads it.
            if (SpawnCornerRadius > 0)
                shell.CornerRadius = new CornerRadius(SpawnCornerRadius);

            InitializeVisualCard();

            appearanceBinder ??= new PanelAppearanceBinder(this, shell, PanelAppearanceResolver.TryResolveSettingsProvider());
            appearanceBinder.Attach();
            return;
        }

        // No (realized) shell: a panel that still declares its own card in AXAML.
        if (!visualCardConnected && PanelCard != null)
            InitializeVisualCard();
    }

    /// <summary>
    /// Hide the card and attach the zoom transform. Called both from InitializePanel (legacy
    /// panels, whose card exists immediately) and from every shell (re)connection.
    /// </summary>
    private void InitializeVisualCard()
    {
        DisconnectCardScale();

        var card = PanelCard;
        if (card == null)
        {
            visualCardConnected = false;
            return;
        }

        if (SpawnCornerRadius > 0 && card is Border cardBorder)
            cardBorder.CornerRadius = new CornerRadius(SpawnCornerRadius);

        card.Opacity = 0.0;
        if (TransformAnimationEnabled)
        {
            cardScale = card.RenderTransform as ScaleTransform ?? new ScaleTransform();
            card.RenderTransform = cardScale;
            cardScale.ScaleX = OpenScaleFrom;
            cardScale.ScaleY = OpenScaleFrom;
            // Every interpolated tick drives the glass with the scale it covers.
            cardScale.PropertyChanged += OnCardScalePropertyChanged;
        }
        else if (card.RenderTransform is ScaleTransform staleScale)
        {
            // Panels that clip themselves with a native window region must not carry
            // the AXAML's decorative zoom transform — it would shrink the card forever.
            staleScale.ScaleX = 1.0;
            staleScale.ScaleY = 1.0;
        }

        ResolveGlassAnimationTargets();
        visualCardConnected = true;

        // A re-applied template rebuilds the card at the template's initial Opacity=0. When the
        // open transition has ALREADY played, reveal the new card immediately instead of leaving
        // a live panel invisible. The guard must be "transition played", not "panel loaded":
        // ConnectPanelShell also runs from OnPanelWindowLoaded, where the card must stay hidden
        // at the spawn scale so the open transition that follows still has something to animate.
        if (openAnimationPlayed)
        {
            // Snap, never re-animate: the open transition has already played, so the
            // reconnected card must sit at its final state immediately. The lingering
            // open-transition setters go first — a still-armed DoubleTransition would
            // swallow these writes and leave the card frozen mid-zoom (detaching the shell
            // kills the card's own compositor animations, but the transform outlives it;
            // a headless run never completes the transition at all).
            card.Transitions = null;
            card.Opacity = 1.0;
            if (TransformAnimationEnabled && cardScale != null)
            {
                cardScale.Transitions = null;
                cardScale.ScaleX = 1.0;
                cardScale.ScaleY = 1.0;
            }
            RequestGlassRender();
        }
    }

    /// <summary>Release the previous card's transform subscription before (re)attaching one.</summary>
    private void DisconnectCardScale()
    {
        if (cardScale == null) return;
        cardScale.PropertyChanged -= OnCardScalePropertyChanged;
        cardScale = null;
    }

    private void OnShellCloseRequested(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        // The single animated close path — never a cast to Window and never a second path.
        Close();
    }

    /// <summary>Ask the rendered glass for a frame now that the surface is really mounted/laid out.</summary>
    private void RequestGlassRender()
    {
        if (glassSurface is { IsVisible: true })
            glassSurface.RequestRender(immediate: true);
    }

    /// <summary>Show + pin above the widget band + activate — the standard way every panel appears.
    /// <see cref="PanelService.Open"/> calls this for the panels it creates, hence the internal reach.</summary>
    protected internal void ShowAsSecondaryPanel(Window? owner)
    {
        if (owner != null)
            Show(owner);
        else
            Show();

        // Secondary panel: above the widget band, below ordinary application windows.
        WidgetZOrder.PinPanelAboveWidgets(this);

        Activate();
    }

    /// <summary>True within <paramref name="cooldownMs"/> after this panel type was last closed (re-open flicker guard).</summary>
    protected static bool PanelCoolingDown<T>(int cooldownMs = ReopenCooldownMs) where T : SecondaryPanelWindow
        => PanelService.IsCoolingDownFor(typeof(T), cooldownMs);

    /// <summary>
    /// Close this panel type's active instance (animated). Returns true when there was one,
    /// in which case the caller must not open a new panel — this is the "toggle" path.
    /// </summary>
    protected static bool TryCloseActivePanel<T>() where T : SecondaryPanelWindow
        => PanelService.TryCloseFor(typeof(T));

    /// <summary>
    /// Drop a panel whose construction failed before it was ever shown. The active-panel map is how
    /// a later open of the same type decides between "toggle" and "create", and a window that never
    /// loaded never fires Closed — without this the failed instance would block the next open as if
    /// a live panel existed.
    /// </summary>
    protected static void AbandonPanel(SecondaryPanelWindow panel)
        => PanelService.AbandonPanel(panel);

    private void OnPanelWindowLoaded(object? sender, RoutedEventArgs e)
    {
        panelLoaded = true;
        LoadedAtUtc = DateTime.UtcNow;

        // The shell's parts (and therefore the animatable card) must exist before the open
        // transition plays — ConnectPanelShell is idempotent, so this is also the safety net for
        // a panel whose template was applied without the TemplateApplied hook being in place yet.
        ConnectPanelShell();

        // 失焦自动切换: panels are desktop furniture too — recognised as such by the focus
        // watcher, and styled with the per-screen light/dark variant of their screen.
        DesktopFurniture.Attach(this);

        PositionPanel();
        ApplyWindowRegion();
        OnPanelLoaded();
        // The surface is mounted and laid out now: ask it for a real frame before the zoom starts.
        RequestGlassRender();
        if (FocusManager?.GetFocusedElement() == null)
        {
            Focus();
        }
        InstallOutsideClickHook();
        PlayOpenAnimation();
    }

    private void OnPanelWindowActivated(object? sender, EventArgs e)
    {
        if (wasActivated) return;
        wasActivated = true;
        activatedAtUtc = DateTime.UtcNow;
    }

    private void OnPanelWindowDeactivated(object? sender, EventArgs e)
    {
        if (closeAnimationRunning || !wasActivated) return;
        if ((DateTime.UtcNow - activatedAtUtc).TotalMilliseconds < DeactivateCloseGraceMs)
        {
            wasActivated = false;
            Dispatcher.UIThread.Post(() =>
            {
                if (!closeAnimationRunning && IsVisible)
                    Activate();
            });
            return;
        }
        Close();
    }

    private void OnPanelWindowClosing(object? sender, WindowClosingEventArgs e)
    {
        UninstallOutsideClickHook();
    }

    /// <inheritdoc />
    /// <remarks>
    /// 失焦自动切换: the panel follows the effective theme of its own screen (the same rule its
    /// opening widget uses); the coordinator pokes this when the focus state changes — the
    /// variant listener in <see cref="PanelAppearanceBinder"/> alone would miss a pure material
    /// swap.
    /// </remarks>
    public void OnFocusThemeChanged()
    {
        appearanceBinder?.Refresh();
        ApplyWindowRegion();
    }

    /// <summary>
    /// Clip the native window to the panel card's rounded rectangle when OS-level Acrylic/Mica blur is used.
    /// In non-native-blur modes (LiquidGlass, Solid), clear the region to allow smooth 32-bit alpha composition.
    /// </summary>
    public virtual void ApplyWindowRegion()
    {
        var handle = TryGetPlatformHandle()?.Handle;
        if (handle == null || handle.Value == IntPtr.Zero) return;

        var theme = PanelAppearanceResolver.ResolveEffectiveTheme(PanelAppearanceResolver.TryResolveSettingsProvider(), this);
        if (theme.UsesNativeBlur)
        {
            var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
            double scaling = screen?.Scaling ?? (DesktopScaling > 0 ? DesktopScaling : 1.0);
            if (scaling <= 0) scaling = 1.0;

            int width = (int)Math.Round(ClientSize.Width * scaling);
            int height = (int)Math.Round(ClientSize.Height * scaling);
            double baseRadius = SpawnCornerRadius > 0
                ? SpawnCornerRadius
                : (connectedShell?.CornerRadius.TopLeft > 0 ? connectedShell.CornerRadius.TopLeft : (PanelCard is Border b ? b.CornerRadius.TopLeft : 18));
            int radius = (int)Math.Round(baseRadius * scaling);

            width = Math.Max(1, width);
            height = Math.Max(1, height);
            radius = Math.Clamp(radius, 0, Math.Min(width, height) / 2);

            InteropService.SetWindowRegion(handle.Value, 0, 0, width, height, radius);
        }
        else
        {
            InteropService.ClearWindowRegion(handle.Value);
        }
    }

    private void OnPanelWindowClosed(object? sender, EventArgs e)
    {
        UninstallOutsideClickHook();

        // Let go of everything that could keep this window alive: the settings/theme
        // subscriptions, the shell events and the rendered glass. The surface's own detach
        // releases the GPU resources; hiding it here matches what each popup used to do in its
        // Closed handler and guarantees nothing paints after the window is gone.
        appearanceBinder?.Detach();
        appearanceBinder = null;

        if (connectedShell != null)
        {
            connectedShell.CloseRequested -= OnShellCloseRequested;
            if (connectedShell.GlassSurface != null)
                connectedShell.GlassSurface.IsVisible = false;
            connectedShell = null;
        }
        DisconnectCardScale();
        visualCardConnected = false;
        openAnimationPlayed = false;
        panelShell = null;

        // Unregister + cooldown recording go through the service, under the panel's current
        // identity key (the legacy type key, or the request key it was rebound to on open).
        PanelService.UnregisterPanel(this);
    }

    private void InstallOutsideClickHook()
    {
        if (!OperatingSystem.IsWindows() || hookHandle != IntPtr.Zero) return;
        try
        {
            mouseHookProc = OutsideClickHookCallback;
            using var curProcess = Process.GetCurrentProcess();
            using var curModule = curProcess.MainModule;
            var hMod = curModule != null ? GetModuleHandle(curModule.ModuleName) : IntPtr.Zero;
            hookHandle = SetWindowsHookEx(WH_MOUSE_LL, mouseHookProc, hMod, 0);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to install outside-click mouse hook: {ex.Message}");
        }
    }

    private void UninstallOutsideClickHook()
    {
        if (hookHandle != IntPtr.Zero)
        {
            try
            {
                UnhookWindowsHookEx(hookHandle);
            }
            catch { }
            hookHandle = IntPtr.Zero;
        }
        mouseHookProc = null;
    }

    private IntPtr OutsideClickHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && !closeAnimationRunning && LoadedAtUtc != DateTime.MinValue
            && (DateTime.UtcNow - LoadedAtUtc).TotalMilliseconds > 150)
        {
            int msg = wParam.ToInt32();
            if (msg is WM_LBUTTONDOWN or WM_RBUTTONDOWN or WM_MBUTTONDOWN
                    or WM_NCLBUTTONDOWN or WM_NCRBUTTONDOWN or WM_NCMBUTTONDOWN)
            {
                var hookStruct = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                var handle = TryGetPlatformHandle()?.Handle;
                if (handle.HasValue && GetWindowRect(handle.Value, out var rect))
                {
                    bool isInside = hookStruct.pt.X >= rect.Left && hookStruct.pt.X <= rect.Right &&
                                    hookStruct.pt.Y >= rect.Top && hookStruct.pt.Y <= rect.Bottom;
                    if (!isInside)
                    {
                        int ptX = hookStruct.pt.X;
                        int ptY = hookStruct.pt.Y;
                        Dispatcher.UIThread.Post(() =>
                        {
                            if (closeAnimationRunning) return;
                            if (IsScreenPointInsideOpenPopup(ptX, ptY)) return;
                            try
                            {
                                Close();
                            }
                            catch { }
                        });
                    }
                }
            }
        }

        return CallNextHookEx(hookHandle, nCode, wParam, lParam);
    }

    private bool IsScreenPointInsideOpenPopup(int x, int y)
    {
        try
        {
            foreach (var popup in this.GetVisualDescendants().OfType<Popup>())
            {
                // Avalonia 12 no longer exposes Popup.Host (IPopupHost is private API);
                // the popup root is the TopLevel that owns the popup's child.
                if (!popup.IsOpen || popup.Child is null) continue;
                if (TopLevel.GetTopLevel(popup.Child) is not TopLevel popupRoot) continue;
                // Inline (overlay) popups resolve to this window, not to a separate popup HWND;
                // treating the whole panel as "inside a popup" would stop outside clicks closing it.
                if (ReferenceEquals(popupRoot, this)) continue;
                var popupHandle = popupRoot.TryGetPlatformHandle()?.Handle;
                if (popupHandle.HasValue && GetWindowRect(popupHandle.Value, out var rect))
                {
                    if (x >= rect.Left && x <= rect.Right && y >= rect.Top && y <= rect.Bottom)
                        return true;
                }
            }
        }
        catch { }
        return false;
    }

    private void OnPanelKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || closeAnimationRunning) return;
        if (OnPanelEscape())
        {
            e.Handled = true;
            return;
        }
        Close();
        e.Handled = true;
    }

    private void PlayOpenAnimation()
    {
        // One open transition per panel: a second call (Loaded plus a late TemplateConnected)
        // would find every value already at its target and animate nothing.
        if (openAnimationPlayed || closeAnimationRunning) return;

        var card = PanelCard;
        if (card == null) return;

        // Pin the scale origin to the opening widget's center (panel position is already
        // final at this point), so the card visibly grows out of the widget. The origin is
        // set while the card is still invisible — a late layout can't cause a visible jump.
        var anchor = ComputeSpawnAnchorOrigin();
        spawnAnchor = anchor;
        ResolveGlassAnimationTargets();
        if (TransformAnimationEnabled && cardScale != null && anchor.HasValue)
            card.RenderTransformOrigin = anchor.Value;

        card.Transitions = new Transitions
        {
            new DoubleTransition { Property = Visual.OpacityProperty, Duration = OpenDuration, Easing = SmoothEasing }
        };

        if (TransformAnimationEnabled && cardScale != null)
        {
            cardScale.Transitions = new Transitions
            {
                new DoubleTransition { Property = ScaleTransform.ScaleXProperty, Duration = OpenDuration, Easing = OpenEasing },
                new DoubleTransition { Property = ScaleTransform.ScaleYProperty, Duration = OpenDuration, Easing = OpenEasing }
            };

            cardScale.ScaleX = 1.0;
            cardScale.ScaleY = 1.0;
        }

        card.Opacity = 1.0;
        openAnimationPlayed = true;
    }

    // ---- Glass transition playback ----

    /// <summary>
    /// Resolve the glass visual the zoom feeds: the shell's explicit <c>PART_GlassSurface</c>
    /// when the panel has one, so a glass control inside the business content is never mistaken
    /// for the shell's surface. Falls back to the first surface for panels that still declare
    /// their own chrome.
    /// </summary>
    private void ResolveGlassAnimationTargets()
    {
        glassSurface = connectedShell?.GlassSurface
                       ?? this.GetVisualDescendants().OfType<LiquidGlassSurface>().FirstOrDefault();
    }

    private void OnCardScalePropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (!panelLoaded) return;
        if (e.Property != ScaleTransform.ScaleXProperty && e.Property != ScaleTransform.ScaleYProperty) return;
        UpdateGlassForScale(cardScale?.ScaleX ?? 1.0);
    }

    /// <summary>Feed the interpolated card scale to the glass surface.</summary>
    private void UpdateGlassForScale(double t)
    {
        var card = PanelCard;
        if (card == null) return;

        if (t >= 0.999)
        {
            glassSurface?.SetAnimationFrameScale(1.0, default);
            return;
        }

        // The surface recomputes its optics for the rect the card currently covers — real glass
        // at animation rate, identically whether or not live wallpaper sampling is on.
        if (glassSurface is { IsVisible: true })
            glassSurface.SetAnimationFrameScale(t, AnchorRelativeToSurface(glassSurface, card));
    }

    /// <summary>The card-space anchor re-expressed inside the glass surface's bounds.</summary>
    private Point AnchorRelativeToSurface(GlassSurfaceBase surface, Visual card)
    {
        var anchor = spawnAnchor ?? new RelativePoint(0.5, 0.5, RelativeUnit.Relative);
        var point = anchor.ToPixels(card.Bounds.Size);
        var topLeft = surface.TranslatePoint(new Point(0, 0), card);
        var size = surface.Bounds.Size;
        if (topLeft == null || size.Width <= 0 || size.Height <= 0) return point;
        return new Point(
            (point.X - topLeft.Value.X) / size.Width,
            (point.Y - topLeft.Value.Y) / size.Height);
    }

    /// <summary>The screen the panel spawns on (primary when the spawn point is unknown).</summary>
    protected Screen? ResolveSpawnScreen()
    {
        if (SpawnScreenCenter.HasValue)
        {
            var spawn = new PixelPoint(
                (int)Math.Round(SpawnScreenCenter.Value.X),
                (int)Math.Round(SpawnScreenCenter.Value.Y));
            return Screens.ScreenFromPoint(spawn) ?? Screens.Primary;
        }
        return Screens.Primary;
    }

    /// <summary>The corner radius the panel's glass is drawn with (the spawn radius, or the panel's own).</summary>
    protected double GlassCardCornerRadius =>
        PanelCard is Border border ? border.CornerRadius.TopLeft : SpawnCornerRadius;

    /// <summary>
    /// The widget's center expressed as a relative origin inside the panel card, so the
    /// scale transition reads as the card growing straight out of the widget (and folding
    /// back into it on close). Falls back to the card's own center when the spawn point is
    /// unknown or the card is not laid out yet. Slightly clamped so a work-area-clamped
    /// panel still folds toward the widget's side rather than from an extreme far-away point.
    /// </summary>
    private RelativePoint? ComputeSpawnAnchorOrigin()
    {
        try
        {
            var spawn = SpawnScreenCenter;
            var card = PanelCard;
            if (spawn == null || card == null) return null;

            var screen = Screens.ScreenFromPoint(new PixelPoint(
                (int)Math.Round(spawn.Value.X),
                (int)Math.Round(spawn.Value.Y)));
            double scale = screen?.Scaling > 0 ? screen.Scaling : 1.0;

            // Spawn and Position are physical pixels; the card measures in DIPs.
            var anchor = new Point(
                (spawn.Value.X - Position.X) / scale,
                (spawn.Value.Y - Position.Y) / scale);
            var topLeft = card.TranslatePoint(new Point(0, 0), this);
            var size = card.Bounds.Size;
            if (topLeft == null || size.Width <= 0 || size.Height <= 0) return null;

            double rx = Math.Clamp((anchor.X - topLeft.Value.X) / size.Width, -0.5, 1.5);
            double ry = Math.Clamp((anchor.Y - topLeft.Value.Y) / size.Height, -0.5, 1.5);
            return new RelativePoint(rx, ry, RelativeUnit.Relative);
        }
        catch
        {
            // The anchored zoom is a nicety, never worth breaking the open transition over:
            // an exception here would leave the card stuck invisible.
            return null;
        }
    }

    /// <summary>
    /// Runs the exit transition first, then really closes. While the exit animation plays,
    /// further Close() calls are ignored so every dismissal path (button, Escape, focus loss,
    /// re-trigger) collapses into one animated close.
    /// </summary>
    /// <remarks>
    /// Window.Close is not virtual in Avalonia, so this hides it: every call site on the
    /// concrete panel types (their own handlers and <see cref="TryCloseActivePanel{T}"/>) binds
    /// to this method, while framework-initiated closes (application shutdown) keep closing
    /// immediately — which is exactly right for shutdown.
    /// </remarks>
    public new void Close() => Close(null);

    public new void Close(object? dialogResult)
    {
        if (closeAnimationRunning) return;
        UninstallOutsideClickHook();

        var card = PanelCard;
        if (card == null || !panelLoaded)
        {
            base.Close(dialogResult);
            return;
        }

        closeAnimationRunning = true;

        card.Transitions = new Transitions
        {
            new DoubleTransition { Property = Visual.OpacityProperty, Duration = CloseDuration, Easing = SmoothEasing }
        };
        card.Opacity = 0.0;

        if (TransformAnimationEnabled && cardScale != null)
        {
            cardScale.Transitions = new Transitions
            {
                new DoubleTransition { Property = ScaleTransform.ScaleXProperty, Duration = CloseDuration, Easing = SmoothEasing },
                new DoubleTransition { Property = ScaleTransform.ScaleYProperty, Duration = CloseDuration, Easing = SmoothEasing }
            };

            cardScale.ScaleX = CloseScaleTo;
            cardScale.ScaleY = CloseScaleTo;
        }

        var timer = new DispatcherTimer { Interval = CloseTimerDuration };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            try { base.Close(dialogResult); } catch { }
        };
        timer.Start();
    }

    private void PositionPanel()
    {
        var spawn = SpawnScreenCenter;
        Screen? screen = null;
        if (spawn.HasValue)
        {
            screen = Screens.ScreenFromPoint(new PixelPoint(
                (int)Math.Round(spawn.Value.X),
                (int)Math.Round(spawn.Value.Y)));
        }
        screen ??= Screens.Primary;
        if (screen == null) return;

        double scale = screen.Scaling > 0 ? screen.Scaling : 1.0;
        double physWidth = Width * scale;
        double physHeight = Height * scale;

        double targetX;
        double targetY;

        if (spawn.HasValue)
        {
            targetX = spawn.Value.X - physWidth / 2.0;
            targetY = spawn.Value.Y - physHeight / 2.0;
        }
        else
        {
            targetX = screen.WorkingArea.X + (screen.WorkingArea.Width - physWidth) / 2.0;
            targetY = screen.WorkingArea.Y + (screen.WorkingArea.Height - physHeight) / 2.0;
        }

        var work = screen.WorkingArea;
        double margin = 16 * scale;
        targetX = Math.Clamp(targetX, work.X + margin, work.X + Math.Max(0, work.Width - physWidth - margin));
        targetY = Math.Clamp(targetY, work.Y + margin, work.Y + Math.Max(0, work.Height - physHeight - margin));

        Position = new PixelPoint((int)Math.Round(targetX), (int)Math.Round(targetY));
    }
}
