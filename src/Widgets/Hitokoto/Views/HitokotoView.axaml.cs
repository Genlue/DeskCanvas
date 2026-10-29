using Avalonia.Input.Platform;
using System;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using DeskCanvas.UI.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DeskCanvas.Core.Interfaces;
using DeskCanvas.Core.Models;
using DeskCanvas.Contracts;
using DeskCanvas.Runtime;
using DeskCanvas.Views;
using Hitokoto.Locales;
using Hitokoto.Models;
using Hitokoto.Services;

namespace Hitokoto.Views;

/// <summary>
/// Primary desktop widget for Hitokoto (一言).
/// Supports double-click to switch quotes, configurable auto-refresh intervals,
/// setting-switched frameless desktop watermark mode, and a Material 3 secondary panel.
/// </summary>
public partial class HitokotoView : UserControl, IFramelessWidget, IWidgetSelfRefreshing, IWidgetSuspendable, IWidgetActivityConsumer, IDisposable, IWidgetSurfaceHints
{
    /// <inheritdoc />
    public string? ColorfulBackgroundResourceKey => "HitokotoCardBackground";

    private readonly IAppSettingsProvider? appSettingsProvider;
    private readonly IWidgetLayoutProvider? widgetLayoutProvider;
    private readonly IWidgetSession? session;
    private HitokotoModel model;
    private HitokotoItem currentItem;

    private DispatcherTimer? autoRefreshTimer;
    private DispatcherTimer? toastTimer;
    private CancellationTokenSource? fetchCts;
    private bool isFetching;
    private bool suspended;
    private bool disposed;
    private bool renderActive = true;

    /// <summary>True between Loaded and Unloaded — the store subscription and the refresh timer are bound to this.</summary>
    private bool attached;
    private bool storeSubscribed;

    /// <inheritdoc />
    public bool IsFrameless => model.IsFrameless;

    public HitokotoView()
        : this(null, null, new HitokotoModel())
    {
    }

    public HitokotoView(HitokotoModel model)
        : this(null, null, model)
    {
    }

    public HitokotoView(IWidgetLayoutProvider widgetLayoutProvider)
        : this(null, widgetLayoutProvider, widgetLayoutProvider.Get().GetModel<HitokotoModel>() ?? new HitokotoModel())
    {
    }

    public HitokotoView(IAppSettingsProvider appSettingsProvider, IWidgetLayoutProvider widgetLayoutProvider)
        : this(appSettingsProvider, widgetLayoutProvider, widgetLayoutProvider.Get().GetModel<HitokotoModel>() ?? new HitokotoModel())
    {
    }

    /// <summary>
    /// <paramref name="session"/> is injected by WidgetFactory when the host runs the session
    /// pipeline: the auto-refresh timer is then additionally gated on the session's activity
    /// (besides attach/suspend). Null keeps the legacy always-on behaviour (preview, legacy
    /// layout entries) — those hosts never push activity either.
    /// </summary>
    public HitokotoView(
        IAppSettingsProvider? appSettingsProvider,
        IWidgetLayoutProvider? widgetLayoutProvider,
        HitokotoModel model,
        IWidgetSession? session = null)
    {
        this.appSettingsProvider = appSettingsProvider;
        this.widgetLayoutProvider = widgetLayoutProvider;
        this.session = session;
        renderActive = session?.Activity.RenderActive ?? true;
        this.model = model.Normalize();
        currentItem = HitokotoStore.Instance.GetCurrentOrFallback(this.model);

        InitializeComponent();

        bool isZh = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.Equals("zh", StringComparison.OrdinalIgnoreCase);
        ToolTip.SetTip(SourceBadgeText, isZh ? "点击在浏览器查看该一言详情" : "Click to view quote details in browser");

        if (this.model.IsFrameless)
        {
            Classes.Add("Frameless");
        }

        SizeChanged += OnSizeChanged;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;

        // Tunnel handler on the quote area so single-click drags the widget window
        // and double-click switches to the next quote ("双击换一句").
        RootGrid.AddHandler(PointerPressedEvent, OnCardPointerPressed, RoutingStrategies.Tunnel);

        ApplyModelVisuals();
        RenderCurrentItem();
        // Store subscriptions and the auto-refresh timer are bound on attach (see OnLoaded) and
        // released on detach (OnUnloaded) instead of living in the constructor: a control that is
        // constructed but never shown (gallery preview) must not listen or tick, and a control
        // that is detached and re-attached must come back live.
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        if (disposed) return;
        attached = true;
        SubscribeStore();

        // Re-read the store on attach: while detached (gallery preview between visits, widget
        // re-hosted) another instance may have moved the current quote on.
        currentItem = HitokotoStore.Instance.GetCurrentOrFallback(model);

        SyncFramelessWindowState();
        ApplyResponsiveLayout(Bounds.Size);
        RenderCurrentItem();
        ConfigureAutoRefreshTimer();

        // Fetch a fresh quote on startup if the cached quote is older than the refresh interval
        // (or if this is a live desktop widget and cached quote has never been fetched).
        if (TopLevel.GetTopLevel(this) is Widget)
        {
            var intervalMinutes = model.RefreshIntervalMinutes > 0 ? model.RefreshIntervalMinutes : 30;
            if (currentItem.FetchedAt == default ||
                (DateTime.Now - currentItem.FetchedAt).TotalMinutes >= intervalMinutes)
            {
                _ = FetchNextQuoteAsync(showLoadingHint: false);
            }
        }
    }

