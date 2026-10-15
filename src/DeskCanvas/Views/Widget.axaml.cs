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
using DeskCanvas.Locales;
using DeskCanvas.Services;
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
    private double ScreenScaling => OwningScreen?.Scaling ?? 1.0;

    /// <summary>True while the host suspended the widgets (fullscreen application active).</summary>
    private bool suspended;

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
        widgetLayoutProvider.DataChanged -= OnWidgetLayoutUpdated;
        appSettingsProvider.DataChanged -= OnAppSettingsUpdated;
        layoutProvider.DataChanged -= OnLayoutDataUpdated;
        profileService.ActiveProfileChanged -= OnProfilesChanged;
        profileService.ProfilesListChanged -= OnProfilesChanged;
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
    }

    /// <summary>Rebuild whatever <see cref="SuspendContent"/> released.</summary>
    public void ResumeContent()
    {
        if (!suspended) return;
        suspended = false;
        (ContentPresenter.Content as IWidgetSuspendable)?.Resume();
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
        RenderOptions.SetTextRenderingMode(this, TextRenderingMode.Antialias);
        UpdateContentSize();
        
        // Manual grid: size is driven by the grid (span derived from the stored pixel size).
        manualSpan = GetSpan();
        gridService.SetSize(this, manualSpan.Value.Columns, manualSpan.Value.Rows);
        SetMinMaxSize(true);
        
        Activated += OnActivated;
        Opened += OnOpened;
        Resized += OnResized;
        PointerPressed += OnPointerPressed;
        AddHandler(PointerPressedEvent, OnPreviewPointerPressed, RoutingStrategies.Tunnel);
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
        ApplyTransparencyHint();
        UpdateContentSize();
        ApplyWidgetRegion();

        // Desktop furniture: the widget lives at the bottom of the z-order for its
        // whole lifetime — clicking, activating, dragging or re-showing it must not
        // raise it above ordinary application windows. Secondary panels (weather
        // forecast & co.) are pinned just above the widget band instead.
        WidgetZOrder.PinWidgetToBottom(this);
    }

    private void OnResized(object? sender, WindowResizedEventArgs e)
    {
        if (tornDown || displayMonitor.IsTopologyChanging) return;
        UpdateContentSize();
        ApplyWidgetRegion();
        if (appSettingsProvider.Get().Theme.UseNativeFrame)
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

    /// <summary>Restore owning display geometry and styling without changing saved layout.</summary>
    public void RefreshDisplayMetrics()
    {
        if (tornDown || displayMonitor.IsTopologyChanging || OwningScreen == null) return;
        restoringDisplayMetrics = true;
        try
        {
            ApplyPosition();
            SetMinMaxSize(false);
            manualSpan ??= GetSpan();
            gridService.SetSize(this, manualSpan.Value.Columns, manualSpan.Value.Rows);
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

    public CornerRadius Radius => (isFrameless || appSettingsProvider.Get().Theme.UseNativeFrame)
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
    /// enabled it is the shared parameters with this screen's state substituted (light/dark
    /// pair + optional surface material) — so two screens can show two materials at once.
    /// </summary>
    public Theme GlassMaterial
    {
        get
        {
            var settings = appSettingsProvider.Get();
            return FocusCoordinator?.ResolveWindowTheme(settings) ?? settings.Theme;
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
        if (overlay.GetVisualRoot() is Window { } other && !ReferenceEquals(other, this)) return;

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
    /// 中键+滚轮切换重叠组件: hold the middle button anywhere over the widget and roll the
    /// wheel to page through the stack — no aiming for the dots strip required. Runs on the
    /// tunnel route ahead of every inner control, and marks the wheel handled so neither the
    /// stacked children's ScrollViewers nor the dots strip react to the same tick (no double
    /// switch). The dots strip's own wheel handler (inside the decoration overlay) stays in
    /// charge when the middle button is up, exactly as before.
    /// </summary>
    private void OnStackChordWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if (!stackMiddleButtonChord) return;
        if (StackWidget is not { } stack || stack.IndicatorItems is not { Count: > 1 } list) return;

        try
        {
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
        // The chord is a press-and-scroll gesture inside the widget: leaving the window
        // ends it (a release outside would otherwise leave the flag stuck on).
        stackMiddleButtonChord = false;
    }

    /// <summary>
    /// True when the card should render the outline highlight ring: glass surface
    /// with the outline width &gt; 0, no native frame.
    /// </summary>
    private bool IsOutlined
    {
        get
        {
            var theme = GlassMaterial;
            return !isFrameless && theme.IsGlass && theme.OutlineWidth > 0 && !theme.UseNativeFrame;
        }
    }

    /// <summary>Highlight ring thickness (DIPs), 0 when the surface is not outlined glass.</summary>
    public Thickness WidgetOutlineThickness
    {
        get
        {
            if (!IsOutlined || IsStackWidget) return new Thickness(0);
            var width = Math.Clamp(GlassMaterial.OutlineWidth, 0, 6);
            return new Thickness(width);
        }
    }

    /// <summary>
    /// Highlight ring brush for outlined glass. The ring is strongest at the
    /// top-left and bottom-right corners and fades linearly along every edge to
    /// nothing at the top-right and bottom-left corners (无→最浓 gradient over
    /// the whole edge, not just a short notch near the corner). Built as a conic
    /// gradient whose sweep starts at the actual top-left corner, so the fades
    /// follow the real corners for any aspect ratio.
    /// </summary>
    public IBrush? WidgetOutlineBrush
    {
        get
        {
            if (!IsOutlined || IsStackWidget) return null;
            if (GlassMaterial.OutlineWidth > 0)
                return BuildOutlineBrush();
            return null;
        }
    }

    private ConicGradientBrush BuildOutlineBrush()
    {
        var width = Math.Max(1, (ClientSize.Width > 0 ? ClientSize.Width : Width) - 2 * WidgetMargin.Left);
        var height = Math.Max(1, (ClientSize.Height > 0 ? ClientSize.Height : Height) - 2 * WidgetMargin.Top);

        var theme = GlassMaterial;
        var cornerAngle = Math.Atan2(width / 2.0, height / 2.0) * 180.0 / Math.PI;   // top-right corner direction
        var startAngle = 360.0 - cornerAngle;                                        // top-left corner direction
        var color = Color.TryParse(theme.EffectiveOutlineColor, out var parsed)
            ? parsed
            : Color.Parse(DeskCanvas.Core.Models.Settings.Theme.DefaultOutlineColor);
        var clear = Colors.Transparent;

        // Conic gradients: 0° = above center (top), clockwise (CSS convention);
        // the Angle property rotates offset 0 to the given direction (here: top-left).
        // Offsets are 0..1 fractions of the full 360° sweep. Segment widths in angle:
        //   TL→TR = 2·cornerAngle (top edge), TR→BR = 180−2·cornerAngle (right edge),
        //   BR→BL = 2·cornerAngle (bottom edge), BL→TL = 180−2·cornerAngle (left edge).
        return new ConicGradientBrush
        {
            Angle = startAngle,
            Center = RelativePoint.Center,
            GradientStops =
            {
                new GradientStop(color, 0),
                new GradientStop(clear, 2 * cornerAngle / 360.0),
                new GradientStop(color, 0.5),
                new GradientStop(clear, 0.5 + 2 * cornerAngle / 360.0),
                new GradientStop(color, 1.0)
            }
        };
    }

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
                    ? new SolidColorBrush(Color.Parse("#1C1C1E"))
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

    public SystemDecorations WidgetSystemDecorations => appSettingsProvider.Get().Theme.UseNativeFrame
        ? SystemDecorations.BorderOnly
        : SystemDecorations.None;

    public bool WidgetExtendClientArea => appSettingsProvider.Get().Theme.UseNativeFrame;
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
        // SystemDecorations.None, which is the widget's normal mode).
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

        var outline = WidgetOutlineThickness;
        var innerW = Math.Max(1, cardW - outline.Left - outline.Right);
        var innerH = Math.Max(1, cardH - outline.Top - outline.Bottom);

        ContentPresenter.Width = innerW;
        ContentPresenter.Height = innerH;

        UpdateAdaptiveRadiusResources();
        var r = Radius.TopLeft;
        var innerR = Math.Max(0, r - outline.Left);
        ContentPresenter.Clip = new RectangleGeometry(new Rect(0, 0, innerW, innerH), innerR, innerR);

        // Outlined glass: the corner-fade notches follow the card aspect ratio.
        Notify(nameof(WidgetOutlineThickness));
        Notify(nameof(WidgetOutlineBrush));
    }

    /// <summary>
    /// Smallest window that fits the content's measured minimum: the content
    /// DesiredSize already includes the view's own margin, so the window just adds
    /// the card margin and the outline ring. Kept side-effect-free so
    /// <c>WidgetAdaptiveChecks</c> can pin the math via reflection.
    /// </summary>
    internal static (double Width, double Height) ComputeContentMinWindowSize(
        Size contentDesired, Thickness windowMargin, Thickness outline, double floor = 48)
    {
        var w = Math.Ceiling(contentDesired.Width + windowMargin.Left + windowMargin.Right + outline.Left + outline.Right);
        var h = Math.Ceiling(contentDesired.Height + windowMargin.Top + windowMargin.Bottom + outline.Top + outline.Bottom);
        return (Math.Max(floor, w), Math.Max(floor, h));
    }

    private (int m, int cw, int ch, int cr)? lastAppliedRegion;

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
        if (appSettingsProvider.Get().Theme.UseNativeFrame)
        {
            if (lastAppliedRegion != null)
            {
                InteropService.ClearWidgetRegion(this);
                lastAppliedRegion = null;
            }
            return;
        }

        // Only frosted glass (acrylic) needs native window clipping because OS DWM
        // applies acrylic blur to the entire HWND.
        // Liquid glass and solid surfaces do NOT use native blur; their transparent
        // margins and anti-aliased rounded corners composite via 32-bit per-pixel alpha,
        // so applying a 1-bit GDI region truncates the anti-aliased curved edge and creates jaggedness.
        bool needsClipping = appSettingsProvider.Get().Theme.UsesNativeBlur;

        if (!needsClipping)
        {
            if (lastAppliedRegion != null)
            {
                InteropService.ClearWidgetRegion(this);
                lastAppliedRegion = null;
            }
            return;
        }

        var scaling = ScreenScaling;
        var margin = (int) Math.Round(WidgetMargin.Left * scaling);
        var width = (int) Math.Round(ClientSize.Width * scaling);
        var height = (int) Math.Round(ClientSize.Height * scaling);

        var cardWidth = Math.Max(1, width - 2 * margin);
        var cardHeight = Math.Max(1, height - 2 * margin);
        var cardRadius = (int) Math.Round(ResolveEffectiveRadius(appSettingsProvider.Get().Dimensions.Radius));

        // The decoration overlay (IWidgetOverlayProvider, e.g. the Stack pagination dots that
        // moved into the plugin) is placed inside the card on this surface per its
        // InsetOnNativeBlur hint — exactly because an out-of-card strip in the native region
        // would flood with the OS blur, and no fill for it works there: a translucent one
        // reads as frosted glass, an opaque one as a slab of a different material (see
        // UpdateOverlayLayout). The card rectangle alone therefore also covers the dots;
        // no extra strip is ever OR-ed in, and the region work stays entirely host-side.
        var key = (margin, cardWidth, cardHeight, cardRadius);
        if (lastAppliedRegion == key) return;
        lastAppliedRegion = key;

        InteropService.SetWidgetRegion(
            this,
            margin,
            margin,
            cardWidth,
            cardHeight,
            cardRadius);
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
        // outlined-glass highlight ring…).
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
    /// the outline, the overlay placement, the content size and the native window region.
    /// </summary>
    private void RefreshThemeVisuals()
    {
        ApplyTransparencyHint();
        Notify(nameof(GlassMaterial));
        Notify(nameof(ShowsGlassMaterial));
        Notify(nameof(WidgetMargin));
        UpdateOverlayLayout();
        Notify(nameof(WidgetCardBackground));
        Notify(nameof(Radius));
        Notify(nameof(InnerRadius));
        Notify(nameof(PillRadius));
        Notify(nameof(SizeMenuTitle));
        Notify(nameof(WidgetOutlineThickness));
        Notify(nameof(WidgetOutlineBrush));
        UpdateContentSize();
        ApplyWidgetRegion();
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

        TransparencyLevelHint = GlassMaterial.UsesNativeBlur
            ? [WindowTransparencyLevel.AcrylicBlur]
            : [WindowTransparencyLevel.Transparent];
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

                if (cm.GetVisualRoot() is WindowBase wb)
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
    }

    private void OnActualThemeVariantChanged(object? sender, EventArgs e)
    {
        Notify(nameof(WidgetCardBackground));
        Notify(nameof(WidgetOutlineBrush));
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
        if (oldLayout != null && (Math.Abs(oldLayout.Width - newLayout.Width) > 0.5 || Math.Abs(oldLayout.Height - newLayout.Height) > 0.5))
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

        if (isFrameless && lastAppliedRegion != null)
        {
            InteropService.ClearWidgetRegion(this);
            lastAppliedRegion = null;
        }

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

        Notify(nameof(IsFrameless));
        Notify(nameof(ShowsGlassMaterial));
        Notify(nameof(WidgetMargin));
        UpdateOverlayLayout();
        Notify(nameof(WidgetCardBackground));
        Notify(nameof(Radius));
        Notify(nameof(InnerRadius));
        Notify(nameof(PillRadius));
        Notify(nameof(WidgetOutlineThickness));
        Notify(nameof(WidgetOutlineBrush));
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
        if (!point.Properties.IsLeftButtonPressed) return;

        var isCtrl = e.KeyModifiers.HasFlag(KeyModifiers.Control)
                     || (OperatingSystem.IsWindows() && (GetKeyState(0x11) & 0x8000) != 0);
        if (!isCtrl) return;

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
            positioned = positioned with { Width = (int) Width, Height = (int) Height };
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
        Transitions = new Transitions
        {
            new DoubleTransition { Property = WidthProperty, Duration = TimeSpan.FromMilliseconds(300) },
            new DoubleTransition { Property = HeightProperty, Duration = TimeSpan.FromMilliseconds(300) }
        };
        gridService.SetSize(this, columns, rows);
        await Task.Delay(320);
        Transitions = null;
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
            var (c, r) = GetSpan();
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
        SaveLayout(settings with { Width = (int)Width, Height = (int)Height });
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
    /// Resolve a stored pixel size into a whole number of grid cells.
    /// <para>
    /// The current build always stores window sizes in DIPs, so the DIP
    /// interpretation is trusted whenever it lands close to a whole number of
    /// cells. Sizes saved by older builds were physical pixels (e.g. 575 px on a
    /// 125% display for a 5-cell span) — those are only used when the DIP reading
    /// is clearly not a whole span. Comparing both and taking the closer one used
    /// to report the wrong span whenever both readings were equidistant (5 cells
    /// on a 125% display: 460 DIP = 4.0 cells physical = 5.0 cells DIP), so the
    /// size steppers showed 4 and setting 4 was a no-op ("adjusting columns
    /// 5 to 4 does nothing").
    /// </para>
    /// </summary>
    private static int ResolveSpan(double size, double cellPx, double scaling)
    {
        var cellDip = cellPx / scaling;
        if (cellDip <= 0) return 1;

        var dip = size / cellDip;
        var dipError = Math.Abs(dip - Math.Round(dip));
        if (dipError <= 0.15) return Math.Max(1, (int) Math.Round(dip));

        var physical = size / cellPx;
        if (Math.Abs(physical - Math.Round(physical)) <= 0.05)
            return Math.Max(1, (int) Math.Round(physical));

        return Math.Max(1, (int) Math.Round(dip));
    }

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
