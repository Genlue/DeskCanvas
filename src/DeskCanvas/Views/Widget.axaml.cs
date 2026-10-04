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

namespace DeskCanvas.Views;

public partial class Widget : Window, INotifyPropertyChanged
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
    private readonly bool isFrameless;

    /// <summary>
    /// 组件右键菜单的固定外壳圆角：与菜单项高亮同圆度。唯一的圆角真相源是 XAML 样式
    /// （App.axaml 的 WidgetContextMenu，组件窗口再在 Widget.axaml 里同值重声明一份）。
    /// 本常量只供 Clip / 原生窗口区域裁剪对齐用，绝不写回 CornerRadius（历史教训见构造
    /// 函数注释）。它必须和那两处 XAML 同步——只改样式不改这里，菜单会被裁成旧圆角。
    /// </summary>
    private const double ContextMenuCornerRadius = 12;

    /// <summary>
    /// Set once this window is being torn down (recreate / close all). While set, the
    /// window must not write to the stored layout any more: by then the layout may
    /// already belong to a different configuration (profile switch), and a write would
    /// re-add this widget to it as a duplicate.
    /// </summary>
    private bool tornDown;

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
        if (tornDown) return;
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
        isFrameless = control is IFramelessWidget;
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
        
        SetMinMaxSize(this.appSettingsProvider.Get().Layout.LockSize);
        RenderOptions.SetTextRenderingMode(this, TextRenderingMode.Antialias);
        UpdateContentSize();
        
        // Manual grid: size is driven by the grid (span derived from the stored pixel size).
        if (appSettingsProvider.Get().Layout.GridMode == GridMode.Manual)
        {
            manualSpan = GetSpan();
            gridService.SetSize(this, manualSpan.Value.Columns, manualSpan.Value.Rows);
        }
        
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
        ContentPresenter.LayoutUpdated += OnContentLayoutUpdated;
        Unloaded += OnUnloaded;
    }

    private void OnOpened(object? sender, EventArgs e)
    {
        // The native window exists now — size the card and clip the OS acrylic backdrop.
        displayMonitor.Attach(this);
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
        UpdateContentSize();
        ApplyWidgetRegion();
        if (appSettingsProvider.Get().Theme.UseNativeFrame)
            AfterResize();
    }

    private void OnActivated(object? sender, EventArgs e)
    {
        ApplyPosition();

        // Manual grid: snap to the nearest cell on every activation (also covers
        // widget positions stored before the grid mode was switched on).
        if (appSettingsProvider.Get().Layout.GridMode == GridMode.Manual)
            gridService.SnapPosition(this);

        Scale();
        ApplyWidgetRegion();
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
        var settings = widgetLayoutProvider.Get();

        if (widgetLayoutProvider.ScreenId == ScreensLayout.LegacyPrimaryId)
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

        var scaling = Screens.ScreenFromWindow(this)?.Scaling ?? 1.0;
        var topLeft = Position;
        var bottomRight = new PixelPoint(
            Position.X + (int)(Width * scaling),
            Position.Y + (int)(Height * scaling));

        var visible = displayMonitor.Attached.Any(attached =>
            attached.Screen.Bounds.Contains(topLeft) || attached.Screen.Bounds.Contains(bottomRight));
        if (visible) return;

        ApplyPosition();
    }

    public bool ShowEditButton => editWidgetWindow != null;
    public string Edit => $"{Locale.Widget_Edit} \"{widgetLayoutProvider.Get().Type}\"";

    public bool IsStackWidget =>
        StackWidget != null
        || widgetLayoutProvider.Get().Type is "Stack" or "StackWidgets"
        || widgetLayoutProvider.Get().SubType is "WidgetStackView" or "WidgetStack";

    public bool ShowNormalEditButton => !IsStackWidget && ShowEditButton;
    public bool ShowStackEditButton => IsStackWidget && ShowEditButton;
    public bool CanEditStackedWidgetChild => StackWidget?.CanEditCurrentChild == true;
    public string EditStackChildTitle => $"{Locale.Widget_Edit} \"{StackWidget?.CurrentChildTitle ?? ""}\"";

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
        displayMonitor.CurrentConfig(this)?.Radius
        ?? appSettingsProvider.Get().Dimensions.Radius;

    public CornerRadius Radius => (isFrameless || appSettingsProvider.Get().Theme.UseNativeFrame)
        ? new(0)
        : new(ResolveEffectiveRadius(EffectiveBaseRadius) / (Screens.ScreenFromWindow(this)?.Scaling ?? 1.0));

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

        if (Application.Current != null)
        {
            Application.Current.Resources["WidgetCardCornerRadius"] = cardRadius;
            Application.Current.Resources["WidgetInnerCornerRadius"] = innerRadius;
            Application.Current.Resources["WidgetPillCornerRadius"] = pillRadius;
        }

        // 菜单圆角不在此处跟随卡片半径（历史教训：四处命令式覆写让 XAML 的圆角永远
        // 不生效）。菜单的 CornerRadius 只由 WidgetContextMenu 样式决定；打开时的
        // Clip / 原生区域裁剪在 OnContextMenuOpened 里按 ContextMenuCornerRadius 对齐。

        Notify(nameof(Radius));
        Notify(nameof(InnerRadius));
        Notify(nameof(PillRadius));
    }

    public Theme GlassMaterial => appSettingsProvider.Get().Theme;

    /// <summary>
    /// True when the card is drawn by the app itself from a desktop snapshot
    /// (液态玻璃 / 柔光玻璃), so the <see cref="Controls.LiquidGlassSurface"/> layer is shown
    /// and the plain card background must stay transparent. Frameless widgets render
    /// their own glyph material instead.
    /// </summary>
    public bool ShowsGlassMaterial => !isFrameless && GlassMaterial.UsesRenderedGlass;

    /// <summary>
    /// Widgets that provide their own edge-to-edge background or tiles (e.g. Note, MapView),
    /// which should suppress the window card outline and background in Colorful theme to avoid
    /// double borders or background leakage.
    /// Note: Flush widgets like AggregateView rely on the window card background and must NOT be suppressed.
    /// </summary>
    private bool IsSelfFramingWidget
    {
        get
        {
            var contentName = ContentPresenter?.Content?.GetType().Name;
            var layout = widgetLayoutProvider?.Get();
            return contentName is "Note" or "MapView"
                   || layout?.SubType is "Note" or "MapView"
                   || layout?.Type is "Notes" or "Map";
        }
    }

    private DeskCanvas.Core.Interfaces.IStackWidget? activeStackWidget;

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

        Notify(nameof(HasStackIndicators));
        Notify(nameof(StackIndicators));
        Notify(nameof(IsStackWidget));
        Notify(nameof(ShowNormalEditButton));
        Notify(nameof(ShowStackEditButton));
        Notify(nameof(CanEditStackedWidgetChild));
        Notify(nameof(EditStackChildTitle));
        UpdateStackIndicatorsReveal();
    }

    private void OnStackIndicatorsChanged(object? sender, EventArgs e)
    {
        try
        {
            Notify(nameof(HasStackIndicators));
            Notify(nameof(StackIndicators));
            Notify(nameof(CanEditStackedWidgetChild));
            Notify(nameof(EditStackChildTitle));
            UpdateStackIndicatorsReveal();
            ApplyWidgetRegion();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Widget] OnStackIndicatorsChanged error: {ex.Message}");
        }
    }

    public DeskCanvas.Core.Interfaces.IStackWidget? StackWidget =>
        activeStackWidget ?? (ContentPresenter?.Content as DeskCanvas.Core.Interfaces.IStackWidget);

    public bool HasStackIndicators => StackWidget != null && (StackWidget.IndicatorItems?.Count ?? 0) > 1;

    public IReadOnlyList<DeskCanvas.Core.Interfaces.StackWidgetIndicatorItem>? StackIndicators =>
        StackWidget?.IndicatorItems;

    public void OnStackIndicatorClicked(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (sender is Button btn && btn.Tag is int index && StackWidget != null)
            {
                StackWidget.SwitchToIndex(index);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Widget] OnStackIndicatorClicked error: {ex.Message}");
        }
    }

    public void OnStackIndicatorsWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        try
        {
            if (StackWidget is { } stack && StackIndicators is { Count: > 1 } list)
            {
                if (stack.IsTransitionActive)
                {
                    e.Handled = true;
                    return;
                }

                var currentIndex = list.FirstOrDefault(i => i.IsActive)?.Index ?? 0;
                if (e.Delta.Y > 0)
                {
                    var nextIndex = (currentIndex - 1 + list.Count) % list.Count;
                    stack.SwitchToIndex(nextIndex);
                    e.Handled = true;
                }
                else if (e.Delta.Y < 0)
                {
                    var nextIndex = (currentIndex + 1) % list.Count;
                    stack.SwitchToIndex(nextIndex);
                    e.Handled = true;
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Widget] OnStackIndicatorsWheelChanged error: {ex.Message}");
        }
    }

    // ---- 重叠组件的切页手势（中键 + 滚轮）与圆点常态隐藏 ----

    /// <summary>True while the middle button is held over this widget (the switch chord's held state).</summary>
    private bool stackMiddleButtonChord;

    /// <summary>Delayed hide for the auto-hidden stack dots (see <see cref="UpdateStackIndicatorsReveal"/>).</summary>
    private DispatcherTimer? stackDotsHideTimer;

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
    /// switch). The dots strip's own wheel handler stays in charge when the middle button is
    /// up, exactly as before.
    /// </summary>
    private void OnStackChordWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if (!stackMiddleButtonChord) return;
        if (StackWidget is not { } stack || StackIndicators is not { Count: > 1 } list) return;

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

    /// <summary>
    /// 常态隐藏小圆点 (StackWidget.AutoHideIndicators): the dots only show while a switch
    /// transition runs or the pointer is over the widget, then hide again after a short
    /// linger. Opacity keeps the strip hit-testable, so the wheel-at-the-origin gesture and
    /// clicking a dot keep working; the pointer-over reveal guarantees the dots are visible
    /// whenever they could be interacted with.
    /// </summary>
    private void UpdateStackIndicatorsReveal()
    {
        var host = StackIndicatorsHost;
        if (host == null) return;

        if (StackWidget is not { } stack || !stack.AutoHideIndicators || !HasStackIndicators)
        {
            stackDotsHideTimer?.Stop();
            host.Opacity = 1;
            return;
        }

        if (stack.IsTransitionActive || IsPointerOver)
        {
            stackDotsHideTimer?.Stop();
            host.Opacity = 1;
            return;
        }

        // Idle: linger briefly before hiding, so a just-finished interaction doesn't flicker.
        if (host.Opacity > 0 && stackDotsHideTimer is not { IsEnabled: true })
        {
            stackDotsHideTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
            stackDotsHideTimer.Tick -= OnStackDotsHideTimerTick;
            stackDotsHideTimer.Tick += OnStackDotsHideTimerTick;
            stackDotsHideTimer.Start();
        }
    }

    private void OnStackDotsHideTimerTick(object? sender, EventArgs e)
    {
        stackDotsHideTimer?.Stop();
        if (StackIndicatorsHost == null) return;

        StackIndicatorsHost.Opacity =
            StackWidget is { } stack && stack.AutoHideIndicators && HasStackIndicators
            && (stack.IsTransitionActive || IsPointerOver)
                ? 1
                : 0;
    }

    protected override void OnPointerEntered(PointerEventArgs e)
    {
        base.OnPointerEntered(e);
        UpdateStackIndicatorsReveal();
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        // The chord is a press-and-scroll gesture inside the widget: leaving the window
        // ends it (a release outside would otherwise leave the flag stuck on).
        stackMiddleButtonChord = false;
        UpdateStackIndicatorsReveal();
    }

    /// <summary>
    /// True when the card should render the outline highlight ring: glass surface
    /// with the outline width &gt; 0, no native frame.
    /// </summary>
    private bool IsOutlined =>
        !isFrameless
        && ((appSettingsProvider.Get().Theme.IsGlass && appSettingsProvider.Get().Theme.OutlineWidth > 0)
            || appSettingsProvider.Get().Theme.IsColorful)
        && !appSettingsProvider.Get().Theme.UseNativeFrame;

    /// <summary>Highlight ring thickness (DIPs), 0 when the surface is not outlined glass.</summary>
    public Thickness WidgetOutlineThickness
    {
        get
        {
            if (!IsOutlined || IsStackWidget) return new Thickness(0);
            if (appSettingsProvider.Get().Theme.IsColorful && IsSelfFramingWidget)
                return new Thickness(0);
            var width = Math.Clamp(appSettingsProvider.Get().Theme.OutlineWidth, 0, 6);
            if (width <= 0 && appSettingsProvider.Get().Theme.IsColorful)
                return new Thickness(1);
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
    /// In Colorful mode, a subtle 1px macOS card rim light is used by default.
    /// </summary>
    public IBrush? WidgetOutlineBrush
    {
        get
        {
            if (!IsOutlined || IsStackWidget) return null;
            if (appSettingsProvider.Get().Theme.IsColorful && IsSelfFramingWidget)
                return null;
            if (appSettingsProvider.Get().Theme.OutlineWidth > 0)
                return BuildOutlineBrush();
            if (appSettingsProvider.Get().Theme.IsColorful && this.TryFindResource("WidgetCardBorderBrush", ActualThemeVariant, out var res) && res is IBrush b)
                return b;
            return null;
        }
    }

    private ConicGradientBrush BuildOutlineBrush()
    {
        var width = Math.Max(1, (ClientSize.Width > 0 ? ClientSize.Width : Width) - 2 * WidgetMargin.Left);
        var height = Math.Max(1, (ClientSize.Height > 0 ? ClientSize.Height : Height) - 2 * WidgetMargin.Top);

        var theme = appSettingsProvider.Get().Theme;
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
        displayMonitor.CurrentConfig(this)?.Margin
        ?? appSettingsProvider.Get().Dimensions.Margin;

    public Thickness WidgetMargin => isFrameless ? new Thickness(0) :
        (appSettingsProvider.Get().Layout.GridMode == GridMode.Manual
            ? new Thickness(EffectiveMargin)
            : new Thickness(0));

    /// <summary>
    /// Margin of the stack pagination dots host. Every surface keeps the dots floating in
    /// the right margin outside the card — except 毛玻璃 (acrylic): DWM applies the OS blur
    /// to the whole clipped window region, so an out-of-card strip holding the dots would
    /// show as a frosted tab behind them (translucent fills there read as glass, and an opaque
    /// one as a foreign slab — the strip's own backdrop is <i>blurred wallpaper</i>). There the
    /// dots move inside the card (over its right edge), which also lets
    /// <see cref="ApplyWidgetRegion"/> clip to the card alone.
    /// </summary>
    public Thickness StackIndicatorsMargin
    {
        get
        {
            if (isFrameless) return new Thickness(0, 0, 1, 0);
            var theme = appSettingsProvider.Get().Theme;
            return theme.UsesNativeBlur && !theme.UseNativeFrame
                ? new Thickness(0, 0, WidgetMargin.Right + 1, 0)
                : new Thickness(0, 0, 1, 0);
        }
    }

    public IBrush WidgetCardBackground
    {
        get
        {
            if (isFrameless || IsStackWidget) return Brushes.Transparent;
            var theme = appSettingsProvider.Get().Theme;
            var variant = ActualThemeVariant;
            if (theme.IsColorful)
            {
                var contentName = ContentPresenter?.Content?.GetType().Name;
                if (contentName == "Forecast")
                {
                    if (this.TryFindResource("WeatherCardBackground", variant, out var wcb) && wcb is IBrush wb)
                        return wb;
                }
                else if (contentName is "Progress" or "ProgressView")
                {
                    if (this.TryFindResource("ProgressCardBackground", variant, out var pcb) && pcb is IBrush pb)
                        return pb;
                }
                else if (contentName == "AnalogI")
                {
                    // Clock Style 1 (AnalogI):
                    // Outer perimeter background is always authentic dark mode charcoal (#1C1C1E)
                    return new SolidColorBrush(Color.Parse("#1C1C1E"));
                }
                else if (IsSelfFramingWidget)
                {
                    return Brushes.Transparent;
                }

                // Explicit fallback guarantee for Colorful theme to ensure pure white (light) or charcoal (dark)
                if (this.TryFindResource("WidgetBackground", variant, out var cb) && cb is IBrush cbrush)
                    return cbrush;
                return variant == Avalonia.Styling.ThemeVariant.Dark
                    ? new SolidColorBrush(Color.Parse("#1C1C1E"))
                    : Brushes.White;
            }
            return this.TryFindResource("WidgetBackground", variant, out var res) && res is IBrush brush
                ? brush
                : Brushes.Transparent;
        }
    }

    /// <summary>
    /// Context-menu size section title: "Grid size" in manual mode, with the
    /// current cell span (S/M/L presets are 1×1 / 2×1 / 2×2; anything else is
    /// a custom size from the dialog).
    /// </summary>
    public string SizeMenuTitle
    {
        get
        {
            if (appSettingsProvider.Get().Layout.GridMode == GridMode.Manual)
            {
                var (columns, rows) = CurrentSpan;
                return $"{Locale.Widget_Size_Grid} · {columns}×{rows}";
            }
            return $"{Locale.Widget_Size} · {(int)Width}×{(int)Height} px";
        }
    }

    public bool IsFreeMode => appSettingsProvider.Get().Layout.GridMode != GridMode.Manual;

    public bool ShowResizeHandle => !isFrameless
                                    && !appSettingsProvider.Get().Theme.UseNativeFrame
                                    && !appSettingsProvider.Get().Layout.LockSize
                                    && appSettingsProvider.Get().Layout.GridMode != GridMode.Manual;

    public void OpenCustomSizeDialog() =>
        new CustomSizeDialog((int)Width, (int)Height, ApplyCustomSize).ShowDialog(this);

    private void ApplyCustomSize(int w, int h)
    {
        SetMinMaxSize(false);
        Width = w;
        Height = h;
        AfterResize();
        SetMinMaxSize(appSettingsProvider.Get().Layout.LockSize || appSettingsProvider.Get().Layout.GridMode == GridMode.Manual);
    }

    /// <summary>The widget's current cell span for the active grid mode.</summary>
    public (int Columns, int Rows) CurrentSpan
    {
        get
        {
            // During a preset resize animation the pixel size carries intermediate
            // values — report the target span until it settles.
            if (pendingSpan is { } target) return target;

            if (appSettingsProvider.Get().Layout.GridMode == GridMode.Manual)
            {
                manualSpan ??= GetSpan();
                return manualSpan.Value;
            }

            var dimensions = appSettingsProvider.Get().Dimensions;
            var unit = dimensions.Size + dimensions.Margin;
            return (
                Math.Max(1, (int) Math.Round((Width + dimensions.Margin) / unit)),
                Math.Max(1, (int) Math.Round((Height + dimensions.Margin) / unit)));
        }
    }

    /// <summary>Target span of an in-flight preset resize animation (null when idle).</summary>
    private (int Columns, int Rows)? pendingSpan;

    public SystemDecorations WidgetSystemDecorations => appSettingsProvider.Get().Theme.UseNativeFrame
        ? SystemDecorations.BorderOnly
        : SystemDecorations.None;

    public bool ToolTipVisible => ShowResizeHandle;
    public bool WidgetExtendClientArea => appSettingsProvider.Get().Theme.UseNativeFrame;
    public void EditWidget() => editWidgetWindow?.Invoke().Show();

    private IFixedSizeWidget? FixedSizeWidget => ContentPresenter.Content as IFixedSizeWidget;
    public bool IsFixedWidget => FixedSizeWidget != null;
    public bool IsFixed2x1Widget => FixedSizeWidget != null && FixedSizeWidget.AllowedBaseSpans.Contains((4, 2));
    public bool IsFixedSquareWidget => FixedSizeWidget != null && FixedSizeWidget.AllowedBaseSpans.Contains((1, 1));
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
                var snapped = fixedWidget.AllowedBaseSpans.Contains((1, 1))
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
                var snapped = fixedWidget.AllowedBaseSpans.Contains((1, 1))
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

    private void OnPointerReleased(object? sender, PointerReleasedEventArgs e) => AfterMove();

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
        ?? displayMonitor.CurrentConfig(this)?.ContentScale
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

    // ---- 内容驱动最小窗口尺寸 ----
    // 自由模式下窗口可以被拖到/填到任意像素（48px 起），而组件内容里总有放不下的
    // 固定宽行（控件栏 pill、按钮组、表头…）：Grid 星号列缩到 0 后固定列溢出，被
    // ContentPresenter.Clip 裁掉（截断）或与同级元素叠在一起（重叠）。这里的闭环是：
    // 内容每次布局完成后读它的 DesiredSize——Avalonia 测量语义下，被压缩的布局会把
    // 放不下的自然尺寸报进 DesiredSize（> 可用尺寸即为溢出信号）——据此抬高窗口的
    // Min 尺寸，让缩放手柄 / 自定义尺寸 / 拖拽都不可能低于"内容能存活"的下限；已经
    // 小于下限的窗口（旧布局、locale 换更长的文案）则长到下限。内容自身在各尺寸档
    // 位的自适应收缩（隐藏次要控件等）会让 DesiredSize 跟着变小，所以这个下限是
    // "视图已尽力收缩后仍需要的"尺寸，而不是视图不作为的借口。

    /// <summary>上一次应用到窗口的 Min 尺寸（去重用：布局高频触发，值没变就不动窗口）。</summary>
    private double appliedMinWidth;
    private double appliedMinHeight;
    private bool minSizeUpdateQueued;

    private void OnContentLayoutUpdated(object? sender, EventArgs e) => QueueContentMinSizeUpdate();

    private void QueueContentMinSizeUpdate()
    {
        if (minSizeUpdateQueued) return;
        minSizeUpdateQueued = true;
        // Background 优先级：排在当前这轮布局之后，读到的 DesiredSize 才包含
        // 视图刚做过的可见性/档位调整；同时把一轮布局里的多次触发合并成一次。
        Dispatcher.UIThread.Post(() =>
        {
            minSizeUpdateQueued = false;
            UpdateContentMinSize();
        }, DispatcherPriority.Background);
    }

    private void UpdateContentMinSize()
    {
        // 无边框组件自管窗口区域与字形裁剪，尺寸即设计，不干预。
        if (isFrameless) return;
        // 手动网格：尺寸由网格驱动，视图靠 SizeTiers 分档自行退化。
        // 锁定尺寸：Min=Max=当前值是用户的显式选择，覆盖它反而破坏锁定。
        var layoutSettings = appSettingsProvider.Get().Layout;
        if (layoutSettings.GridMode == GridMode.Manual || layoutSettings.LockSize) return;
        // 预设档位过渡动画进行中：Width/Height 携带中间值，此时抬高 Min 会
        // 把动画钳死在目标尺寸上，300ms 过渡直接跳变。
        if (pendingSpan != null) return;
        // DesiredSize 只在真实测量后有意义（挂载初期为 0），等下一轮布局。
        if (ContentPresenter.Child is not Layoutable child || !child.IsMeasureValid) return;

        var desired = child.DesiredSize;
        var (minW, minH) = ComputeContentMinWindowSize(desired, WidgetMargin, WidgetOutlineThickness);
        if (Math.Abs(minW - appliedMinWidth) < 0.5 && Math.Abs(minH - appliedMinHeight) < 0.5) return;
        appliedMinWidth = minW;
        appliedMinHeight = minH;

        MinWidth = minW;
        MinHeight = minH;

        // 实际尺寸低于新下限（旧版本存的布局、locale 切到更长文案后）：长到能放下
        // 为止。只增不减——空间变大时视图会展示更多内容，Min 跟着涨是正常方向；
        // 绝不自动缩窗，避免和用户手里正在进行的拖拽打架。
        if (Width < minW || Height < minH)
        {
            Width = Math.Max(Width, minW);
            Height = Math.Max(Height, minH);
            AfterResize();
        }
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

        var scaling = Screens.ScreenFromWindow(this)?.Scaling ?? 1.0;
        var margin = (int) Math.Round(WidgetMargin.Left * scaling);
        var width = (int) Math.Round(ClientSize.Width * scaling);
        var height = (int) Math.Round(ClientSize.Height * scaling);

        var cardWidth = Math.Max(1, width - 2 * margin);
        var cardHeight = Math.Max(1, height - 2 * margin);
        var cardRadius = (int) Math.Round(ResolveEffectiveRadius(appSettingsProvider.Get().Dimensions.Radius));

        // The stack pagination dots are placed inside the card on this surface
        // (see StackIndicatorsMargin) — exactly because an out-of-card strip in the native
        // region would flood with the OS blur, and no fill for it works there: a translucent one
        // reads as frosted glass, an opaque one as a slab of a different material. The card
        // rectangle alone therefore also covers the dots; no extra strip is ever OR-ed in.
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
        // The tray icon can also be switched from the tray menu itself.
        Notify(nameof(TrayIconVisible));

        if (oldData?.Layout.LockSize != newData.Layout.LockSize)
            SetMinMaxSize(newData.Layout.LockSize);

        // Grid mode / grid geometry changed → re-apply the grid-driven size and
        // re-snap the position while keeping the cell span.
        if (oldData?.Layout.GridMode != newData.Layout.GridMode || oldData?.Grid != newData.Grid)
        {
            if (newData.Layout.GridMode == GridMode.Manual)
            {
                manualSpan ??= GetSpan();
                var (columns, rows) = manualSpan.Value;

                SetMinMaxSize(false);
                gridService.SetSize(this, columns, rows);
                SetMinMaxSize(true);
                AfterMove();
                AfterResize();
            }
            else
            {
                manualSpan = null;
                SetMinMaxSize(newData.Layout.LockSize);
                AfterResize();
            }
            Notify(nameof(ShowResizeHandle));
            Notify(nameof(IsFreeMode));
            Notify(nameof(SizeMenuTitle));
        }

        if (oldData?.Dimensions != newData.Dimensions)
        {
            AfterMove();
            AfterResize();
        }

        // Make bound properties reactive so style changes apply immediately
        // (margin from grid lines, corner radius, context menu, tooltip and the
        // outlined-glass highlight ring…).
        if (oldData?.Dimensions != newData.Dimensions || oldData?.Layout.GridMode != newData.Layout.GridMode
            || oldData?.Layout.LockSize != newData.Layout.LockSize || oldData?.Theme != newData.Theme)
        {
            // Surface switch (毛玻璃↔纯色): reconfigure the native transparency.
            if (oldData?.Theme?.UsesNativeBlur != newData.Theme.UsesNativeBlur)
                ApplyTransparencyHint();
            Notify(nameof(GlassMaterial));
            Notify(nameof(ShowsGlassMaterial));
            Notify(nameof(WidgetMargin));
            Notify(nameof(StackIndicatorsMargin));
            Notify(nameof(WidgetCardBackground));
            Notify(nameof(Radius));
            Notify(nameof(InnerRadius));
            Notify(nameof(PillRadius));
            Notify(nameof(SizeMenuTitle));
            Notify(nameof(ToolTipVisible));
            Notify(nameof(ShowResizeHandle));
            Notify(nameof(IsFreeMode));
            Notify(nameof(WidgetOutlineThickness));
            Notify(nameof(WidgetOutlineBrush));
            UpdateContentSize();
            ApplyWidgetRegion();
        }
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

        TransparencyLevelHint = appSettingsProvider.Get().Theme.UsesNativeBlur
            ? [WindowTransparencyLevel.AcrylicBlur]
            : [WindowTransparencyLevel.Transparent];
    }

    private void SetMinMaxSize(bool lockSize)
    {
        if (appSettingsProvider.Get().Layout.GridMode == GridMode.Manual)
        {
            // Manual grid: size is fully grid-driven — lock to the current size
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
            return;
        }

        // 自由模式的下限不能低于内容测出来的存活下限（见 UpdateContentMinSize）：
        // 解锁/初始路径重置 Min 时若直接回到 48，内容会被重新允许拖进截断区。
        var minSize = 48.0;

        MinWidth = lockSize ? Width : Math.Max(minSize, appliedMinWidth);
        MinHeight = lockSize ? Height : Math.Max(minSize, appliedMinHeight);
        MaxWidth = lockSize ? Width : double.PositiveInfinity;
        MaxHeight = lockSize ? Height : double.PositiveInfinity;
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
        Notify(nameof(EditStackChildTitle));
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
        stackDotsHideTimer?.Stop();
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
        ContentPresenter.LayoutUpdated -= OnContentLayoutUpdated;
        AttachStackWidget(null);
    }

    private void OnActualThemeVariantChanged(object? sender, EventArgs e)
    {
        Notify(nameof(WidgetCardBackground));
        Notify(nameof(WidgetOutlineBrush));
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
        }

        if (oldLayout?.ContentScale != newLayout.ContentScale)
        {
            Scale();
            Notify(nameof(ScaleMenuTitle));
        }
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

        // In manual grid mode, ignore drag if the click landed in the outer grid margin
        if (appSettingsProvider.Get().Layout.GridMode == GridMode.Manual)
        {
            var margin = WidgetMargin;
            var p = e.GetPosition(this);
            var card = new Rect(margin.Left, margin.Top,
                Math.Max(0, (ClientSize.Width > 0 ? ClientSize.Width : Width) - margin.Left - margin.Right),
                Math.Max(0, (ClientSize.Height > 0 ? ClientSize.Height : Height) - margin.Top - margin.Bottom));
            if (!card.Contains(p)) return;
        }

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

        // In manual grid mode, ignore drag if the click landed in the outer grid margin
        if (appSettingsProvider.Get().Layout.GridMode == GridMode.Manual)
        {
            var margin = WidgetMargin;
            var p = e.GetPosition(this);
            var card = new Rect(margin.Left, margin.Top,
                Math.Max(0, (ClientSize.Width > 0 ? ClientSize.Width : Width) - margin.Left - margin.Right),
                Math.Max(0, (ClientSize.Height > 0 ? ClientSize.Height : Height) - margin.Top - margin.Bottom));
            if (!card.Contains(p)) return;
        }

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
    /// handles it and the legacy mouse pipeline delivers it. When it does not, the dragged
    /// position was never saved and the widget visibly jumped back to its stored position
    /// on the next activation (dragged "from a distance"). Queuing the commit behind the
    /// move callback makes the save independent of the release event for every widget.
    /// </para>
    /// </summary>
    private void BeginMoveDragWithCommit(PointerPressedEventArgs e)
    {
        BeginMoveDrag(e);
        Dispatcher.UIThread.Post(AfterMove, DispatcherPriority.Background);
    }

    private void AfterMove()
    {
        // If a Stack edit window is open, check if this desktop widget was dropped into it
        if (StackDropCoordinator.IsActive && !IsStackWidget)
        {
            POINT pt = default;
            var cursorValid = OperatingSystem.IsWindows() && GetCursorPos(out pt);
            var cursorPos = cursorValid ? new PixelPoint(pt.X, pt.Y) : (PixelPoint?)null;
            var scaling = Screens.ScreenFromWindow(this)?.Scaling ?? 1.0;
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

        var appSettings = appSettingsProvider.Get();
        
        // Manual grid: snapping is always enforced.
        if (appSettings.Layout.GridMode == GridMode.Manual || appSettings.Layout.SnapPosition) 
            gridService.SnapPosition(this);
        
        // Cross-screen move: capture the span against the ORIGINAL screen before
        // ownership transfers (after the transfer the stored size would be read
        // against the new cell and become ambiguous, e.g. 2×2 → 1×1).
        var owning = displayMonitor.FindByConfigId(widgetLayoutProvider.ScreenId);
        var current = displayMonitor.Find(this);
        var movedToAnotherScreen = current != null && owning != null && owning.Screen.Bounds != current.Screen.Bounds;
        // owning is not null whenever movedToAnotherScreen is true (see the condition above).
        var span = movedToAnotherScreen ? (manualSpan ?? ResolveSpanFor(owning!)) : (Columns: 1, Rows: 1);
        
        TransferOwnership();
        
        if (movedToAnotherScreen && appSettings.Layout.GridMode == GridMode.Manual)
        {
            SetMinMaxSize(false);
            gridService.SetSize(this, span.Columns, span.Rows);
            SetMinMaxSize(true);
        }
        
        SaveLayout(StorePosition(widgetLayoutProvider.Get()));
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

        var config = current.Config ?? displayMonitor.EnsureConfig(current);
        var screens = layoutProvider.Get();
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

        layoutProvider.Save(screens);
        widgetLayoutProvider.ScreenId = config.Id;
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
        if (widgetLayoutProvider.ScreenId == ScreensLayout.LegacyPrimaryId)
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
        SetMinMaxSize(appSettingsProvider.Get().Layout.LockSize || appSettingsProvider.Get().Layout.GridMode == GridMode.Manual);
        pendingSpan = null;
    }

    private void AfterResize()
    {
        var appSettings = appSettingsProvider.Get();
        
        if (appSettings.Layout.GridMode == GridMode.Manual)
        {
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

    private void OnResizeHandlePressed(object? sender, PointerPressedEventArgs e)
    {
        // Manual grid: widget size is grid-driven, free resizing is disabled.
        if (appSettingsProvider.Get().Layout.GridMode == GridMode.Manual) return;
        if (appSettingsProvider.Get().Layout.LockSize) return;
        // Left button only: a middle press here must not enter the modal resize loop
        // (it would swallow the wheel and kill the stack switch chord), and a right
        // press must keep bubbling to the context menu.
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;

        CanResize = true;
        BeginResizeDrag(WindowEdge.SouthEast, e);
        CanResize = false;
        AfterResize();
        e.Handled = true;
    }

    private void Resize(object? sender, PointerPressedEventArgs e) => OnResizeHandlePressed(sender, e);

    /// <summary>
    /// The widget's cell span (columns, rows) derived from the stored pixel size.
    /// </summary>
    private (int Columns, int Rows) GetSpan()
    {
        if (appSettingsProvider.Get().Layout.GridMode != GridMode.Manual)
        {
            var dimensions = appSettingsProvider.Get().Dimensions;
            var unit = dimensions.Size + dimensions.Margin;
            return (
                Math.Max(1, (int) Math.Round((Width + dimensions.Margin) / unit)),
                Math.Max(1, (int) Math.Round((Height + dimensions.Margin) / unit)));
        }

        var (cellPx, _, _) = GetGridMetrics();
        var scaling = Screens.ScreenFromWindow(this)?.Scaling ?? 1.0;
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
        var grid = attached.Config?.Grid ?? appSettingsProvider.Get().Grid ?? DeskCanvas.Core.Models.Settings.Grid.Default;
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
        var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary ?? Screens.All.FirstOrDefault();
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
        var screen = Screens.ScreenFromWindow(this)
                     ?? Screens.Primary
                     ?? Screens.All.FirstOrDefault();
        var area = screen?.WorkingArea;
        // Per-screen manual grid (the screen the widget currently sits on),
        // falling back to the global grid, then the default.
        var grid = displayMonitor.CurrentConfig(this)?.Grid
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
        var config = newScreens.FindById(widgetLayoutProvider.ScreenId);
        var oldConfig = oldScreens?.FindById(widgetLayoutProvider.ScreenId);
        if (config == null) return; // this widget is no longer in the layout

        if (Equals(oldConfig?.Grid, config.Grid) && Equals(oldConfig?.ContentScale, config.ContentScale)
            && oldConfig?.Margin == config.Margin && oldConfig?.Radius == config.Radius)
            return;

        if (oldConfig?.Margin != config.Margin || oldConfig?.Radius != config.Radius)
        {
            AfterResize();
            Notify(nameof(WidgetMargin));
            Notify(nameof(StackIndicatorsMargin));
            Notify(nameof(Radius));
            Notify(nameof(InnerRadius));
            Notify(nameof(PillRadius));
            UpdateContentSize();
            ApplyWidgetRegion();
        }

        if (appSettingsProvider.Get().Layout.GridMode == GridMode.Manual && !Equals(oldConfig?.Grid, config.Grid))
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

        Scale();
        Notify(nameof(ScaleMenuTitle));
        ApplyWidgetRegion();
    }
}
