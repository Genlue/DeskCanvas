using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data.Converters;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DeskCanvas.Core.Interfaces;
using DeskCanvas.Core.Models.Settings;
using DeskCanvas.Locales;
using DeskCanvas.Services;
using DeskCanvas.UI.Animation;
using DeskCanvas.ViewModels;

namespace DeskCanvas.Views;

/// <summary>
/// The 聚焦搜索 overlay: a centered card that drops down over whatever the user is doing, shows
/// the shortcut folder as a Launchpad-style grid, and filters it as the user types.
/// <para>
/// <b>Why a host window and not a widget:</b> it must exist with zero widgets on the desktop,
/// must sit above any foreground app, and must not participate in the grid / bottom-band z-order.
/// See <c>docs/聚焦搜索可行性研究.md</c> §4.
/// </para>
/// <para>
/// <b>Three materials, one rule:</b> every visual decision comes from
/// <see cref="PanelAppearanceResolver"/> — the same resolver the secondary panels use. 毛玻璃 uses
/// the native acrylic backdrop under a translucent fill, 液态玻璃 hands the card to
/// <see cref="Controls.LiquidGlassSurface"/>, 全彩 paints an opaque card. Nothing here branches on
/// the material name.
/// </para>
/// <para>
/// <b>Focus zones:</b> the keyboard walks Search → (Recents →) List with the arrow keys and
/// Search → Categories → List → Preview with Tab; the zone is tracked here because the list
/// containers and the chips are focus targets the view model must not know about.
/// </para>
/// </summary>
public partial class SpotlightWindow : Window
{
    private const int WmActivate = 0x0006;
    private const int WaInactive = 0;
    private const int WaActive = 1;

    /// <summary>
    /// How long after a show an activation message is treated as "still settling". Windows tells a
    /// window that shows without taking the foreground WA_INACTIVE immediately, and releasing the
    /// summon's Alt can bounce focus back to the shell; a real click elsewhere cannot land this fast.
    /// </summary>
    private const int SummonFocusGraceMs = 500;

    /// <summary>
    /// How many times one summon may take the foreground back after a deactivation that was not
    /// the user leaving (see <see cref="ReclaimForeground"/>). Three covers the real cases — a
    /// widget being rebuilt, the shell answering the Alt release — and stops a window that keeps
    /// stealing focus from turning the handler into a loop.
    /// </summary>
    private const int MaxReclaimsPerSummon = 3;

    /// <summary>Minimum gap between two reclaims, so a burst of deactivations is one reaction.</summary>
    private const int ReclaimIntervalMs = 300;

    /// <summary>
    /// How long after a deactivation the foreground owner is read. The OS publishes the new
    /// foreground only once the activation handshake finishes, which is after this window's
    /// WM_ACTIVATE(WA_INACTIVE) has been dispatched — reading it inline reports the old owner.
    /// </summary>
    private const int DeactivationProbeMs = 90;

    /// <summary>
    /// Re-read cadence while a deactivation is still ambiguous. One read is not enough: a capture
    /// surface (Win+Shift+S 剪裁层) is created <b>after</b> the overlay was deactivated, so for the
    /// first frames the OS still names whatever was in front before — an ordinary app, which used
    /// to be read as "the user left".
    /// </summary>
    private const int DeactivationProbeStepMs = 75;

    /// <summary>
    /// Total budget for deciding a deactivation. Long enough for the slowest capture surface to
    /// appear and claim the keyboard, short enough that Alt-Tabbing away still closes the overlay
    /// without a noticeable lag.
    /// </summary>
    private const int DeactivationWatchMs = 540;

    /// <summary>Poll cadence while waiting for a capture surface to hand the keyboard back.</summary>
    private const int CaptureReturnPollMs = 140;

    /// <summary>
    /// How long to keep waiting for a capture to finish. A capture that stays open longer than this
    /// (the user left the 截图工具 up) is not going to hand the keyboard back on its own.
    /// </summary>
    private const int CaptureReturnWatchMs = 30_000;

    /// <summary>Preview column width (DIP) the settings' card width is extended by.</summary>
    private const double PreviewPaneWidth = 280;

    /// <summary>Total window width cap when the preview pane is shown.</summary>
    private const double MaxTotalWidth = 1080;

    /// <summary>Below this total width the preview pane degrades away (窄窗退化).</summary>
    private const double MinPreviewTotalWidth = 980;

    /// <summary>
    /// Floor for the preview hero image (DIP). It is the height the slot keeps while a file has no
    /// thumbnail yet (the icon state) and for an image that is wider than it is tall — the pane's
    /// 240 DIP width would otherwise give a panorama a 40 DIP letterbox strip.
    /// </summary>
    private const double PreviewMediaBaseHeight = 150;

    /// <summary>
    /// Room kept free below a hero that has grown to its ceiling. A hero sized to fill the pane
    /// <b>exactly</b> makes the vertical scrollbar appear; the scrollbar narrows the pane, which
    /// re-wraps the text under the hero, which lowers the ceiling, which hides the scrollbar again
    /// — a layout loop. A few DIP of slack is what makes "just filled the card" a stable state.
    /// </summary>
    private const double PreviewMediaCapSlack = 4;

    /// <summary>
    /// Height of the card while it is a bare search bar (搜索栏式唤出, empty query): <b>exactly one
    /// row</b> — the field's 14 + 34 + 10 margins plus the card's 10 DIP bottom air. Nothing else is
    /// drawn there (no hint line): the gesture already said "type", and a second line turned the
    /// strip into a small card. It grows to the configured height as results arrive.
    /// </summary>
    private const double SearchBarHeight = 68;

    /// <summary>Summon fade duration — critically damped: an overshooting alpha would clip at 1.</summary>
    private const int SummonFadeMs = MotionTokens.FadeMs;

    /// <summary>
    /// Summon micro-scale + rise duration, on the mod's bounce spring (岛展开 0.42 s / kBounceDamping
    /// — the same budget the secondary panels open with).
    /// </summary>
    private const int SummonScaleMs = MotionTokens.ExpandMs;

    /// <summary>Starting scale for the summon micro-scale transition.</summary>
    private const double SummonScaleFrom = MotionTokens.SummonScaleFrom;

    /// <summary>Where the summon rise starts, in DIP above the resting position (自上而下落定).</summary>
    private const double SummonRiseFrom = MotionTokens.SummonRiseFrom;

    /// <summary>
    /// The bar → card height morph (搜索条 ↔ 结果卡), on the bounce spring. Only the height and
    /// the radius travel; the width branch of <see cref="ApplyCardGeometry"/> never animates.
    /// </summary>
    private const int CardMorphMs = MotionTokens.ExpandMs;

    /// <summary>Padding over the morph duration, so the settle pass never races the last frame.</summary>
    private const int CardMorphSettlePadMs = MotionTokens.SettlePadMs;

    /// <summary>Dismiss fade + reverse morph; Hide() follows once this flight lands.</summary>
    private const int DismissAnimationMs = MotionTokens.CollapseMs;

    private readonly SpotlightViewModel viewModel;
    private readonly IAppSettingsProvider settings;

    private int selectedIndex;
    private bool applyingLayout;

    /// <summary>
    /// The live tile panel. Kept so its <c>ItemWidth</c>/<c>ItemHeight</c> can follow the grid's real
    /// content width: computing the tile size from the settings' card width instead leaves a dead
    /// strip on the right whenever the control ends up wider or narrower than that number (the
    /// reported "5 icons on the left").
    /// </summary>
    private WrapPanel? gridPanel;

    /// <summary>
    /// The 最近使用 row's panel. Same pitch as the grid's (<see cref="gridPanel"/>): one row of the
    /// same cells, so a recent icon sits exactly as wide as the same icon in the grid below.
    /// </summary>
    private WrapPanel? recentPanel;

    /// <summary>
    /// The list's own scroller. Its <see cref="ScrollViewer.Viewport"/> is the only width that is
    /// actually available to the tiles — <c>GridList.Bounds.Width</c> still contains the vertical
    /// scrollbar, so dividing by it produced N tiles that did not fit and left the last column
    /// empty (the reported "right column can never be filled").
    /// </summary>
    private ScrollViewer? gridScroll;

    /// <summary>Last applied metrics, so a layout notification cannot re-enter layout forever.</summary>
    private double lastCellWidth = -1;
    private double lastIconSize = -1;
    private double lastRowHeight = -1;
    private Thickness lastSelectionInset = default;
    private double lastLabelWidth = -1;
    private double lastLabelHeight = -1;

    /// <summary>
    /// How many lines a tile name may use (the 应用名称行数 setting). Kept here because
    /// <c>TextBlock.MaxLines</c> is an integer and cannot come from a DynamicResource, so every
    /// realized tile gets it written by <see cref="ApplyTileGeometry"/>.
    /// </summary>
    private int lastNameLines = -1;

    /// <summary>
    /// The launcher's own screen-sampling heartbeat. The overlay samples the live screen rather than
    /// the wallpaper, and the desktop behind it can change while the card is open (a video, a
    /// window being dragged, an animating page) — sampling only once at summon froze the glass on
    /// the frame that happened to be behind it at that moment, which reads as "it only ever shows
    /// the settings page I opened it from".
    /// </summary>
    private readonly DispatcherTimer screenSampleTimer = new();

    /// <summary>One capture in flight at a time; a slow BitBlt must not stack ticks.</summary>
    private bool screenSampleBusy;

    /// <summary>Whether the current material wants live screen frames (液态玻璃 + 实时采样).</summary>
    private bool screenSampling;

    /// <summary>Bounds subscription; released on close so the window is not rooted by it.</summary>
    private IDisposable? boundsSubscription;

    private SpotlightFocusZone focusZone = SpotlightFocusZone.Search;
    private int recentIndex;
    private readonly List<Button> categoryChips = [];

    /// <summary>
    /// The app-grid bar's chips, index-aligned with <see cref="SpotlightViewModel.AppCategories"/>
    /// (0 = 全部). Kept apart from <see cref="categoryChips"/>: that one is the search filter bar,
    /// this one is the launchpad's clusters, and the two are never on screen together.
    /// </summary>
    private readonly List<Button> appCategoryChips = [];

    /// <summary>The view model subscriptions made in the constructor, detached on close.</summary>
    private readonly System.Collections.Specialized.NotifyCollectionChangedEventHandler recentItemsChanged;
    private readonly System.Collections.Specialized.NotifyCollectionChangedEventHandler resultsChanged;

    /// <summary>
    /// Category index → its parsed lock gesture, rebuilt from settings. Parsing happens once per
    /// settings change instead of on every key press; an unparseable entry is simply absent, so a
    /// hand-edited settings file can never make a keystroke throw.
    /// </summary>
    private Dictionary<int, GlobalHotkeyGesture> categoryGestures = [];
    private bool previewEnabled;
    private bool previewShown;

    /// <summary>
    /// How this summon started. 应用列表式 opens on the grid; 搜索栏式 opens as a bare search field
    /// (no grid, no recents) that grows into the results the moment something is typed. The two are
    /// separate hotkeys (see <c>LauncherSettings.SearchHotkey</c>), and the mode survives until the
    /// next summon — it is a property of the gesture, not of the query.
    /// </summary>
    private SpotlightSummonMode summonMode = SpotlightSummonMode.AppList;

    private bool transitionsDisabled;

    /// <summary>
    /// Set while <see cref="ApplyThemeVariant"/> writes the window's variant, so the resulting
    /// <c>ActualThemeVariantChanged</c> does not re-enter <see cref="ApplyAppearance"/>.
    /// </summary>
    private bool applyingVariant;

    /// <summary>
    /// The platform settings this window listens to while 跟随系统 is in play. The window's own
    /// variant is pinned to the launcher's mode, so an OS flip never reaches
    /// <c>ActualThemeVariantChanged</c> — this is the only signal that Windows changed.
    /// </summary>
    private IPlatformSettings? platformSettings;

    private CancellationTokenSource? summonAnimationCts;

    // ---- card-height morph (动效交接 §5): the shared easing pair, the in-flight flags and the
    // settle timer that re-arms the resting geometry once the flight lands. ----
    private static readonly SpringEasing CardMorphEasing = SpringMotion.CreateEasing(SpringMotion.Bounce);
    private static readonly SpringEasing CardFadeEasing = SpringMotion.CreateEasing(SpringMotion.Smooth);

    /// <summary>True while the card's own height morph is in flight (液态玻璃 / 全彩).</summary>
    private bool cardMorphRunning;

    /// <summary>True while the window-height morph is in flight (亚克力 — the window IS the shape).</summary>
    private bool windowMorphRunning;

    /// <summary>The size the running morph is heading for; the settle pass lands on it.</summary>
    private double morphTargetHeight;

    /// <summary>Width counterpart of <see cref="morphTargetHeight"/> (the preview column unfolds into it).</summary>
    private double morphTargetWidth;

    /// <summary>
    /// The card's width without the preview column, i.e. the width the results list owns. The pane's
    /// revealed width is <c>cardWidth - morphBaseWidth</c>, so the results column never reflows as
    /// the pane opens — it is the card that grows, not the list that shrinks.
    /// </summary>
    private double morphBaseWidth;

    /// <summary>Screen-space centre the window is re-centred on during a 亚克力 width morph.</summary>
    private double morphCentreX = double.MinValue / 2;

    /// <summary>True once the revealed preview content has started its fade/rise.</summary>
    private bool previewRevealStarted;

    /// <summary>The rise transform the revealed preview content travels on (null before first use).</summary>
    private TranslateTransform? previewRevealRise;

    /// <summary>The configured card radius cached at morph start (never re-read per frame).</summary>
    private double morphConfigRadius;

    /// <summary>The settle timer that follows a morph and re-arms the resting card geometry.</summary>
    private DispatcherTimer? morphSettleTimer;

    /// <summary>
    /// True while 亚克力 temporarily degraded to an opaque card for a window-height morph: the
    /// OS blur is a property of the HWND, so while the window itself is mid-resize the card must
    /// paint itself instead of relying on the backdrop (动效交接 §5.3 — “卡片长出来之后玻璃才凝住”).
    /// </summary>
    private bool acrylicMorphDegraded;

    /// <summary>Whether this summon ever owned the foreground (see <see cref="OnWindowMessage"/>).</summary>
    private bool activatedSinceSummon;

    /// <summary>When the current summon showed; bounds the activation grace window.</summary>
    private long summonedAt;

    /// <summary>How many times this summon has taken the foreground back (see ReclaimForeground).</summary>
    private int reclaimsSinceSummon;

    /// <summary>When the last reclaim happened; throttles two reactions into one.</summary>
    private long lastReclaimAt;

    /// <summary>Id of the pending deactivation probe; a newer one supersedes the older.</summary>
    private long deactivationProbe;

    /// <summary>
    /// Id of the pending "wait for the capture to finish" watch; a newer one supersedes the older,
    /// and a dismiss/summon cancels it.
    /// </summary>
    private long captureWatch;

    private enum SpotlightFocusZone
    {
        Search,
        Categories,
        Recents,
        List,
        Preview,
    }

    public SpotlightWindow(SpotlightViewModel viewModel, IAppSettingsProvider settings)
    {
        InitializeComponent();

        this.viewModel = viewModel;
        this.settings = settings;

        DataContext = viewModel;

        // The lists are fed from the view model's collections directly (not through bindings):
        // the window owns the grid/list swap, and the view model stays binding-agnostic.
        // The handlers are named so Closed can detach them: the view model outlives this window
        // (it is the service's, not the window's), and a closed window still reacting to a result
        // change means UpdateMode → ScreenFromWindow on a disposed platform implementation.
        GridList.ItemsSource = viewModel.GridItems;
        ResultList.ItemsSource = viewModel.Results;
        RecentItemsControl.ItemsSource = viewModel.RecentItems;

        recentItemsChanged = (_, _) => UpdateEmptyRows();
        resultsChanged = (_, e) =>
        {
            // A wholesale rebuild (Clear, i.e. Reset) means the rows the highlight bar is
            // sitting on are about to be replaced: drop the carried position so the new list
            // gets a fresh snap instead of a glide sweeping across unrelated rows (or worse,
            // a glide that dies mid-flight and leaves the bar stranded).
            if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Reset)
                ResetResultHighlight();
            if (viewModel.IsFiltering) UpdateMode();
        };
        viewModel.RecentItems.CollectionChanged += recentItemsChanged;
        viewModel.Results.CollectionChanged += resultsChanged;
        BuildCategoryChips();
        BuildAppCategoryChips();

        WindowRoles.Register(this, WindowRole.Spotlight);

        // Live screen sampling: the interval is the user's 采样间隔, so it is re-read on every
        // settings change (UpdateScreenSampleTimer) rather than fixed here.
        screenSampleTimer.Tick += OnScreenSampleTick;

        // Focus loss is the primary dismissal path (click the desktop, switch apps). The hook
        // must run before Avalonia handles the message so a hidden window never keeps focus.
        Win32Properties.AddWndProcHookCallback(this, OnWindowMessage);

        // The TextBox swallows the arrow keys for caret movement, so navigation keys are picked
        // up on the tunneling route before it can mark them handled. Everything the field needs
        // (typing, caret Left/Right, Backspace) is left untouched.
        QueryBox.AddHandler(KeyDownEvent, OnQueryBoxTunnelKeyDown, RoutingStrategies.Tunnel);
        QueryBox.GotFocus += (_, _) => focusZone = SpotlightFocusZone.Search;

        QueryBox.TextChanged += OnQueryChanged;
        GridList.SelectionChanged += (_, _) => SyncSelectionFrom(GridList);

        // Bounds (not SizeChanged): the grid's width can change without its size event firing, e.g.
        // when the window is re-arranged at a different width by the host. Every change recomputes
        // the tile width, which is what keeps N tiles exactly filling the row.
        boundsSubscription = GridList.GetObservable(BoundsProperty).Subscribe(_ => UpdateGridMetrics());
        // The height morph drives three things per frame — the capsule radius, the glass's covered
        // rect and (亚克力) the card that follows the animating window — off the two properties
        // that actually animate, so everything stays in phase with the visible transition.
        Card.PropertyChanged += OnCardSizeAnimated;
        PropertyChanged += OnWindowSizeAnimated;
        // 毛玻璃 clips the HWND to the card's rounded rectangle (ApplyWindowRegion). That region
        // is measured from ClientSize, so a width change — the preview column growing the card by
        // 280 DIP is the one that matters — must re-cut it or the new column stays outside the
        // window and never appears. Resized fires after the layout pass, i.e. with the new size.
        Resized += (_, _) =>
        {
            RefreshWindowRegion();
            // Same reason for the glass: its draw rect is state the surface holds, and a resize is
            // the one event that says that state is stale. Covers every width path that does not
            // go through ApplyCardGeometry (a DPI change, the host re-arranging the window).
            SyncGlassShape();
        };
        // A container realized after the last metrics pass (a scroll, a reload) still has to be
        // painted with the current tile geometry — templates alone only resolve it at creation.
        GridList.ContainerPrepared += (_, _) => ApplyTileGeometry();
        // Same rule for the recents strip: it usually appears *after* the last metrics pass (the
        // first launch is what fills it), so its buttons would otherwise keep the template defaults
        // and lose the halo inset the grid's tiles have.
        RecentItemsControl.ContainerPrepared += (_, _) => ApplyTileGeometry();
        // LayoutUpdated is the safety net for the two widths that are only known after a layout
        // pass (the scroll viewport, and a scrollbar appearing). The metric guard makes it a no-op
        // when nothing moved, so this stays cheap on scroll.
        GridList.LayoutUpdated += OnGridLayoutUpdated;
        // The preview hero follows its image's aspect, so its ceiling has to be recomputed on every
        // pane layout: both inputs (the pane's own height, the height of everything under the hero)
        // are only current after a pass. Same guard — a value that did not move sets nothing.
        PreviewScroll.LayoutUpdated += OnPreviewLayoutUpdated;
        Closed += (_, _) =>
        {
            boundsSubscription?.Dispose();
            Card.PropertyChanged -= OnCardSizeAnimated;
            PropertyChanged -= OnWindowSizeAnimated;
            morphSettleTimer?.Stop();
            morphSettleTimer = null;
            cardMorphRunning = false;
            windowMorphRunning = false;
            highlightDriving = false;
            ResultList.LayoutUpdated -= OnResultListLayoutUpdated;
            GridList.LayoutUpdated -= OnGridLayoutUpdated;
            PreviewScroll.LayoutUpdated -= OnPreviewLayoutUpdated;
            summonAnimationCts?.Cancel();
            summonAnimationCts?.Dispose();
            summonAnimationCts = null;
            screenSampleTimer.Stop();
            if (platformSettings != null)
            {
                platformSettings.ColorValuesChanged -= OnPlatformColorValuesChanged;
                platformSettings = null;
            }

            // The view model belongs to the service and keeps running after this window closes.
            // Everything the constructor subscribed has to come back off, or a later keystroke
            // reaches a window whose platform implementation is gone (UpdateMode → ScreenFromWindow).
            viewModel.RecentItems.CollectionChanged -= recentItemsChanged;
            viewModel.Results.CollectionChanged -= resultsChanged;
            viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            settings.DataChanged -= OnSettingsChanged;
        };
        ResultList.SelectionChanged += (_, _) => SyncSelectionFrom(ResultList);
        // The highlight bar has to follow the SELECTED ROW, not just the selection index: a
        // scroll or a late-realized container moves the row under a constant index. Both events
        // are cheap to satisfy — the schedule call is idempotent and no-ops at rest.
        ResultList.LayoutUpdated += OnResultListLayoutUpdated;
        ResultList.ContainerPrepared += (_, _) => ScheduleResultHighlightUpdate();
        // 应用网格: ONE click opens the app (Launchpad's semantics, and what a grid of icons
        // promises). The search results keep the double-click: that list is a selection surface
        // first — a single click there is how you preview a row — while a tile has no such job.
        // handledEventsToo: a ListBoxItem may consume the gesture for its own selection, and the
        // tile's own click must still reach the window.
        GridList.AddHandler(InputElement.TappedEvent, OnGridTileTapped, RoutingStrategies.Bubble, handledEventsToo: true);
        ResultList.DoubleTapped += (_, _) => _ = ActivateSelectedAsync();
        // Right-click a tile: file it into one of the app categories (the only editor for them —
        // the overlay deliberately avoids modal dialogs, which would deactivate and dismiss it).
        GridList.AddHandler(ContextRequestedEvent, OnGridContextRequested, RoutingStrategies.Tunnel);
        OpenFolderButton.Click += (_, _) => LauncherCatalog.OpenFolder(viewModel.Folder);

