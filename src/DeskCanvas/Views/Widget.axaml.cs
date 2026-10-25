using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DeskCanvas.Core.Interfaces;
using DeskCanvas.Core.Models;
using DeskCanvas.Core.Models.Settings;
using DeskCanvas.Contracts;
using DeskCanvas.Locales;
using DeskCanvas.Runtime;
using DeskCanvas.Services;
using DeskCanvas.UI.Animation;
using DeskCanvas.Views.Controls;

namespace DeskCanvas.Views;

public partial class Widget : Window, INotifyPropertyChanged, IFocusThemedWindow
{
    private readonly IWidgetLayoutProvider widgetLayoutProvider;
    private readonly IAppSettingsProvider appSettingsProvider;
    private readonly IGridService<Widget> gridService;
    private readonly ILayoutProvider layoutProvider;
    private readonly DisplayMonitorService displayMonitor;
    private readonly Func<UserControl> userControl;
    private readonly Func<Settings> settingsWindow;
    private readonly Func<EditWidget>? editWidgetWindow;
    private readonly ProfileService profileService;
    private (int Columns, int Rows)? manualSpan;
    private bool isFrameless;

    // 多彩（全彩）卡片的固定 macOS 底色（P2 挂账，待语义键收敛后统一）。与
    // ColorfulResources.WidgetBackground 的 Dark 值同源，但不从资源解析——上面
    // WidgetCardBackground 的多彩分支是文档化的显式回退保证：per-state 多个 surface
    // 字典同时加载时，别的 material 的 WidgetBackground 可能遮蔽共享键。也不可换
    // SolidBackgroundDark/Light——那是用户可调的纯色主题底色（默认 #2E2E2E），值不同。
    private static readonly Color ColorfulCardBackgroundDark = Color.Parse("#1C1C1E");

    /// <summary>Resolved lazily: the widget is also instantiated in contexts without the app
    /// services (design-time / checks), where the 失焦自动切换 material resolution degrades to
    /// the shared theme.</summary>
    private FocusThemeCoordinator? focusCoordinator;
    private FocusThemeCoordinator? FocusCoordinator =>
        focusCoordinator ??= App.Services?.GetService(typeof(FocusThemeCoordinator)) as FocusThemeCoordinator;

    /// <summary>
    /// Reads the shared menu surface radius for Clip / native window region alignment. The XAML
    /// styles own CornerRadius; this value only keeps the acrylic popup's native clipping in sync
    /// when the design token changes. The fallback preserves the existing 12px appearance while
    /// resources are unavailable during early window construction.
    /// </summary>
    private static double ContextMenuCornerRadius
    {
        get
        {
            if (ThemeScopeResolver.TryFindResource<CornerRadius>(null, "HostSurfaceCornerRadius", null, out var radius))
                return radius.TopLeft;

            return 12;
        }
    }

    /// <summary>
    /// Set once this window is being torn down (recreate / close all). While set, the
    /// window must not write to the stored layout any more: by then the layout may
    /// already belong to a different configuration (profile switch), and a write would
    /// re-add this widget to it as a duplicate.
    /// </summary>
    private bool tornDown;
    private bool restoringDisplayMetrics;
    private bool moveCommitPending;

    /// <summary>True between WM_ENTERSIZEMOVE and WM_EXITSIZEMOVE (a native move loop is running).</summary>
    private bool moveLoopActive;

    /// <summary>Persistent ownership, independent of Windows temporarily moving this window.</summary>
    public string ScreenId => widgetLayoutProvider.ScreenId;
    public ScreenLayout? ScreenConfig => layoutProvider.Get().FindById(ScreenId);
    public Avalonia.Platform.Screen? OwningScreen => displayMonitor.FindByConfigId(ScreenId)?.Screen;
    private double ScreenScaling => OwningScreen?.Scaling ?? (DesktopScaling > 0 ? DesktopScaling : 1.0);

    /// <summary>True while the host suspended the widgets (fullscreen application active).</summary>
    private bool suspended;

    /// <summary>
    /// True while the host paused ticking in the freeze-in-place mode (高级设置 → 最大化或全屏时 →
    /// 暂停): the window stays visible, nothing is released, only the updates stop.
    /// </summary>
    private bool ticksPaused;

    /// <summary>The final activity of a tearing-down widget: its display is going away.</summary>
    private static readonly ActivitySnapshot TeardownActivity = new(ActivityReason.DisplayDetached);

    public bool IsFrameless => isFrameless;

    /// <inheritdoc />
    public new event PropertyChangedEventHandler? PropertyChanged;