    /// <summary>
    /// Detach-only teardown: the view may be attached again (the gallery rebuilds every preview on
    /// each visit), so it releases its store subscription and stops its timers but stays usable.
    /// <see cref="Dispose"/> is the permanent path, called by the host when the control is dropped.
    /// </summary>
    private void OnUnloaded(object? sender, RoutedEventArgs e)
    {
        attached = false;
        UnsubscribeStore();
        autoRefreshTimer?.Stop();
        toastTimer?.Stop();
        fetchCts?.Cancel();
    }

    private void SubscribeStore()
    {
        if (!attached || storeSubscribed || disposed) return;
        storeSubscribed = true;
        HitokotoStore.Instance.Changed += OnStoreChanged;
        HitokotoStore.Instance.QuoteSelected += OnQuoteSelected;
    }

    private void UnsubscribeStore()
    {
        if (!storeSubscribed) return;
        storeSubscribed = false;
        HitokotoStore.Instance.Changed -= OnStoreChanged;
        HitokotoStore.Instance.QuoteSelected -= OnQuoteSelected;
    }

    /// <inheritdoc />
    public void Refresh(WidgetLayout layout)
    {
        var updatedModel = layout.GetModel<HitokotoModel>();
        if (updatedModel == null) return;

        var prevCategories = model.Categories != null ? string.Join(",", model.Categories) : "";
        var nextNormalized = updatedModel.Normalize();
        var nextCategories = nextNormalized.Categories != null ? string.Join(",", nextNormalized.Categories) : "";
        var filterChanged = prevCategories != nextCategories
                            || model.MinLength != nextNormalized.MinLength
                            || model.MaxLength != nextNormalized.MaxLength
                            || model.CustomApiUrl != nextNormalized.CustomApiUrl;

        model = nextNormalized;
        SyncFramelessWindowState();
        ApplyModelVisuals();
        ApplyResponsiveLayout(Bounds.Size);
        RenderCurrentItem();
        ConfigureAutoRefreshTimer();

        if (filterChanged)
        {
            _ = FetchNextQuoteAsync(showLoadingHint: false);
        }
    }

    private void SyncFramelessWindowState()
    {
        if (model.IsFrameless)
        {
            if (!Classes.Contains("Frameless"))
                Classes.Add("Frameless");
            Padding = new Thickness(12, 10);
        }
        else
        {
            Classes.Remove("Frameless");
            Padding = new Thickness(0);
        }

        (TopLevel.GetTopLevel(this) as Widget)?.RefreshFramelessState();
    }

    private void ApplyModelVisuals()
    {
        // Font family resolution
        if (!string.IsNullOrWhiteSpace(model.FontFamily))
        {
            QuoteTextBlock.FontFamily = new FontFamily(model.FontFamily);
            AttributionText.FontFamily = new FontFamily(model.FontFamily);
        }
        else if (model.StyleMode == HitokotoStyleMode.PoeticCenter)
        {
            QuoteTextBlock.FontFamily = new FontFamily("STZhongsong, Songti SC, SimSun, Georgia, serif");
            AttributionText.FontFamily = new FontFamily("STZhongsong, Songti SC, SimSun, Georgia, serif");
        }
        else
        {
            QuoteTextBlock.ClearValue(TextBlock.FontFamilyProperty);
            AttributionText.ClearValue(TextBlock.FontFamilyProperty);
        }

        // Alignment by style mode
        if (model.StyleMode == HitokotoStyleMode.PoeticCenter)
        {
            QuoteTextBlock.TextAlignment = TextAlignment.Center;
            QuoteTextBlock.HorizontalAlignment = HorizontalAlignment.Center;
            AttributionText.HorizontalAlignment = HorizontalAlignment.Center;
            Grid.SetColumn(AttributionText, 0);
            Grid.SetColumnSpan(AttributionText, 3);
        }
        else
        {
            QuoteTextBlock.TextAlignment = TextAlignment.Left;
            QuoteTextBlock.HorizontalAlignment = HorizontalAlignment.Stretch;
            AttributionText.HorizontalAlignment = HorizontalAlignment.Right;
            Grid.SetColumn(AttributionText, 2);
            Grid.SetColumnSpan(AttributionText, 1);
        }

        // Subtle text shadow in frameless mode so text remains readable on any desktop wallpaper
        if (model.IsFrameless)
        {
            var shadow = new DropShadowEffect
            {
                BlurRadius = 8,
                OffsetX = 0,
                OffsetY = 1.5,
                Color = Color.FromArgb(185, 0, 0, 0)
            };
            QuoteTextBlock.Effect = shadow;
            AttributionText.Effect = shadow;
            CategoryText.Effect = shadow;
        }
        else
        {
            QuoteTextBlock.Effect = null;
            AttributionText.Effect = null;
            CategoryText.Effect = null;
        }

        UpdateActionToolbarVisibility();
    }