        viewModel.PropertyChanged += OnViewModelPropertyChanged;

        settings.DataChanged += OnSettingsChanged;
        // The app's variant changed (system flip, 失焦自动切换, a settings save): re-resolve. A
        // variant this window pinned itself is skipped — ApplyAppearance is already running.
        ActualThemeVariantChanged += (_, _) =>
        {
            if (!applyingVariant) ApplyAppearance();
        };
        if (Application.Current?.PlatformSettings is { } platform)
        {
            platformSettings = platform;
            platform.ColorValuesChanged += OnPlatformColorValuesChanged;
        }

        Opened += (_, _) =>
        {
            // First moment the native handle exists — this is where Alt-Tab membership can be
            // dropped (a launcher must not occupy an Alt-Tab slot).
            InteropService.RemoveWindowFromAltTab(this);
            var handle = TryGetPlatformHandle()?.Handle;
            if (handle.HasValue && handle.Value != IntPtr.Zero)
            {
                transitionsDisabled = true;
                InteropService.DisableWindowTransitions(handle.Value);
            }

            // Re-apply appearance now that native handle exists so native window region and sampler can be bound
            ApplyAppearance();

            // The overlay is on screen from here on, so the live loop can start.
            UpdateScreenSampleTimer();
        };
    }

    /// <summary>Show the overlay (or hide it when already open). Bound to the global hotkey.</summary>
    /// <param name="mode">
    /// Which surface this gesture opens: the app grid (<see cref="SpotlightSummonMode.AppList"/>)
    /// or a bare search bar. Ignored when the overlay is already open — the same hotkey then just
    /// dismisses it.
    /// </param>
    public void Toggle(SpotlightSummonMode mode = SpotlightSummonMode.AppList)
    {
        // IsVisible alone is not enough: Avalonia's flag and the HWND's WS_VISIBLE can drift apart
        // (a hide that reached the HWND without going through Hide(), a failed first show). Toggling
        // on a stale flag is what made the overlay answer "already open" to a hotkey while nothing
        // was on screen — the second press then "closed" a window the user never saw.
        // Both must agree on "open": a disagreement is repaired by re-showing (see Summon).
        if (IsReallyVisible() && IsVisible)
        {
            // Open already. The gesture that opened it closes it (Spotlight's toggle); the other
            // gesture switches the surface instead — pressing "search" while the app grid is up
            // should hand you the search bar, not an empty desktop.
            if (summonMode == mode)
            {
                Dismiss("toggle");
                return;
            }

            Summon(mode);
            return;
        }

        Summon(mode);
    }

    /// <summary>Open the overlay on the monitor that owns the cursor and focus the search field.</summary>
    public void Summon(SpotlightSummonMode mode = SpotlightSummonMode.AppList)
    {
        summonMode = mode;

        // Reset query and ViewModel state FIRST before laying out and positioning the window.
        // This ensures the window is never shown with stale dimensions (e.g. preview pane from a
        // previous search, or full height in search-bar mode), preventing visual resize/move jumps.
        QueryBox.Text = string.Empty;
        viewModel.SetQuery(string.Empty);
        viewModel.SetCategory(0);
        // A cluster filter belongs to one summon, not to the grid: coming back to a grid that
        // silently hides most of the apps reads as "my shortcuts are gone".
        viewModel.SetAppCategory(0);
        // Only the app grid wears the App Store artwork; 搜索栏式唤出's empty field is a search.
        viewModel.SetAppGridVisible(mode == SpotlightSummonMode.AppList);
        UpdateClearButton();
        selectedIndex = summonMode == SpotlightSummonMode.AppList ? -1 : 0;
        recentIndex = 0;
        focusZone = SpotlightFocusZone.Search;

        viewModel.Reload();
        UpdateMode();

        ApplyLayout();
        PlaceOnActiveScreen();
        ApplyAppearance();

        // Refresh the live screen frame while the overlay is still hidden, so the first glass
        // prepare does not pay for a capture and cannot sample a frame that predates this summon.
        var launcher = settings.Get().EffectiveLauncher;
        if (launcher.GlassSampleScreen && ResolveLauncherTheme(launcher).Theme.UsesRenderedGlass)
        {
            ScreenSnapshotProvider.Capture();
        }

        // A fresh summon starts "not yet activated": Windows sends WA_INACTIVE to a window that
        // never got the foreground (the foreground-lock refuses SetForegroundWindow for a process
        // the user did not just click), and treating that as "the user clicked away" dismissed the
        // overlay inside its own Show(). See OnWindowMessage.
        activatedSinceSummon = false;
        summonedAt = Environment.TickCount64;
        reclaimsSinceSummon = 0;
        deactivationProbe++;   // a probe from the previous summon must not act on this one
        captureWatch++;        // nor a capture watch armed by the previous summon

        var handle = TryGetPlatformHandle()?.Handle;
        if (handle.HasValue && handle.Value != IntPtr.Zero)
        {
            transitionsDisabled = true;
            InteropService.DisableWindowTransitions(handle.Value);
        }

        // Prepare the summon's first frame before Show() so no full-opacity or un-animated frame
        // ever paints. 打开动画 off skips straight to the final state instead.
        var animateSummon = settings.Get().EffectiveLauncher.AnimateSummon;
        if (animateSummon)
        {
            PrepareSummonAnimationInitialState();
        }
        else
        {
            PrepareStaticSummonState();
        }

        var wasVisible = IsVisible;
        if (!IsVisible)
        {
            Show();
        }
        else if (!IsReallyVisible())
        {
            // Avalonia says visible, the OS says hidden (a hide that never reached the HWND, or a
            // show the platform refused). Re-show instead of activating a window nobody can see —
            // this drift is what makes the hotkey answer "already open" to an invisible overlay.
            Hide();
            Show();
        }

        Activate();
        BringToForeground();

        if (screenSampling && GlassSurface.IsVisible)
        {
            GlassSurface.RequestRender(immediate: true);
        }

        GlassDiagnostics.Event(
            $"[spotlight] summon mode={mode} wasVisible={wasVisible} avaloniaVisible={IsVisible} " +
            $"hwndVisible={IsReallyVisible()}");

        // The template applies on the first layout pass; focusing before that is a no-op.
        Dispatcher.UIThread.Post(() => QueryBox.Focus(), DispatcherPriority.Loaded);

        UpdateScreenSampleTimer();
        if (animateSummon)
        {
            Dispatcher.UIThread.Post(PlaySummonAnimation, DispatcherPriority.Render);
        }
    }

    /// <summary>
    /// Hide without tearing the window down — the next summon reuses it.
    /// <para>
    /// The hide itself is <b>deferred</b> behind the exit animation (reverse morph + fade, on the
    /// spring vocabulary): the overlay folds back toward the search bar instead of vanishing. The
    /// four call sites (hotkey, deactivation, Enter, Escape) keep their meaning — only the moment
    /// of <see cref="Hide"/> moves. A summon during the flight cancels the pending hide through
    /// <see cref="summonAnimationCts"/> (the same gate the summon animation uses), so
    /// "收起中再唤出" interrupts cleanly instead of stacking two animations.
    /// </para>
    /// </summary>
    /// <param name="reason">
    /// Why it closed. Recorded because "the overlay vanished on its own" is otherwise untraceable:
    /// the four paths (hotkey, deactivation, Enter, Escape) look identical from the outside.
    /// </param>
    public void Dismiss(string reason = "api")
    {
        if (!IsVisible && !IsReallyVisible()) return;

        summonAnimationCts?.Cancel();
        summonAnimationCts?.Dispose();
        var cts = new CancellationTokenSource();
        summonAnimationCts = cts;
        var token = cts.Token;

        var animate = settings.Get().EffectiveLauncher.AnimateSummon;
        var delayMs = 0;
        if (animate)
        {
            delayMs = DismissAnimationMs + CardMorphSettlePadMs;
            PlayDismissAnimation();
        }
        else
        {
            // 打开动画 off: the overlay disappears at once, exactly as before.
            SurfaceRoot.Transitions = null;
            ResetSurfaceRootTransform();
            SurfaceRoot.Opacity = 0;
        }

        DispatcherTimer.RunOnce(() =>
        {
            if (token.IsCancellationRequested) return;
            FinishDismiss(reason);
        }, TimeSpan.FromMilliseconds(delayMs));
    }

    /// <summary>
    /// The exit flight: the card folds back down-and-smaller while fading out on the critically
    /// damped spring, and — when the bar ever grew into the card (搜索栏式 with results) — the
    /// height morphes back toward the search bar first, so the leave reads as the summon reversed.
    /// </summary>
    private void PlayDismissAnimation()
    {
        var fadeDuration = TimeSpan.FromMilliseconds(DismissAnimationMs);

        SurfaceRoot.Transitions = new Transitions
        {
            new DoubleTransition { Property = Visual.OpacityProperty, Duration = fadeDuration, Easing = CardFadeEasing }
        };

        if (SurfaceRootRenderTransforms() is { } transforms)
        {
            var scaleDuration = TimeSpan.FromMilliseconds(SummonScaleMs);
            transforms.Rise.Transitions = null;
            transforms.Scale.Transitions = new Transitions
            {
                new DoubleTransition { Property = ScaleTransform.ScaleXProperty, Duration = scaleDuration, Easing = CardMorphEasing },
                new DoubleTransition { Property = ScaleTransform.ScaleYProperty, Duration = scaleDuration, Easing = CardMorphEasing },
            };

            transforms.Scale.ScaleX = SummonScaleFrom;
            transforms.Scale.ScaleY = SummonScaleFrom;
            transforms.Rise.Y = SummonRiseFrom;
        }

        SurfaceRoot.Opacity = 0;

        if (summonMode == SpotlightSummonMode.Search && Card.Height > SearchBarHeight + 1)
        {
            AnimateCardGeometry(SearchBarHeight);
        }
    }

    /// <summary>The real hide, plus every piece of teardown the old synchronous Dismiss did.</summary>
    private void FinishDismiss(string reason)
    {
        // Snap any still-armed morph to rest so the next summon starts from a clean geometry.
        morphSettleTimer?.Stop();
        morphSettleTimer = null;
        cardMorphRunning = false;
        windowMorphRunning = false;
        Card.Transitions = null;
        Transitions = null;

        SurfaceRoot.Transitions = null;
        ResetSurfaceRootTransform();
        if (Card.RenderTransform is ScaleTransform cs) cs.Transitions = null;
        if (GlassSurface.RenderTransform is ScaleTransform gs) gs.Transitions = null;
        Card.Opacity = 1;
        GlassSurface.Opacity = 1;
        ResetResultHighlight();
        Hide();
        activatedSinceSummon = false;
        deactivationProbe++;
        captureWatch++;
        screenSampleTimer.Stop();
        GlassDiagnostics.Event($"[spotlight] dismissed ({reason})");

        // Drop the full-desktop frame while the overlay is closed: the next summon captures a fresh
        // one before it shows (see <see cref="Summon"/>), so nothing is lost but an idle ~14 MiB
        // bitmap that keeps the screen of a minute ago alive. The morph degradation is released
        // here too — AFTER the hide, so the glass never samples through a torn-down capture.
        ScreenSnapshotProvider.Release();
        ExitAcrylicMorphDegradation();
    }

    /// <summary>
    /// Start/stop the launcher's own screen-sampling loop, and re-apply the user's 采样间隔.
    /// <para>
    /// The launcher is the one surface that samples the <b>live screen</b>; the shared wallpaper
    /// pacer only publishes when the wallpaper itself changes, so on a still wallpaper an open
    /// launcher would never re-render — it kept refracting whatever was behind it at summon time.
    /// This timer is that missing heartbeat, throttled again inside
    /// <see cref="ScreenSnapshotProvider.RefreshIfDue"/> so the settings' interval stays authoritative.
    /// </para>
    /// </summary>
    private void UpdateScreenSampleTimer()
    {
        var wanted = IsVisible && screenSampling && GlassSurface.IsVisible;
        if (!wanted)
        {
            screenSampleTimer.Stop();
            return;
        }

        // A 3 ms request is honoured by the provider's own clock; the dispatcher tick only has to
        // be fast enough not to be the limit, and never so fast that it starves the UI thread.
        var interval = Math.Clamp(ScreenSnapshotProvider.IntervalMs, 16, LauncherSettings.MaxGlassSampleInterval);
        screenSampleTimer.Interval = TimeSpan.FromMilliseconds(interval);
        if (!screenSampleTimer.IsEnabled) screenSampleTimer.Start();
    }

    /// <summary>
    /// One live tick: re-capture the desktop and repaint the glass only when its pixels actually
    /// changed. The capture runs on a worker — a 2560×1440 BitBlt on the UI thread would stutter
    /// the caret and the tile hover.
    /// </summary>
    private async void OnScreenSampleTick(object? sender, EventArgs e)
    {
        if (screenSampleBusy || !IsVisible || !screenSampling) return;

        screenSampleBusy = true;
        try
        {
            var changed = await Task.Run(ScreenSnapshotProvider.RefreshIfDue).ConfigureAwait(true);
            if (!changed || !IsVisible || !screenSampling) return;

            // Sampling a newer frame invalidates the prepared material, not the window's geometry —
            // RequestSampledFrame is the path that repaints without bumping the revision (which
            // would throw away an in-flight prepare on every tick).
            GlassSurface.RequestSampledFrame();
        }
        catch
        {
            // A refused capture is already traced by the provider; the loop keeps ticking.
        }
        finally
        {
            screenSampleBusy = false;
        }
    }

    /// <summary>
    /// Checks-only seam: resolve and apply the material without opening the window, so a visual
    /// check can render all three surfaces headlessly. Production code opens through
    /// <see cref="Summon"/>, which calls this on <c>Opened</c>.
    /// </summary>
    public void ApplyAppearanceForChecks()
    {
        ApplyLayout();
        ApplyAppearance();
        UpdateMode();
    }

    /// <summary>
    /// Checks-only seam: apply a summon mode without opening the window. Production reaches the
    /// same state through <see cref="Toggle"/>, which needs a real Show this harness cannot do.
    /// </summary>
    public void ApplySummonModeForChecks(SpotlightSummonMode mode)
    {
        summonMode = mode;
        viewModel.SetAppGridVisible(mode == SpotlightSummonMode.AppList);
        UpdateMode();
    }

    /// <summary>Checks-only seam: the named card part, for pixel / property assertions.</summary>
    public Border CardForChecks => Card;

    /// <summary>
    /// Checks-only seam: pre-select a result row <b>before</b> the first layout pass. A detached
    /// window only lays its list out once, so a row picked after that pass is selected in the view
    /// model but never realised in the list — the screenshot would show an empty list beside a
    /// filled pane.
    /// </summary>
    public void PreSelectResultForChecks(int index)
    {
        // Only the row index: moving the focus zone would make the next focus pass "repair" an
        // unrealised list (SelectedIndex still -1 before layout) by selecting row 0 instead.
        selectedIndex = index;
    }

    /// <summary>
    /// Checks-only seam: type into the field the way a keystroke does. A headless TextBox does not
    /// raise <c>TextChanged</c>, so the view model is told directly — but the box and the mode pass
    /// are updated too, otherwise a screenshot shows an empty field over the results it is meant
    /// to explain.
    /// </summary>
    public void SetQueryForChecks(string? text)
    {
        var value = text ?? string.Empty;
        QueryBox.Text = value;
        UpdateClearButton();

        // The view model may already hold this query (a shot prepared its state before the window
        // existed). Re-running it would clear the results — and the selection — for no reason.
        if (!string.Equals(viewModel.Query, value, StringComparison.Ordinal)) viewModel.SetQuery(value);

        if (value.Length == 0) selectedIndex = -1;
        UpdateMode();
    }

    /// <summary>
    /// Checks-only seam: push one key through the real keyboard contract (the same path the WndProc
    /// and the field's tunnel handler take), so arrow keys can be asserted without an input device.
    /// The window is never shown in the checks harness, so this skips the "on screen" gate the same
    /// way a shown window would have already passed it.
    /// </summary>
    /// <returns>Whether the contract claimed the key.</returns>
    public bool HandleKeyForChecks(Key key, KeyModifiers modifiers = KeyModifiers.None)
    {
        var args = new KeyEventArgs
        {
            RoutedEvent = InputElement.KeyDownEvent,
            Key = key,
            KeyModifiers = modifiers,
        };

        HandleKeyCore(args);
        return args.Handled;
    }

    /// <summary>Checks-only seam: the liquid-glass layer, for visibility assertions.</summary>
    public Control GlassSurfaceForChecks => GlassSurface;

    /// <summary>Checks-only seam: the card's explicit height, for morph-geometry assertions.</summary>
    public double CardHeightForChecks => Card.Height;

    /// <summary>The card's own width (the window's is final during a preview unfold).</summary>
    public double CardWidthForChecks => Card.Width;

    /// <summary>Whether the pane's own visibility rule wants it right now (预览栏只在筛选时出现).</summary>
    public bool PreviewPaneShownForChecks => previewShown;

    /// <summary>Width of the preview column as it currently stands (0…280).</summary>
    public double PreviewColumnWidthForChecks => CardColumns.ColumnDefinitions.Count >= 3
        ? CardColumns.ColumnDefinitions[2].Width.Value
        : 0;

    /// <summary>Start a geometry flight with both dimensions, for the unfold checks.</summary>
    public void AnimateCardMorphForChecks(double width, double height) => AnimateCardMorph(width, height);

    /// <summary>Checks-only seam: the card's current radius, for morph-geometry assertions.</summary>
    public double CardRadiusForChecks => Card.CornerRadius.TopLeft;

    /// <summary>Checks-only seam: whether a height morph is in flight.</summary>
    public bool CardMorphRunningForChecks => cardMorphRunning || windowMorphRunning;

    /// <summary>
    /// Checks-only seam: run the height morph without the "overlay is visible" gate a headless
    /// harness cannot pass. The transition itself needs a render clock, so a check follows this
    /// with <see cref="SettleCardMorphForChecks"/> to land on the resting geometry.
    /// </summary>
    public void AnimateCardGeometryForChecks(double height) => AnimateCardGeometry(height);

    /// <summary>Checks-only seam: land the morph's resting geometry synchronously.</summary>
    public void SettleCardMorphForChecks() => SettleCardMorph();

    /// <summary>Checks-only seam: whether the results' floating highlight bar is shown.</summary>
    public bool ResultHighlightVisibleForChecks => ResultHighlight.IsVisible;

    /// <summary>Checks-only seam: the selected row's rect the highlight is currently aiming at (null when
    /// there is no selection or no realized container) — the oracle the bar must converge on.
    /// </summary>
    public Rect? ResultHighlightTargetForChecks => ResultHighlightTarget();

    /// <summary>
    /// Checks-only seam: the highlight bar's rect in its host panel (x/y from the margin, plus
    /// the explicit size), for asserting it lands on the selected row.
    /// </summary>
    public Rect ResultHighlightRectForChecks => ResultHighlight.Bounds;

    // ---- appearance ---------------------------------------------------------------

    /// <summary>
    /// Resolve the current material and push it onto the window. Called on open, on a theme
    /// variant change and on any settings change, so 失焦自动切换 / a live material swap applies
    /// without recreating the window.
    /// </summary>
    private void ApplyAppearance()
    {
        var launcher = settings.Get().EffectiveLauncher;

        // The launcher may follow the application theme or override it field by field; either way
        // the material rule itself stays in PanelAppearanceResolver (no per-material branching here).
        // The light/dark decision comes FIRST: the card colour, the text brushes and the glass
        // coating are all resolved for it (see LauncherSettings.ResolveIsDark).
        var (global, theme, isDark) = ResolveLauncherTheme(launcher);
        var radius = launcher.EffectiveCornerRadius(settings.Get().Dimensions.Radius);
        var appearance = PanelAppearanceResolver.Resolve(theme, isDark, radius);

        // The overlay's own variant follows that decision. Everything keyed on the variant inside
        // this window — the Fluent chrome of the search field, the glass coating's light/dark
        // branch, the separator brushes — would otherwise read the widgets' mode while the card
        // paints the launcher's.
        ApplyThemeVariant(isDark);

        Card.CornerRadius = new CornerRadius(radius);
        Card.Background = ResolveCardBackground(appearance, launcher.ResolveBackgroundColor(global, isDark));
        Card.BorderBrush = appearance.CardBorderBrush;
        // Always keep the hairline: for 液态玻璃 it is the card's own outline, and it is also the
        // safety net if the glass surface cannot sample a backdrop (the card still reads as a card
        // instead of floating text).
        Card.BorderThickness = new Thickness(1);

        GlassSurface.Material = appearance.GlassMaterial;
        GlassSurface.CornerRadius = new CornerRadius(radius);
        GlassSurface.IsVisible = appearance.UseRenderedGlass;

        // 液态玻璃 has its own pipeline here: sample the live screen behind the overlay instead of
        // the wallpaper, because the launcher floats over real window content. The interval is the
        // user's (采样间隔) and is pushed into the provider here so a settings change applies to the
        // next frame without recreating anything.
        //
        // The native handle is what keeps the capture honest: with it, the provider excludes only
        // this overlay (and anything drawn above it), so the panel or page the launcher was summoned
        // from stays in the frame. Without it (before the first Show) the whole process is excluded
        // and the glass refracts whatever is behind our windows — the wallpaper — which is exactly
        // the "还是采样壁纸" report.
        var sampleScreen = appearance.UseRenderedGlass && launcher.GlassSampleScreen;
        GlassSurface.ScreenSampling = sampleScreen;
        screenSampling = sampleScreen;
        var sampler = sampleScreen ? TryGetPlatformHandle()?.Handle ?? IntPtr.Zero : IntPtr.Zero;
        ScreenSnapshotProvider.Configure(sampleScreen, launcher.GlassSampleInterval, sampler);
        UpdateScreenSampleTimer();

        // The one line that answers "why does the glass still look like the wallpaper": which
        // material resolved, whether screen sampling is on, at what interval and for which window.
        GlassDiagnostics.Event(
            $"[spotlight] material={theme.EffectiveSurface} renderedGlass={appearance.UseRenderedGlass} " +
            $"sampleScreen={sampleScreen} interval={ScreenSnapshotProvider.IntervalMs}ms " +
            $"sampler=0x{sampler:X} exclude=0x{ScreenSnapshotProvider.ExcludeWindow:X}");

        TransparencyLevelHint = appearance.TransparencyLevels;

        // A mid-morph settings change must not undo the 亚克力 morph degradation: the window is
        // mid-resize with no OS blur behind it, so the translucent level has to stay off until
        // the settle pass restores it (ExitAcrylicMorphDegradation).
        if (acrylicMorphDegraded)
            TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent };

        // Text AA follows the material (see PanelAppearanceResolver.ResolveTextRenderingMode):
        // the ClearType-class subpixel mask blends per channel assuming opaque pixels, so over
        // the translucent 毛玻璃/液态玻璃 cards — where the overlay alpha-composites with the
        // live desktop — it reads as red/green fringing (色散) and must render grayscale; the
        // opaque 全彩 card keeps subpixel, which is what keeps 11–12 px tile names crisp.
        // Applied here rather than the constructor because the material (and with 失焦自动切换
        // even the screen's state) resolves per summon and per settings change.
        SurfaceTextRendering.Apply(this, PanelAppearanceResolver.ResolveTextRenderingMode(appearance));

        // Text and selection colours are pushed into the window's resources so every template
        // (including item containers created later) reads the same rule through DynamicResource.
        // Window scope, not card scope: a DynamicResource used inside a DataTemplate resolves
        // against the dictionary that DEFINES the template (Window.Resources), not the nearest
        // ancestor in the visual tree. Card-scoped brushes reached the search field but left every
        // tile on the light-mode default, so dark mode rendered dark text on a dark card.
        Resources["SpotlightPrimaryText"] = appearance.TitleBrush;
        Resources["SpotlightSecondaryText"] = appearance.SubtitleBrush;
        Resources["SpotlightPlaceholderBrush"] = new SolidColorBrush(
            isDark ? Color.FromArgb(26, 255, 255, 255) : Color.FromArgb(20, 0, 0, 0));
        Resources["SpotlightTileHoverBrush"] = new SolidColorBrush(
            isDark ? Color.FromArgb(24, 255, 255, 255) : Color.FromArgb(15, 0, 0, 0));
        Resources["SpotlightSelectionBrush"] = BuildSelectionBrush(theme, isDark);

        ApplyWindowRegion(theme, radius);

        // The geometry usually resolves before the material does (Summon: ApplyLayout → then
        // ApplyAppearance), and a shape feed is dropped while Material is still null — so the rect
        // is re-fed here, once the surface knows what to paint.
        SyncGlassShape();
    }

    /// <summary>
    /// The launcher's own theme and light/dark mode, in one place: the settings resolve the mode
    /// (跟随全局 / 跟随系统 / 浅色 / 深色), then the theme is resolved <i>for</i> that mode.
    /// </summary>
    private (Theme Global, Theme Theme, bool IsDark) ResolveLauncherTheme(LauncherSettings launcher)
    {
        var global = PanelAppearanceResolver.ResolveEffectiveTheme(settings, this);
        var appIsDark = ActualThemeVariant == ThemeVariant.Dark;
        var isDark = launcher.ResolveIsDark(global, appIsDark, PanelAppearanceResolver.IsSystemDark(appIsDark));
        return (global, launcher.ResolveTheme(global, isDark), isDark);
    }

    /// <summary>
    /// Pin this window's theme variant to the launcher's mode. Guarded: setting it raises
    /// <c>ActualThemeVariantChanged</c>, which is one of the paths back into
    /// <see cref="ApplyAppearance"/>.
    /// </summary>
    private void ApplyThemeVariant(bool isDark)
    {
        var target = isDark ? ThemeVariant.Dark : ThemeVariant.Light;
        if (RequestedThemeVariant == target) return;

        applyingVariant = true;
        try
        {
            RequestedThemeVariant = target;
        }
        finally
        {
            applyingVariant = false;
        }
    }

    /// <summary>
    /// Windows flipped light/dark. 跟随系统 is the mode this exists for; the other modes resolve to
    /// the same answer, and re-applying is idempotent, so no mode test is needed here.
    /// </summary>
    private void OnPlatformColorValuesChanged(object? sender, PlatformColorValues e)
        => Dispatcher.UIThread.Post(() =>
        {
            if (IsVisible) ApplyAppearance();
        });

    /// <summary>
    /// The card fill for the background colour the launcher resolved (its own 背景色 or the global
    /// 纯色背景色 for the launcher's mode).
    /// <list type="bullet">
    /// <item><description><b>全彩</b> takes the colour as-is — that card is opaque by definition.</description></item>
    /// <item><description><b>毛玻璃</b> keeps the material's alpha and takes only the RGB: an opaque
    /// pick would hide the native blur behind a flat rectangle.</description></item>
    /// <item><description><b>液态玻璃</b> stays transparent here; its coating receives the colour
    /// through <see cref="LauncherSettings.ResolveTheme"/>, which is what the optics sample.</description></item>
    /// </list>
    /// </summary>
    private static IBrush ResolveCardBackground(PanelAppearance appearance, string hex)
    {
        if (!Color.TryParse(hex, out var color)) return appearance.CardBackground;

        return appearance.Surface switch
        {
            SurfaceStyle.Colorful => new SolidColorBrush(color),
            SurfaceStyle.LiquidGlass => appearance.CardBackground,
            _ => new SolidColorBrush(
                appearance.CardBackground is ISolidColorBrush { Color: var existing }
                    ? Color.FromArgb(existing.A, color.R, color.G, color.B)
                    : color),
        };
    }

    /// <summary>
    /// Accent-tinted selection, like macOS Spotlight's highlighted row. Falls back to a neutral
    /// wash when no accent is resolvable (headless checks, very early start-up).
    /// </summary>
    private static IBrush BuildSelectionBrush(Theme theme, bool isDark)
    {
        var accent = TryResolveAccent(theme);
        if (accent is { } color)
        {
            var alpha = (byte)(isDark ? 72 : 56);
            return new SolidColorBrush(Color.FromArgb(alpha, color.R, color.G, color.B));
        }

        return new SolidColorBrush(isDark ? Color.FromArgb(40, 255, 255, 255) : Color.FromArgb(26, 0, 0, 0));
    }

    private static Color? TryResolveAccent(Theme theme)
    {
        if (!string.IsNullOrWhiteSpace(theme.AccentColor)
            && Color.TryParse(theme.AccentColor, out var parsed))
        {
            return parsed;
        }

        try
        {
            if (Application.Current?.Resources.TryGetResource("SystemAccentColor", null, out var value) == true
                && value is Color systemAccent)
            {
                return systemAccent;
            }
        }
        catch
        {
            // Accent is decorative; never let its lookup break the open path.
        }

        return null;
    }

    /// <summary>
    /// Clip the native window to the card's rounded rectangle for the materials that rely on the
    /// OS backdrop (毛玻璃). The other two composite with 32-bit alpha, where a 1-bit region would
    /// only add aliased corners. Mirrors <c>SecondaryPanelWindow.ApplyWindowRegion</c>.
    /// </summary>
    private void ApplyWindowRegion(Theme theme, double radiusDip)
    {
        var handle = TryGetPlatformHandle()?.Handle;
        if (handle == null || handle.Value == IntPtr.Zero) return;

        if (!theme.UsesNativeBlur)
        {
            InteropService.ClearWindowRegion(handle.Value);
            return;
        }

        var scaling = Screens.ScreenFromWindow(this)?.Scaling ?? (DesktopScaling > 0 ? DesktopScaling : 1.0);
        if (scaling <= 0) scaling = 1.0;

        var width = Math.Max(1, (int)Math.Round(ClientSize.Width * scaling));
        var height = Math.Max(1, (int)Math.Round(ClientSize.Height * scaling));
        var radius = Math.Clamp((int)Math.Round(radiusDip * scaling), 0, Math.Min(width, height) / 2);

        InteropService.SetWindowRegion(handle.Value, 0, 0, width, height, radius);
    }

    /// <summary>
    /// Re-cut the native region for the window's CURRENT client size. Called on every resize:
    /// the 毛玻璃 region is a bitmap of the card at one size, and a window that grew for the
    /// preview column while keeping the old region renders that column outside the HWND — the
    /// reported "亚克力主题下预览栏无法弹出".
    /// </summary>
    private void RefreshWindowRegion()
    {
        if (!IsVisible) return;

        var launcher = settings.Get().EffectiveLauncher;
        var (_, theme, _) = ResolveLauncherTheme(launcher);
        ApplyWindowRegion(theme, launcher.EffectiveCornerRadius(settings.Get().Dimensions.Radius));
    }

    // ---- layout -------------------------------------------------------------------

    /// <summary>
    /// Apply the card size and the grid's static inputs from settings. The tile and icon sizes are
    /// NOT set here: they depend on the width the list really has, which is only known after a
    /// layout pass (<see cref="UpdateGridMetrics"/>).
    /// </summary>
    private void ApplyLayout()
    {
        var launcher = settings.Get().EffectiveLauncher;

        applyingLayout = true;
        try
        {
            // The panel is rebuilt rather than restyled: ItemWidth lives on the WrapPanel itself
            // and an ItemsPanelTemplate cannot see DynamicResource reliably (a 0 width there
            // collapses every tile). Rebuilding also re-flows correctly when Columns changes.
            GridList.ItemsPanel = new FuncTemplate<Panel?>(() =>
            {
                gridPanel = new WrapPanel
                {
                    Orientation = Avalonia.Layout.Orientation.Horizontal,
                };

                return gridPanel;
            });

            // The 最近使用 row is laid out on the same pitch, for the same reason: the tile is the
            // grid's tile, so the cell it sits in has to be the grid's cell. A WrapPanel (not a
            // UniformGrid) is what lets the row keep that pitch instead of stretching its items
            // across the card — the strip is left-aligned like the grid's first row.
            RecentItemsControl.ItemsPanel = new FuncTemplate<Panel?>(() =>
            {
                recentPanel = new WrapPanel
                {
                    Orientation = Avalonia.Layout.Orientation.Horizontal,
                    // A panel created after the last metrics pass (the row is built when the first
                    // recent arrives) still has to start at the current cell, not at "auto".
                    ItemWidth = lastCellWidth > 0 ? lastCellWidth : double.NaN,
                    ItemHeight = lastRowHeight > 0 ? lastRowHeight : double.NaN,
                };

                return recentPanel;
            });

            // A rebuilt panel starts with no ItemWidth, so the cache must forget the old one or
            // UpdateGridMetrics would decide "nothing changed" and leave the new panel unsized.
            lastCellWidth = -1;
            ApplyCardGeometry(launcher, force: true);
            SeedGridGeometry(launcher);
        }
        finally
        {
            applyingLayout = false;
        }
    }

    /// <summary>
    /// Size the card for the current state.
    /// <para>
    /// <b>The preview column exists only while searching.</b> On the app grid it is not merely
    /// hidden: the window gives its 280 DIP back to the grid, because a phone-shaped blank strip
    /// beside a Launchpad-style grid is worse than no preview at all. Searching brings it back and
    /// the card grows around its centre, so the motion reads as "the card made room", not as a
    /// jump to the right.
    /// </para>
    /// <para>
    /// The same rule drives the search-mode summon: an empty query there is a search <b>bar</b>
    /// (one row), not a 560 DIP card with a hole in it, and it grows to full height the moment the
    /// first result arrives — that growth is the <b>height morph</b>: instant while the overlay is
    /// not on screen (and in every headless check), on the bounce spring when it is.
    /// </para>
    /// <para>
    /// A width change is the same flight, not a separate one: the preview column is what makes the
    /// card wider, and it unfolds out of the card's own growth (<see cref="ApplyPaneReveal"/>). The
    /// only difference between the materials is <b>what carries the width</b> — 液态玻璃/全彩 jump
    /// the window to its final size and animate the card inside it, 亚克力 animates the window and
    /// the card follows, re-centring on every step because the overlay is centred on the screen.
    /// </para>
    /// </summary>
    private void ApplyCardGeometry(LauncherSettings launcher, bool force = false)
    {
        var filtering = viewModel.IsFiltering;
        // PreviewBar on AND something to preview: the pane follows the result list, not the window.
        previewEnabled = launcher.PreviewBar && filtering;
        var width = previewEnabled ? Math.Min(launcher.Width + PreviewPaneWidth, MaxTotalWidth) : launcher.Width;
        var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
        var scaling = screen?.Scaling ?? 1.0;
        if (scaling <= 0) scaling = 1.0;
        var screenMaxHeight = screen != null
            ? (screen.WorkingArea.Height / scaling - 30)
            : LauncherSettings.MaxHeight;
        var targetHeight = Math.Min(launcher.Height, screenMaxHeight);
        var height = summonMode == SpotlightSummonMode.Search && !filtering
            ? SearchBarHeight
            : targetHeight;

        var widthChanged = Math.Abs(Width - width) > 0.5;
        var heightChanged = Math.Abs(Height - height) > 0.5;

        // The card keeps its own width so the preview column can unfold *inside* a window that is
        // already at its final size. Both dimensions travel on one flight: "results arrived" is a
        // single gesture (grow down, make room to the right), not a drop followed by a slide.
        morphBaseWidth = Math.Min(launcher.Width, MaxTotalWidth);

        // Whether this change is a flight decides how the window's width gets set below — both the
        // centring shift and the assignment itself are the instant path's job, never both.
        var morphing = (widthChanged || heightChanged || force) && CanAnimateCardMorph(width, height);

        // 亚克力 animates the window's own width, so the shift happens per frame in
        // RecentreDuringWidthMorph. Doing it here as well would jump the window left by half the
        // growth *before* the growth starts, and then grow it back out from there.
        var windowWidthMorphs = morphing && UsesNativeBlurTheme();
        if (widthChanged && !force && IsVisible && !windowWidthMorphs)
        {
            // Grow/shrink around the card's centre: the window is centred by PlaceOnActiveScreen,
            // and letting it grow only to the right would visibly slide the card off centre.
            // 液态玻璃/全彩 need it here because their window lands at its final width at once.
            scaling = screen?.Scaling ?? (DesktopScaling > 0 ? DesktopScaling : 1.0);
            if (scaling <= 0) scaling = 1.0;
            var delta = (int)Math.Round((width - Width) * scaling / 2);
            Position = new PixelPoint(Position.X - delta, Position.Y);
        }

        // The width is never written here: AnimateCardMorph owns it for the flight (for 亚克力 the
        // assignment is what starts the transition — writing it first left the property already at
        // its target, so the width never animated and the card stayed narrow until the settle), and
        // ApplyCardGeometryInstant writes it on every non-animated path.
        if (widthChanged || heightChanged || force)
        {
            if (morphing)
            {
                AnimateCardMorph(width, height);
            }
            else
            {
                ApplyCardGeometryInstant(width, height);
            }
        }

        // While a flight owns the geometry, the pane column is driven per frame by
        // SyncMorphingCardShape — writing the resting value here would snap it to the end state on
        // the first frame of the animation.
        if (!CardMorphInFlight) UpdatePreviewVisibility();

        // A width change moves the card's right edge — the preview column appearing is exactly
        // that. On the instant path the glass has to be told right here, or it keeps drawing the
        // narrow rect it was last given and the new column sits on bare desktop; mid-flight the
        // per-frame driver feeds it instead, and this call only pins the departure frame.
        SyncGlassShape();
    }

    /// <summary>
    /// Whether the bar ↔ card height change may morph: only a visible overlay with the animation
    /// enabled. Everything else (first layout, headless checks, settings re-layouts while hidden)
    /// takes the instant path, so no check ever sees a half-way height.
    /// </summary>
    private bool CanAnimateCardMorph(double width, double height)
        => IsVisible
           && settings.Get().EffectiveLauncher.AnimateSummon
           && !applyingLayout
           && (Math.Abs(Card.Height - height) > 0.5 || Math.Abs(Card.Width - width) > 0.5);

    /// <summary>True while a geometry flight (card or window) owns the card's size.</summary>
    private bool CardMorphInFlight => cardMorphRunning || windowMorphRunning;

    /// <summary>
    /// Resize the window and the card to a height with no transition at all: the resting geometry
    /// every non-animated path (and every headless check) lands on.
    /// </summary>
    private void ApplyCardGeometryInstant(double width, double height)
    {
        morphSettleTimer?.Stop();
        morphSettleTimer = null;
        cardMorphRunning = false;
        windowMorphRunning = false;
        Card.Transitions = null;
        if (Transitions != null) Transitions = null;
        ExitAcrylicMorphDegradation();

        morphTargetWidth = width;
        morphTargetHeight = height;
        morphConfigRadius = ConfiguredCardRadius();

        Width = width;
        Height = height;
        Card.Width = width;
        Card.Height = height;
        Card.CornerRadius = new CornerRadius(morphConfigRadius);
        UpdatePreviewVisibility();
        EndPreviewReveal();
        if (GlassSurface.IsVisible)
            GlassSurface.SetAnimationFrameShape(new Rect(0, 0, width, height), morphConfigRadius);
    }

    /// <summary>
    /// One geometry flight, split by material exactly as the material physics demand (动效交接 §5.3):
    /// <list type="bullet">
    /// <item><description><b>液态玻璃 / 全彩</b> — the window lands at its final size at once (no
    /// <c>SetWindowPos</c> storm, no region re-cut) and the <b>card</b> travels inside it; the glass
    /// follows the covered rect every frame through <see cref="GlassSurfaceBase.SetAnimationFrameShape"/>.
    /// A width change here is what makes the preview column unfold instead of appearing.</description></item>
    /// <item><description><b>亚克力</b> — the window IS the shape, so the window's own width and
    /// height animate (every step is a resize + a region re-cut in <see cref="RefreshWindowRegion"/>),
    /// the window is re-centred on every width step because the overlay is centred on the screen, and
    /// the card temporarily paints itself opaque because there is no OS blur behind a mid-resize
    /// window.</description></item>
    /// </list>
    /// </summary>
    private void AnimateCardMorph(double width, double height)
    {
        morphTargetWidth = width;
        morphTargetHeight = height;
        morphConfigRadius = ConfiguredCardRadius();

        var fromWidth = double.IsNaN(Card.Width) || Card.Width <= 0 ? width : Card.Width;
        var fromHeight = double.IsNaN(Card.Height) || Card.Height <= 0 ? Height : Card.Height;
        morphSettleTimer?.Stop();

        var duration = TimeSpan.FromMilliseconds(CardMorphMs);
        if (UsesNativeBlurTheme())
        {
            EnterAcrylicMorphDegradation();
            Card.Transitions = null;
            Card.Width = fromWidth;
            Card.Height = fromHeight;
            windowMorphRunning = true;
            cardMorphRunning = false;

            // Growing a centred window only to the right would slide the card off centre, so the
            // centre is captured once and the left edge follows every width step.
            morphCentreX = Position.X + Width * WindowScaling() / 2;

            Transitions = new Transitions
            {
                new DoubleTransition { Property = WidthProperty, Duration = duration, Easing = CardMorphEasing },
                new DoubleTransition { Property = HeightProperty, Duration = duration, Easing = CardMorphEasing },
            };
            Width = width;
            Height = height;
            SyncMorphingCardShape(fromWidth, fromHeight);
        }
        else
        {
            ExitAcrylicMorphDegradation();
            // The window is final from the first frame; only the card (and the glass) travel.
            Width = width;
            Height = height;
            windowMorphRunning = false;
            cardMorphRunning = true;
            Card.Transitions = new Transitions
            {
                new DoubleTransition { Property = WidthProperty, Duration = duration, Easing = CardMorphEasing },
                new DoubleTransition { Property = HeightProperty, Duration = duration, Easing = CardMorphEasing },
            };
            Card.Width = width;
            Card.Height = height;
            // The first frame the transition covers is still the pre-morph rect (the property's
            // animated value starts at the departure size), so that is what the glass is fed.
            SyncMorphingCardShape(fromWidth, fromHeight);
        }

        morphSettleTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(CardMorphMs + CardMorphSettlePadMs) };
        morphSettleTimer.Tick += (_, _) =>
        {
            morphSettleTimer?.Stop();
            morphSettleTimer = null;
            SettleCardMorph();
        };
        morphSettleTimer.Start();

        // The rows' entrance belongs to the one morph that means "results just arrived": the bar
        // growing into the card. A per-keystroke filter rebuild changes nothing here (the height
        // never moves), so the list cannot flicker on typing.
        if (fromHeight <= SearchBarHeight + 1 && height > fromHeight + 1 && viewModel.IsFiltering)
        {
            StaggerResultRows();
        }
    }

    /// <summary>Compatibility entry point for the checks and the height-only call sites.</summary>
    private void AnimateCardGeometry(double height) => AnimateCardMorph(Width, height);

    /// <summary>The morph is over (or the window hid mid-flight): land on the resting geometry.</summary>
    private void SettleCardMorph()
    {
        morphSettleTimer = null;
        cardMorphRunning = false;
        windowMorphRunning = false;
        Card.Transitions = null;
        Transitions = null;

        // Always land, hidden or not: a dismiss that outran the morph leaves the card on its
        // animated size, and the next summon must start from the resting geometry whatever path it
        // takes. (The glass's shape feed and the region are both no-ops while hidden.)
        Card.Width = morphTargetWidth;
        Card.Height = morphTargetHeight;
        Width = morphTargetWidth;
        Height = morphTargetHeight;
        Card.CornerRadius = new CornerRadius(morphConfigRadius);
        UpdatePreviewVisibility();
        if (GlassSurface.IsVisible)
            GlassSurface.SetAnimationFrameShape(new Rect(0, 0, morphTargetWidth, morphTargetHeight), morphConfigRadius);
        ExitAcrylicMorphDegradation();
    }

    /// <summary>Screen scaling of the window's display, for the per-step recentring.</summary>
    private double WindowScaling()
    {
        var scaling = Screens.ScreenFromWindow(this)?.Scaling ?? (DesktopScaling > 0 ? DesktopScaling : 1.0);
        return scaling > 0 ? scaling : 1.0;
    }

    /// <summary>The card radius the settings resolve right now (the morph caches it once per flight).</summary>
    private double ConfiguredCardRadius()
        => settings.Get().EffectiveLauncher.EffectiveCornerRadius(settings.Get().Dimensions.Radius);

    /// <summary>Whether the launcher currently resolves to the OS-blurred material (亚克力).</summary>
    private bool UsesNativeBlurTheme()
        => ResolveLauncherTheme(settings.Get().EffectiveLauncher).Theme.UsesNativeBlur;

    /// <summary>The capsule rule while the card morphs: never wider than half the current height.</summary>
    private double MorphRadius(double height) => Math.Min(morphConfigRadius, height / 2);

    /// <summary>
    /// Feed the glass surface the rect the card covers <b>right now</b>, whatever moved it.
    /// <para>
    /// The height morph has its own per-frame driver (<see cref="SyncMorphingCardShape"/>, which
    /// also drives the width once the card's width is the thing that moves). What this answers is
    /// every path <i>outside</i> a flight: the preview column widening the card by
    /// <see cref="PreviewPaneWidth"/> DIP in a single step, with the surface still painting the
    /// rect it was last given. That is why 液态玻璃 showed the preview column over bare desktop —
    /// the window had grown, the glass had not. It is the same failure 毛玻璃 had with its native
    /// region, which <see cref="RefreshWindowRegion"/> answers by re-cutting on every resize.
    /// </para>
    /// <para>
    /// Called from <see cref="ApplyCardGeometry"/> (the width branch), <see cref="ApplyAppearance"/>
    /// (the material can resolve <i>after</i> the geometry, and a feed before the material exists is
    /// dropped) and the <c>Resized</c> handler (any other path that moves the card's edge, including
    /// a DPI change). During a morph the card's animated height is what is real, so the height is
    /// read off the card rather than from the morph target.
    /// </para>
    /// </summary>
    private void SyncGlassShape()
    {
        if (!GlassSurface.IsVisible) return;

        var height = Card.Height;
        if (double.IsNaN(height) || height <= 0) height = Height;
        if (double.IsNaN(height) || height <= 0) return;

        // The card's width, not the window's: while the preview column unfolds the window is
        // already final and only the card is narrower. Reading the window here was what left the
        // glass painting the pre-unfold rect for the whole flight.
        var width = Card.Width;
        if (double.IsNaN(width) || width <= 0) width = Width;
        if (double.IsNaN(width) || width <= 0) width = Bounds.Width;
        if (double.IsNaN(width) || width <= 0) return;

        // The card's own radius, not the settings': mid-morph it is the capsule of the animated
        // height, at rest it is the configured radius ApplyAppearance pushed onto both of them.
        var radius = Card.CornerRadius.TopLeft;
        if (radius <= 0) radius = morphConfigRadius;

        GlassSurface.SetAnimationFrameShape(new Rect(0, 0, Math.Max(1, width), Math.Max(1, height)), radius);
    }

    /// <summary>
    /// Per morph frame: keep the card's radius a capsule of its current height and feed the glass
    /// the rect that is actually covered, so the refraction follows the card's edge instead of
    /// the window's.
    /// </summary>
    private void SyncMorphingCardShape(double width, double height)
    {
        var radius = MorphRadius(height);
        if (Math.Abs(Card.CornerRadius.TopLeft - radius) > 0.01)
            Card.CornerRadius = new CornerRadius(radius);

        // The pane's reveal rides the card's width, so the column is always exactly as wide as the
        // room the card just made. That is what keeps the results column from re-wrapping mid-flight.
        ApplyPaneReveal(width);

        if (GlassSurface.IsVisible)
            GlassSurface.SetAnimationFrameShape(new Rect(0, 0, Math.Max(1, width), Math.Max(1, height)), radius);
    }

    /// <summary>
    /// Drive the preview column from the card's current width: 0 when the card is only as wide as
    /// the results list, <see cref="PreviewPaneWidth"/> once it has made the full room.
    /// </summary>
    private void ApplyPaneReveal(double cardWidth)
    {
        if (morphBaseWidth <= 0) return;

        var pane = Math.Clamp(cardWidth - morphBaseWidth, 0, PreviewPaneWidth);
        var shown = pane > 0.5;
        if (shown) BeginPreviewReveal(); else EndPreviewReveal();

        PreviewPane.IsVisible = shown;
        PreviewHairline.IsVisible = shown;

        var columns = CardColumns.ColumnDefinitions;
        if (columns.Count >= 3)
        {
            columns[1].Width = new GridLength(shown ? 1 : 0);
            columns[2].Width = new GridLength(pane);
        }
    }

    /// <summary>
    /// The pane's content arrives <b>after</b> the card has made room, not at the same instant: a
    /// fade plus an 8 DIP rise, delayed by <see cref="MotionTokens.PaneRevealDelayMs"/>. Without the
    /// delay the content is already there when the column starts to widen, which reads as the text
    /// being squeezed out rather than the pane unfolding.
    /// </summary>
    private void BeginPreviewReveal()
    {
        if (previewRevealStarted) return;
        previewRevealStarted = true;

        PreviewContent.Opacity = 0;
        previewRevealRise = new TranslateTransform(0, MotionTokens.PaneRevealRiseFrom);
        PreviewContent.RenderTransform = previewRevealRise;

        var rise = previewRevealRise;
        DispatcherTimer.RunOnce(() =>
        {
            // The pane may have collapsed (or been re-revealed with a new transform) inside the
            // delay: only the flight that is still installed gets to run.
            if (!previewRevealStarted || !ReferenceEquals(PreviewContent.RenderTransform, rise))
                return;

            PreviewContent.Transitions = new Transitions
            {
                new DoubleTransition { Property = Visual.OpacityProperty, Duration = ResultStaggerDuration, Easing = CardFadeEasing },
            };
            rise.Transitions = new Transitions
            {
                new DoubleTransition { Property = TranslateTransform.YProperty, Duration = ResultStaggerDuration, Easing = CardFadeEasing },
            };
            PreviewContent.Opacity = 1;
            rise.Y = 0;

            DispatcherTimer.RunOnce(() =>
            {
                PreviewContent.Transitions = null;
                rise.Transitions = null;
                if (ReferenceEquals(PreviewContent.RenderTransform, rise)) PreviewContent.RenderTransform = null;
            }, ResultStaggerDuration + TimeSpan.FromMilliseconds(40));
        }, TimeSpan.FromMilliseconds(MotionTokens.PaneRevealDelayMs));
    }

    /// <summary>The pane collapsed (or the flight was abandoned): drop the reveal state.</summary>
    private void EndPreviewReveal()
    {
        if (!previewRevealStarted) return;
        previewRevealStarted = false;
        PreviewContent.Transitions = null;
        PreviewContent.Opacity = 1;
        if (previewRevealRise != null)
        {
            previewRevealRise.Transitions = null;
            if (ReferenceEquals(PreviewContent.RenderTransform, previewRevealRise))
                PreviewContent.RenderTransform = null;
            previewRevealRise = null;
        }
    }

    private void OnCardSizeAnimated(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (!cardMorphRunning) return;
        if (e.Property != WidthProperty && e.Property != HeightProperty) return;
        SyncMorphingCardShape(Card.Width, Card.Height);
    }

    private void OnWindowSizeAnimated(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (!windowMorphRunning) return;

        if (e.Property == WidthProperty)
        {
            // 亚克力: the card follows the animating window (the window is the shape), the 1-bit
            // region is re-cut by the Resized handler on every step, and the window is re-centred
            // because it grows around the screen centre rather than to the right.
            Card.Width = Width;
            RecentreDuringWidthMorph();
        }
        else if (e.Property == HeightProperty)
        {
            Card.Height = Height;
        }
        else
        {
            return;
        }

        SyncMorphingCardShape(Card.Width, Card.Height);
    }

    private void RecentreDuringWidthMorph()
    {
        if (morphCentreX <= double.MinValue / 2) return;
        var x = (int)Math.Round(morphCentreX - Width * WindowScaling() / 2);
        if (Position.X != x) Position = new PixelPoint(x, Position.Y);
    }

    /// <summary>
    /// 亚克力's morph degradation: while the window itself is mid-resize there is no settled OS
    /// blur behind it, so the card paints itself opaque for the flight — "the card grows, then
    /// the glass sets" reads better than a region-edge that jitters for 320 ms.
    /// </summary>
    private void EnterAcrylicMorphDegradation()
    {
        if (acrylicMorphDegraded) return;
        acrylicMorphDegraded = true;
        if (Card.Background is ISolidColorBrush { Color: var color })
            Card.Background = new SolidColorBrush(Color.FromArgb(255, color.R, color.G, color.B));
        TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent };
    }

    /// <summary>
    /// End the morph degradation. ApplyAppearance is idempotent and the single source of the card
    /// fill, the transparency level and the native region — re-running it restores exactly the
    /// pre-degradation state, whatever changed in the meantime.
    /// </summary>
    private void ExitAcrylicMorphDegradation()
    {
        if (!acrylicMorphDegraded) return;
        acrylicMorphDegraded = false;
        ApplyAppearance();
    }

    /// <summary>Per-row delay of the results stagger (12–18 ms, ≤120 ms total — 动效交接 §5.2-3).</summary>
    private const int ResultStaggerStepMs = MotionTokens.RowStaggerStepMs;

    /// <summary>One row's stagger flight: an opacity fade-in plus a 4 DIP rise.</summary>
    private static readonly TimeSpan ResultStaggerDuration = TimeSpan.FromMilliseconds(MotionTokens.RowStaggerMs);

    /// <summary>
    /// The results' entrance: the realized rows fade in and rise 4 DIP, staggered ~14 ms apart.
    /// Runs only for the bar → card morph (the first results arriving) — never for a filter
    /// rebuild, which is what keeps "输入即筛选" flicker-free.
    /// </summary>
    private void StaggerResultRows()
    {
        var rows = ResultList.GetRealizedContainers().Take(8).ToList();
        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            row.Opacity = 0;
            var rise = new TranslateTransform(0, MotionTokens.RowRiseFrom);
            row.RenderTransform = rise;

            DispatcherTimer.RunOnce(() =>
            {
                // The list may have been rebuilt by a faster keystroke — only an attached row animates.
                if (row.Parent == null) return;
                row.Transitions = new Transitions
                {
                    new DoubleTransition { Property = Visual.OpacityProperty, Duration = ResultStaggerDuration, Easing = CardFadeEasing },
                };
                rise.Transitions = new Transitions
                {
                    new DoubleTransition { Property = TranslateTransform.YProperty, Duration = ResultStaggerDuration, Easing = CardFadeEasing },
                };
                row.Opacity = 1;
                rise.Y = 0;

                // Detach once the flight lands: a lingering opacity transition would swallow any
                // later programmatic write on the same row.
                DispatcherTimer.RunOnce(() =>
                {
                    row.Transitions = null;
                    rise.Transitions = null;
                    if (ReferenceEquals(row.RenderTransform, rise)) row.RenderTransform = null;
                }, ResultStaggerDuration + TimeSpan.FromMilliseconds(40));
            }, TimeSpan.FromMilliseconds(i * ResultStaggerStepMs));
        }
    }

    /// <summary>Show or collapse the preview column; the width rule lives with the layout.</summary>
    private void UpdatePreviewVisibility()
    {
        previewShown = previewEnabled && Width >= MinPreviewTotalWidth;
        PreviewPane.IsVisible = previewShown;
        PreviewHairline.IsVisible = previewShown;

        var columns = CardColumns.ColumnDefinitions;
        if (columns.Count >= 3)
        {
            columns[1].Width = new GridLength(previewShown ? 1 : 0);
            columns[2].Width = new GridLength(previewShown ? PreviewPaneWidth : 0);
        }

        // Resized covers a width change, but the pane can also appear without the window's size
        // moving (a narrow card where the column was already reserved). Post once after layout:
        // the region must always describe the card that is actually on screen.
        if (IsVisible)
        {
            Dispatcher.UIThread.Post(RefreshWindowRegion, DispatcherPriority.Loaded);
        }
    }

    private void OnPreviewLayoutUpdated(object? sender, EventArgs e) => UpdatePreviewMediaMaxHeight();

    /// <summary>
    /// Cap the hero image at the room the pane has left under it.
    /// <para>
    /// The frame has no <c>Height</c> of its own: the image inside is measured Uniform against the
    /// pane's width, so the box takes the content's aspect and a portrait page is drawn as tall as
    /// it is. What a fixed height used to guarantee — a preview that never pushes the details out
    /// of the card — is now this ceiling. Everything under the hero (title, type line, detail rows,
    /// excerpt, action buttons) keeps its measured height, so the hero may take what is left of the
    /// card and no more. The card's height is the 最大高度 from settings (clamped to the screen in
    /// <see cref="ApplyCardGeometry"/>), which is exactly "settings height minus the info below".
    /// </para>
    /// <para>
    /// Runs from <c>LayoutUpdated</c> because <see cref="Layoutable.Bounds"/> of the siblings below
    /// is only current after the pass that arranged them, and because the pane's height changes
    /// with no event of its own (the card is resized from settings). Setting
    /// <see cref="Layoutable.MaxHeight"/> invalidates a measure, so the 0.5 DIP guard is what keeps
    /// the second pass from scheduling a third; <see cref="PreviewMediaCapSlack"/> is what keeps a
    /// hero that fills the pane exactly from flipping the scrollbar on and off forever.
    /// </para>
    /// </summary>
    private void UpdatePreviewMediaMaxHeight()
    {
        if (PreviewContent == null || !PreviewContent.IsVisible) return;

        var viewport = PreviewScroll.Bounds.Height;
        if (viewport <= 0) return;

        var below = 0.0;
        var gaps = 0;
        var afterHero = false;
        foreach (var child in PreviewContent.Children)
        {
            if (!afterHero)
            {
                if (!ReferenceEquals(child, PreviewMediaFrame)) continue;
                afterHero = true;
                continue;
            }

            // Collapsed children contribute neither a height nor a gap — the StackPanel skips
            // them the same way, so the sum below equals what the pane really spends.
            if (!child.IsVisible) continue;
            below += child.Bounds.Height;
            gaps++;
        }

        var available = viewport
            - PreviewContent.Margin.Top - PreviewContent.Margin.Bottom
            - below - PreviewContent.Spacing * gaps
            - PreviewMediaCapSlack;

        var cap = Math.Max(PreviewMediaBaseHeight, available);
        if (Math.Abs(PreviewMediaFrame.MaxHeight - cap) > 0.5) PreviewMediaFrame.MaxHeight = cap;
    }

    /// <summary>
    /// Derive the grid from the card width <b>before</b> the first layout pass.
    /// <para>
    /// The tiles are created by that pass, and a template reads its geometry resources once, when it
    /// is built — so without a seed the first frame is painted with the placeholder sizes and only
    /// the frame after it shows the real grid. <see cref="UpdateGridMetrics"/> refines the numbers
    /// from the list's real viewport a moment later (the scrollbar and the hairline make this
    /// estimate a pixel or two wide); nothing here is ever the final word, it just means the panel is
    /// never left unsized.
    /// </para>
    /// </summary>
    private void SeedGridGeometry(LauncherSettings launcher)
    {
        // Card width minus the preview column when present, minus the list's 18 px margins on both
        // sides and the 1 px hairline. The second argument is 0: an estimate is not a measurement
        // and must not be traced as one.
        var contentWidth = Width - (previewShown ? PreviewPaneWidth + 1 : 0);
        var available = Math.Max(1, contentWidth - 37);
        ApplyMetrics(launcher.GridFor(available), available: 0);
    }

    private void OnGridLayoutUpdated(object? sender, EventArgs e) => UpdateGridMetrics();

    private void OnResultListLayoutUpdated(object? sender, EventArgs e) => ScheduleResultHighlightUpdate();

    /// <summary>
    /// Derive the cell, the icon and the row height from the width the tiles actually get.
    /// <para>
    /// The width must come from the list's <see cref="ScrollViewer.Viewport"/>: <c>GridList.Bounds</c>
    /// still contains the vertical scrollbar, so dividing it by the column count produced N tiles
    /// that did not fit — the last column stayed empty and the row overflowed into a scrollbar.
    /// </para>
    /// <para>
    /// The icon size is a <b>result</b>, not an input: <c>icon = cell − cell × gap%</c>. That is what
    /// makes "N icons, evenly spread" hold at any window width.
    /// </para>
    /// </summary>
    private void UpdateGridMetrics()
    {
        if (applyingLayout || gridPanel == null) return;

        gridScroll ??= GridList.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();

        var available = gridScroll?.Viewport.Width ?? 0;
        if (available < 1) available = GridList.Bounds.Width;
        if (available < 1) return;

        ApplyMetrics(settings.Get().EffectiveLauncher.GridFor(available), available);
    }

    /// <summary>
    /// Push one derived grid onto the panel, the tile resources and the live tiles.
    /// </summary>
    /// <param name="metrics">The geometry to apply.</param>
    /// <param name="available">
    /// The width the metrics were derived from, for the trace. 0 when the caller only had an estimate
    /// (<see cref="SeedGridGeometry"/>), which must not be logged as if it were measured.
    /// </param>
    private void ApplyMetrics(LauncherGridMetrics metrics, double available)
    {
        var changed = StoreMetrics(metrics);

        // Unconditional: the panel is rebuilt on every ApplyLayout and the seed runs before the
        // template has created it, so "the numbers did not change" says nothing about whether the
        // live panel has ever been told them. Same-value setters are no-ops.
        if (gridPanel != null)
        {
            gridPanel.ItemWidth = metrics.CellWidth;
            gridPanel.ItemHeight = metrics.RowHeight;
        }

        // The recents strip follows the grid's cell, not its own width: that is what makes the two
        // rows line up column for column.
        if (recentPanel != null)
        {
            recentPanel.ItemWidth = metrics.CellWidth;
            recentPanel.ItemHeight = metrics.RowHeight;
        }

        ApplyTileGeometry();
        if (!changed || available < 1) return;

        // The grid's geometry is derived, so the trace is the only place to see what it resolved
        // to: "N icons per row, each this big" is the whole contract of the new model.
        GlassDiagnostics.Event(
            $"[spotlight] grid {metrics.Columns}×{available:F0}px cell {metrics.CellWidth:F1} " +
            $"icon {metrics.IconSize:F1} row {metrics.RowHeight:F1} names {settings.Get().EffectiveLauncher.ShowNames} " +
            $"rowGap {settings.Get().EffectiveLauncher.RowGapPercent}% inset {lastSelectionInset.Left:F1},{lastSelectionInset.Top:F1}");
    }

    /// <summary>
    /// Derive the halo and remember the numbers. Returns true when anything changed, so the caller
    /// knows whether the panel and the tiles have to be touched. Deliberately free of the panel: the
    /// seed runs before the first layout pass has created it.
    /// </summary>
    private bool StoreMetrics(LauncherGridMetrics metrics)
    {
        // The selection highlight is a halo around the TILE (icon + label), not the whole cell: a
        // cell-sized box around a small icon (a 6-column grid with 45% gutters is 130 × 147 for a
        // 72 px icon) reads as a giant empty rectangle. `pad` is the air the halo keeps around the
        // content, capped by the row gutter so two neighbouring halos can never touch; the insets
        // that produce it are the leftover gutter split between the two sides.
        var rowGutter = Math.Max(0, metrics.RowHeight - metrics.ContentHeight);
        var pad = Math.Max(2.0, Math.Min(8.0, rowGutter / 2));
        var selectionInset = new Thickness(
            Math.Max(0, (metrics.CellWidth - metrics.IconSize) / 2 - pad),
            Math.Max(0, rowGutter / 2 - pad));
        var haloWidth = Math.Max(metrics.IconSize, metrics.CellWidth - 2 * selectionInset.Left);
        var nameLines = settings.Get().EffectiveLauncher.NameLines;

        var unchanged = Math.Abs(metrics.CellWidth - lastCellWidth) < 0.01
                        && Math.Abs(metrics.IconSize - lastIconSize) < 0.01
                        && Math.Abs(metrics.RowHeight - lastRowHeight) < 0.01
                        && Math.Abs(metrics.LabelHeight - lastLabelHeight) < 0.01
                        && nameLines == lastNameLines
                        && selectionInset.Equals(lastSelectionInset)
                        && Math.Abs(haloWidth - lastLabelWidth) < 0.01;
        if (unchanged) return false;

        lastCellWidth = metrics.CellWidth;
        lastIconSize = metrics.IconSize;
        lastRowHeight = metrics.RowHeight;
        lastLabelHeight = metrics.LabelHeight;
        lastNameLines = nameLines;
        lastSelectionInset = selectionInset;
        lastLabelWidth = haloWidth;

        Resources["SpotlightTileWidth"] = metrics.CellWidth;
        Resources["SpotlightIconSize"] = metrics.IconSize;
        Resources["SpotlightTileHeight"] = metrics.RowHeight;
        Resources["SpotlightTileContentHeight"] = metrics.ContentHeight;
        Resources["SpotlightLabelWidth"] = haloWidth;
        Resources["SpotlightLabelHeight"] = metrics.LabelHeight;
        return true;
    }

    /// <summary>
    /// Paint the current geometry onto every realized tile.
    /// <para>
    /// The templates read the same numbers through <c>DynamicResource</c>, but that only applies to a
    /// container created <i>after</i> the dictionary was written — a resource entry replaced at run
    /// time is not re-pushed into an already-built template on every platform/build. Since the icon,
    /// the tile width, the name's max width and the selection inset all change on the same event
    /// (a resize, a column count, a row gap), the authoritative path is this one: write the values
    /// straight onto the live visuals. The resources stay as the defaults a brand-new container
    /// starts from.
    /// </para>
    /// </summary>
    private void ApplyTileGeometry()
    {
        if (lastCellWidth < 1) return;

        foreach (var container in GridList.GetRealizedContainers())
        {
            // The grid's halo is the item's presenter: the ListBoxItem itself is the whole cell,
            // and a cell-sized highlight around a smaller icon reads as an empty rectangle.
            var presenter = container.GetVisualDescendants()
                .OfType<Avalonia.Controls.Presenters.ContentPresenter>()
                .FirstOrDefault();
            PaintTile(container, presenter);
        }

        // The recents strip carries the same tiles, so it needs the same numbers. Its halo is the
        // Button (there is no ListBoxItem to paint), which is why the inset is passed in rather
        // than assumed to be a presenter.
        foreach (var container in RecentItemsControl.GetRealizedContainers())
        {
            var button = container.GetVisualDescendants().OfType<Button>().FirstOrDefault();
            PaintTile(container, button);
        }
    }

    /// <summary>
    /// Write the current tile geometry onto one realized container, and inset
    /// <paramref name="halo"/> so the highlight hugs the tile instead of the cell.
    /// </summary>
    private void PaintTile(Control container, Control? halo)
    {
        if (halo != null) halo.Margin = lastSelectionInset;

        // The tile gets the SAME height for every app: icon + gap + the reserved label box.
        // Without that, a one-line name produced a shorter tile, the container centred it, and
        // its icon sat a few pixels lower than the two-line name next to it.
        var tile = FindNamed<StackPanel>(container, "TileRoot");
        if (tile != null)
        {
            tile.Width = lastCellWidth;
            if (lastLabelHeight > 0) tile.Height = lastIconSize + LauncherGridMetrics.LabelSpacing + lastLabelHeight;
            else tile.ClearValue(HeightProperty);
        }

        var icon = FindNamed<Avalonia.Controls.Grid>(container, "TileIcon");
        if (icon != null)
        {
            icon.Width = lastIconSize;
            icon.Height = lastIconSize;
        }

        var label = FindNamed<TextBlock>(container, "TileLabel");
        if (label != null)
        {
            label.MaxWidth = lastLabelWidth;
            // MaxLines is an int with no resource binding, so the per-tile line budget is
            // written here — it is what lets a long name wrap to the user's 应用名称行数.
            if (lastNameLines > 0) label.MaxLines = lastNameLines;
            if (lastLabelHeight > 0) label.Height = lastLabelHeight;
            else label.ClearValue(HeightProperty);
        }
    }

    private static T? FindNamed<T>(Control root, string name) where T : Control
        => root.GetVisualDescendants().OfType<T>().FirstOrDefault(control => control.Name == name);

    /// <summary>Center horizontally on the monitor under the cursor, a little above the middle.</summary>
    private void PlaceOnActiveScreen()
    {
        GetCursorPos(out var cursor);
        var screen = Screens.ScreenFromPoint(new PixelPoint(cursor.X, cursor.Y)) ?? Screens.Primary;
        if (screen == null)
        {
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            return;
        }

        var scaling = screen.Scaling <= 0 ? 1.0 : screen.Scaling;
        var centerX = screen.Bounds.X + screen.Bounds.Width / 2.0;
        var top = screen.Bounds.Y + screen.Bounds.Height * 0.18;
        var workBottom = screen.WorkingArea.Y + screen.WorkingArea.Height;
        if (top + Height * scaling > workBottom - 10)
        {
            top = Math.Max(screen.WorkingArea.Y + 10, workBottom - Height * scaling - 10);
        }

        Position = new PixelPoint(
            (int)Math.Round(centerX - Width * scaling / 2.0),
            (int)Math.Round(top));
    }

    // ---- summon animation -----------------------------------------------------------

    /// <summary>The summon transform pair on the surface root: a micro-scale plus the downward rise.</summary>
    private sealed record SurfaceRootTransforms(ScaleTransform Scale, TranslateTransform Rise);

    /// <summary>
    /// Read the surface root's summon transforms. The pair is created by
    /// <see cref="PrepareSummonAnimationInitialState"/> (scale first, then the rise — the rise
    /// must not be scaled by the zoom, or the drop distance would shrink mid-flight).
    /// </summary>
    private SurfaceRootTransforms? SurfaceRootRenderTransforms()
        => SurfaceRoot.RenderTransform is TransformGroup { Children.Count: >= 2 } group
           && group.Children[0] is ScaleTransform scale
           && group.Children[1] is TranslateTransform rise
            ? new SurfaceRootTransforms(scale, rise)
            : null;

    /// <summary>Detach every transition on the root's summon transforms and snap them to rest.</summary>
    private void ResetSurfaceRootTransform()
    {
        if (SurfaceRootRenderTransforms() is { } transforms)
        {
            transforms.Scale.Transitions = null;
            transforms.Scale.ScaleX = 1d;
            transforms.Scale.ScaleY = 1d;
            transforms.Rise.Transitions = null;
            transforms.Rise.X = 0;
            transforms.Rise.Y = 0;
            return;
        }

        if (SurfaceRoot.RenderTransform is ScaleTransform bare)
        {
            bare.Transitions = null;
            bare.ScaleX = 1d;
            bare.ScaleY = 1d;
        }
    }

    /// <summary>
    /// The 打开动画 = off path: reset everything the animated path would have prepared to its
    /// final state, so the card is painted at full opacity and scale on the very first frame.
    /// Dismiss leaves the surface at opacity 0, so skipping this would show nothing at all.
    /// </summary>
    private void PrepareStaticSummonState()
    {
        summonAnimationCts?.Cancel();
        summonAnimationCts?.Dispose();
        summonAnimationCts = null;

        SurfaceRoot.Transitions = null;
        ResetSurfaceRootTransform();
        SurfaceRoot.Opacity = 1d;

        Card.Transitions = null;
        GlassSurface.Transitions = null;
        if (Card.RenderTransform is ScaleTransform cs) cs.Transitions = null;
        if (GlassSurface.RenderTransform is ScaleTransform gs) gs.Transitions = null;
        Card.RenderTransform = null;
        GlassSurface.RenderTransform = null;
        Card.Opacity = 1d;
        GlassSurface.Opacity = 1d;
    }

    /// <summary>
    /// Prepares the card and glass surface for the summon animation before the window becomes visible.
    /// Setting Opacity = 0 and the micro-scale + rise initial state before Show() prevents any
    /// full-opacity or un-animated initial frames from flashing on screen.
    /// </summary>
    private void PrepareSummonAnimationInitialState()
    {
        summonAnimationCts?.Cancel();
        summonAnimationCts?.Dispose();
        summonAnimationCts = null;

        var origin = summonMode == SpotlightSummonMode.Search
            ? new RelativePoint(0.5, 0.5, RelativeUnit.Relative)
            : new RelativePoint(0.5, Math.Clamp(34.0 / Math.Max(1.0, Height), 0.01, 0.5), RelativeUnit.Relative);

        SurfaceRoot.RenderTransformOrigin = origin;
        SurfaceRoot.Transitions = null;

        // Micro-scale around the card's anchor plus a small rise above the resting position:
        // together they read as the card dropping in from the search bar, not as a fade.
        SurfaceRoot.RenderTransform = new TransformGroup
        {
            Children =
            {
                new ScaleTransform(SummonScaleFrom, SummonScaleFrom),
                new TranslateTransform(0, SummonRiseFrom),
            },
        };

        SurfaceRoot.Opacity = 0;

        Card.Transitions = null;
        GlassSurface.Transitions = null;
        if (Card.RenderTransform is ScaleTransform cs) cs.Transitions = null;
        if (GlassSurface.RenderTransform is ScaleTransform gs) gs.Transitions = null;
        Card.RenderTransform = null;
        GlassSurface.RenderTransform = null;
        Card.Opacity = 1;
        GlassSurface.Opacity = 1;
    }

    /// <summary>
    /// Summons the overlay on the spring vocabulary: the micro-scale and the rise land on the
    /// mod's bounce spring (the same ~3.8% overshoot the secondary panels open with), the fade
    /// stays critically damped so the alpha never overshoots past 1.
    /// </summary>
    private void PlaySummonAnimation()
    {
        summonAnimationCts?.Cancel();
        summonAnimationCts?.Dispose();
        summonAnimationCts = null;

        var fadeDuration = TimeSpan.FromMilliseconds(SummonFadeMs);
        var scaleDuration = TimeSpan.FromMilliseconds(SummonScaleMs);

        SurfaceRoot.Transitions = new Transitions
        {
            new DoubleTransition { Property = Visual.OpacityProperty, Duration = fadeDuration, Easing = CardFadeEasing }
        };

        if (SurfaceRootRenderTransforms() is { } transforms)
        {
            transforms.Scale.Transitions = new Transitions
            {
                new DoubleTransition { Property = ScaleTransform.ScaleXProperty, Duration = scaleDuration, Easing = CardMorphEasing },
                new DoubleTransition { Property = ScaleTransform.ScaleYProperty, Duration = scaleDuration, Easing = CardMorphEasing },
            };
            transforms.Rise.Transitions = new Transitions
            {
                new DoubleTransition { Property = TranslateTransform.YProperty, Duration = scaleDuration, Easing = CardMorphEasing },
            };

            transforms.Scale.ScaleX = 1d;
            transforms.Scale.ScaleY = 1d;
            transforms.Rise.Y = 0;
        }

        SurfaceRoot.Opacity = 1d;

        if (screenSampling && GlassSurface.IsVisible)
        {
            GlassSurface.RequestRender(immediate: true);
        }
    }

    // ---- interaction --------------------------------------------------------------

    private void OnQueryChanged(object? sender, TextChangedEventArgs e)
    {
        if (applyingLayout) return;
        UpdateClearButton();
        viewModel.SetQuery(QueryBox.Text);
        selectedIndex = 0;
        UpdateMode();
    }

    private void OnClearQuery(object? sender, RoutedEventArgs e) => ClearQuery();

    /// <summary>The ⊗ button and Esc / Alt+Backspace share one clear path.</summary>
    private void ClearQuery()
    {
        QueryBox.Text = string.Empty;
        viewModel.SetQuery(string.Empty);
        viewModel.SetCategory(0);
        UpdateClearButton();
        selectedIndex = summonMode == SpotlightSummonMode.AppList ? -1 : 0;
        UpdateMode();
    }

    private void UpdateClearButton() => ClearButton.IsVisible = !string.IsNullOrEmpty(QueryBox.Text);

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SpotlightViewModel.IsFiltering)
            or nameof(SpotlightViewModel.IsEmptyFolder)
            or nameof(SpotlightViewModel.IsEmptyResult)
            or nameof(SpotlightViewModel.Items))
        {
            UpdateMode();
        }
        else if (e.PropertyName is nameof(SpotlightViewModel.ActiveCategory))
        {
            RefreshCategoryChips();

            // A category switch is a new result set: its first row is the new selection, so the
            // very next ↓ moves to the second row instead of re-selecting the first one.
            selectedIndex = 0;
            SyncResultSelection();
        }
        else if (e.PropertyName is nameof(SpotlightViewModel.Categories))
        {
            // 设置 › 类别快捷键 rearranged the bar: rebuild the chips (and their key map) in place.
            BuildCategoryChips();
        }
        else if (e.PropertyName is nameof(SpotlightViewModel.Results))
        {
            // 重新筛选或网页结果后补：Results 是整体重建的，选中行必须重新落位。
            SyncResultSelection();
        }
        else if (e.PropertyName is nameof(SpotlightViewModel.GridItems))
        {
            // The category filter produced a different tile set: the bar's selected capsule
            // follows the view model, and the keyboard's current row no longer exists.
            RefreshAppCategoryChips();
            selectedIndex = -1;
            GridList.SelectedIndex = -1;
            // A different tile set starts at its first row. Without this the offset survives the
            // switch and a 6-app cluster opens scrolled halfway down (with the strip on 全部, past
            // it) — the grid would look empty until the user scrolled back up.
            GridScroller.Offset = new Vector(0, 0);
            UpdateMode();
        }
        else if (e.PropertyName is nameof(SpotlightViewModel.AppCategories))
        {
            BuildAppCategoryChips();
            UpdateMode();
        }
    }

    /// <summary>
    /// Re-seat the list's selection after the view model rebuilt <see cref="SpotlightViewModel.Results"/>.
    /// <para>
    /// Clearing the collection drops the ListBox's selection, so without this the keyboard's
    /// "current row" and what the user sees disagree — the first ↓ after a category switch used to
    /// re-select row 0 instead of moving. The previous position is kept (clamped); only a row that
    /// no longer exists falls back to the first selectable one.
    /// </para>
    /// </summary>
    private void SyncResultSelection()
    {
        if (!viewModel.IsFiltering) return;

        var items = viewModel.Results;
        if (items.Count == 0)
        {
            ResultList.SelectedIndex = -1;
            selectedIndex = -1;
            ValidateFocusZone();
            return;
        }

        var target = NearestSelectableIndex(items, Math.Clamp(selectedIndex, 0, items.Count - 1), +1);
        if (target < 0)
        {
            ResultList.SelectedIndex = -1;
            ValidateFocusZone();
            return;
        }

        selectedIndex = target;
        if (ResultList.SelectedIndex != target) ResultList.SelectedIndex = target;
        ResultList.ScrollIntoView(target);
        ValidateFocusZone();
        ScheduleResultHighlightUpdate();
    }

    /// <summary>Switch between grid / list / empty states and keep the selection valid.</summary>
    private void UpdateMode()
    {
        var filtering = viewModel.IsFiltering;
        var emptyFolder = viewModel.IsEmptyFolder;
        var emptyResult = viewModel.IsEmptyResult;
        var emptyCategory = viewModel.IsEmptyAppCategory;
        var gridMode = summonMode == SpotlightSummonMode.AppList;

        GridList.IsVisible = gridMode && !filtering && !emptyFolder;
        // The 最近使用 strip lives INSIDE this scroller (it scrolls away with the tiles), so the
        // scroller — not the list — is what has to leave the screen with it. GridList keeps its own
        // flag: IsGridMode / ActiveListVisible / SyncSelectionFrom all read it.
        GridScroller.IsVisible = GridList.IsVisible;
        ResultList.IsVisible = filtering && !emptyResult;
        EmptyState.IsVisible = gridMode && emptyFolder;
        NoResultState.IsVisible = emptyResult;
        EmptyCategoryState.IsVisible = gridMode && emptyCategory && !emptyFolder;

        // The cluster bar belongs to the grid: a search query has its own chips on the same row,
        // and the empty-folder state is about the launcher folder itself, not about a category.
        AppCategoryBar.IsVisible = gridMode && !filtering && !emptyFolder;

        // The card's size follows the mode, not the query alone: the preview column exists only
        // while searching, and the search-bar summon is one row tall until a result arrives.
        ApplyCardGeometry(settings.Get().EffectiveLauncher);

        UpdateEmptyRows();

        var active = filtering ? ResultList : GridList;
        var count = filtering ? viewModel.Results.Count : viewModel.GridItems.Count;

        if (count == 0)
        {
            active.SelectedIndex = -1;
            ValidateFocusZone();
            return;
        }

        // The list can contain non-selectable group captions ("文件"), so "index 0" is not
        // necessarily a row the user can open.
        var target = filtering
            ? NearestSelectableIndex(viewModel.Results, Math.Clamp(selectedIndex >= 0 ? selectedIndex : 0, 0, count - 1), +1)
            : (selectedIndex >= 0 ? Math.Clamp(selectedIndex, 0, count - 1) : -1);

        if (target < 0)
        {
            active.SelectedIndex = -1;
            ValidateFocusZone();
            return;
        }

        selectedIndex = target;
        active.SelectedIndex = target;
        ScrollActiveIntoView(active, target);
        ValidateFocusZone();

        // The mode swap may have realized (or dropped) the selected row's container — the
        // highlight bar retargets after the swap, not only on selection changes.
        ScheduleResultHighlightUpdate();
    }

    /// <summary>The empty-query rows are driven by counts alone (the VM fills them per settings).</summary>
    private void UpdateEmptyRows()
    {
        // The grid's satellite rows belong to 应用列表式 only: a search bar that opens with a
        // "最近使用" strip is not a search bar.
        var gridMode = summonMode == SpotlightSummonMode.AppList;

        // …and to 全部 only. A cluster is a hand-made slice of the grid, so a strip of "whatever you
        // launched last across every app" would put apps the user did not file into that cluster
        // right on top of it — the one place where the row would actively contradict the view.
        var onAllApps = viewModel.ActiveAppCategory == 0;

        RecentRow.IsVisible = gridMode
                              && onAllApps
                              && settings.Get().EffectiveLauncher.RecentApps
                              && viewModel.RecentItems.Count > 0
                              && !viewModel.IsFiltering;
    }

    /// <summary>
    /// Scroll one list row into view: the result list owns its own scroller, the app grid does not
    /// (its scroller is shared with the 最近使用 strip, see <see cref="ScrollTileIntoView"/>).
    /// </summary>
    private void ScrollActiveIntoView(ListBox list, int index)
    {
        if (ReferenceEquals(list, GridList))
        {
            if (GridList.ContainerFromIndex(index) is Control tile) ScrollTileIntoView(tile);
            return;
        }

        list.ScrollIntoView(index);
    }

    /// <summary>
    /// Bring a tile — a grid cell or a 最近使用 tile, both live inside
    /// <see cref="GridScroller"/> — into the visible band.
    /// <para>
    /// <c>ListBox.ScrollIntoView</c> has nothing to move here: the list is measured at full height
    /// inside the shared scroller, so its own ScrollViewer has no extent to scroll. The offset is
    /// derived from the tile's position inside the scroller's CONTENT, which the scroll offset does
    /// not touch — so the comparison against <see cref="ScrollViewer.Offset"/> stays in one
    /// coordinate space even while the view is mid-scroll.
    /// </para>
    /// </summary>
    private void ScrollTileIntoView(Control tile)
    {
        if (GridScroller.Content is not Control content) return;
        if (tile.TranslatePoint(new Point(0, 0), content) is not { } origin) return;

        var viewport = GridScroller.Viewport.Height;
        if (viewport <= 0) return;

        var current = GridScroller.Offset.Y;
        var target = current;
        if (origin.Y < current)
        {
            target = origin.Y;
        }
        else if (origin.Y + tile.Bounds.Height > current + viewport)
        {
            target = origin.Y + tile.Bounds.Height - viewport;
        }

        target = Math.Max(0, target);
        if (Math.Abs(target - current) > 0.5) GridScroller.Offset = new Vector(GridScroller.Offset.X, target);
    }

    /// <summary>
    /// The first selectable row at or after <paramref name="from"/>, walking in
    /// <paramref name="direction"/> when the row itself is a header. -1 when there is none.
    /// </summary>
    private static int NearestSelectableIndex(
        System.Collections.Generic.IReadOnlyList<SpotlightItem> items, int from, int direction)
    {
        for (var i = from; i >= 0 && i < items.Count; i += direction)
        {
            if (items[i].IsSelectable) return i;
        }

        // Nothing after `from` — fall back to the other end so the selection never gets stuck.
        for (var i = from; i >= 0 && i < items.Count; i -= direction)
        {
            if (items[i].IsSelectable) return i;
        }

        return -1;
    }

    private void SyncSelectionFrom(ListBox list)
    {
        if (!list.IsVisible) return;
        if (list.SelectedIndex >= 0) selectedIndex = list.SelectedIndex;

        // The preview pane binds through the view model, so every selection change — pointer,
        // keyboard, or mode swap — has to reach SetSelected. Container content IS the row object.
        viewModel.SetSelected(list.SelectedItem as SpotlightItem);

        if (ReferenceEquals(list, ResultList)) ScheduleResultHighlightUpdate();
    }

    // ---- result-highlight spring: the integrator's first real consumer ----------------
    //
    // The results' selection is a floating bar (ResultHighlight) whose target moves with every
    // arrow press, every rebuilt result set and every scroll step. A transition would restart
    // from rest on each retarget — the bar would teleport, then animate. The Spring keeps its
    // velocity across retargets (动效交接 §5.2-3), so holding ↓ reads as one continuous glide.

    // NOT readonly: Spring is a struct, and a mutating call on a readonly field compiles to a
    // defensive copy — the spring's state would silently never move (the bar would pin at 0).
    private Spring highlightY = new();
    private Spring highlightHeight = new();
    private bool highlightDriving;
    private TimeSpan highlightLastFrame;
    private bool highlightHasFrame;

    /// <summary>
    /// Retarget the highlight bar at the currently selected row: snap on first appearance, glide
    /// afterwards, hide only when there is really nothing selectable. Idempotent — the layout and
    /// selection events that call it fire far more often than the target actually moves.
    /// </summary>
    private void ScheduleResultHighlightUpdate()
    {
        // Really no selection: the bar leaves with the selection it was following.
        if (!ResultList.IsVisible || ResultList.SelectedIndex < 0)
        {
            highlightDriving = false;
            ResultHighlight.IsVisible = false;
            return;
        }

        // The selected row's container is momentarily missing (a virtualized rebuild, a scroll
        // recycling rows): HOLD the position and stop driving. ContainerPrepared and
        // LayoutUpdated are guaranteed to re-run this method once the row exists again — hiding
        // here instead is what let a mid-rebuild position survive as the "resting" one.
        if (ResultHighlightTarget() is not { } rect)
        {
            highlightDriving = false;
            return;
        }

        if (!ResultHighlight.IsVisible)
        {
            // First appearance: arrive exactly on the row, no glide from nowhere.
            highlightY.SnapTo(rect.Y);
            highlightHeight.SnapTo(rect.Height);
            PositionResultHighlight(rect);
            ResultHighlight.IsVisible = true;
        }
        else
        {
            highlightY.SetTarget(rect.Y);
            highlightHeight.SetTarget(rect.Height);
        }

        // Keep driving after the snap too: the position the bar landed on may itself be a
        // mid-flight one (a scroll settling, the stagger still running), and only the loop that
        // re-reads the target every frame can walk the bar to the row's FINAL rect.
        if (!highlightDriving)
        {
            highlightDriving = true;
            highlightHasFrame = false;
            DriveResultHighlight();
        }
    }

    /// <summary>
    /// One frame of the highlight loop: re-read the target (a scroll or a rebuilt list may have
    /// moved the selected row), step both springs on the frame's dt, and re-arm the render-clock
    /// callback while either spring still moves. A headless harness only ticks this clock when a
    /// check forces it, so the loop simply parks between checks.
    /// </summary>
    private void DriveResultHighlight()
    {
        RequestAnimationFrame(frame =>
        {
            if (!highlightDriving) return;

            var dt = highlightHasFrame
                ? Math.Max(0, (frame - highlightLastFrame).TotalSeconds)
                : 1.0 / 60.0;
            highlightLastFrame = frame;
            highlightHasFrame = true;

            if (!ResultList.IsVisible || ResultList.SelectedIndex < 0)
            {
                highlightDriving = false;
                ResultHighlight.IsVisible = false;
                return;
            }

            // Container momentarily gone: park on the current position (NOT hidden — the
            // selection still exists) and let the next ContainerPrepared/LayoutUpdated restart.
            if (ResultHighlightTarget() is not { } rect)
            {
                highlightDriving = false;
                return;
            }

            highlightY.SetTarget(rect.Y);
            highlightHeight.SetTarget(rect.Height);

            var moving = highlightY.Step(dt, 0.26, SpringMotion.Lively);
            moving = highlightHeight.Step(dt, 0.26, SpringMotion.Lively) || moving;
            PositionResultHighlight(new Rect(rect.X, highlightY.Value, rect.Width, highlightHeight.Value));

            if (moving)
            {
                DriveResultHighlight();
            }
            else
            {
                highlightDriving = false;
            }
        });
    }

    /// <summary>
    /// Drop the highlight bar's carried state. A new result set is arriving (or the overlay is
    /// closing): the old position means nothing on it, and a glide from that position would sweep
    /// across unrelated rows — or die mid-sweep and strand the bar (the reported “选中框锁在
    /// 中间偏下”). The next schedule snaps afresh.
    /// </summary>
    private void ResetResultHighlight()
    {
        highlightDriving = false;
        ResultHighlight.IsVisible = false;
        highlightY.SnapTo(0);
        highlightHeight.SnapTo(0);
    }

    private void PositionResultHighlight(Rect rect)
    {
        // VerticalAlignment=Top is pinned in the XAML: Margin alone positions nothing while the
        // default Stretch centres an explicit-height child (the "stuck at the panel's middle"
        // defect). The alignments are static; only the margins and the size travel.
        ResultHighlight.Margin = new Thickness(rect.X, rect.Y, 0, 0);
        ResultHighlight.Width = Math.Max(4, rect.Width);
        ResultHighlight.Height = Math.Max(8, rect.Height);
    }

    /// <summary>
    /// The selected row's highlight rectangle in the highlight bar's own coordinate space (the
    /// panel the bar floats in), taken from the row's content presenter so the bar lands exactly
    /// where the row's own background used to.
    /// </summary>
    private Rect? ResultHighlightTarget()
    {
        if (!ResultList.IsVisible || ResultList.SelectedIndex < 0) return null;
        if (ResultList.ContainerFromIndex(ResultList.SelectedIndex) is not Control container) return null;
        if (ResultHighlight.Parent is not Control host) return null;

        var presenter = container.GetVisualDescendants()
            .OfType<Avalonia.Controls.Presenters.ContentPresenter>()
            .FirstOrDefault();
        var source = presenter ?? container;

        // A row still being laid out (its arrange has not run yet) reports a zero-height rect —
        // snapping onto that would flash the bar at the list's top edge. LayoutUpdated fires
        // again once the row has its real rect, which is when the bar may follow it.
        if (source.Bounds.Height < 1 || source.Bounds.Width < 1) return null;

        return source.TranslatePoint(new Point(0, 0), host) is { } topLeft
            ? new Rect(topLeft, source.Bounds.Size)
            : null;
    }

    private async Task ActivateSelectedAsync(bool reveal = false)
    {
        var item = (viewModel.IsFiltering ? ResultList.SelectedItem : GridList.SelectedItem) as SpotlightItem;
        if (item == null || !item.IsSelectable) return;

        // 单空格搜索记录行：Enter 与点击一样只把查询文字填回输入框，不启动任何东西。
        if (item.Kind == SpotlightItemKind.SearchHistory)
        {
            FillQueryFromHistory(item.Name);
            return;
        }

        // RecordLaunch is the view model's job now; true means the row actually opened.
        var opened = await viewModel.ActivateAsync(item, reveal);
        if (opened) Dismiss();
    }

    /// <summary>
    /// Put the calculator's answer on the clipboard and close the overlay — the same contract the
    /// other Enter paths follow (an action happened, so the launcher gets out of the way). The
    /// number is copied without the pill's "= ", because that is what the user wants to paste.
    /// </summary>
    private async Task CopyCalculationAsync()
    {
        var value = viewModel.CalculationValue;
        if (string.IsNullOrEmpty(value)) return;

        try
        {
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard != null) await clipboard.SetTextAsync(value);
        }
        catch
        {
            // A refused clipboard must not keep the overlay on screen: the query is still there
            // if the user summons it again.
        }

        Dismiss();
    }

    private async Task ActivateItemAsync(SpotlightItem? item)
    {
        if (item == null || !item.IsSelectable)
        {
            GlassDiagnostics.Event($"[spotlight] activate skipped — item={(item == null ? "null" : item.Kind.ToString())}");
            return;
        }

        // 单空格搜索记录行：点击是"把文字填进输入框"，不是打开。
        if (item.Kind == SpotlightItemKind.SearchHistory)
        {
            GlassDiagnostics.Event($"[spotlight] history fill '{item.Name}'");
            FillQueryFromHistory(item.Name);
            return;
        }

        var opened = await viewModel.ActivateAsync(item);
        GlassDiagnostics.Event($"[spotlight] activate '{item.Id}' -> {opened}");
        if (opened) Dismiss();
    }

    /// <summary>
    /// 单空格搜索记录行的"执行"：把文字填进输入框并按手动输入走完整流程（TextChanged →
    /// SetQuery → 正常搜索）。不关闭面板、不启动任何东西——记录行展示的是"你输入过什么"，
    /// 而不是一次搜索结果。
    /// </summary>
    private void FillQueryFromHistory(string text)
    {
        QueryBox.Text = text;
        QueryBox.CaretIndex = text.Length;
        QueryBox.Focus();
    }

    private void OnRecentTileClick(object? sender, RoutedEventArgs e)
        => _ = ActivateItemAsync((e.Source as Control)?.DataContext as SpotlightItem);

    private void OnPreviewOpen(object? sender, RoutedEventArgs e)
    {
        // The pane's buttons act on the bound row, not on the list's selection: a click can land
        // while the list is mid-rebind, and "nothing happened" is indistinguishable from "the
        // button was never wired" without this line.
        GlassDiagnostics.Event($"[spotlight] preview open — item={viewModel.Preview.Item?.Id ?? "null"}");
        _ = ActivateItemAsync(viewModel.Preview.Item);
    }

    private void OnPreviewReveal(object? sender, RoutedEventArgs e)
    {
        var item = viewModel.Preview.Item;
        if (item == null) return;
        _ = RunPreviewAsync(viewModel.ActivateAsync(item, reveal: true));
    }

    private void OnPreviewRun(object? sender, RoutedEventArgs e)
        => _ = ActivateItemAsync(viewModel.Preview.Item);

    private void OnPreviewCopy(object? sender, RoutedEventArgs e)
    {
        var item = viewModel.Preview.Item;
        if (item == null) return;
        // Copy stays open: closing the launcher over the user's head reads as data loss.
        _ = RunPreviewAsync(viewModel.ActivateAsync(item));
    }

    private static async Task RunPreviewAsync(Task<bool> activation)
    {
        try
        {
            await activation;
        }
        catch
        {
            // Activation failures surface through the view model; never break the UI thread.
        }
    }

    // ---- keyboard: one handler, two routes ------------------------------------------

    private void OnQueryBoxTunnelKeyDown(object? sender, KeyEventArgs e) => HandleKey(e);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        HandleKey(e);
        if (!e.Handled) base.OnKeyDown(e);
    }

    /// <summary>
    /// The whole keyboard contract. Reached twice by design: tunneling (ahead of the field's own key
    /// handling) while the search field holds focus, and bubbling for every other focus target.
    /// Typed characters and plain Backspace are never marked handled, so IME and editing stay
    /// untouched. Plain ←/→ stay with the field (caret movement) too; the category bar answers to
    /// <b>Ctrl+←/→</b> — in the app grid that walks the cluster capsules, while a query is up it
    /// walks the filter chips (see <see cref="StepCategory"/>).
    /// </summary>
    private void HandleKey(KeyEventArgs e)
    {
        if (!IsVisible) return;
        HandleKeyCore(e);
    }

    /// <summary>The contract itself, without the "is the window on screen" gate.</summary>
    private void HandleKeyCore(KeyEventArgs e)
    {
        var ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        var alt = e.KeyModifiers.HasFlag(KeyModifiers.Alt);
        var shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);

        switch (e.Key)
        {
            case Key.Escape:
                // First Esc empties the query (and resets the category); only the second closes.
                if (!string.IsNullOrEmpty(QueryBox.Text)) ClearQuery();
                else Dismiss();
                e.Handled = true;
                return;

            case Key.Enter:
                // A valid expression owns Enter: the answer the user just produced is what they
                // want, and no app row could be a better target than the formula they typed.
                if (!ctrl && !shift && viewModel.HasCalculation)
                {
                    _ = CopyCalculationAsync();
                    e.Handled = true;
                    return;
                }

                // Ctrl/Shift+Enter reveals a file hit instead of opening it — Spotlight's
                // "show in Finder" gesture, and the only way to get at a file's folder.
                _ = ActivateSelectedAsync(reveal: ctrl || shift);
                e.Handled = true;
                return;

            case Key.Tab when e.KeyModifiers is KeyModifiers.None or KeyModifiers.Shift:
                MoveFocusZone(shift ? -1 : 1);
                e.Handled = true;
                return;

            case Key.Back when alt && !ctrl:
                ClearQuery();
                e.Handled = true;
                return;

            case Key.OemComma when ctrl && !alt:
                viewModel.RequestOpenSettings();
                e.Handled = true;
                return;
        }

        // Category lock (F1–F7 by default, user-rebindable). Checked before the modifier gate:
        // the gesture may legitimately carry Ctrl/Alt/Shift, and it must win over typing even
        // while the search field owns focus.
        if (TrySelectCategoryByGesture(e))
        {
            return;
        }

        // Ctrl + ←/→ walks the category bar: the app grid's clusters while the grid is up, the
        // search filter chips while a query is. Ctrl is what makes this a category gesture —
        // plain ←/→ belong to the text field again (caret movement), which is why the plain-arrow
        // branch below only steps a bar when the bar itself holds the focus.
        if (ctrl && !alt && e.Key is Key.Left or Key.Right)
        {
            if (StepCategory(e.Key == Key.Left ? -1 : 1)) e.Handled = true;
            return;
        }

        if (ctrl || alt) return;

        switch (e.Key)
        {
            case Key.Down:
                HandleDown();
                e.Handled = true;
                return;

            case Key.Up:
                HandleUp();
                e.Handled = true;
                return;

            case Key.Left:
                if (HandleLeftRight(-1)) e.Handled = true;
                return;

            case Key.Right:
                if (HandleLeftRight(1)) e.Handled = true;
                return;
        }
    }

    private void HandleDown()
    {
        switch (focusZone)
        {
            case SpotlightFocusZone.Search:
            case SpotlightFocusZone.Categories:
                EnterNextZoneDown();
                break;

            case SpotlightFocusZone.Recents:
                EnterGrid();
                break;

            case SpotlightFocusZone.List:
                MoveSelection(viewModel.IsFiltering ? 1 : GridColumns);
                break;

            case SpotlightFocusZone.Preview:
                break;
        }
    }

    private void HandleUp()
    {
        switch (focusZone)
        {
            case SpotlightFocusZone.List when viewModel.IsFiltering:
                if (ResultList.SelectedIndex <= FirstListIndex())
                {
                    focusZone = SpotlightFocusZone.Search;
                    QueryBox.Focus();
                }
                else
                {
                    MoveSelection(-1);
                }

                break;

            case SpotlightFocusZone.List:
                if (GridList.SelectedIndex < GridColumns)
                {
                    StepBackFromGrid();
                }
                else
                {
                    MoveSelection(-GridColumns);
                }

                break;

            case SpotlightFocusZone.Recents:
                focusZone = SpotlightFocusZone.Search;
                QueryBox.Focus();
                break;
        }
    }

    /// <summary>
    /// Plain ←/→. They only mean "next / previous category" when a bar itself holds the focus;
    /// everywhere else they are left alone so the search field keeps its caret movement (the
    /// category gesture is Ctrl+←/→, see <see cref="StepCategory"/>).
    /// </summary>
    /// <returns>True when the key was consumed.</returns>
    private bool HandleLeftRight(int delta)
    {
        switch (focusZone)
        {
            case SpotlightFocusZone.Categories:
                return StepCategory(delta);

            case SpotlightFocusZone.Recents:
                StepRowIndex(RecentItemsControl, ref recentIndex, delta);
                return true;

            case SpotlightFocusZone.List:
                // The results list is vertical: ←/→ have nothing to move there.
                if (viewModel.IsFiltering) return false;
                MoveSelection(delta);
                return true;
        }

        return false;
    }

    /// <summary>
    /// Move the category lock one position. The two bars share this gesture but never share the
    /// screen: while a query is up it is the search filter chips, otherwise the app grid's
    /// clusters. Both are <b>rings</b> — ← on the first chip lands on the last one — so neither end
    /// is a dead key.
    /// </summary>
    /// <returns>True when a bar was on screen and consumed the step.</returns>
    private bool StepCategory(int delta)
    {
        if (viewModel.IsFiltering)
        {
            var count = viewModel.Categories.Count;
            if (count == 0) return false;

            var next = ((viewModel.ActiveCategory + delta) % count + count) % count;
            viewModel.SetCategory(next);
            return true;
        }

        if (!AppCategoryBar.IsVisible || viewModel.AppCategories.Count <= 1) return false;
        viewModel.StepAppCategory(delta);
        return true;
    }

    /// <summary>Down from the search field / category bar: filtering jumps straight into the list,
    /// the empty query chains 最近 → 网格.</summary>
    private void EnterNextZoneDown()
    {
        if (viewModel.IsFiltering)
        {
            EnterListFromTop();
            return;
        }

        if (RecentRow.IsVisible)
        {
            EnterRowIndex(RecentItemsControl, ref recentIndex);
            focusZone = SpotlightFocusZone.Recents;
            return;
        }

        EnterGrid();
    }

    private void EnterListFromTop()
    {
        // ↓ means "the next row". A row is already chosen the moment a query or a category is
        // set (the top hit), so re-selecting row 0 here would swallow the first keystroke after
        // every category switch — the list would only start moving on the second ↓.
        if (ResultList.SelectedIndex >= 0)
        {
            if (ResultList.SelectedIndex + 1 < ResultList.ItemCount) MoveSelection(1);
            focusZone = SpotlightFocusZone.List;
            return;
        }

        selectedIndex = 0;
        UpdateMode();
        focusZone = SpotlightFocusZone.List;
    }

    private void EnterGrid()
    {
        if (!ActiveListVisible) return;
        if (ActiveList.SelectedIndex < 0)
        {
            selectedIndex = 0;
            UpdateMode();
        }

        focusZone = SpotlightFocusZone.List;
    }

    private void StepBackFromGrid()
    {
        if (RecentRow.IsVisible)
        {
            EnterRowIndex(RecentItemsControl, ref recentIndex);
            focusZone = SpotlightFocusZone.Recents;
        }
        else
        {
            focusZone = SpotlightFocusZone.Search;
            QueryBox.Focus();
        }
    }

    private void EnterRowIndex(ItemsControl control, ref int index)
    {
        index = 0;
        FocusRowIndex(control, index);
    }

    private void StepRowIndex(ItemsControl control, ref int index, int delta)
    {
        var count = control.ItemCount;
        if (count == 0) return;
        index = Math.Clamp(index + delta, 0, count - 1);
        FocusRowIndex(control, index);
    }

    private void FocusRowIndex(ItemsControl control, int index)
    {
        if (control.ContainerFromIndex(index) is not Control container) return;
        (container.GetVisualDescendants().OfType<Button>().FirstOrDefault() ?? container).Focus();

        // The strip scrolls with the grid now, so a row the user scrolled past is off-screen and
        // focusing it does not bring it back on its own.
        ScrollTileIntoView(container);
    }

    private int FirstListIndex() => viewModel.IsFiltering ? NearestSelectableIndex(viewModel.Results, 0, +1) : 0;

    private ListBox ActiveList => viewModel.IsFiltering ? ResultList : GridList;

    private bool ActiveListVisible
        => ActiveList.IsVisible && ActiveList.ItemCount > 0;

    private bool IsGridMode => GridList.IsVisible;

    private int GridColumns => Math.Max(1, settings.Get().EffectiveLauncher.Columns);

    private void MoveSelection(int delta)
    {
        var list = viewModel.IsFiltering ? ResultList : GridList;
        var count = list.ItemCount;
        if (count == 0) return;

        var next = Math.Clamp(list.SelectedIndex + delta, 0, count - 1);

        // Group captions are not rows: step over them instead of selecting a label.
        if (viewModel.IsFiltering)
        {
            var direction = delta >= 0 ? 1 : -1;
            var resolved = NearestSelectableIndex(viewModel.Results, next, direction);
            if (resolved >= 0) next = resolved;
        }

        list.SelectedIndex = next;
        ScrollActiveIntoView(list, next);
        selectedIndex = next;
        focusZone = SpotlightFocusZone.List;
    }

    // ---- focus zones (Tab cycle) ----------------------------------------------------

    /// <summary>Tab cycle: search box → category chips → list → preview buttons → search box.</summary>
    private void MoveFocusZone(int delta)
    {
        var order = new List<SpotlightFocusZone>();
        if (CategoryBarOnScreen) order.Add(SpotlightFocusZone.Categories);
        if (ActiveListVisible) order.Add(SpotlightFocusZone.List);
        if (FirstPreviewButton() != null) order.Add(SpotlightFocusZone.Preview);

        if (order.Count == 0)
        {
            focusZone = SpotlightFocusZone.Search;
            QueryBox.Focus();
            return;
        }

        var index = order.IndexOf(focusZone);
        if (index < 0)
        {
            focusZone = delta > 0 ? order[0] : order[^1];
            FocusZoneTarget(focusZone);
            return;
        }

        var next = index + delta;
        if (next < 0 || next >= order.Count)
        {
            focusZone = SpotlightFocusZone.Search;
            QueryBox.Focus();
            return;
        }

        focusZone = order[next];
        FocusZoneTarget(focusZone);
    }

    /// <summary>
    /// True while a category bar owns row 1: the search filter chips while a query is up, or the
    /// app grid's cluster capsules while the grid is. They never share the row.
    /// </summary>
    private bool CategoryBarOnScreen =>
        (CategoryBar.IsVisible && categoryChips.Count > 0)
        || (AppCategoryBar.IsVisible && appCategoryChips.Count > 0);

    /// <summary>The chips of whichever bar is on screen (the grid's clusters win when both exist).</summary>
    private List<Button> ChipsOnScreen =>
        AppCategoryBar.IsVisible && appCategoryChips.Count > 0 ? appCategoryChips : categoryChips;

    private void FocusZoneTarget(SpotlightFocusZone zone)
    {
        switch (zone)
        {
            case SpotlightFocusZone.Categories:
                var chips = ChipsOnScreen;
                if (chips.Count == 0) break;
                var active = AppCategoryBar.IsVisible ? viewModel.ActiveAppCategory : viewModel.ActiveCategory;
                chips[Math.Clamp(active, 0, chips.Count - 1)].Focus();
                break;

            case SpotlightFocusZone.List:
                var list = ActiveList;
                if (list.SelectedIndex < 0)
                {
                    selectedIndex = 0;
                    UpdateMode();
                }

                if (list.ContainerFromIndex(list.SelectedIndex) is ListBoxItem container)
                {
                    container.Focus();
                    // Tab can land on a row the wheel already scrolled past: the grid's scroller is
                    // shared with the 最近使用 strip, so the list cannot bring its own row back.
                    if (ReferenceEquals(list, GridList)) ScrollTileIntoView(container);
                }

                break;

            case SpotlightFocusZone.Preview:
                FirstPreviewButton()?.Focus();
                break;
        }
    }

    private Button? FirstPreviewButton()
    {
        if (!previewShown) return null;
        return PreviewOpenButton.IsVisible ? PreviewOpenButton
            : PreviewRevealButton.IsVisible ? PreviewRevealButton
            : PreviewRunButton.IsVisible ? PreviewRunButton
            : PreviewCopyButton.IsVisible ? PreviewCopyButton
            : null;
    }

    /// <summary>A zone whose host row disappeared (query cleared, mode swap) folds back to Search.</summary>
    private void ValidateFocusZone()
    {
        if (focusZone == SpotlightFocusZone.Categories && !CategoryBarOnScreen)
        {
            focusZone = SpotlightFocusZone.Search;
        }

        if (focusZone == SpotlightFocusZone.List && !ActiveListVisible)
        {
            focusZone = SpotlightFocusZone.Search;
        }

        if (focusZone == SpotlightFocusZone.Recents && !RecentRow.IsVisible)
        {
            focusZone = SpotlightFocusZone.Search;
        }

        if (focusZone == SpotlightFocusZone.Preview && FirstPreviewButton() == null)
        {
            focusZone = SpotlightFocusZone.Search;
        }
    }

    // ---- category chips -------------------------------------------------------------

    private void BuildCategoryChips()
    {
        CategoryBar.Children.Clear();
        categoryChips.Clear();

        foreach (var category in viewModel.Categories)
        {
            var chip = new Button
            {
                Classes = { "category-chip" },
                Content = new TextBlock { Text = category.Label },
            };
            var index = categoryChips.Count;
            chip.Click += (_, _) => viewModel.SetCategory(index);
            CategoryBar.Children.Add(chip);
            categoryChips.Add(chip);
        }

        RefreshCategoryChips();
        RebuildCategoryGestures();
    }

    /// <summary>
    /// Parse the configured category gestures once per settings change. The chip tooltip is the
    /// only place the binding is visible, so it is written in the same pass.
    /// </summary>
    private void RebuildCategoryGestures()
    {
        var hotkeys = settings.Get().EffectiveLauncher.EffectiveCategoryHotkeys;
        var parsed = new Dictionary<int, GlobalHotkeyGesture>();

        for (var index = 0; index < viewModel.Categories.Count; index++)
        {
            var key = CategoryKey(viewModel.Categories[index].Kind);
            if (!hotkeys.TryGetValue(key, out var text) || string.IsNullOrWhiteSpace(text)) continue;
            if (!GlobalHotkeyGesture.TryParse(text, out var gesture, out _)) continue;

            // Duplicate bindings are resolved by bar order: the first chip owns the key, exactly
            // as LauncherSettings.Normalize resolved them in storage.
            if (parsed.Values.Any(existing => existing == gesture)) continue;
            parsed[index] = gesture;
        }

        categoryGestures = parsed;

        for (var index = 0; index < categoryChips.Count; index++)
        {
            ToolTip.SetTip(categoryChips[index],
                parsed.TryGetValue(index, out var gesture)
                    ? gesture.Text
                    : Locale.Spotlight_Category_NoHotkey);
        }
    }

    /// <summary>Storage key of a category (must match <see cref="LauncherCategoryKeys"/>).</summary>
    private static string CategoryKey(SpotlightCategoryKind kind) => kind switch
    {
        SpotlightCategoryKind.All => LauncherCategoryKeys.All,
        SpotlightCategoryKind.Apps => LauncherCategoryKeys.Apps,
        SpotlightCategoryKind.Images => LauncherCategoryKeys.Images,
        SpotlightCategoryKind.Files => LauncherCategoryKeys.Files,
        SpotlightCategoryKind.Folders => LauncherCategoryKeys.Folders,
        SpotlightCategoryKind.Widgets => LauncherCategoryKeys.Widgets,
        SpotlightCategoryKind.Web => LauncherCategoryKeys.Web,
        _ => LauncherCategoryKeys.Clipboard,
    };

    /// <summary>Lock the search to the category whose gesture was pressed. Returns true when one matched.</summary>
    private bool TrySelectCategoryByGesture(KeyEventArgs e)
    {
        foreach (var (index, gesture) in categoryGestures)
        {
            if (!gesture.Matches(e.Key, e.KeyModifiers)) continue;

            viewModel.SetCategory(index);
            e.Handled = true;
            return true;
        }

        return false;
    }

    private void RefreshCategoryChips()
    {
        for (var i = 0; i < categoryChips.Count; i++)
        {
            categoryChips[i].Classes.Set("selected", i == viewModel.ActiveCategory);
        }
    }

    // ---- app-grid categories: the capsule bar under the search field ----------------------
    //
    // macOS Launchpad's clusters, expressed as a filter: 全部, then the user's own categories, then
    // a trailing ＋ capsule. The bar owns no dialogs — opening a modal from inside the overlay
    // deactivates it, and the deactivation probe would either dismiss the launcher or yank the
    // keyboard back out of the dialog. Naming therefore happens inline (the chip turns into a
    // TextBox) and every other action is a right-click menu.

    private void BuildAppCategoryChips()
    {
        AppCategoryBar.Children.Clear();
        appCategoryChips.Clear();

        var categories = viewModel.AppCategories;
        for (var i = 0; i < categories.Count; i++)
        {
            var index = i;
            var chip = new Button
            {
                Classes = { "category-chip" },
                Content = new TextBlock { Text = categories[i].Name },
            };
            chip.Click += (_, _) => viewModel.SetAppCategory(index);
            // 全部 is synthetic: it cannot be renamed or deleted, and its menu would only offer
            // actions that must fail.
            if (index > 0) chip.ContextMenu = BuildAppCategoryMenu(categories[index]);
            AppCategoryBar.Children.Add(chip);
            appCategoryChips.Add(chip);
        }

        var add = new Button
        {
            Classes = { "category-chip", "add-category" },
            Content = new TextBlock { Text = "+" },
        };
        ToolTip.SetTip(add, Locale.Spotlight_AppCategory_New);
        add.Click += (_, _) => CreateAppCategory();
        AppCategoryBar.Children.Add(add);

        RefreshAppCategoryChips();
    }

    private void RefreshAppCategoryChips()
    {
        for (var i = 0; i < appCategoryChips.Count; i++)
        {
            appCategoryChips[i].Classes.Set("selected", i == viewModel.ActiveAppCategory);
        }
    }

    /// <summary>Right-click menu of a category capsule: rename / delete.</summary>
    private ContextMenu BuildAppCategoryMenu(LauncherAppCategory category)
    {
        var menu = new ContextMenu();

        var rename = new MenuItem { Header = Locale.Spotlight_AppCategory_Rename };
        rename.Click += (_, _) => BeginRenameAppCategory(IndexOfAppCategory(category.Id));
        menu.Items.Add(rename);

        var delete = new MenuItem { Header = Locale.Spotlight_AppCategory_Delete };
        delete.Click += (_, _) => viewModel.DeleteAppCategory(category.Id);
        menu.Items.Add(delete);

        return menu;
    }

    private int IndexOfAppCategory(string id) =>
        viewModel.AppCategories.ToList().FindIndex(category => category.Id == id);

    /// <summary>Create a category and put its chip straight into rename mode (its name is a placeholder).</summary>
    private void CreateAppCategory()
    {
        var created = viewModel.CreateAppCategory(null);
        BeginRenameAppCategory(IndexOfAppCategory(created.Id));
    }

    /// <summary>
    /// Edit a category's name in place: the chip is swapped for a TextBox for the duration, so the
    /// overlay never has to open a window. Enter commits, Esc cancels, losing focus commits (the
    /// same contract an inline rename has everywhere else).
    /// </summary>
    private void BeginRenameAppCategory(int index)
    {
        if (index <= 0 || index >= viewModel.AppCategories.Count || index >= appCategoryChips.Count) return;

        var category = viewModel.AppCategories[index];
        var chip = appCategoryChips[index];
        var position = AppCategoryBar.Children.IndexOf(chip);
        if (position < 0) return;

        var box = new TextBox
        {
            Text = category.Name,
            MinWidth = 96,
            MaxLength = SpotlightAppCategoryStore.MaxNameLength,
            PlaceholderText = Locale.Spotlight_AppCategory_RenameHint,
            Classes = { "category-rename" },
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
        };

        var done = false;
        void Finish(bool commit)
        {
            // Guards the Enter → rebuild → LostFocus cascade: only the first call acts.
            if (done) return;
            done = true;

            if (commit) viewModel.RenameAppCategory(category.Id, box.Text);
            BuildAppCategoryChips();
            QueryBox.Focus();
        }

        box.KeyDown += (_, e) =>
        {
            switch (e.Key)
            {
                case Key.Enter:
                    e.Handled = true;
                    Finish(true);
                    break;
                case Key.Escape:
                    e.Handled = true;
                    Finish(false);
                    break;
            }
        };
        box.LostFocus += (_, _) => Finish(true);

        AppCategoryBar.Children[position] = box;
        Dispatcher.UIThread.Post(() =>
        {
            box.Focus();
            box.SelectAll();
        });
    }

    /// <summary>
    /// One click on a tile opens it. The search results keep their double-click (a click there is
    /// how you preview a row); a grid tile has no second job, so a single click is the whole
    /// gesture — Launchpad's semantics.
    /// </summary>
    private void OnGridTileTapped(object? sender, TappedEventArgs e)
    {
        // Empty space between tiles must not launch whatever happens to be selected.
        if (TileUnder(e.Source as Control) is not { } item) return;
        _ = ActivateItemAsync(item);
    }

    /// <summary>Right-click on a tile: file it into a category (or create one on the spot).</summary>
    private void OnGridContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (TileUnder(e.Source as Control) is not { Entry: { } entry } item) return;

        var current = viewModel.CategoryOfEntry(entry.Key);
        var menu = new ContextMenu();

        menu.Items.Add(new MenuItem
        {
            Header = Locale.Spotlight_AppCategory_MoveTo,
            IsEnabled = false,
        });

        var none = new MenuItem
        {
            Header = Locale.Spotlight_AppCategory_None,
            ToggleType = MenuItemToggleType.Radio,
            IsChecked = current == null,
        };
        none.Click += (_, _) => viewModel.MoveToAppCategory(entry.Key, null);
        menu.Items.Add(none);

        foreach (var category in viewModel.AppCategories.Where(category => category.Id.Length > 0))
        {
            var id = category.Id;
            var option = new MenuItem
            {
                Header = category.Name,
                ToggleType = MenuItemToggleType.Radio,
                IsChecked = current?.Id == id,
            };
            option.Click += (_, _) => viewModel.MoveToAppCategory(entry.Key, id);
            menu.Items.Add(option);
        }

        menu.Items.Add(new Separator());

        var create = new MenuItem { Header = Locale.Spotlight_AppCategory_New };
        create.Click += (_, _) =>
        {
            var created = viewModel.CreateAppCategory(null);
            viewModel.MoveToAppCategory(entry.Key, created.Id);
            // A fresh category is called "分类 N": let the user name it without a second trip.
            BeginRenameAppCategory(IndexOfAppCategory(created.Id));
        };
        menu.Items.Add(create);

        menu.Open(GridList);
        e.Handled = true;
    }

    /// <summary>
    /// The tile a pointer event came from: walks up from the hit control to its
    /// <see cref="ListBoxItem"/>, and returns null once it reaches the list itself (a click on the
    /// empty area between tiles).
    /// </summary>
    private static SpotlightItem? TileUnder(Control? source)
    {
        for (var visual = source as Visual; visual != null; visual = visual.GetVisualParent())
        {
            if (visual is ListBoxItem { DataContext: SpotlightItem item }) return item;
            if (visual is ListBox) return null;
        }

        return null;
    }

    /// <summary>
    /// Checks-only seam: the tile a click at <paramref name="source"/> resolves to. The single-click
    /// gesture must launch a tile and must NOT launch the selected app when the click landed on the
    /// empty area of the grid.
    /// </summary>
    public static SpotlightItem? TileUnderForChecks(Control? source) => TileUnder(source);

    // ---- window messages / settings ------------------------------------------------

    private IntPtr OnWindowMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (!transitionsDisabled && hwnd != IntPtr.Zero)
        {
            transitionsDisabled = true;
            InteropService.DisableWindowTransitions(hwnd);
        }

        if (message != WmActivate) return IntPtr.Zero;

        var state = wParam.ToInt64() & 0xFFFF;
        if (state == WaActive)
        {
            // The overlay really owns the keyboard now; from here on losing it means "clicked away".
            activatedSinceSummon = true;
            return IntPtr.Zero;
        }

        if (state != WaInactive || !IsReallyVisible()) return IntPtr.Zero;

        // Every deactivation inside the grace window is the aftermath of the summon itself, not a
        // dismissal: a show refused the foreground is told WA_INACTIVE immediately, and releasing
        // the summon's Alt can bounce focus back to the shell (the Alt+Space system-menu dance).
        // A real click elsewhere cannot land this fast, so both cases just wait the window out.
        if (Environment.TickCount64 - summonedAt < SummonFocusGraceMs)
        {
            GlassDiagnostics.Event(
                "[spotlight] ignored a deactivation inside the summon grace window");
            return IntPtr.Zero;
        }

        // Past the grace window but never activated: the window is on screen yet cannot take
        // input, so leaving it up would be a dead panel — close it and say why.
        if (!activatedSinceSummon)
        {
            GlassDiagnostics.Event(
                "[spotlight] no foreground could be taken (foreground lock) — closing instead of " +
                "leaving an unusable strip");
            Dismiss("no-foreground");
            return IntPtr.Zero;
        }

        // WA_INACTIVE alone is not proof that the user left: on this desktop the launcher was
        // killed again and again by deactivations it could not explain (a widget window shown
        // while the overlay was open, the shell answering the Alt release, a display rebuild
        // re-showing furniture). Decide by *who* took the keyboard over — but one hop later:
        // while this message is being dispatched the OS has not published the new foreground yet
        // (measured: GetForegroundWindow still names this window), so asking now answers with the
        // old owner. The click evidence, in contrast, only exists right now.
        ScheduleDeactivationProbe(clickInProgress: IsMouseButtonDown());
        return IntPtr.Zero;
    }

    /// <summary>
    /// Watch the foreground for a short while after a deactivation and decide once the evidence is
    /// in. A newer deactivation supersedes a pending watch, and a watch never acts on a closed
    /// overlay.
    /// <para>
    /// This is a <b>watch</b>, not a single read, because the window that takes the keyboard is not
    /// necessarily the window the user went to. A capture is created asynchronously: Win+Shift+S
    /// deactivates the overlay first and the 剪裁层 appears a few hundred milliseconds later, so a
    /// single read at 90 ms still saw whatever was in front before and closed the panel — the
    /// reported 截图时面板消失. Re-reading until <see cref="DeactivationWatchMs"/> is what lets the
    /// capture surface show up and claim the deactivation it caused.
    /// </para>
    /// </summary>
    private void ScheduleDeactivationProbe(bool clickInProgress)
    {
        var token = ++deactivationProbe;
        var elapsed = DeactivationProbeMs;

        void Step()
        {
            if (token != deactivationProbe) return;
            if (!IsReallyVisible()) return;

            if (ResolveDeactivation(clickInProgress, lastChance: elapsed >= DeactivationWatchMs)) return;

            elapsed += DeactivationProbeStepMs;
            DispatcherTimer.RunOnce(
                Step, TimeSpan.FromMilliseconds(DeactivationProbeStepMs), DispatcherPriority.Background);
        }

        DispatcherTimer.RunOnce(
            Step, TimeSpan.FromMilliseconds(DeactivationProbeMs), DispatcherPriority.Background);
    }

    /// <summary>
    /// Decide what one deactivation meant, from whoever holds the keyboard now.
    /// </summary>
    /// <param name="clickInProgress">Whether a mouse button was held when the deactivation arrived.</param>
    /// <param name="lastChance">Whether the watch is out of time and must commit to a decision.</param>
    /// <returns><c>true</c> once the decision is made and the watch can stop.</returns>
    private bool ResolveDeactivation(bool clickInProgress, bool lastChance)
    {
        var handle = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        var foreground = GetForegroundWindow();

        // A capture surface taking the keyboard is never "the user went somewhere else" — the
        // overlay is very often exactly what the user is trying to capture (截图时面板不要消失).
        // Checked FIRST because a capture can also be started by clicking a toolbar button
        // (clickInProgress). Stay open and do NOT reclaim the foreground: the tool needs the
        // input for its region selection, and a tug-of-war would break the capture.
        if (IsCaptureOverlay(foreground))
        {
            GlassDiagnostics.Event("[spotlight] kept open across a deactivation caused by a capture surface");
            ArmCaptureReturnWatch();
            return true;
        }

        // A held mouse button at the moment of deactivation is a click: on another window, on
        // the taskbar, or on one of our own widget cards. Whatever the foreground says by now,
        // the user clicked away — close, exactly like Spotlight. No waiting: a click is already
        // unambiguous, and delaying it would make every dismissal feel sluggish.
        if (clickInProgress)
        {
            Dismiss("deactivated (click)");
            return true;
        }

        if (foreground == IntPtr.Zero || foreground == handle)
        {
            // No new owner (a window was destroyed, a display rebuild is in flight) or we hold it
            // again: a transient state, not navigation.
            GlassDiagnostics.Event("[spotlight] ignored a deactivation that named no other window");
            return true;
        }

        // Our own windows activating themselves (a widget shown after a display change) is
        // programmatic, not the user leaving — take the keyboard back and stay open.
        if (BelongsToThisProcess(foreground))
        {
            GlassDiagnostics.Event("[spotlight] kept open across a deactivation caused by our own window");
            ReclaimForeground();
            return true;
        }

        // The shell is what the Alt release bounces to, and landing on the bare desktop is not
        // navigation either. Get the keyboard back instead of vanishing.
        if (IsShellWindow(foreground))
        {
            GlassDiagnostics.Event("[spotlight] kept open across a deactivation caused by the shell");
            ReclaimForeground();
            return true;
        }

        // An ordinary window owns the keyboard — most likely the user switching apps. Wait the
        // rest of the watch out before committing: a capture surface is created asynchronously,
        // so the app that was in front a moment ago is exactly what this branch sees while one is
        // starting. The wait costs a no-click dismissal at most DeactivationWatchMs.
        if (!lastChance) return false;

        GlassDiagnostics.Event(
            $"[spotlight] deactivation to another app 0x{foreground:X} ({DescribeWindow(foreground)})");
        Dismiss("deactivated");
        return true;
    }

    /// <summary>
    /// Whether the window that took the keyboard is a <b>capture surface</b> — Windows' own capture
    /// stack (Win+Shift+S 剪裁层, 截图和草图, Win11 截图工具) or a third-party capture tool.
    /// <para>
    /// Two tests, because neither is complete on its own. The <b>process name</b> is exact and
    /// cheap, but it only covers the tools someone thought to list, and it misses the capture modes
    /// built into other programs. The <b>window shape</b> — a borderless, composition-painted
    /// window covering its monitor — is what every one of those overlays has in common and what no
    /// window the user "went to" has. That is also why the name list can stay closed: a chat client
    /// that merely <i>offers</i> a 截图 hotkey still closes the overlay when the user switches to its
    /// main window, while its capture mode is recognised by shape.
    /// </para>
    /// </summary>
    private static bool IsCaptureOverlay(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return false;
        return IsCaptureProcess(hwnd) || IsBorderlessMonitorCover(hwnd);
    }

    /// <summary>
    /// Capture tools by process name. Matched case-insensitively: the exe name is the vendor's
    /// spelling, not a contract — Windows' own capture ships as <c>SnippingTool</c> on Win11 and as
    /// <c>ScreenSketch</c> / <c>ScreenClippingHost</c> on other builds.
    /// </summary>
    private static bool IsCaptureProcess(IntPtr hwnd)
    {
        try
        {
            GetWindowThreadProcessId(hwnd, out var processId);
            if (processId == 0) return false;

            using var process = System.Diagnostics.Process.GetProcessById((int)processId);
            var name = process.ProcessName;
            foreach (var candidate in CaptureProcessNames)
            {
                if (string.Equals(candidate, name, StringComparison.OrdinalIgnoreCase)) return true;
            }
        }
        catch
        {
            // A process that already exited (some capture layers are transient) must not break
            // the deactivation decision.
        }

        return false;
    }

    /// <summary>
    /// The shape fallback: a visible, caption-less top-level window, painted through composition,
    /// covering (nearly) the monitor it sits on. Every capture overlay looks like this; a normal
    /// application window does not, because it keeps <c>WS_CAPTION</c> even when maximized and
    /// paints through the normal redirection bitmap even when full-screen.
    /// </summary>
    private static bool IsBorderlessMonitorCover(IntPtr hwnd)
    {
        // Never our own furniture (a widget, the canvas) or the desktop itself — both are
        // borderless and can cover a monitor.
        if (BelongsToThisProcess(hwnd) || IsShellWindow(hwnd)) return false;
        if (!IsWindowVisible(hwnd)) return false;

        var style = unchecked((uint)GetWindowLong(hwnd, GwlStyle));
        if ((style & WsCaption) != 0) return false;   // an ordinary window, maximized or not
        if ((style & WsChild) != 0) return false;     // a control, not a top-level surface

        // Composition-painted: layered (per-pixel alpha) or redirection-bitmap-less (DWM only).
        // A full-screen video or a F11 browser is neither, which is what keeps them from being
        // mistaken for a capture surface.
        var exStyle = unchecked((uint)GetWindowLong(hwnd, GwlExStyle));
        if ((exStyle & (WsExLayered | WsExNoRedirectionBitmap)) == 0) return false;

        if (!GetWindowRect(hwnd, out var bounds)) return false;
        var width = bounds.Right - bounds.Left;
        var height = bounds.Bottom - bounds.Top;
        if (width <= 0 || height <= 0) return false;

        var monitor = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
        if (monitor == IntPtr.Zero) return false;

        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref info)) return false;

        var monitorWidth = info.Monitor.Right - info.Monitor.Left;
        var monitorHeight = info.Monitor.Bottom - info.Monitor.Top;
        if (monitorWidth <= 0 || monitorHeight <= 0) return false;

        // 90%: an overlay covers the monitor exactly, and the slack absorbs a DPI-virtualized
        // GetWindowRect on a scaled display.
        return width >= monitorWidth * 0.9 && height >= monitorHeight * 0.9;
    }

    /// <summary>
    /// Wait for a capture to hand the keyboard back.
    /// <para>
    /// The overlay stayed open on purpose, but it is unfocused now. Without this the user takes the
    /// shot, looks at the panel that is still on screen and finds that typing does nothing — which
    /// reads as "it closed anyway". Bounded and conditional: it reclaims only when the capture
    /// surface is gone <b>and</b> nothing else claimed the keyboard (nothing, us, or the bare
    /// desktop). A user who moved on to another app keeps that app; the overlay simply stays open
    /// behind it.
    /// </para>
    /// </summary>
    private void ArmCaptureReturnWatch()
    {
        var token = ++captureWatch;
        var waited = 0;

        void Step()
        {
            if (token != captureWatch) return;
            if (!IsReallyVisible()) return;

            var foreground = GetForegroundWindow();
            if (IsCaptureOverlay(foreground))
            {
                waited += CaptureReturnPollMs;
                if (waited < CaptureReturnWatchMs)
                {
                    DispatcherTimer.RunOnce(
                        Step, TimeSpan.FromMilliseconds(CaptureReturnPollMs), DispatcherPriority.Background);
                }

                return;
            }

            var handle = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
            if (foreground == handle) return;   // the keyboard came back on its own

            if (foreground == IntPtr.Zero || IsShellWindow(foreground))
            {
                GlassDiagnostics.Event("[spotlight] capture finished — taking the keyboard back");
                ReclaimForeground();
            }
        }

        DispatcherTimer.RunOnce(
            Step, TimeSpan.FromMilliseconds(CaptureReturnPollMs), DispatcherPriority.Background);
    }

    /// <summary>Process names that are a capture surface on sight (see <see cref="IsCaptureOverlay"/>).</summary>
    private static readonly string[] CaptureProcessNames =
    {
        // Windows' own capture stack.
        "ScreenClipping", "ScreenClippingHost", "ScreenSketch", "SnippingTool", "SnipAndSketch",
        // Third-party capture tools.
        "Snipaste", "PixPin", "ShareX", "Greenshot", "FSCapture", "FastStoneCapture",
        "PicPick", "Lightshot", "Screenpresso",
    };

    /// <summary>For the log only: <c>process/window-class</c>, so an unexpected dismissal is traceable.</summary>
    private static string DescribeWindow(IntPtr hwnd)
    {
        try
        {
            var className = new StringBuilder(64);
            GetClassName(hwnd, className, className.Capacity);
            GetWindowThreadProcessId(hwnd, out var processId);
            var name = "?";
            try
            {
                using var process = System.Diagnostics.Process.GetProcessById((int)processId);
                name = process.ProcessName;
            }
            catch
            {
                // Exited already — the class name is still worth logging.
            }

            return $"{name}/{className}";
        }
        catch
        {
            return "?";
        }
    }

    /// <summary>
    /// Take the foreground back after a deactivation that was not the user leaving, so a stolen
    /// focus cannot leave the overlay on screen but unable to receive a keystroke.
    /// <para>
    /// Bounded twice: at most <see cref="MaxReclaimsPerSummon"/> times per summon, and never more
    /// often than once per <see cref="ReclaimIntervalMs"/>. A window that keeps fighting for the
    /// foreground must not turn into a tug-of-war on the UI thread — if the steals continue, the
    /// overlay simply stays put and the next real click closes it.
    /// </para>
    /// </summary>
    private void ReclaimForeground()
    {
        var now = Environment.TickCount64;
        if (reclaimsSinceSummon >= MaxReclaimsPerSummon) return;
        if (now - lastReclaimAt < ReclaimIntervalMs) return;

        reclaimsSinceSummon++;
        lastReclaimAt = now;
        BringToForeground();
    }

    /// <summary>Whether any mouse button is physically down right now (a click is in progress).</summary>
    private static bool IsMouseButtonDown()
    {
        const int VkLeftButton = 0x01, VkRightButton = 0x02, VkMiddleButton = 0x04;
        const int VkXButton1 = 0x05, VkXButton2 = 0x06;

        // The high bit is "currently down"; the low bit (pressed since the last call) is
        // deliberately ignored — a stale press is not a click in progress.
        return (GetAsyncKeyState(VkLeftButton) & 0x8000) != 0
            || (GetAsyncKeyState(VkRightButton) & 0x8000) != 0
            || (GetAsyncKeyState(VkMiddleButton) & 0x8000) != 0
            || (GetAsyncKeyState(VkXButton1) & 0x8000) != 0
            || (GetAsyncKeyState(VkXButton2) & 0x8000) != 0;
    }

    /// <summary>Whether the window belongs to this process (our widgets, our panels).</summary>
    private static bool BelongsToThisProcess(IntPtr hwnd)
    {
        GetWindowThreadProcessId(hwnd, out var processId);
        return processId == (uint)Environment.ProcessId;
    }

    /// <summary>
    /// Whether the window is the shell itself (taskbar, secondary taskbars, desktop). A shell
    /// activation is never "the user opened another app" in a way that should close the overlay.
    /// </summary>
    private static bool IsShellWindow(IntPtr hwnd)
    {
        var buffer = new StringBuilder(64);
        if (GetClassName(hwnd, buffer, buffer.Capacity) <= 0) return false;

        return buffer.ToString() is "Shell_TrayWnd" or "Shell_SecondaryTrayWnd" or "Progman" or "WorkerW";
    }

    /// <summary>
    /// Take the foreground the way the shell does, since <see cref="Window.Activate"/> alone is
    /// refused while another process owns the foreground.
    /// </summary>
    private void BringToForeground()
    {
        var handle = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (handle == IntPtr.Zero) return;

        var foreground = GetForegroundWindow();
        if (foreground == handle) return;

        var targetThread = foreground == IntPtr.Zero ? 0 : GetWindowThreadProcessId(foreground, out _);
        var ourThread = GetCurrentThreadId();
        var attached = targetThread != 0 && targetThread != ourThread
                       && AttachThreadInput(ourThread, targetThread, true);
        try
        {
            SetForegroundWindow(handle);
            BringWindowToTop(handle);
            SetActiveWindow(handle);
        }
        finally
        {
            if (attached) AttachThreadInput(ourThread, targetThread, false);
        }
    }

    /// <summary>
    /// Visibility as the operating system sees it. Avalonia's <see cref="Visual.IsVisible"/> is its
    /// own bookkeeping; a show refused by the platform leaves it <c>true</c> over a hidden HWND.
    /// </summary>
    private bool IsReallyVisible()
    {
        var handle = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        return handle != IntPtr.Zero ? IsWindowVisible(handle) : IsVisible;
    }

    private void OnSettingsChanged(object? sender, AppSettings? oldData, AppSettings newData)
    {
        ApplyLayout();
        ApplyAppearance();
        // The category bindings are local keystrokes, so a settings save is the only moment they
        // can change — rebuild before the next key press, not lazily inside the handler.
        RebuildCategoryGestures();

        if (IsVisible)
        {
            UpdateMode();
            if (screenSampling)
            {
                ScreenSnapshotProvider.Capture();
                GlassSurface.RequestSampledFrame();
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT point);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool BringWindowToTop(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern IntPtr SetActiveWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint attach, uint attachTo, bool doAttach);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hwnd, StringBuilder buffer, int maxCount);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hwnd, int index);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    // Window-style bits the capture-surface test reads (IsBorderlessMonitorCover).
    private const int GwlStyle = -16;
    private const int GwlExStyle = -20;
    private const uint WsCaption = 0x00C00000;
    private const uint WsChild = 0x40000000;
    private const uint WsExLayered = 0x00080000;
    private const uint WsExNoRedirectionBitmap = 0x00200000;
    private const uint MonitorDefaultToNearest = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect Work;
        public uint Flags;
    }
}