    private void Notify(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    /// <summary>
    /// Stop this window from observing or writing the stored layout. Called by the
    /// factory right before it closes the window as part of a layout replacement.
    /// </summary>
    public void PrepareForTeardown()
    {
        tornDown = true;
        displayMonitor.ScreensChanged -= RestoreWhenScreenReady;
        widgetLayoutProvider.DataChanged -= OnWidgetLayoutUpdated;
        appSettingsProvider.DataChanged -= OnAppSettingsUpdated;
        layoutProvider.DataChanged -= OnLayoutDataUpdated;
        profileService.ActiveProfileChanged -= OnProfilesChanged;
        profileService.ProfilesListChanged -= OnProfilesChanged;

        // Session teardown (A0): record the detach on the session and drop its registry entry.
        // Both run fire-and-forget inside the registry — the close sequence must never await.
        var instanceId = new WidgetInstanceId(widgetLayoutProvider.Get().InstanceId);
        if (!instanceId.IsEmpty)
        {
            (ContentPresenter.Content as IWidgetActivityConsumer)?.OnSessionActivity(TeardownActivity);
            WidgetSessionRegistry.Remove(instanceId, TeardownActivity);
        }
    }

    /// <summary>
    /// Release the resources the widget content opts to release (pre-rendered material
    /// caches, decoded frames, background render queues). Widgets that do not implement
    /// <see cref="IWidgetSuspendable"/> simply keep their state while hidden.
    /// </summary>
    public void SuspendContent()
    {
        if (suspended) return;
        suspended = true;
        (ContentPresenter.Content as IWidgetSuspendable)?.Suspend();
        ReportSessionActivity(ActivityReason.ScreenCovered);
    }

    /// <summary>Rebuild whatever <see cref="SuspendContent"/> released.</summary>
    public void ResumeContent()
    {
        if (!suspended) return;
        suspended = false;
        (ContentPresenter.Content as IWidgetSuspendable)?.Resume();
        ReportSessionActivity(ActivityReason.DesktopVisible);
    }

    /// <summary>
    /// Freeze-in-place cover handling (高级设置 → 最大化或全屏时 → 暂停): the window keeps
    /// showing its last frame, but the hosted content is told the desktop is covered so its
    /// own timers and data polling gate themselves. Unlike <see cref="SuspendContent"/> nothing
    /// is hidden and no cache is released — the widget is meant to be seen again unchanged the
    /// moment the covering window moves. Widgets that are hidden-suspended are skipped: their
    /// sessions already report the covered reason.
    /// </summary>
    public void PauseTicks()
    {
        if (suspended || ticksPaused) return;
        ticksPaused = true;
        ReportSessionActivity(ActivityReason.ScreenCovered);
    }

    /// <summary>Undo <see cref="PauseTicks"/> (no-op when inactive).</summary>
    public void ResumeTicks()
    {
        if (!ticksPaused) return;
        ticksPaused = false;
        ReportSessionActivity(ActivityReason.DesktopVisible);
    }

    /// <summary>
    /// Push a host activity transition into this widget's session and to the hosted content.
    /// Widgets on legacy layout entries without a stable identity have no session and are
    /// skipped here, exactly like the session-less preview.
    /// </summary>
    private void ReportSessionActivity(ActivityReason reason)
    {
        var instanceId = new WidgetInstanceId(widgetLayoutProvider.Get().InstanceId);
        if (instanceId.IsEmpty) return;
        var snapshot = new ActivitySnapshot(reason);
        WidgetSessionRegistry.ReportActivity(instanceId, snapshot);
        (ContentPresenter.Content as IWidgetActivityConsumer)?.OnSessionActivity(snapshot);
    }

    /// <summary>
    /// Single write path for this widget's stored entry. Saves are dropped once the
    /// window is being torn down (see <see cref="tornDown"/>) — the layout is no longer
    /// ours to modify at that point.
    /// </summary>
    private void SaveLayout(WidgetLayout layout)
    {
        if (tornDown || restoringDisplayMetrics || displayMonitor.IsTopologyChanging) return;
        widgetLayoutProvider.Save(layout);
    }

    public Widget(IAppSettingsProvider appSettingsProvider, IWidgetLayoutProvider widgetLayoutProvider, 
        IGridService<Widget> gridService, ILayoutProvider layoutProvider, DisplayMonitorService displayMonitor,
        ProfileService profileService,
        Func<UserControl> userControl, Func<Settings> settingsWindow, 
        Func<EditWidget>? editWidgetWindow = null)
    {
        this.settingsWindow = settingsWindow;
        this.editWidgetWindow = editWidgetWindow;
        this.widgetLayoutProvider = widgetLayoutProvider;
        this.userControl = userControl;
        this.appSettingsProvider = appSettingsProvider;
        this.gridService = gridService;
        this.layoutProvider = layoutProvider;
        this.displayMonitor = displayMonitor;
        this.profileService = profileService;
        
        InitializeComponent();

        var control = userControl();
        isFrameless = control is IFramelessWidget { IsFrameless: true };
        // Host-owned class markers only (Frameless / Flush). The content inset itself is NOT
        // set here — it comes from the .widget-content-host styles in App.axaml, which the
        // card Border below carries, and which the 组件库 preview carries as well so both
        // hosts inset a widget identically (see WidgetContentHost).
        WidgetContentHost.Prepare(control);
        ContentPresenter.Content = control;
        AttachStackWidget(control);
        
        // The native transparency level is a LOCAL value, not a style: a runtime
        // surface switch then deterministically reconfigures the existing window
        // (style-based switching could leave the OS blur backdrop behind, making
        // a translucent solid card look like frosted glass).
        ApplyTransparencyHint();

        Height = widgetLayoutProvider.Get().Height;
        Width = widgetLayoutProvider.Get().Width;
        Title = $"{widgetLayoutProvider.Get().Type} {widgetLayoutProvider.Get().SubType}";
        DataContext = this;
        
        SetMinMaxSize(false);
        // Text AA follows the card material (see ApplyTextRenderingMode): subpixel over the
        // opaque 多彩 card — the historic "grey mush" guard for 11–12 px labels — grayscale
        // over the translucent 毛玻璃/液态玻璃 materials, whose alpha compositing turns a
        // subpixel mask into red/green fringing.
        ApplyTextRenderingMode();
        UpdateContentSize();
        
        // Manual grid: the span (size steppers / occupancy) is derived from the stored
        // pixel size, but the window size IS the stored size. The grid must not re-derive
        // the size against the current DPI: span × cell ÷ scaling keeps the physical
        // footprint DPI-invariant while the rest of the desktop scales with DPI, so a
        // scaling change (or a restart under a changed one) rewrote every widget's size —
        // the reported "组件放弃原尺寸去适应 DPI" defect. The size only ever changes
        // through the user paths (size steppers, grid editor, cross-screen drag, grid
        // settings) and is re-asserted from storage on topology events.
        manualSpan = GetSpan();
        SetMinMaxSize(true);
        
        Activated += OnActivated;
        Opened += OnOpened;
        Resized += OnResized;
        PointerPressed += OnPointerPressed;
        AddHandler(PointerPressedEvent, OnPreviewPointerPressed, RoutingStrategies.Tunnel);
        // Ctrl + 右键 = 组件菜单（见 OnWidgetContextRequested）。必须走隧道：内容视图自己的
        // 右键菜单在冒泡阶段比宿主更靠近事件源，晚一步就只能看着它先打开。
        AddHandler(ContextRequestedEvent, OnWidgetContextRequested, RoutingStrategies.Tunnel, handledEventsToo: true);
        // handledEventsToo: an inner control that marks the release as handled (Button,
        // ToggleSwitch, a drag-aware view…) used to stop the route before the window and
        // silently skip the position commit — the widget then jumped back to its stored
        // position on the next activation. AfterMove is idempotent, so a second call from
        // the deterministic post-drag commit below costs nothing.
        AddHandler(PointerReleasedEvent, OnPointerReleased, RoutingStrategies.Bubble, handledEventsToo: true);
        // 重叠组件的"中键+滚轮"切页手势 (see OnStackChordWheelChanged): tunnel handlers see
        // every press/release/wheel before the inner controls do, and handledEventsToo keeps
        // the chord alive over TextBoxes and drag-aware children that routinely mark the
        // press handled. The reveal state of the auto-hidden dots rides on the same enter/
        // exit edges.
        AddHandler(PointerPressedEvent, OnStackChordPointerPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(PointerReleasedEvent, OnStackChordPointerReleased, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(PointerWheelChangedEvent, OnStackChordWheelChanged, RoutingStrategies.Tunnel, handledEventsToo: true);
        // Native move-loop bracket (WM_ENTERSIZEMOVE / WM_EXITSIZEMOVE, see OnWidgetWndProc):
        // the deterministic end-of-drag commit rides WM_EXITSIZEMOVE, the message the native
        // loop itself sends when it ends — with the window already resting at the final drop
        // position and before the synthesized mouse-up. Windows-only: the hook is a native
        // wndproc callback; other platforms (and the headless host) have no native move loop
        // and rely on the Background-priority fallback in BeginMoveDragWithCommit.
        if (OperatingSystem.IsWindows())
            Win32Properties.AddWndProcHookCallback(this, OnWidgetWndProc);
        widgetLayoutProvider.DataChanged += OnWidgetLayoutUpdated;
        appSettingsProvider.DataChanged += OnAppSettingsUpdated;
        layoutProvider.DataChanged += OnLayoutDataUpdated;
        profileService.ActiveProfileChanged += OnProfilesChanged;
        profileService.ProfilesListChanged += OnProfilesChanged;
        if (ContextMenu != null)
        {
            // 菜单外壳圆角只由 XAML 样式决定（WidgetContextMenu = 12px，与菜单项高亮同
            // 圆度）。这里曾按组件卡片半径（可达 36+）命令式覆写——样式改了也白改，
            // 每次打开菜单都被写回大圆角，就是"圆角怎么改都不变小"的元凶。代码只保留
            // 裁剪职责：Clip 与原生窗口区域仍需要跟着菜单的圆角走（ContextMenuCornerRadius）。
            ContextMenu.Opened += OnContextMenuOpened;
            ContextMenu.GetObservable(Visual.BoundsProperty).Subscribe(bounds =>
            {
                if (bounds.Width > 0 && bounds.Height > 0)
                {
                    ContextMenu.Clip = new RectangleGeometry(
                        new Rect(0, 0, bounds.Width, bounds.Height),
                        ContextMenuCornerRadius, ContextMenuCornerRadius);
                }
            });
        }
        ActualThemeVariantChanged += OnActualThemeVariantChanged;
        Unloaded += OnUnloaded;
    }

    private void OnOpened(object? sender, EventArgs e)
    {
        // The native window exists now — size the card and clip the OS acrylic backdrop.
        displayMonitor.Attach(this);
        // 失焦自动切换: the widget is desktop furniture — the focus watcher must recognise it
        // as such (clicking a widget never flips the theme), and the coordinator assigns this
        // window's per-screen light/dark variant.
        DesktopFurniture.Attach(this);
        WindowRoles.Register(this, WindowRole.DesktopWidget);
        ApplyTransparencyHint();

        // Restore the stored geometry HERE, not only on activation. A widget added from
        // the 组件库 is opened while the settings window owns the foreground, and this
        // very handler pins it to the bottom band — Windows then routinely never
        // activates it, so the activation path (OnActivated → RefreshDisplayMetrics)
        // never ran: the new window kept the OS cascade position instead of the free
        // cell Gallery.ComputePlacement had picked, so the card showed up outside the
        // grid (upper middle of the screen) and SnapPosition never ran either.
        // Restoring on every open is idempotent — ApplyPosition reads the stored entry,
        // SnapPosition lands on the same cell, and ClampRestoredPosition only pulls a
        // stranded window back onto its own working area.
        RefreshDisplayMetrics();
        if (OwningScreen == null)
        {
            // The owning screen is not resolvable yet (a display change is still settling):
            // RefreshDisplayMetrics bailed out, so the window kept the OS default position —
            // the card shows up in the middle of the screen instead of its stored cell.
            // Restoring on the next activation is what made it jump the moment the user
            // started dragging it; restore as soon as the display set is committed instead.
            // (-= first: Opened fires again every time a hidden widget is shown again.)
            displayMonitor.ScreensChanged -= RestoreWhenScreenReady;
            displayMonitor.ScreensChanged += RestoreWhenScreenReady;
        }

        UpdateContentSize();
        ApplyWidgetRegion();

        // Desktop furniture: the widget lives at the bottom of the z-order for its
        // whole lifetime — clicking, activating, dragging or re-showing it must not
        // raise it above ordinary application windows. Secondary panels (weather
        // forecast & co.) are pinned just above the widget band instead.
        WidgetZOrder.PinWidgetToBottom(this);

        // The window's "loaded" signal: the widget is visible on the desktop (A0 wiring).
        ReportSessionActivity(ActivityReason.DesktopVisible);
    }

    /// <summary>
    /// One-shot companion to the <c>OwningScreen == null</c> branch in <see cref="OnOpened"/>:
    /// while the display set is still settling the widget cannot resolve its screen, so it is
    /// shown wherever the OS puts it. Restore the stored geometry as soon as the screen set is
    /// committed, instead of leaving it to the next activation (which is exactly when the user
    /// grabs the card, so the correction reads as "the card teleports the moment I drag it").
    /// </summary>
    private void RestoreWhenScreenReady(object? sender, EventArgs e)
    {
        if (tornDown || OwningScreen == null) return;
        displayMonitor.ScreensChanged -= RestoreWhenScreenReady;
        RefreshDisplayMetrics();
    }

    private void OnResized(object? sender, WindowResizedEventArgs e)
    {
        if (tornDown || displayMonitor.IsTopologyChanging) return;
        UpdateContentSize();
        ApplyWidgetRegion();
        // Native-frame mode re-runs the resize tail on EVERY frame of a span
        // transition: that persisted the animated intermediate size and fed the
        // post-animation snap with dirty data, which made the final AfterResize
        // actively SetSize back to the previous span. The Resize tail owns the
        // window until pendingSpan clears.
        if (GlassMaterial.UseNativeFrame && pendingSpan == null)
            AfterResize();
    }

    private void OnActivated(object? sender, EventArgs e)
    {
        if (tornDown || displayMonitor.IsTopologyChanging || OwningScreen == null) return;
        RefreshDisplayMetrics();
        InteropService.RemoveWindowFromAltTab(this);
    }

    /// <summary>
    /// Place the window from the stored position. The legacy "primary" entry keeps
    /// absolute coordinates (v1 layout format); per-screen entries store positions
    /// relative to that screen's working-area origin, so re-arranging the desktop
    /// (or replugging at the same spot) restores widgets on the right screen.
    /// </summary>
    private void ApplyPosition()
    {
        if (displayMonitor.IsTopologyChanging || OwningScreen == null) return;
        var settings = widgetLayoutProvider.Get();

        if (ScreenConfig?.Key == null)
        {
            Position = new PixelPoint(settings.X, settings.Y);
            return;
        }

        var workingArea = displayMonitor.FindByConfigId(widgetLayoutProvider.ScreenId)?.Screen.WorkingArea;
        Position = new PixelPoint((workingArea?.X ?? 0) + settings.X, (workingArea?.Y ?? 0) + settings.Y);
    }

    /// <summary>
    /// Snap the window back onto its own screen when it was left stranded: Windows keeps
    /// windows wherever they were when a display disappeared (a remote-control tool's virtual
    /// screen, an unplugged monitor), which can be coordinates that no attached screen covers.
    /// The window then sits on dead desktop space — invisible, unclickable — until it is
    /// repositioned. Called after display-topology changes; a window that is still on any
    /// attached screen is left exactly where it is.
    /// </summary>
    public void EnsureOnScreen()
    {
        if (displayMonitor.Attached.Count == 0) return;

        var scaling = ScreenScaling;
        var topLeft = Position;
        var bottomRight = new PixelPoint(
            Position.X + (int)(Width * scaling),
            Position.Y + (int)(Height * scaling));

        var visible = displayMonitor.Attached.Any(attached =>
            attached.Screen.Bounds.Contains(topLeft) || attached.Screen.Bounds.Contains(bottomRight));
        if (visible) return;

        ApplyPosition();
    }

    /// <summary>
    /// Restore owning display geometry and styling without changing saved layout: the
    /// stored position and the <b>stored size</b> are re-asserted (a scaling change makes
    /// Windows keep the window's physical size, which silently rewrites its DIP size),
    /// then the position is re-snapped to the screen's grid and clamped onto the working
    /// area.
    /// <para>
    /// The size is deliberately NOT re-derived from the grid: span × cell ÷ scaling
    /// would re-fit the widget to the new DPI — abandoning the size the user set — while
    /// every DIP-sized neighbour (fonts, controls, other windows) scales with the DPI.
    /// The widget keeps its stored DIP size across scaling changes and scales physically
    /// like any other desktop window. The grid keeps governing size only through the user
    /// paths (size steppers, grid editor, cross-screen drag, grid settings).
    /// </para>
    /// </summary>
    public void RefreshDisplayMetrics()
    {
        if (tornDown || displayMonitor.IsTopologyChanging || OwningScreen == null) return;
        // A span change from the context menu is still animating: the stored layout
        // still carries the OLD size, so re-asserting it here snaps the window back
        // mid-transition (the 300ms transition re-targets the old size — the reported
        // "size creeps a little then instantly reverts") and the unconditional
        // manualSpan reset below turns the steppers into a no-op afterwards. Skip
        // and let the next poller tick / activation re-run this once it settled.
        if (pendingSpan != null) return;
        restoringDisplayMetrics = true;
        try
        {
            var layout = widgetLayoutProvider.Get();
            ApplyPosition();
            SetMinMaxSize(false);
            Width = layout.Width;
            Height = layout.Height;
            manualSpan = GetSpan();
            gridService.SnapPosition(this);
            ClampRestoredPosition();
            SetMinMaxSize(true);
            RefreshScreenStyle();
        }
        finally
        {
            restoringDisplayMetrics = false;
        }
    }

    private void RefreshScreenStyle()
    {
        Notify(nameof(EffectiveMargin));
        Notify(nameof(EffectiveBaseRadius));
        Notify(nameof(WidgetMargin));
        UpdateOverlayLayout();
        Notify(nameof(ScaleMenuTitle));
        Notify(nameof(SizeMenuTitle));
        Notify(nameof(SizeColumnsValue));
        Notify(nameof(SizeRowsValue));
        Scale();
        UpdateAdaptiveRadiusResources();
        ApplyWidgetRegion();
    }

    private void ClampRestoredPosition()
    {
        if (OwningScreen is not { } screen) return;
        var area = screen.WorkingArea;
        var width = (int) Math.Ceiling(Width * screen.Scaling);
        var height = (int) Math.Ceiling(Height * screen.Scaling);
        // Clamp presentation only: keep the saved relative position for when a
        // reduced desktop returns to its previous resolution or working area.
        Position = new PixelPoint(
            Math.Clamp(Position.X, area.X, area.X + Math.Max(0, area.Width - width)),
            Math.Clamp(Position.Y, area.Y, area.Y + Math.Max(0, area.Height - height)));
    }

    public bool ShowEditButton => editWidgetWindow != null;
    public string Edit => $"{Locale.Widget_Edit} \"{widgetLayoutProvider.Get().Type}\"";

    public bool IsStackWidget =>
        StackWidget != null
        || widgetLayoutProvider.Get().Type is "Stack" or "StackWidgets"
        || widgetLayoutProvider.Get().SubType is "WidgetStackView" or "WidgetStack";

    private static bool IsZh => System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.Equals("zh", StringComparison.OrdinalIgnoreCase);

    public bool ShowNormalEditButton => !IsStackWidget && ShowEditButton;
    public bool ShowStackEditButton => IsStackWidget && ShowEditButton;
    public bool CanEditStackedWidgetChild => StackWidget?.CanEditCurrentChild == true;
    public string EditStackTitle => IsZh ? "编辑重叠组件…" : "Edit Widget Stack…";
    public string EditStackChildTitle => $"{Locale.Widget_Edit} \"{StackWidget?.CurrentChildTitle ?? ""}\"";

    public string Preset4x2StdTitle => IsZh ? "4×2 (标准 100%)" : "4×2 (Standard 100%)";
    public string Preset6x3ScaleTitle => IsZh ? "6×3 (等比放大 150%)" : "6×3 (Scaled 150%)";
    public string Preset8x4ScaleTitle => IsZh ? "8×4 (等比放大 200%)" : "8×4 (Scaled 200%)";
    public string Preset1x1SmallTitle => IsZh ? "1×1 (小 100%)" : "1×1 (Small 100%)";
    public string Preset2x2SquareStdTitle => IsZh ? "2×2 (标准 200%)" : "2×2 (Standard 200%)";
    public string Preset3x3MediumTitle => IsZh ? "3×3 (中 300%)" : "3×3 (Medium 300%)";
    public string Preset4x4LargeTitle => IsZh ? "4×4 (大 400%)" : "4×4 (Large 400%)";
    public string Preset2x2BaseStdTitle => IsZh ? "2×2 (标准 100%)" : "2×2 (Standard 100%)";
    public string Preset3x3ScaleTitle => IsZh ? "3×3 (等比放大 150%)" : "3×3 (Scaled 150%)";
    public string Preset4x4ScaleTitle => IsZh ? "4×4 (等比放大 200%)" : "4×4 (Scaled 200%)";
    public string PresetMap2x2Title => IsZh ? "2×2 (小号方形 200%)" : "2×2 (Small Square 200%)";
    public string PresetMap4x2Title => IsZh ? "4×2 (中号横版 400×200%)" : "4×2 (Medium Wide 400×200%)";
    public string PresetMap4x4Title => IsZh ? "4×4 (大号全景 400×400%)" : "4×4 (Large Panoramic 400×400%)";

    public void EditCurrentStackedWidget()
    {
        StackWidget?.EditCurrentChild(this);
    }

    /// <summary>
    /// Adaptively scale corner radius for widgets of different sizes.
    /// 1x1 small widgets scale to ~55% (iOS app icon style, balanced curvature).
    /// 1xN or Nx1 strip widgets scale to ~72% (prevents excessive rounding on the short edge).
    /// Standard and large widgets (2x2, 4x2, etc.) keep the full configured radius.
    /// </summary>
    private double ResolveEffectiveRadius(double baseRadius)
    {
        if (baseRadius <= 0) return 0;

        int cols = 2, rows = 2;
        if (pendingSpan.HasValue)
        {
            cols = pendingSpan.Value.Columns;
            rows = pendingSpan.Value.Rows;
        }
        else if (manualSpan.HasValue)
        {
            cols = manualSpan.Value.Columns;
            rows = manualSpan.Value.Rows;
        }
        else
        {
            try
            {
                (cols, rows) = GetSpan();
            }
            catch { }
        }

        var width = ClientSize.Width > 0 ? ClientSize.Width : Width;
        var height = ClientSize.Height > 0 ? ClientSize.Height : Height;
        var margin = WidgetMargin.Left;
        var cardW = Math.Max(1, width - 2 * margin);
        var cardH = Math.Max(1, height - 2 * margin);
        var minSide = Math.Min(cardW, cardH);

        // 1x1 small widget (single file, icon tile, or <= 90px square)
        if ((cols <= 1 && rows <= 1) || minSide <= 90)
        {
            return Math.Max(4, Math.Round(baseRadius * 0.55));
        }

        // 1xN or Nx1 strip widget (e.g. 2x1, 4x1, 1x2, 1x4, or short edge <= 125px)
        if (cols <= 1 || rows <= 1 || minSide <= 125)
        {
            return Math.Max(6, Math.Round(baseRadius * 0.72));
        }

        return baseRadius;
    }

    public double EffectiveBaseRadius =>
        ScreenConfig?.Radius
        ?? appSettingsProvider.Get().Dimensions.Radius;

    public CornerRadius Radius => (isFrameless || GlassMaterial.UseNativeFrame)
        ? new(0)
        : new(ResolveEffectiveRadius(EffectiveBaseRadius) / ScreenScaling);

    /// <summary>
    /// Concentric inner corner radius for cards/boxes placed inside the widget (e.g. Translator textboxes, Clipboard item cards).
    /// Follows Apple HIG concentric curvature: R_inner ≈ round(R_outer * 0.70). If outer is square (0), inner is 0.
    /// </summary>
    public CornerRadius InnerRadius
    {
        get
        {
            var r = Radius.TopLeft;
            if (r <= 0) return new CornerRadius(0);
            return new CornerRadius(Math.Max(2, Math.Round(r * 0.70)));
        }
    }

    /// <summary>
    /// Concentric pill / button corner radius for controls placed inside the widget (e.g. language menu pills, action buttons).
    /// </summary>
    public CornerRadius PillRadius
    {
        get
        {
            var r = Radius.TopLeft;
            if (r <= 0) return new CornerRadius(0);
            return new CornerRadius(Math.Max(2, Math.Round(r * 0.40)));
        }
    }

    /// <summary>
    /// Publish dynamic corner radius resources to the widget's ResourceDictionary so child views can bind via DynamicResource.
    /// </summary>
    private void UpdateAdaptiveRadiusResources()
    {
        var cardRadius = Radius;
        var innerRadius = InnerRadius;
        var pillRadius = PillRadius;

        Resources["WidgetCardCornerRadius"] = cardRadius;
        Resources["WidgetInnerCornerRadius"] = innerRadius;
        Resources["WidgetPillCornerRadius"] = pillRadius;

        // 菜单圆角不在此处跟随卡片半径（历史教训：四处命令式覆写让 XAML 的圆角永远
        // 不生效）。菜单的 CornerRadius 只由 WidgetContextMenu 样式决定；打开时的
        // Clip / 原生区域裁剪在 OnContextMenuOpened 里按 ContextMenuCornerRadius 对齐。

        Notify(nameof(Radius));
        Notify(nameof(InnerRadius));
        Notify(nameof(PillRadius));
    }

    /// <summary>
    /// The theme this window renders with. Usually the shared theme; while 失焦自动切换 is
    /// enabled it is the shared parameters with <b>this window's own screen's</b> state
    /// substituted (light/dark pair + optional surface material) — so two screens can show
    /// two materials at once, and a window whose screen cannot be resolved falls back to the
    /// uniform state theme.
    /// </summary>
    public Theme GlassMaterial
    {
        get
        {
            var settings = appSettingsProvider.Get();
            return FocusCoordinator?.ResolveWindowTheme(settings, this) ?? settings.Theme;
        }
    }

    /// <summary>
    /// True when the card is drawn by the app itself from a desktop snapshot
    /// (液态玻璃 / 柔光玻璃), so the <see cref="Controls.LiquidGlassSurface"/> layer is shown
    /// and the plain card background must stay transparent. Frameless widgets render
    /// their own glyph material instead.
    /// </summary>
    public bool ShowsGlassMaterial => !isFrameless && GlassMaterial.UsesRenderedGlass;

    private DeskCanvas.Core.Interfaces.IStackWidget? activeStackWidget;

    /// <summary>The decoration overlay provider for the current content (null = no overlay).</summary>
    private IWidgetOverlayProvider? overlayProvider;

    /// <summary>The overlay control handed over by the provider; one instance per host window.</summary>
    private Control? overlayControl;

    /// <summary>
    /// The hosted content's stack contract (if any). Behavior query only — kept here so the
    /// chord gesture, the edit path and the drop coordinator keep working unchanged for new
    /// providers and legacy adapters alike.
    /// </summary>
    public DeskCanvas.Core.Interfaces.IStackWidget? StackWidget =>
        activeStackWidget ?? (ContentPresenter?.Content as DeskCanvas.Core.Interfaces.IStackWidget);

    private void AttachStackWidget(object? content)
    {
        if (activeStackWidget != null)
        {
            activeStackWidget.IndicatorItemsChanged -= OnStackIndicatorsChanged;
            activeStackWidget = null;
        }

        if (content is DeskCanvas.Core.Interfaces.IStackWidget stack)
        {
            activeStackWidget = stack;
            activeStackWidget.IndicatorItemsChanged += OnStackIndicatorsChanged;
        }

        AttachOverlay();

        Notify(nameof(IsStackWidget));
        Notify(nameof(ShowNormalEditButton));
        Notify(nameof(ShowStackEditButton));
        Notify(nameof(CanEditStackedWidgetChild));
        Notify(nameof(EditStackChildTitle));
    }

    /// <summary>
    /// Wire the generic decoration overlay (W10): the content may hand over a decoration
    /// control through <see cref="IWidgetOverlayProvider"/> (the Stack widget does since its
    /// dots moved into the plugin). Content that only implements the legacy
    /// <c>IStackWidget</c> contract (deployed older DLLs) reaches the same slot through
    /// <see cref="LegacyStackOverlayAdapter"/>, so its pagination dots survive the host-side
    /// cleanup. The overlay control is created once by the provider per host instance —
    /// never here, never per getter access.
    /// </summary>
    private void AttachOverlay()
    {
        OverlayHost.Content = null;
        overlayControl = null;
        overlayProvider = null;

        var content = ContentPresenter?.Content;
        if (content == null) return;

        var provider = content switch
        {
            IWidgetOverlayProvider own => own,
            DeskCanvas.Core.Interfaces.IStackWidget legacy => new LegacyStackOverlayAdapter(legacy),
            _ => null,
        };

        if (provider?.Overlay is not { } overlay) return;

        // One visual child per window: if the control is still rooted in another window,
        // refuse to attach rather than throwing from the ContentPresenter or silently
        // stealing the child.
        if (TopLevel.GetTopLevel(overlay) is Window { } other && !ReferenceEquals(other, this)) return;

        overlayProvider = provider;
        overlayControl = overlay;
        OverlayHost.Content = overlay;
        UpdateOverlayLayout();
    }

    /// <summary>
    /// (Re-)apply the overlay placement from the provider's <see cref="WidgetOverlayHints"/>:
    /// outside-card strips sit in the rightmost margin (1 DIP off the grid line);
    /// inside-card strips (and out-of-card strips the provider refuses to relocate during
    /// native-blur clipping) sit over the card's right edge. Native acrylic moves any
    /// <see cref="WidgetOverlayHints.InsetOnNativeBlur"/> strip inside the card — the historic
    /// StackIndicatorsMargin semantics, now expressed as a contract instead of a host-side
    /// Stack branch. Region merging itself stays here (ApplyWidgetRegion / InteropService).
    /// </summary>
    private void UpdateOverlayLayout()
    {
        if (overlayControl == null) return;

        var theme = GlassMaterial;
        var hints = overlayProvider!.OverlayHints;

        var nativeBlurClip = !isFrameless && theme.UsesNativeBlur && !theme.UseNativeFrame;
        // An out-of-card strip would be clipped/flooded by the native blur region: honour the
        // provider's inset hint, and fall back to the inset when the provider asks for a
        // region merge the single-rect GDI region cannot express.
        var inset = nativeBlurClip && (hints.InsetOnNativeBlur
            || (hints.MergeBoundsIntoWindowRegion && hints.Placement == WidgetOverlayPlacement.OutsideCardRight));
        var inside = inset || hints.Placement == WidgetOverlayPlacement.InsideCardRight;

        OverlayHost.Margin = inside
            ? new Thickness(0, 0, WidgetMargin.Right + 1, 0)
            : new Thickness(0, 0, 1, 0);
        if (!double.IsNaN(hints.HitAreaHeight))
            OverlayHost.MinHeight = hints.HitAreaHeight;

        var width = ClientSize.Width > 0 ? ClientSize.Width : Width;
        var height = ClientSize.Height > 0 ? ClientSize.Height : Height;
        overlayProvider.OnOverlayLayoutChanged(new WidgetOverlayLayoutContext(
            Math.Max(0, width - 2 * WidgetMargin.Left),
            Math.Max(0, height - 2 * WidgetMargin.Top),
            WidgetMargin,
            ScreenScaling,
            NativeBlurActive: nativeBlurClip,
            Frameless: isFrameless,
            InsetApplied: inset));
    }

    private void OnStackIndicatorsChanged(object? sender, EventArgs e)
    {
        try
        {
            Notify(nameof(CanEditStackedWidgetChild));
            Notify(nameof(EditStackChildTitle));
            ApplyWidgetRegion();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Widget] OnStackIndicatorsChanged error: {ex.Message}");
        }
    }

    // ---- 重叠组件的切页手势（中键 + 滚轮） ----

    /// <summary>True while the middle button is held over this widget (the switch chord's held state).</summary>
    private bool stackMiddleButtonChord;

    private void OnStackChordPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (StackWidget == null) return;

        var point = e.GetCurrentPoint(this);
        if (point.Properties.IsMiddleButtonPressed)
            stackMiddleButtonChord = true;
        else if (point.Properties.IsLeftButtonPressed || point.Properties.IsRightButtonPressed)
            stackMiddleButtonChord = false;
    }

    private void OnStackChordPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton == MouseButton.Middle)
            stackMiddleButtonChord = false;
    }

    /// <summary>
    /// Whether the switch chord's button is held right now.
    /// <para>
    /// The live state from the event is the primary signal. The cached flag is only a fallback:
    /// an inner control (Button, ScrollViewer content, a drag-aware child) captures the pointer
    /// on press, and a capture makes the window see a spurious "pointer left" — the cached flag
    /// was cleared there while the middle button was still down, so every subsequent wheel tick
    /// over that control was ignored ("only the non-interactive padding switches pages").
    /// </para>
    /// </summary>
    private bool IsMiddleButtonChordHeld(PointerEventArgs e) =>
        stackMiddleButtonChord || e.GetCurrentPoint(this).Properties.IsMiddleButtonPressed;

    /// <summary>
    /// 中键+滚轮切换重叠组件: hold the middle button anywhere over the widget and roll the
    /// wheel to page through the stack — no aiming for the dots strip required. Runs on the
    /// tunnel route ahead of every inner control, and marks the wheel handled so neither the
    /// stacked children's ScrollViewers nor the dots strip react to the same tick (no double
    /// switch). The dots strip's own wheel handler (inside the decoration overlay) stays in
    /// charge when the middle button is up, exactly as before.
    /// </summary>
    private void OnStackChordWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if (StackWidget is not { } stack || stack.IndicatorItems is not { Count: > 1 } list) return;
        if (!IsMiddleButtonChordHeld(e)) return;

        try
        {
            // Re-arm the cached flag from the live state: a capture-induced exit may have
            // cleared it mid-gesture, and the following wheel ticks should keep working even
            // on platforms that only report the button state on the press.
            if (e.GetCurrentPoint(this).Properties.IsMiddleButtonPressed)
                stackMiddleButtonChord = true;

            if (stack.IsTransitionActive)
            {
                e.Handled = true;
                return;
            }

            var currentIndex = list.FirstOrDefault(i => i.IsActive)?.Index ?? 0;
            var nextIndex = e.Delta.Y > 0
                ? (currentIndex - 1 + list.Count) % list.Count
                : (currentIndex + 1) % list.Count;
            stack.SwitchToIndex(nextIndex);
            e.Handled = true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Widget] OnStackChordWheelChanged error: {ex.Message}");
        }
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        // The chord is a press-and-scroll gesture inside the widget: leaving the window ends it
        // (a release outside would otherwise leave the flag stuck on). Never end it while the
        // middle button is still down — an inner control's pointer capture raises the same
        // "pointer exited" on the window even though the pointer never left the card.
        if (!e.GetCurrentPoint(this).Properties.IsMiddleButtonPressed)
            stackMiddleButtonChord = false;
    }

    /// <summary>
    /// True when the card should draw the specular outline itself instead of leaving it to the
    /// wallpaper-sampled renderer: every material except 液态玻璃, on the rounded card, with no
    /// native frame. 毛玻璃 (OS acrylic) and 全彩 (solid fill) have no shader to light their rim,
    /// so the host overlays the same highlight they used to get from 描边.
    /// </summary>
    public bool ShowsSpecularHighlight =>
        !isFrameless && !IsStackWidget && !GlassMaterial.UsesRenderedGlass && !GlassMaterial.UseNativeFrame;

    /// <summary>Rim strength, the same 0-100 高光 value the liquid-glass shader reads.</summary>
    public double HighlightStrength => GlassMaterial.EffectiveLiquidGlass.Highlight;

    /// <summary>Rim light direction in degrees, shared with the liquid-glass shader.</summary>
    public double HighlightAngle => GlassMaterial.EffectiveLiquidGlass.HighlightAngle;

    /// <summary>
    /// Margin between the widget content and the grid lines (manual grid mode).
    /// </summary>
    public double EffectiveMargin =>
        ScreenConfig?.Margin
        ?? appSettingsProvider.Get().Dimensions.Margin;

    public Thickness WidgetMargin => isFrameless ? new Thickness(0) : new Thickness(EffectiveMargin);

    public IBrush WidgetCardBackground
    {
        get
        {
            if (isFrameless || IsStackWidget) return Brushes.Transparent;
            var theme = GlassMaterial;
            var variant = ActualThemeVariant;
            if (theme.IsColorful)
            {
                // 可选外观提示（W10 §9.1）：内容视图通过 IWidgetSurfaceHints 声明自己的
                // Colorful 卡片画刷资源键，宿主从该组件自己的资源上下文按明暗变体解析；
                // 不再按视图类型名（Forecast/ProgressView…）分支。键解析不到（或内容
                // 未实现接口，包括旧版 DLL）自然退回下方 WidgetBackground 通用回退。
                if (ContentPresenter?.Content is IWidgetSurfaceHints hints
                    && hints.ColorfulBackgroundResourceKey is { Length: > 0 } key
                    && ContentPresenter.Content is Control hintedHost
                    && ThemeScopeResolver.TryFindResource<IBrush>(hintedHost, key, variant, out var hintedBrush)
                    && hintedBrush != null)
                {
                    return hintedBrush;
                }

                // Explicit fallback guarantee for 多彩: pure white (light) / charcoal (dark) —
                // the palette's own WidgetBackground values, computed here rather than read
                // from the resource: with several surface dictionaries loaded at once
                // (per-state materials) another material's WidgetBackground could shadow it.
                return variant == Avalonia.Styling.ThemeVariant.Dark
                    ? new SolidColorBrush(ColorfulCardBackgroundDark)
                    : Brushes.White;
            }

            // Rendered glass (液态玻璃 / 新液态玻璃): the card stays transparent — the
            // LiquidGlassSurface layer draws the material (see ShowsGlassMaterial).
            if (theme.UsesRenderedGlass) return Brushes.Transparent;

            // 毛玻璃 / 纯色: the card is the user's background color coated with the shared
            // opacity. Computed here — instead of the surface dictionaries' WidgetBackground
            // resource — so a per-state surface selection can differ per window (the resource
            // is app-wide and would show whichever dictionary was loaded last).
            var dark = variant == Avalonia.Styling.ThemeVariant.Dark;
            var hex = dark ? theme.EffectiveSolidBackgroundDark : theme.EffectiveSolidBackgroundLight;
            var fallback = dark
                ? DeskCanvas.Core.Models.Settings.Theme.DefaultSolidBackgroundDark
                : DeskCanvas.Core.Models.Settings.Theme.DefaultSolidBackgroundLight;
            return new SolidColorBrush(ThemeResourceHelper.ParseColor(hex, fallback), theme.OpacityLevel);
        }
    }

    /// <summary>
    /// Context-menu size section title: "Grid size", with the current cell span.
    /// </summary>
    public string SizeMenuTitle
    {
        get
        {
            var (columns, rows) = CurrentSpan;
            return $"{Locale.Widget_Size_Grid} · {columns}×{rows}";
        }
    }

    /// <summary>The widget's current cell span for the active grid.</summary>
    public (int Columns, int Rows) CurrentSpan
    {
        get
        {
            // During a preset resize animation the pixel size carries intermediate
            // values — report the target span until it settles.
            if (pendingSpan is { } target) return target;

            manualSpan ??= GetSpan();
            return manualSpan.Value;
        }
    }

    /// <summary>Target span of an in-flight preset resize animation (null when idle).</summary>
    private (int Columns, int Rows)? pendingSpan;

    public WindowDecorations WidgetSystemDecorations => GlassMaterial.UseNativeFrame
        ? WindowDecorations.BorderOnly
        : WindowDecorations.None;

    public bool WidgetExtendClientArea => GlassMaterial.UseNativeFrame;
    public void EditWidget() => editWidgetWindow?.Invoke().Show();

    private IFixedSizeWidget? FixedSizeWidget => ContentPresenter.Content as IFixedSizeWidget;
    public bool IsFixedWidget => FixedSizeWidget != null;
    public bool IsFixed2x1Widget => FixedSizeWidget != null && FixedSizeWidget.AllowedBaseSpans.Contains((4, 2));
    public bool IsFixedSquareWidget => FixedSizeWidget != null && FixedSizeWidget.AllowedBaseSpans.Contains((1, 1));
    public bool IsFixed2x2SquareWidget => FixedSizeWidget != null && FixedSizeWidget.AllowedBaseSpans.Contains((2, 2));
    public bool IsMapWidget => ContentPresenter.Content?.GetType().Name == "MapView";

    public void SetFixedSpanPreset(string spanTag)
    {
        if (spanTag.Split('x') is [string cStr, string rStr]
            && int.TryParse(cStr, out var c) && int.TryParse(rStr, out var r))
        {
            _ = Resize(c, r);
        }
    }

    /// <summary>
    /// Context-menu stepper value: the widget's column span. Setting it resizes
    /// the widget to the new span (the 300 ms transition and the post-animation
    /// snap/lock happen inside <see cref="Resize"/>).
    /// </summary>
    public decimal? SizeColumnsValue
    {
        get => CurrentSpan.Columns;
        set
        {
            var (columns, rows) = CurrentSpan;
            if (value is not { } target) return;
            var next = Math.Clamp((int) Math.Round(target), 1, 999);
            if (next == columns) return;
            if (FixedSizeWidget is { } fixedWidget)
            {
                var isSquareLattice = fixedWidget.AllowedBaseSpans.Contains((1, 1))
                                      || fixedWidget.AllowedBaseSpans.Contains((2, 2));
                var snapped = isSquareLattice
                    ? fixedWidget.SnapSpan(next, next)
                    : fixedWidget.SnapSpan(next, rows);
                if (snapped.Columns == columns && snapped.Rows == rows) return;
                _ = Resize(snapped.Columns, snapped.Rows);
                return;
            }
            _ = Resize(next, rows);
        }
    }

    /// <summary>Context-menu stepper value: the widget's row span (see <see cref="SizeColumnsValue"/>).</summary>
    public decimal? SizeRowsValue
    {
        get => CurrentSpan.Rows;
        set
        {
            var (columns, rows) = CurrentSpan;
            if (value is not { } target) return;
            var next = Math.Clamp((int) Math.Round(target), 1, 999);
            if (next == rows) return;
            if (FixedSizeWidget is { } fixedWidget)
            {
                var isSquareLattice = fixedWidget.AllowedBaseSpans.Contains((1, 1))
                                      || fixedWidget.AllowedBaseSpans.Contains((2, 2));
                var snapped = isSquareLattice
                    ? fixedWidget.SnapSpan(next, next)
                    : fixedWidget.SnapSpan(columns, next);
                if (snapped.Columns == columns && snapped.Rows == rows) return;
                _ = Resize(snapped.Columns, snapped.Rows);
                return;
            }
            _ = Resize(columns, next);
        }
    }

    public void OpenSettings() => settingsWindow.Invoke().ShowAndActivate();

    /// <summary>
    /// Tray icon visibility. The tray menu can hide the icon, so this widget menu item is the
    /// escape hatch that brings it back — hiding it is never a dead end.
    /// </summary>
    public bool TrayIconVisible
    {
        get => appSettingsProvider.Get().ShowTrayIcon;
        set
        {
            var settings = appSettingsProvider.Get();
            if (settings.ShowTrayIcon == value) return;
            appSettingsProvider.Save(settings with { ShowTrayIcon = value });
        }
    }

    private void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (moveCommitPending) AfterMove();
    }

    private void Scale()
    {
        // Content scale only: the card (Border) keeps the size the grid and the
        // widget margin dictate (cell span × cell size − 2×margin). The scale is
        // a render transform on the content inside the card, centered:
        // 1.0 = the content fills the card, 0.5 = half the card (centered),
        // 2.0 = twice the card (clipped by the card's ClipToBounds).
        UpdateContentSize();
        var contentScale = EffectiveContentScale;

        if (Math.Abs(contentScale - 1.0) < 0.001)
        {
            ContentPresenter.RenderTransform = null;
            return;
        }

        ContentPresenter.RenderTransform = new ScaleTransform(contentScale, contentScale);
        ContentPresenter.RenderTransformOrigin = new RelativePoint(0.5, 0.5, RelativeUnit.Relative);
    }

    /// <summary>
    /// The widget's effective content scale: its own choice (context menu),
    /// then the per-screen default, then 1.0.
    /// </summary>
    private double EffectiveContentScale =>
        widgetLayoutProvider.Get().ContentScale
        ?? ScreenConfig?.ContentScale
        ?? 1.0;

    /// <summary>Context-menu scale entry, showing the current effective ratio.</summary>
    public string ScaleMenuTitle => $"{Locale.Widget_Scale} · {EffectiveContentScale:0.##}×";

    /// <summary>Open the free-form scale input dialog for this widget.</summary>
    public void OpenScaleDialog() =>
        new ScaleDialog(EffectiveContentScale, ApplyContentScale).ShowDialog(this);

    private void ApplyContentScale(double? value)
    {
        var settings = widgetLayoutProvider.Get();
        if (settings.ContentScale == value) return;

        SaveLayout(settings with { ContentScale = value });
        Scale();
        Notify(nameof(ScaleMenuTitle));
    }

    private void OnProfilesChanged(object? sender, EventArgs e) => Notify(nameof(ProfileMenuItems));

    private static readonly StreamGeometry CheckmarkGeometry =
        StreamGeometry.Parse("M9 16.17L4.83 12l-1.42 1.41L9 19 21 7l-1.41-1.41z");

    /// <summary>
    /// Dynamic sub-menu items for 1-click profile switching from the context menu.
    /// </summary>
    public IReadOnlyList<Control> ProfileMenuItems
    {
        get
        {
            var list = new List<Control>();
            var profiles = profileService.GetProfiles();
            var active = profileService.GetActiveProfile();

            foreach (var p in profiles)
            {
                var isActive = string.Equals(p, active, StringComparison.OrdinalIgnoreCase);
                var item = new MenuItem
                {
                    Header = p,
                    Icon = isActive ? new PathIcon { Data = CheckmarkGeometry, Width = 12, Height = 12 } : null
                };
                var targetName = p;
                item.Click += (_, _) =>
                {
                    if (!isActive)
                    {
                        profileService.SwitchProfile(targetName);
                    }
                };
                list.Add(item);
            }

            list.Add(new Separator());

            var manageItem = new MenuItem
            {
                Header = Locale.Profiles_Manage ?? "管理方案…"
            };
            manageItem.Click += (_, _) =>
            {
                var win = settingsWindow();
                win.Show();
                win.Activate();
            };
            list.Add(manageItem);

            return list;
        }
    }

    /// <summary>
    /// Size the content presenter to the card's inner area (the grid cell minus the
    /// widget margin). Without an explicit size the presenter measures the widget
    /// view with an unbounded width: views whose layout depends on their width
    /// (e.g. the monitor's left-aligned ring and percentage text) collapse to their
    /// natural content width, so the whole card no longer fills the cell and the
    /// icon position shifts with the text width.
    /// </summary>
    private void UpdateContentSize()
    {
        // ClientSize is only available once the native window exists; before that
        // the window size properties carry the same value (client == window with
        // WindowDecorations.None, which is the widget's normal mode).
        var width = ClientSize.Width > 0 ? ClientSize.Width : Width;
        var height = ClientSize.Height > 0 ? ClientSize.Height : Height;
        if (width <= 0 || height <= 0) return;

        if (isFrameless)
        {
            Border.Width = width;
            Border.Height = height;
            Border.Margin = new Thickness(0);
            Border.BorderThickness = new Thickness(0);
            Border.CornerRadius = new CornerRadius(0);
            Border.BorderBrush = null;
            Border.Background = Brushes.Transparent;

            ContentPresenter.Width = width;
            ContentPresenter.Height = height;
            ContentPresenter.Clip = null;

            UpdateAdaptiveRadiusResources();
            return;
        }

        var margin = WidgetMargin.Left;
        var cardW = Math.Max(1, width - 2 * margin);
        var cardH = Math.Max(1, height - 2 * margin);

        Border.Width = cardW;
        Border.Height = cardH;

        if (IsStackWidget)
        {
            ContentPresenter.Width = cardW;
            ContentPresenter.Height = cardH;
            ContentPresenter.Clip = null;
            UpdateAdaptiveRadiusResources();
            return;
        }

        ContentPresenter.Width = cardW;
        ContentPresenter.Height = cardH;

        UpdateAdaptiveRadiusResources();
        var r = Radius.TopLeft;
        // The rounded clip keeps every ordinary view's artwork inside the card. A view that
        // paints past the edge on purpose (the launchers' hover zoom — see
        // IWidgetSurfaceHints.AllowsCardOverflow) opts out: this clip, not the card Border, is
        // what cut a zoomed icon at the card edge. Under OS blur the native 1-bit region is the
        // real clip and cannot express a bleed, so those materials keep the rounded clip.
        ContentPresenter.Clip = ContentPaintsOutsideCard && !GlassMaterial.UsesNativeBlur
            ? null
            : new RectangleGeometry(new Rect(0, 0, cardW, cardH), r, r);
    }

    /// <summary>
    /// True when the hosted view declared that it draws outside the card's rounded edge
    /// (<see cref="IWidgetSurfaceHints.AllowsCardOverflow"/>). Read from the presenter's content
    /// rather than cached, so a view swapped at runtime is honoured on the next size pass.
    /// </summary>
    private bool ContentPaintsOutsideCard =>
        ContentPresenter?.Content is IWidgetSurfaceHints { AllowsCardOverflow: true };

    /// <summary>
    /// Smallest window that fits the content's measured minimum: the content
    /// DesiredSize already includes the view's own margin, so the window just adds
    /// the card margin. Kept side-effect-free so <c>WidgetAdaptiveChecks</c> can pin
    /// the math via reflection.
    /// </summary>
    internal static (double Width, double Height) ComputeContentMinWindowSize(
        Size contentDesired, Thickness windowMargin, double floor = 48)
    {
        var w = Math.Ceiling(contentDesired.Width + windowMargin.Left + windowMargin.Right);
        var h = Math.Ceiling(contentDesired.Height + windowMargin.Top + windowMargin.Bottom);
        return (Math.Max(floor, w), Math.Max(floor, h));
    }

    private (int x, int y, int cw, int ch, int cr)? lastAppliedRegion;

    /// <summary>
    /// True while a 1-bit rounded region is actually set on the HWND. Deliberately kept
    /// apart from <see cref="lastAppliedRegion"/> (pure geometry bookkeeping): forgetting the
    /// key must never be mistaken for "the native clip is already gone". The old single-field
    /// design made exactly that mistake on a material switch — the key was cleared before
    /// <see cref="ApplyWidgetRegion"/> ran, so its non-clipping branch skipped
    /// <c>ClearWidgetRegion</c> and left the acrylic clip (and the missing bottom rim edge)
    /// on screen until something rebuilt the window.
    /// </summary>
    private bool regionApplied;

    /// <summary>Remove the native clip if one is applied. Cheap and idempotent: it never
    /// touches the HWND when no region is set, so it can be called from any path without
    /// rebuilding the backdrop on every settings save.</summary>
    private void ClearWidgetClip()
    {
        lastAppliedRegion = null;
        if (!regionApplied) return;
        InteropService.ClearWidgetRegion(this);
        regionApplied = false;
    }

    /// <summary>
    /// Clip the native window (and therefore the OS-level acrylic backdrop) to the
    /// card rectangle: the grid cell inset by the widget margin, with the corner
    /// radius. Outside the card the window becomes fully transparent and
    /// click-through; the frosted glass never covers the empty grid cells.
    /// </summary>
    private void ApplyWidgetRegion()
    {
        // Frameless widgets manage their own window region and glyph clipping;
        // never clear or overwrite their region externally.
        if (isFrameless) return;

        // Native frame: no custom clipping.
        if (GlassMaterial.UseNativeFrame)
        {
            ClearWidgetClip();
            return;
        }

        // Only frosted glass (acrylic) needs native window clipping because OS DWM
        // applies acrylic blur to the entire HWND.
        // Liquid glass and solid surfaces do NOT use native blur; their transparent
        // margins and anti-aliased rounded corners composite via 32-bit per-pixel alpha,
        // so applying a 1-bit GDI region truncates the anti-aliased curved edge and creates jaggedness.
        bool needsClipping = GlassMaterial.UsesNativeBlur;

        if (!needsClipping)
        {
            ClearWidgetClip();
            return;
        }

        var scaling = ScreenScaling;
        // The region is a 1-bit GDI mask in whole device pixels. Derive its edges from the
        // card's own device rectangle once: rounding the client size and the margin apart
        // can leave the card's last row/column — exactly where the 1 px rim hairline sits —
        // outside the mask. Floor the near edges and ceil the far ones so the card's
        // anti-aliased boundary pixel is inside the clip as well, and note that
        // InteropService.SetWindowRegion adds the extra row/column CreateRoundRectRgn
        // silently drops — without that the mask ends one pixel above the card's bottom
        // edge and cuts the bottom hairline away (top survives because the near edge is
        // inclusive).
        var left = (int)Math.Floor(WidgetMargin.Left * scaling);
        var top = (int)Math.Floor(WidgetMargin.Top * scaling);
        var right = (int)Math.Ceiling((ClientSize.Width - WidgetMargin.Right) * scaling);
        var bottom = (int)Math.Ceiling((ClientSize.Height - WidgetMargin.Bottom) * scaling);

        var cardWidth = Math.Max(1, right - left);
        var cardHeight = Math.Max(1, bottom - top);
        // Same radius source as the card border/rim: Radius is already expressed in DIP so
        // that Radius × scaling is the configured device-pixel radius.
        var cardRadius = (int)Math.Round(Radius.TopLeft * scaling);

        // The decoration overlay (IWidgetOverlayProvider, e.g. the Stack pagination dots that
        // moved into the plugin) is placed inside the card on this surface per its
        // InsetOnNativeBlur hint — exactly because an out-of-card strip in the native region
        // would flood with the OS blur, and no fill for it works there: a translucent one
        // reads as frosted glass, an opaque one as a slab of a different material (see
        // UpdateOverlayLayout). The card rectangle alone therefore also covers the dots;
        // no extra strip is ever OR-ed in, and the region work stays entirely host-side.
        var key = (left, top, cardWidth, cardHeight, cardRadius);
        if (regionApplied && lastAppliedRegion == key) return;

        // Only remember the geometry when the OS actually took the region: before the HWND
        // exists (early layout passes) the call fails, and caching the key anyway would make
        // every later pass skip the clip until the size happened to change.
        var applied = InteropService.SetWidgetRegion(
            this,
            left,
            top,
            cardWidth,
            cardHeight,
            cardRadius);
        lastAppliedRegion = applied ? key : null;
        regionApplied = applied;
    }

    private void OnAppSettingsUpdated(object sender, AppSettings? oldData, AppSettings newData)
    {
        if (tornDown) return;
        // The tray icon can also be switched from the tray menu itself.
        Notify(nameof(TrayIconVisible));

        // Grid geometry changed → re-apply the grid-driven size and
        // re-snap the position while keeping the cell span.
        if (oldData?.Grid != newData.Grid)
        {
            manualSpan ??= GetSpan();
            var (columns, rows) = manualSpan.Value;

            SetMinMaxSize(false);
            gridService.SetSize(this, columns, rows);
            SetMinMaxSize(true);
            AfterMove();
            AfterResize();
            Notify(nameof(SizeMenuTitle));
        }

        if (oldData?.Dimensions != newData.Dimensions)
        {
            AfterMove();
            AfterResize();
        }

        // Make bound properties reactive so style changes apply immediately
        // (margin from grid lines, corner radius, context menu, and the
        // 高光 specular rim…).
        if (oldData?.Dimensions != newData.Dimensions || oldData?.Theme != newData.Theme)
        {
            RefreshThemeVisuals();
        }
    }

    /// <summary>
    /// The refresh set shared by the manual path (a settings save that touched the theme)
    /// and the focus path (<see cref="OnFocusThemeChanged"/>) — kept as one method so a
    /// focus-state material swap re-resolves exactly what switching the same theme by
    /// hand re-resolves: the native transparency hint, the card brush, the glass layer,
    /// the specular rim, the overlay placement, the content size and the native window region.
    /// </summary>
    private void RefreshThemeVisuals()
    {
        ApplyTransparencyHint();
        ApplyTextRenderingMode();
        Notify(nameof(GlassMaterial));
        Notify(nameof(ShowsGlassMaterial));
        Notify(nameof(WidgetMargin));
        UpdateOverlayLayout();
        Notify(nameof(WidgetCardBackground));
        Notify(nameof(Radius));
        Notify(nameof(InnerRadius));
        Notify(nameof(PillRadius));
        Notify(nameof(SizeMenuTitle));
        Notify(nameof(ShowsSpecularHighlight));
        Notify(nameof(HighlightStrength));
        Notify(nameof(HighlightAngle));
        UpdateContentSize();
        ApplyWidgetRegion();

        // The rim's geometry can be unchanged across a material swap (all the acrylic/solid
        // materials share one 高光 angle), so the layer may still hold the previous surface's
        // raster. Repaint it explicitly once the native clip for the new material is applied.
        SpecularRimLayer?.InvalidateVisual();
    }

    /// <summary>
    /// (Re-)resolve the window's text antialiasing from the live card material, per
    /// <see cref="PanelAppearanceResolver.ResolveTextRenderingMode(DeskCanvas.Core.Models.Settings.Theme)"/>:
    /// the ClearType-class subpixel mask is only correct over opaque pixels, so translucent
    /// materials (毛玻璃 native blur, 液态玻璃 rendered glass) must render grayscale — a subpixel
    /// mask alpha-composited with the live desktop reads as red/green fringing (色散), while the
    /// opaque 多彩 card keeps subpixel, which is what keeps 11–12 px labels crisp. Re-run by
    /// <see cref="RefreshThemeVisuals"/> (settings saves, 失焦自动切换 material swaps) and
    /// <see cref="RefreshFramelessState"/> so a runtime switch re-resolves with the material.
    /// </summary>
    private void ApplyTextRenderingMode()
    {
        // Frameless widgets paint their own glyph geometry (FramelessGlyphGeometry's own SK
        // pipeline, tuned separately) — keep their historic explicit subpixel untouched.
        var mode = isFrameless
            ? TextRenderingMode.SubpixelAntialias
            : PanelAppearanceResolver.ResolveTextRenderingMode(GlassMaterial);
        SurfaceTextRendering.Apply(this, mode);
    }

    /// <summary>
    /// Native window transparency: <see cref="WindowTransparencyLevel.AcrylicBlur"/>
    /// for glass surfaces (OS-level live blur, per-frame desktop sampling) and
    /// <see cref="WindowTransparencyLevel.Transparent"/> for solid surfaces
    /// (per-pixel alpha, no blur — the opacity slider blends the card with the
    /// desktop without a frosted look). Applied as a local value so runtime
    /// surface switches always reconfigure the existing native window.
    /// </summary>
    private void ApplyTransparencyHint()
    {
        if (isFrameless) return;

        WindowTransparencyLevel[] target = GlassMaterial.UsesNativeBlur
            ? [WindowTransparencyLevel.AcrylicBlur]
            : [WindowTransparencyLevel.Transparent];

        if (TransparencyLevelHint == null || !TransparencyLevelHint.SequenceEqual(target))
        {
            TransparencyLevelHint = target;

            // The hint swap reconfigures the native backdrop, so the native clip is removed
            // and the remembered key dropped: ApplyWidgetRegion re-applies it when the new
            // material still needs one. For 毛玻璃 → 全彩 / 液态玻璃 the removal is the actual
            // fix — leaving the old 1-bit rounded region on the HWND kept cutting the card
            // (and its bottom/right rim hairline) until something rebuilt the window.
            ClearWidgetClip();
        }
    }

    private void SetMinMaxSize(bool lockSize)
    {
        // Grid-driven size: lock to the current size
        // (unlock temporarily while the span changes via the context menu).
        if (lockSize)
        {
            MinWidth = MaxWidth = Width;
            MinHeight = MaxHeight = Height;
        }
        else
        {
            MinWidth = MinHeight = 1;
            MaxWidth = MaxHeight = double.PositiveInfinity;
        }
    }

    /// <summary>
    /// Refresh the profile entries of the context menu right before it opens. Wired as a named
    /// handler (not a lambda) so it can be detached on unload — a lambda subscribed to the
    /// window's own context menu would root the closed window through it.
    private void OnContextMenuOpened(object? sender, RoutedEventArgs e)
    {
        Notify(nameof(ProfileMenuItems));
        Notify(nameof(IsStackWidget));
        Notify(nameof(ShowNormalEditButton));
        Notify(nameof(ShowStackEditButton));
        Notify(nameof(CanEditStackedWidgetChild));
        Notify(nameof(EditStackTitle));
        Notify(nameof(EditStackChildTitle));
        Notify(nameof(Edit));
        Notify(nameof(SizeMenuTitle));
        Notify(nameof(ScaleMenuTitle));
        Notify(nameof(Preset4x2StdTitle));
        Notify(nameof(Preset6x3ScaleTitle));
        Notify(nameof(Preset8x4ScaleTitle));
        Notify(nameof(Preset1x1SmallTitle));
        Notify(nameof(Preset2x2SquareStdTitle));
        Notify(nameof(Preset3x3MediumTitle));
        Notify(nameof(Preset4x4LargeTitle));
        Notify(nameof(Preset2x2BaseStdTitle));
        Notify(nameof(Preset3x3ScaleTitle));
        Notify(nameof(Preset4x4ScaleTitle));
        Notify(nameof(PresetMap2x2Title));
        Notify(nameof(PresetMap4x2Title));
        Notify(nameof(PresetMap4x4Title));
        if (sender is ContextMenu cm)
        {
            // 圆角完全交给 XAML 样式（见 WidgetContextMenu）——此前这里按组件卡片
            // 半径覆写 CornerRadius，正是样式修改永远不生效的根因。代码只负责两件样式
            // 做不到的事：毛玻璃 Popup 的原生窗口区域按同一 ContextMenuCornerRadius 圆角裁剪，以及 Clip
            // 兜底（亚克力模糊背景下菜单内容的圆角裁切）。
            if (cm.Bounds.Width > 0 && cm.Bounds.Height > 0)
            {
                cm.Clip = new RectangleGeometry(
                    new Rect(0, 0, cm.Bounds.Width, cm.Bounds.Height),
                    ContextMenuCornerRadius, ContextMenuCornerRadius);
            }

            Dispatcher.UIThread.Post(() =>
            {
                if (cm.Bounds.Width > 0 && cm.Bounds.Height > 0)
                {
                    cm.Clip = new RectangleGeometry(
                        new Rect(0, 0, cm.Bounds.Width, cm.Bounds.Height),
                        ContextMenuCornerRadius, ContextMenuCornerRadius);
                }

                if (TopLevel.GetTopLevel(cm) is WindowBase wb)
                {
                    var handle = wb.TryGetPlatformHandle()?.Handle;
                    if (handle.HasValue && handle.Value != IntPtr.Zero)
                    {
                        InteropService.DisableWindowBorder(handle.Value);
                        var scaling = wb.DesktopScaling;
                        var w = (int)Math.Round(wb.ClientSize.Width * scaling);
                        var h = (int)Math.Round(wb.ClientSize.Height * scaling);
                        var radiusPx = (int)Math.Round(ContextMenuCornerRadius * scaling);
                        InteropService.SetWindowRegion(handle.Value, 0, 0, w, h, radiusPx);
                    }
                }
            }, DispatcherPriority.Render);
        }
    }

    private void OnUnloaded(object? sender, RoutedEventArgs e)
    {
        PointerPressed -= OnPointerPressed;
        RemoveHandler(PointerPressedEvent, OnPreviewPointerPressed);
        RemoveHandler(PointerReleasedEvent, OnPointerReleased);
        RemoveHandler(PointerPressedEvent, OnStackChordPointerPressed);
        RemoveHandler(PointerReleasedEvent, OnStackChordPointerReleased);
        RemoveHandler(PointerWheelChangedEvent, OnStackChordWheelChanged);
        Resized -= OnResized;
        Activated -= OnActivated;
        Opened -= OnOpened;
        Unloaded -= OnUnloaded;
        if (ContextMenu != null)
            ContextMenu.Opened -= OnContextMenuOpened;
        widgetLayoutProvider.DataChanged -= OnWidgetLayoutUpdated;
        appSettingsProvider.DataChanged -= OnAppSettingsUpdated;
        layoutProvider.DataChanged -= OnLayoutDataUpdated;
        profileService.ActiveProfileChanged -= OnProfilesChanged;
        profileService.ProfilesListChanged -= OnProfilesChanged;
        ActualThemeVariantChanged -= OnActualThemeVariantChanged;
        AttachStackWidget(null);

        // Off the desktop (A0 wiring). On the normal close path the session entry was already
        // released by PrepareForTeardown, so this report is a no-op; it only matters for a
        // window that leaves the visual tree without the teardown pass.
        ReportSessionActivity(ActivityReason.TrayHidden);
    }

    private void OnActualThemeVariantChanged(object? sender, EventArgs e)
    {
        Notify(nameof(WidgetCardBackground));
    }

    /// <inheritdoc />
    /// <remarks>
    /// Called by <see cref="FocusThemeCoordinator"/> when the 失焦自动切换 state changes this
    /// window's effective theme — the light/dark variant may stay put while the surface
    /// material is swapped, so the material paths are re-resolved here (the same set the
    /// shared-theme path notifies). Content that resolves its own colours (the Stack widget)
    /// is poked through <see cref="IFocusThemedContent"/>.
    /// </remarks>
    public void OnFocusThemeChanged()
    {
        if (tornDown) return;

        RefreshThemeVisuals();
        (ContentPresenter.Content as IFocusThemedContent)?.OnFocusThemeChanged();
    }

    private void OnWidgetLayoutUpdated(object? sender, WidgetLayout? oldLayout, WidgetLayout newLayout)
    {
        // While a context-menu resize is animating this callback fires from this
        // window's own intermediate SaveLayout (the provider does not dedupe saves):
        // writing the stored size back re-targets the running transition and the
        // nested AfterResize re-persists the intermediate value — a feedback loop
        // that stalls the animation at a half-way size. The Resize tail owns the
        // window until pendingSpan clears and unifies the stored entry then.
        if (oldLayout != null && pendingSpan == null
            && (Math.Abs(oldLayout.Width - newLayout.Width) > 0.5 || Math.Abs(oldLayout.Height - newLayout.Height) > 0.5))
        {
            Width = newLayout.Width;
            Height = newLayout.Height;
            AfterResize();
        }

        if (!Equals(oldLayout?.Settings, newLayout.Settings))
        {
            // Views that manage their own state (Reminders, Notes) refresh in
            // place so the editing session survives their own saves and the
            // per-widget settings dialog. Stateless views over their model are
            // recreated, which is also how they pick up external changes.
            if (ContentPresenter.Content is IWidgetSelfRefreshing selfRefreshing)
                selfRefreshing.Refresh(newLayout);
            else
            {
                var newCtrl = userControl();
                ContentPresenter.Content = newCtrl;
                AttachStackWidget(newCtrl);
            }

            RefreshFramelessState();
        }

        if (oldLayout?.ContentScale != newLayout.ContentScale)
        {
            Scale();
            Notify(nameof(ScaleMenuTitle));
        }
    }

    /// <summary>
    /// Re-evaluate whether the hosted control is in frameless mode after its settings
    /// refresh, updating the card border, glass surface, margins and native window region.
    /// </summary>
    public void RefreshFramelessState()
    {
        if (ContentPresenter.Content is UserControl ctrl)
            WidgetContentHost.Prepare(ctrl);

        var next = ContentPresenter.Content is IFramelessWidget { IsFrameless: true };
        if (isFrameless == next) return;
        isFrameless = next;

        if (isFrameless) ClearWidgetClip();

        if (isFrameless)
        {
            TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        }
        else
        {
            ApplyTransparencyHint();
            Border.ClearValue(Border.MarginProperty);
            Border.ClearValue(Border.BorderThicknessProperty);
            Border.ClearValue(Border.CornerRadiusProperty);
            Border.ClearValue(Border.BorderBrushProperty);
            Border.ClearValue(Border.BackgroundProperty);
        }

        // The frameless flip changes which pipeline paints the card, so the text AA the
        // constructor resolved for the old mode no longer matches the material.
        ApplyTextRenderingMode();

        Notify(nameof(IsFrameless));
        Notify(nameof(ShowsGlassMaterial));
        Notify(nameof(WidgetMargin));
        UpdateOverlayLayout();
        Notify(nameof(WidgetCardBackground));
        Notify(nameof(Radius));
        Notify(nameof(InnerRadius));
        Notify(nameof(PillRadius));
        Notify(nameof(ShowsSpecularHighlight));
        Notify(nameof(HighlightStrength));
        Notify(nameof(HighlightAngle));
        UpdateContentSize();
        ApplyWidgetRegion();
    }

    [DllImport("user32.dll")]
    private static extern short GetKeyState(int nVirtKey);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    private void OnPreviewPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var point = e.GetCurrentPoint(this);

        // Ctrl + 右键 的修饰键状态只能在这一刻取到（ContextRequestedEventArgs 不带修饰键），
        // 记下来给 OnWidgetContextRequested 用：右键按下必然先于上下文请求（松开时）到达。
        if (point.Properties.IsRightButtonPressed)
        {
            contextRequestWithControl = IsControlModifier(e.KeyModifiers);
            return;
        }

        if (!point.Properties.IsLeftButtonPressed) return;

        if (!IsControlModifier(e.KeyModifiers)) return;

        if (appSettingsProvider.Get().Layout.LockPosition) return;

        // Ignore drag if the click landed in the outer grid margin
        var margin = WidgetMargin;
        var p = e.GetPosition(this);
        var card = new Rect(margin.Left, margin.Top,
            Math.Max(0, (ClientSize.Width > 0 ? ClientSize.Width : Width) - margin.Left - margin.Right),
            Math.Max(0, (ClientSize.Height > 0 ? ClientSize.Height : Height) - margin.Top - margin.Bottom));
        if (!card.Contains(p)) return;

        ToolTip.SetIsOpen(this, false);
        e.Handled = true;
        BeginMoveDragWithCommit(e);
    }

    /// <summary>
    /// 最近一次右键按下时 Ctrl 是否按着。组件窗口从不获得焦点（ShowActivated=False），
    /// 键盘事件到不了它，所以只能在指针事件里顺手记下修饰键状态。
    /// </summary>
    private bool contextRequestWithControl;

    /// <summary>
    /// Ctrl + 右键：忽略组件交互，直接打开宿主（组件）菜单——与 Ctrl + 左键拖动同一套修饰键
    /// 语义（「按住 Ctrl，这一下不算组件的」）。
    /// <para>
    /// 为什么需要这条通道：内容视图可以自带右键菜单（单链接组件就有一个），而
    /// <see cref="InputElement.ContextRequested"/> 是冒泡事件——内容比宿主更靠近事件源，
    /// 先一步打开自己的菜单并标记 Handled，宿主的菜单便再也不会出现。图标比例调到最大时，
    /// 卡片上几乎每一寸都是内容，于是「编辑 / 尺寸 / 缩放 / 移除 / 设置」这一层彻底够不到。
    /// 这里在隧道阶段抢先处理：Ctrl 按住时吞掉这次上下文请求，改开宿主菜单，内容菜单连
    /// 打开的机会都没有。不按 Ctrl 时原样放行（内容菜单照旧）。
    /// </para>
    /// </summary>
    private void OnWidgetContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (ContextMenu is not { } menu) return;

        var withControl = contextRequestWithControl || IsControlKeyHeld();
        contextRequestWithControl = false;
        if (!withControl) return;

        e.Handled = true;
        if (menu.IsOpen) return;

        try
        {
            // PlacementTarget 固定成窗口本身：默认路径会把目标设成 e.Source（内容里的
            // 某个控件），Pointer 定位下虽无差别，但菜单重开时留着旧锚点会飘。
            menu.PlacementTarget = this;
            menu.Open(this);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Widget] Ctrl+右键 打开组件菜单失败: {ex.Message}");
        }
    }

    /// <summary>Ctrl 是否按着：优先取指针事件自带的修饰键，其次问系统（Windows）。</summary>
    private static bool IsControlModifier(KeyModifiers modifiers) =>
        modifiers.HasFlag(KeyModifiers.Control)
        || (OperatingSystem.IsWindows() && (GetKeyState(0x11) & 0x8000) != 0);

    /// <summary>
    /// Ctrl 是否真的按着。<see cref="ContextRequestedEventArgs"/> 不带修饰键状态，而组件窗口
    /// 从不获得焦点（ShowActivated=False），键盘事件也到不了——只能问系统，与 Ctrl + 左键
    /// 拖动那条路径用的是同一个兜底。
    /// </summary>
    private static bool IsControlKeyHeld() =>
        OperatingSystem.IsWindows() && (GetKeyState(0x11) & 0x8000) != 0;

    public void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (appSettingsProvider.Get().Layout.LockPosition) return;

        // 重叠组件上中键是"按住+滚轮切换"手势的按住键：绝不能让它进入窗口移动的模态
        // 循环，否则后续滚轮事件会被移动循环吞掉，切页手势失效。（非重叠组件保持原行为。）
        if (StackWidget != null && e.GetCurrentPoint(this).Properties.IsMiddleButtonPressed) return;

        // Ignore drag if the click landed in the outer grid margin
        var margin = WidgetMargin;
        var p = e.GetPosition(this);
        var card = new Rect(margin.Left, margin.Top,
            Math.Max(0, (ClientSize.Width > 0 ? ClientSize.Width : Width) - margin.Left - margin.Right),
            Math.Max(0, (ClientSize.Height > 0 ? ClientSize.Height : Height) - margin.Top - margin.Bottom));
        if (!card.Contains(p)) return;

        ToolTip.SetIsOpen(this, false);
        if (e.GetCurrentPoint(this).Properties.IsRightButtonPressed) return;

        BeginMoveDragWithCommit(e);
    }

    /// <summary>
    /// Start the native move drag and commit the position deterministically when it ends.
    /// <para>
    /// Avalonia runs the native move loop inside a posted Send-priority callback (see
    /// <c>WindowImpl.BeginMoveDrag</c>), which swallows the physical mouse-up — the
    /// <see cref="OnPointerReleased"/> that is supposed to run <see cref="AfterMove"/> is
    /// synthesized afterwards at client point (0,0) and only fires if nothing in the tree
    /// handles it and the legacy mouse pipeline delivers it. The commit therefore rides
    /// <see cref="WmExitSizeMove"/> (see <see cref="OnWidgetWndProc"/>): the message the
    /// native loop itself sends when it ends, with the window already resting at the final
    /// drop position. The Background-priority job below is only a fallback for drags that
    /// never enter the loop at all — while the loop IS active it must stay inert
    /// (<see cref="CommitPendingMoveAfterLoop"/>): Background work runs whenever Win32
    /// input goes quiet, i.e. exactly when the user pauses mid-drag to aim at a grid cell;
    /// committing there consumed the pending flag and the release silently skipped the
    /// grid snap, leaving the widget where it was dropped until the next click
    /// re-committed it.
    /// </para>
    /// </summary>
    private void BeginMoveDragWithCommit(PointerPressedEventArgs e)
    {
        moveCommitPending = true;
        BeginMoveDrag(e);
        Dispatcher.UIThread.Post(CommitPendingMoveAfterLoop, DispatcherPriority.Background);
    }

    private const uint WmEnterSizeMove = 0x0231;
    private const uint WmExitSizeMove = 0x0232;

    /// <summary>
    /// Native window hook: WM_ENTERSIZEMOVE / WM_EXITSIZEMOVE bracket the native move
    /// loop, and the exit message is the one deterministic end-of-drag signal — sent by
    /// the loop itself, with the window already at the final drop position, before the
    /// synthesized mouse-up. Never marks the message handled: Avalonia's own processing
    /// of the bracket must stay unaffected.
    /// </summary>
    private IntPtr OnWidgetWndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmEnterSizeMove)
        {
            moveLoopActive = true;
        }
        else if (msg == WmExitSizeMove)
        {
            moveLoopActive = false;
            if (moveCommitPending) AfterMove();
        }
        return IntPtr.Zero;
    }

    /// <summary>
    /// Fallback commit for drags that never enter the native move loop (BeginMoveDrag
    /// failing, or a host without WM_ENTERSIZEMOVE/WM_EXITSIZEMOVE). While the loop is
    /// active this stays inert: Background-priority dispatcher work runs whenever Win32
    /// input goes quiet — exactly when the user pauses mid-drag to aim at a grid cell —
    /// and consuming the flag there left the release without a commit.
    /// </summary>
    internal void CommitPendingMoveAfterLoop()
    {
        if (!moveCommitPending || moveLoopActive) return;
        AfterMove();
    }

    private void AfterMove()
    {
        displayMonitor.Refresh();
        if (tornDown || restoringDisplayMetrics || displayMonitor.IsTopologyChanging || OwningScreen == null)
        {
            moveCommitPending = false;
            return;
        }
        var userMove = moveCommitPending;
        moveCommitPending = false;
        // If a Stack edit window is open, check if this desktop widget was dropped into it
        if (userMove && StackDropCoordinator.IsActive && !IsStackWidget)
        {
            POINT pt = default;
            var cursorValid = OperatingSystem.IsWindows() && GetCursorPos(out pt);
            var cursorPos = cursorValid ? new PixelPoint(pt.X, pt.Y) : (PixelPoint?)null;
            var scaling = ScreenScaling;
            var centerPos = new PixelPoint(Position.X + (int)(ClientSize.Width * scaling / 2),
                                           Position.Y + (int)(ClientSize.Height * scaling / 2));

            if ((cursorPos.HasValue && StackDropCoordinator.ContainsScreenPoint(cursorPos.Value))
                || StackDropCoordinator.ContainsScreenPoint(centerPos))
            {
                var layout = widgetLayoutProvider.Get();
                if (layout.Type is not "Stack" and not "StackWidgets" && StackDropCoordinator.TryAccept(layout))
                {
                    Remove();
                    return;
                }
            }
        }

        // Cross-screen move: capture the span against the ORIGINAL screen before
        // ownership transfers (after the transfer the stored size would be read
        // against the new cell and become ambiguous, e.g. 2×2 → 1×1).
        var owning = displayMonitor.FindByConfigId(widgetLayoutProvider.ScreenId);
        var current = displayMonitor.Find(this);
        var movedToAnotherScreen = userMove && current != null && owning != null && owning.Screen.Bounds != current.Screen.Bounds;
        // owning is not null whenever movedToAnotherScreen is true (see the condition above).
        var span = movedToAnotherScreen ? (manualSpan ?? ResolveSpanFor(owning!)) : (Columns: 1, Rows: 1);
        
        if (movedToAnotherScreen) TransferOwnership();
        
        if (movedToAnotherScreen)
        {
            manualSpan = span;
            SetMinMaxSize(false);
            gridService.SetSize(this, span.Columns, span.Rows);
            SetMinMaxSize(true);
        }

        gridService.SnapPosition(this);
        if (movedToAnotherScreen) RefreshScreenStyle();
        
        var positioned = StorePosition(widgetLayoutProvider.Get());
        if (movedToAnotherScreen)
            positioned = positioned with { Width = (int) Math.Round(Width), Height = (int) Math.Round(Height) };
        SaveLayout(positioned);
    }

    /// <summary>
    /// If the widget was dragged onto another screen, transfer its entry to that
    /// screen's configuration (its layout entry moves, positions become relative
    /// to the new screen's working area, and the widget's <see cref="IWidgetLayoutProvider.ScreenId"/>
    /// is rebound so every subsequent save lands in the right screen).
    /// </summary>
    private void TransferOwnership()
    {
        var current = displayMonitor.Find(this);
        if (current == null) return;

        var owning = displayMonitor.FindByConfigId(widgetLayoutProvider.ScreenId);
        if (owning != null && owning.Screen.Bounds == current.Screen.Bounds) return;

        var matched = current.Config ?? displayMonitor.EnsureConfig(current);
        var screens = layoutProvider.Get();
        var config = screens.FindById(matched.Id) ?? matched;
        var oldConfig = screens.FindById(widgetLayoutProvider.ScreenId);
        var widget = widgetLayoutProvider.Get();

        // Removal must go through the identity index: `item != widget` relies on record
        // value equality, which compares the Settings JsonElement by document reference
        // and therefore never matches an entry that was re-read from disk. The stale
        // entry then stayed behind AND a copy was appended to the target screen — one
        // widget rendered twice.
        if (oldConfig != null)
            screens = screens.WithScreen(oldConfig with
            {
                Layout = RemoveByIdentity(oldConfig.Layout, widget)
            });

        screens = screens.UpsertScreen(config with
        {
            Layout = [.. RemoveByIdentity(config.Layout, widget), widget]
        });

        restoringDisplayMetrics = true;
        try
        {
            widgetLayoutProvider.ScreenId = config.Id;
            layoutProvider.Save(screens);
        }
        finally
        {
            restoringDisplayMetrics = false;
        }
    }

    /// <summary>Remove this widget's entry (by reference, then by identity) from a list.</summary>
    private static List<WidgetLayout> RemoveByIdentity(List<WidgetLayout> layout, WidgetLayout widget)
    {
        var index = WidgetLayout.IndexOfIdentity(layout, widget);
        return index < 0 ? layout : [.. layout.Where((_, i) => i != index)];
    }

    /// <summary>
    /// Store the window position: absolute for the legacy "primary" entry (v1
    /// layout format), relative to the owning screen's working-area origin for
    /// per-screen entries — so display re-arrangement or replugging a screen in
    /// the same spot restores each widget on its own screen.
    /// </summary>
    private WidgetLayout StorePosition(WidgetLayout settings)
    {
        if (ScreenConfig?.Key == null)
            return settings with { X = Position.X, Y = Position.Y };

        var workingArea = displayMonitor.FindByConfigId(widgetLayoutProvider.ScreenId)?.Screen.WorkingArea;
        return settings with
        {
            X = Position.X - (workingArea?.X ?? 0),
            Y = Position.Y - (workingArea?.Y ?? 0)
        };
    }

    private async Task Resize(int columns, int rows)
    {
        // Remember the target span while the transition runs: the animated Width/
        // Height carry intermediate values, and both the snap/lock below and the
        // children's tier resolution (SizeTiers) must see the TARGET, not the
        // mid-animation size (locking to the animated value previously made the
        // resize snap back to the old span, e.g. M → stays 2×2).
        pendingSpan = (columns, rows);
        manualSpan = (columns, rows);
        SetMinMaxSize(false);
        // The span change rides the mod's bounce spring (动效交接 §6): a restrained ~3.8%
        // overshoot before the window settles on the target span. 亚克力 re-cuts the region on
        // every step of this window-level resize; the overshoot adds no new steps beyond what
        // the old linear 300 ms already cost.
        var spanEasing = SpringMotion.CreateEasing(SpringMotion.Bounce);
        Transitions = new Transitions
        {
            new DoubleTransition { Property = WidthProperty, Duration = TimeSpan.FromMilliseconds(300), Easing = spanEasing },
            new DoubleTransition { Property = HeightProperty, Duration = TimeSpan.FromMilliseconds(300), Easing = spanEasing }
        };
        gridService.SetSize(this, columns, rows);
        await Task.Delay(320);
        Transitions = null;
        // Force the window onto the target size before any bookkeeping. The
        // transition must not be trusted to have delivered: a headless probe
        // under Avalonia 12.1.4 shows the interpolation can strand the effective
        // value a fraction above the START size (353→354, the reported "creep")
        // and that clearing Transitions does NOT reliably fall back to the local
        // value when the platform resize round-trip has been re-writing Width
        // during the animation. Re-asserting the span size after the collection
        // is cleared lands exactly on target in both environments (verified by
        // tests/_resizetransprobe) and is a no-op when the animation did finish.
        gridService.SetSize(this, columns, rows);
        AfterResize();
        SetMinMaxSize(true);
        pendingSpan = null;
    }

    private void AfterResize()
    {
        displayMonitor.Refresh();
        if (tornDown || restoringDisplayMetrics || displayMonitor.IsTopologyChanging || OwningScreen == null) return;
        
        if (FixedSizeWidget is { } fixedWidget)
        {
            // Derive the span from the LIVE window size, not from the stored layout:
            // the save below has not run yet, so GetSpan() here validated the OLD
            // entry (a no-op in the happy path) — or worse, an intermediate value
            // persisted mid-animation, which turned this check into the thing that
            // snapped the widget back to its previous span.
            var (cellPx, _, _) = GetGridMetrics();
            var scaling = ScreenScaling;
            var (c, r) = (ResolveSpan(Width, cellPx, scaling), ResolveSpan(Height, cellPx, scaling));
            var (sc, sr) = fixedWidget.SnapSpan(c, r);
            if (sc != c || sr != r)
            {
                manualSpan = (sc, sr);
                gridService.SetSize(this, sc, sr);
            }
        }
        else
        {
            gridService.SnapSize(this);
        }
        
        Scale();
        var settings = widgetLayoutProvider.Get();
        // Math.Round, not a truncating (int) cast: the truncation used to shave up
        // to 1 DIP off every save, and each ≥0.5 DIP delta then re-wrote the window
        // size through OnWidgetLayoutUpdated — the sub-pixel "creep" on every resize.
        SaveLayout(settings with { Width = (int) Math.Round(Width), Height = (int) Math.Round(Height) });
        Notify(nameof(Radius));
        UpdateContentSize();
        ApplyWidgetRegion();
        Notify(nameof(SizeMenuTitle));
        // Re-sync the context-menu size steppers with the committed span (the
        // binding does not refresh itself while the menu is open).
        Notify(nameof(SizeColumnsValue));
        Notify(nameof(SizeRowsValue));
    }

    public void Remove()
    {
        widgetLayoutProvider.Remove();
        Close();
    }

    /// <summary>
    /// The widget's cell span (columns, rows) derived from the stored pixel size.
    /// </summary>
    private (int Columns, int Rows) GetSpan()
    {
        var (cellPx, _, _) = GetGridMetrics();
        var scaling = ScreenScaling;
        var layout = widgetLayoutProvider.Get();
        // Window sizes are DIPs while the grid metrics are physical — convert.
        return (ResolveSpan(layout.Width, cellPx, scaling), ResolveSpan(layout.Height, cellPx, scaling));
    }

    /// <summary>
    /// Resolve the widget's cell span against a SPECIFIC screen's grid (used when
    /// the widget is dragged to another screen, so the span is preserved even
    /// though the cell size differs between the screens).
    /// </summary>
    private (int Columns, int Rows) ResolveSpanFor(AttachedScreen attached)
    {
        var grid = (attached.Config is { } config ? layoutProvider.Get().FindById(config.Id) : null)?.Grid ?? appSettingsProvider.Get().Grid ?? DeskCanvas.Core.Models.Settings.Grid.Default;
        var area = attached.Screen.WorkingArea;
        var (cellPx, _, _) = GridMetrics.Resolve(grid, area.X, area.Y, area.Width, area.Height);
        var layout = widgetLayoutProvider.Get();
        var scaling = attached.Screen.Scaling;
        return (ResolveSpan(layout.Width, cellPx, scaling), ResolveSpan(layout.Height, cellPx, scaling));
    }

    /// <summary>
    /// Resolve the widget's cell span against a specific Grid configuration.
    /// </summary>
    private (int Columns, int Rows) ResolveSpanAgainstGrid(DeskCanvas.Core.Models.Settings.Grid grid)
    {
        var screen = OwningScreen;
        var area = screen?.WorkingArea;
        var scaling = screen?.Scaling ?? 1.0;
        var (cellPx, _, _) = GridMetrics.Resolve(grid, area?.X ?? 0, area?.Y ?? 0, area?.Width ?? 1920, area?.Height ?? 1080);
        var layout = widgetLayoutProvider.Get();
        return (ResolveSpan(layout.Width, cellPx, scaling), ResolveSpan(layout.Height, cellPx, scaling));
    }
    
    /// <summary>
    /// Resolve a stored pixel size into a whole number of grid cells
    /// (see <see cref="GridMetrics.ResolveSpan"/> — the gallery's occupancy test measures the
    /// same stored size in the same physical pixels, so placement and restore agree on how
    /// much of the grid a widget takes).
    /// </summary>
    private static int ResolveSpan(double size, double cellPx, double scaling)
        => GridMetrics.ResolveSpan(size, cellPx, scaling);

    /// <summary>
    /// Resolve the manual grid metrics for the current screen (fallback: primary screen).
    /// </summary>
    private (int cell, int x, int y) GetGridMetrics()
    {
        var screen = OwningScreen;
        var area = screen?.WorkingArea;
        // Per-screen manual grid (the screen the widget currently sits on),
        // falling back to the global grid, then the default.
        var grid = ScreenConfig?.Grid
                   ?? appSettingsProvider.Get().Grid
                   ?? DeskCanvas.Core.Models.Settings.Grid.Default;
        return GridMetrics.Resolve(
            grid,
            area?.X ?? 0,
            area?.Y ?? 0,
            area?.Width ?? 1920,
            area?.Height ?? 1080);
    }

    /// <summary>
    /// React to per-screen configuration changes — grid geometry, content scale
    /// or an ownership transfer from a cross-screen drag — by re-applying the
    /// grid-driven size, the content scale and the OS clipping region.
    /// </summary>
    private void OnLayoutDataUpdated(object? sender, ScreensLayout? oldScreens, ScreensLayout newScreens)
    {
        if (tornDown || restoringDisplayMetrics || displayMonitor.IsTopologyChanging || OwningScreen == null) return;
        var config = newScreens.FindById(widgetLayoutProvider.ScreenId);
        var oldConfig = oldScreens?.FindById(widgetLayoutProvider.ScreenId);
        if (config == null) return; // this widget is no longer in the layout

        if (Equals(oldConfig?.Grid, config.Grid) && Equals(oldConfig?.ContentScale, config.ContentScale)
            && oldConfig?.Margin == config.Margin && oldConfig?.Radius == config.Radius)
            return;

        if (oldConfig?.Margin != config.Margin || oldConfig?.Radius != config.Radius)
        {
            Notify(nameof(WidgetMargin));
            UpdateOverlayLayout();
            Notify(nameof(Radius));
            Notify(nameof(InnerRadius));
            Notify(nameof(PillRadius));
            UpdateContentSize();
            ApplyWidgetRegion();
        }

        if (!Equals(oldConfig?.Grid, config.Grid))
        {
            manualSpan ??= (oldConfig?.Grid != null ? ResolveSpanAgainstGrid(oldConfig.Grid) : GetSpan());
            var (columns, rows) = manualSpan.Value;

            SetMinMaxSize(false);
            gridService.SetSize(this, columns, rows);
            SetMinMaxSize(true);
            AfterResize();

            // The grid editor just saved a NEW grid: re-snap to the new cell
            // origin immediately and persist the position (before this fix the
            // widgets only re-snapped on activation, so closing the editor with
            // "完成" appeared to do nothing).
            gridService.SnapPosition(this);
            SaveLayout(StorePosition(widgetLayoutProvider.Get()));
        }

        RefreshScreenStyle();
    }
}