    private void ApplyResponsiveLayout(Size size)
    {
        var w = size.Width > 0 ? size.Width : (widgetLayoutProvider?.Get().Width ?? 300);
        var h = size.Height > 0 ? size.Height : (widgetLayoutProvider?.Get().Height ?? 150);

        bool isStrip = h <= 96;          // 2x1 or 4x1 strip
        bool isCompactSquare = w <= 210; // 2x2 square
        bool isLarge = w >= 340 && h >= 240; // 4x4 large card

        // Watermark visibility
        WatermarkQuote.IsVisible = model.StyleMode == HitokotoStyleMode.ModernQuote && !isStrip;
        WatermarkQuote.FontSize = isCompactSquare ? 48 : (isLarge ? 82 : 64);

        // Top bar & bottom bar visibility
        if (isStrip)
        {
            TopBarGrid.IsVisible = false;
            BottomBarGrid.IsVisible = model.ShowAttribution;
            SourceBadgeText.IsVisible = false;
            QuoteTextBlock.MaxLines = 2;
            QuoteTextBlock.TextTrimming = TextTrimming.CharacterEllipsis;
        }
        else
        {
            TopBarGrid.IsVisible = model.ShowCategoryTag || !model.AutoHideActions || IsPointerOver;
            CategoryPill.IsVisible = model.ShowCategoryTag;
            BottomBarGrid.IsVisible = model.ShowAttribution;
            SourceBadgeText.IsVisible = !isCompactSquare && model.StyleMode != HitokotoStyleMode.PoeticCenter;
            QuoteTextBlock.MaxLines = 0;
            QuoteTextBlock.TextTrimming = TextTrimming.None;
        }

        // Measure-driven button degradation on narrow cards so buttons never clip
        LikeBtn.IsVisible = model.ShowLikeCount && w >= 175;
        CopyBtn.IsVisible = w >= 150;
        FavoriteBtn.IsVisible = w >= 130;
        NextBtn.IsVisible = true;
        ExpandBtn.IsVisible = true;

        // Font size scaling
        double targetFontSize;
        if (model.FontSize > 0)
        {
            targetFontSize = isStrip ? Math.Min(model.FontSize, 14) : model.FontSize;
        }
        else
        {
            var len = currentItem.Hitokoto?.Length ?? 0;
            if (isStrip)
                targetFontSize = 12.5;
            else if (isCompactSquare)
                targetFontSize = len > 32 ? 12.5 : 13.5;
            else if (isLarge)
                targetFontSize = len > 50 ? 16 : 18.5;
            else
                targetFontSize = len > 42 ? 14 : 15.5;
        }

        QuoteTextBlock.FontSize = targetFontSize;
        QuoteTextBlock.LineHeight = Math.Round(targetFontSize * 1.52);
        AttributionText.FontSize = isStrip ? 11 : (isLarge ? 13 : 12);
    }

    private void RenderCurrentItem()
    {
        CategoryText.Text = currentItem.CategoryName;
        QuoteTextBlock.Text = currentItem.Hitokoto;
        AttributionText.Text = currentItem.FormattedAttribution;
        SourceBadgeText.Text = currentItem.Id > 0 ? $"hitokoto.cn #{currentItem.Id}" : "hitokoto.cn";

        var isLiked = HitokotoStore.Instance.IsLiked(currentItem.Uuid);
        // Filled vs outline is the state cue (开发规范 §4.4: a vector, not a text glyph).
        LikeIcon.Data = isLiked ? CommonGlyphs.HeartFilled : CommonGlyphs.HeartOutline;
        LikeCountText.Text = currentItem.Likes.ToString();

        var isFav = HitokotoStore.Instance.IsFavorite(currentItem.Uuid);
        FavoriteIcon.Data = isFav ? CommonGlyphs.StarFilled : CommonGlyphs.StarOutline;

        ApplyResponsiveLayout(Bounds.Size);
    }