/// <summary>True while a result row is NOT broken: gates the icon slot against the 断链 overlay.</summary>
public sealed class SpotlightNotBrokenConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is false;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Broken shortcuts dim their whole row to 60% instead of disappearing.</summary>
public sealed class SpotlightBrokenOpacityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? 0.6 : 1.0;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Non-empty string gate: the row badge and the preview's ContentText.</summary>
public sealed class SpotlightHasTextConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is string text && text.Length > 0;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// <see cref="SpotlightGlyphKind"/> → the <b>filled</b> mark (SVG artwork). Bound to the fill Path
/// that only shows for app/file searches; the stroked twin below covers the rest.
/// </summary>
public sealed class SpotlightGlyphFillConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is SpotlightGlyphKind kind ? SpotlightGlyphs.Fill(kind) : SpotlightGlyphs.Fill(SpotlightGlyphKind.Search);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary><see cref="SpotlightGlyphKind"/> → the <b>stroked</b> mark (magnifier / calculator).</summary>
public sealed class SpotlightGlyphStrokeConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is SpotlightGlyphKind kind ? SpotlightGlyphs.Stroke(kind) : SpotlightGlyphs.Stroke(SpotlightGlyphKind.Search);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// How the overlay was summoned — the two hotkeys' contract.
/// <para>
/// <see cref="AppList"/> is the Launchpad-style surface (grid of apps, recents, quick actions).
/// <see cref="Search"/> is a bare search bar: the same card, but it opens one row tall with the
/// field focused and no grid, because the user pressed the gesture that says "I want to type".
/// </para>
/// </summary>
public enum SpotlightSummonMode
{
    /// <summary>应用列表式: the app grid first.</summary>
    AppList,

    /// <summary>搜索栏式: the field only, results grow into the card as they arrive.</summary>
    Search,
}