    private void ConfigureAutoRefreshTimer()
    {
        autoRefreshTimer?.Stop();
        if (!attached || suspended || disposed || !renderActive || model.RefreshIntervalMinutes <= 0)
            return;

        autoRefreshTimer ??= new DispatcherTimer();
        autoRefreshTimer.Interval = TimeSpan.FromMinutes(Math.Max(1, model.RefreshIntervalMinutes));
        autoRefreshTimer.Tick -= OnAutoRefreshTick;
        autoRefreshTimer.Tick += OnAutoRefreshTick;
        autoRefreshTimer.Start();
    }

    private void OnAutoRefreshTick(object? sender, EventArgs e)
    {
        if (suspended || disposed || !renderActive) return;
        _ = FetchNextQuoteAsync(showLoadingHint: false);
    }

    /// <summary>
    /// Host activity push (the <see cref="IWidgetActivityConsumer"/> path, invoked on the UI
    /// thread): the auto-refresh timer follows the snapshot's RenderActive, beside the
    /// attach/suspend gates the legacy paths already had. Session-less views ignore the push
    /// entirely (the host never sends one to them).
    /// </summary>
    public void OnSessionActivity(ActivitySnapshot activity)
    {
        if (session == null) return;
        renderActive = activity.RenderActive;
        if (renderActive)
        {
            ConfigureAutoRefreshTimer();
        }
        else
        {
            autoRefreshTimer?.Stop();
        }
    }

    /// <summary>Check seam: the injected session (null on the legacy path).</summary>
    public IWidgetSession? SessionForChecks => session;

    /// <summary>Check seam: the latest session gate value.</summary>
    public bool RenderActiveForChecks => renderActive;

    public async Task FetchNextQuoteAsync(bool showLoadingHint = true)
    {
        if (isFetching || disposed) return;
        isFetching = true;

        fetchCts?.Cancel();
        fetchCts?.Dispose();
        fetchCts = new CancellationTokenSource();
        var token = fetchCts.Token;

        try
        {
            if (showLoadingHint)
            {
                QuoteContentHost.Opacity = 0.35;
            }

            var next = await HitokotoService.FetchQuoteAsync(model, currentItem.Uuid, token);
            if (token.IsCancellationRequested || disposed) return;

            currentItem = next;
            HitokotoStore.Instance.RecordQuote(next);

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                RenderCurrentItem();
                QuoteContentHost.Opacity = 1.0;
            });
        }
        catch (OperationCanceledException)
        {
            // Ignored
        }
        catch
        {
            await Dispatcher.UIThread.InvokeAsync(() => QuoteContentHost.Opacity = 1.0);
        }
        finally
        {
            isFetching = false;
        }
    }

    // ---------- Interaction ----------

    private void OnCardPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;

        // Ignore clicks on buttons so toolbar buttons work normally
        if (e.Source is Visual sourceVisual && FindAncestorButton(sourceVisual) != null)
            return;

        if (e.ClickCount >= 2)
        {
            // Requirement 3: 双击换一句
            _ = FetchNextQuoteAsync(showLoadingHint: true);
            e.Handled = true;
            return;
        }

        if (e.ClickCount == 1)
        {
            // Pass single-click through to the Widget window so dragging works anywhere on the card
            (TopLevel.GetTopLevel(this) as Widget)?.OnPointerPressed(this, e);
            e.Handled = true;
        }
    }

    private static Button? FindAncestorButton(Visual? visual)
    {
        var current = visual;
        while (current != null)
        {
            if (current is Button btn) return btn;
            current = current.GetVisualParent();
        }
        return null;
    }

    protected override void OnPointerEntered(PointerEventArgs e)
    {
        base.OnPointerEntered(e);
        UpdateActionToolbarVisibility();
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        UpdateActionToolbarVisibility();
    }

    private void UpdateActionToolbarVisibility()
    {
        if (ActionToolbar == null) return;
        if (!model.AutoHideActions || IsPointerOver)
        {
            ActionToolbar.Opacity = 1.0;
            ActionToolbar.IsHitTestVisible = true;
        }
        else
        {
            ActionToolbar.Opacity = 0.0;
            ActionToolbar.IsHitTestVisible = false;
        }
    }

    private void OnNextClicked(object? sender, RoutedEventArgs e)
    {
        _ = FetchNextQuoteAsync(showLoadingHint: true);
    }

    private async void OnLikeClicked(object? sender, RoutedEventArgs e)
    {
        if (HitokotoStore.Instance.IsLiked(currentItem.Uuid))
        {
            var isZh = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.Equals("zh", StringComparison.OrdinalIgnoreCase);
            ShowToast(isZh ? "已经点过赞啦" : "Already liked");
            return;
        }

        currentItem = HitokotoStore.Instance.MarkLiked(currentItem);
        RenderCurrentItem();
        ShowToast(Locale.Hitokoto_LikedToast);
        await HitokotoService.SubmitLikeAsync(currentItem.Uuid);
    }

    private void OnFavoriteClicked(object? sender, RoutedEventArgs e)
    {
        var nowFav = HitokotoStore.Instance.ToggleFavorite(currentItem);
        RenderCurrentItem();
        ShowToast(nowFav ? Locale.Hitokoto_FavoritedToast : Locale.Hitokoto_UnfavoritedToast);
    }

    private async void OnCopyClicked(object? sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel?.Clipboard != null)
        {
            await topLevel.Clipboard.SetTextAsync(currentItem.CopyPlainText);
            ShowToast(Locale.Hitokoto_Copied);
        }
    }

    private void OnOpenPanelClicked(object? sender, RoutedEventArgs e)
    {
        OpenPopup();
    }

    public void OpenPopup()
    {
        Point? screenCenter = null;
        if (TopLevel.GetTopLevel(this) is { } topLevel)
        {
            var centerLocal = new Point(Bounds.Width / 2, Bounds.Height / 2);
            var rootPoint = this.TranslatePoint(centerLocal, topLevel);
            if (rootPoint.HasValue)
            {
                var sp = topLevel.PointToScreen(rootPoint.Value);
                screenCenter = new Point(sp.X, sp.Y);
            }
        }

        var owner = TopLevel.GetTopLevel(this) as Window;
        var r = (TopLevel.GetTopLevel(this) as Widget)?.Radius.TopLeft ?? 0;
        double? cornerRadius = r > 0 ? r : null;

        HitokotoPopupWindow.ShowPopup(
            model,
            currentItem,
            screenCenter,
            owner,
            () => FetchNextQuoteAsync(showLoadingHint: true),
            cornerRadius);
    }

    private void OnSourceBadgePressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = currentItem.WebUrl,
                UseShellExecute = true
            });
            e.Handled = true;
        }
        catch
        {
            // Ignore shell launch error
        }
    }

    private void ShowToast(string message)
    {
        ToastText.Text = message;
        ToastBanner.IsVisible = true;
        toastTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1600) };
        toastTimer.Stop();
        toastTimer.Tick -= OnToastTick;
        toastTimer.Tick += OnToastTick;
        toastTimer.Start();
    }

    private void OnToastTick(object? sender, EventArgs e)
    {
        toastTimer?.Stop();
        ToastBanner.IsVisible = false;
    }

    private void OnStoreChanged(object? sender, EventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (disposed) return;
            var latest = HitokotoStore.Instance.GetCurrentOrFallback(model);
            if (string.Equals(latest.Uuid, currentItem.Uuid, StringComparison.OrdinalIgnoreCase))
            {
                currentItem = latest;
            }
            RenderCurrentItem();
        });
    }

    private void OnQuoteSelected(object? sender, HitokotoItem selected)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (disposed) return;
            currentItem = selected;
            RenderCurrentItem();
        });
    }

    private void OnSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        ApplyResponsiveLayout(e.NewSize);
    }

    /// <inheritdoc />
    public void Suspend()
    {
        suspended = true;
        autoRefreshTimer?.Stop();
    }

    /// <inheritdoc />
    public void Resume()
    {
        if (!suspended) return;
        suspended = false;
        ConfigureAutoRefreshTimer();
    }

    /// <summary>
    /// Permanent teardown (the host drops the control). Idempotent, and distinct from the
    /// detach that <see cref="OnUnloaded"/> performs: after this the view stays silent even if it
    /// is attached again.
    /// </summary>
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        attached = false;

        SizeChanged -= OnSizeChanged;
        Loaded -= OnLoaded;
        Unloaded -= OnUnloaded;
        UnsubscribeStore();
        RootGrid.RemoveHandler(PointerPressedEvent, OnCardPointerPressed);

        autoRefreshTimer?.Stop();
        toastTimer?.Stop();
        fetchCts?.Cancel();
        fetchCts?.Dispose();
        fetchCts = null;
    }
}
