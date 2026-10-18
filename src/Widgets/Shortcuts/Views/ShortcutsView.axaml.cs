using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Styling;
using Avalonia.Threading;
using DeskCanvas.Core.Interfaces;
using DeskCanvas.Core.Models;
using DeskCanvas.Core.Models.Settings;
using DeskCanvas.Services;
using DeskCanvas.Views;
using Shortcuts.Models;
using Shortcuts.Services;
using Shortcuts.ViewModels;

namespace Shortcuts.Views;

public partial class ShortcutsView : UserControl, IWidgetSelfRefreshing
{
    private readonly IWidgetLayoutProvider? layoutProvider;
    private ShortcutsModel model;
    private readonly ShortcutsCardViewModel viewModel = new();
    private WidgetTier currentTier = (WidgetTier)(-1);
    private bool eventsAttached;
    private double cardRadius = 16;
    private IAppSettingsProvider? subscribedSettings;

    public ShortcutsView() : this(null, new ShortcutsModel(), null, null)
    {
    }

    public ShortcutsView(
        IWidgetLayoutProvider? layoutProvider,
        ShortcutsModel model,
        IAppSettingsProvider? appSettings,
        WidgetFactory? widgetFactory)
    {
        this.layoutProvider = layoutProvider;
        this.model = model ?? new ShortcutsModel();
        ShortcutsRuntime.Publish(appSettings, widgetFactory);

        DataContext = viewModel;
        InitializeComponent();

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        SizeChanged += OnSizeChanged;

        // Per-window theme variants (light/dark can differ per screen): keep the tiles' text
        // brushes in sync with whatever variant this card actually renders in. The tile radius
        // follows the same scoped resources, so re-resolve it whenever the variant changes.
        ActualThemeVariantChanged += (_, _) =>
        {
            ApplyCardRadius();
            viewModel.ApplyTheme(ActualThemeVariant == ThemeVariant.Dark);
        };
    }

    // ---- lifecycle ---------------------------------------------------------------

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        AttachEvents();
        ShortcutsRuntime.PublishTopLevel(TopLevel.GetTopLevel(this));
        ApplyCardRadius();
        viewModel.ApplyTheme(ActualThemeVariant == ThemeVariant.Dark);
        EnsureHotkeys();
        if (Bounds.Width > 0 && Bounds.Height > 0)
            ApplySize(Bounds.Size);
        else
            Dispatcher.UIThread.Post(() => ApplySize(Bounds.Size), DispatcherPriority.Loaded);
    }

    private void OnUnloaded(object? sender, RoutedEventArgs e)
    {
        ShortcutsRuntime.ReleaseTopLevel(TopLevel.GetTopLevel(this));
        DetachEvents();
    }

    private void AttachEvents()
    {
        if (eventsAttached) return;
        eventsAttached = true;
        ShortcutsStore.ModelChanged += OnStoreChanged;
        ShortcutRunner.RunEvent += OnRunEvent;
        subscribedSettings = ShortcutsRuntime.AppSettings;
        if (subscribedSettings != null)
            subscribedSettings.DataChanged += OnAppSettingsChanged;
        viewModel.Rebuild();
    }

    private void DetachEvents()
    {
        if (!eventsAttached) return;
        eventsAttached = false;
        ShortcutsStore.ModelChanged -= OnStoreChanged;
        ShortcutRunner.RunEvent -= OnRunEvent;
        if (subscribedSettings != null)
        {
            subscribedSettings.DataChanged -= OnAppSettingsChanged;
            subscribedSettings = null;
        }
    }

    private void OnStoreChanged(ShortcutsDocument document, object? sender)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            viewModel.Rebuild();
            EnsureHotkeys();
        }
        else
        {
            Dispatcher.UIThread.Post(() =>
            {
                viewModel.Rebuild();
                EnsureHotkeys();
            });
        }
    }

    private void OnAppSettingsChanged(object? sender, AppSettings? oldData, AppSettings newData)
    {
        if (Dispatcher.UIThread.CheckAccess()) ApplyCardRadius();
        else Dispatcher.UIThread.Post(ApplyCardRadius);
    }

    /// <summary>Start the OS hotkey host only once a shortcut actually has a binding.</summary>
    private void EnsureHotkeys()
    {
        try
        {
            if (ShortcutsStore.Get().Shortcuts.Any(s => !string.IsNullOrWhiteSpace(s.Hotkey)))
                ShortcutHotkeyService.EnsureStarted();
        }
        catch
        {
            // Hotkey setup must never take the card down.
        }
    }

    /// <summary>
    /// Resolve the host card radius (already scaled by the widget shell) and push it into the
    /// tiles. The app-settings fallback is in physical pixels, so convert it to DIPs.
    /// </summary>
    private void ApplyCardRadius()
    {
        double radius;
        if (ThemeScopeResolver.TryFindResource<CornerRadius>(this, "WidgetCardCornerRadius",
                ActualThemeVariant, out var cardCorner))
        {
            radius = Math.Max(0, cardCorner.TopLeft);
        }
        else
        {
            try
            {
                var raw = ShortcutsRuntime.AppSettings?.Get().Dimensions.Radius ?? 16;
                var scaling = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0;
                if (!double.IsFinite(scaling) || scaling <= 0) scaling = 1.0;
                radius = Math.Max(0, raw / scaling);
            }
            catch
            {
                radius = 16;
            }
        }

        cardRadius = radius;
        viewModel.SetCardRadius(radius);
    }

    private void OnTileBorderSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if ((sender as Control)?.DataContext is not ShortcutTileViewModel tile) return;
        if (e.NewSize.Width > 0 && e.NewSize.Height > 0)
            tile.SetTileSize(e.NewSize.Width, e.NewSize.Height);
    }

    private void OnRunEvent(RunEvent runEvent)
    {
        if (Dispatcher.UIThread.CheckAccess()) viewModel.ApplyRunEvent(runEvent);
        else Dispatcher.UIThread.Post(() => viewModel.ApplyRunEvent(runEvent));
    }

    private void OnSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (e.NewSize.Width > 0 && e.NewSize.Height > 0)
            ApplySize(e.NewSize);
    }

    private void ApplySize(Size size)
    {
        if (size.Width <= 0 || size.Height <= 0) return;

        var tier = ResolveTier(size);
        if (tier == currentTier) return;
        currentTier = tier;
        viewModel.SetCardRadius(cardRadius);
        viewModel.ApplyLayout(model, tier);
    }

    private WidgetTier ResolveTier(Size size)
    {
        try
        {
            var spanTier = SizeTiers.ResolveTier(this, size);
            if (spanTier != WidgetTier.Other) return spanTier;
        }
        catch
        {
            // Fall through to the pixel bands.
        }

        if (size.Width <= SizeTiers.CellSide && size.Height <= SizeTiers.CellSide)
            return WidgetTier.Cell;
        if (size.Width >= 280 && size.Height >= 280)
            return WidgetTier.Large;
        if (size.Width >= 260 && size.Width > size.Height * 1.35)
            return WidgetTier.Medium;

        return WidgetTier.Small;
    }

    /// <inheritdoc />
    public void Refresh(WidgetLayout layout)
    {
        if (layout.Settings is not { } settings || settings.ValueKind != JsonValueKind.Object)
            return;

        try
        {
            var updated = settings.Deserialize<ShortcutsModel>();
            if (updated == null) return;
            model = updated;
            currentTier = (WidgetTier)(-1);
            ApplySize(Bounds.Size);
        }
        catch
        {
            // A malformed settings blob keeps the last good model.
        }
    }

    // ---- interaction -------------------------------------------------------------

    /// <summary>Background click: open the quick panel (tiles consume their own clicks).</summary>
    private void OnCardPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        ShortcutsPopupWindow.ShowPopup(ScreenCenter(), TopLevel.GetTopLevel(this) as Window, SpawnCornerRadius);
        e.Handled = true;
    }

    private async void OnTileClicked(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if ((sender as Control)?.DataContext is not ShortcutTileViewModel tile) return;

        try
        {
            if (tile.IsCreate)
            {
                ShowCreateMenu(sender as Control);
                return;
            }

            // A click on a running tile stops it.
            if (ShortcutRunner.IsRunning(tile.ShortcutId))
            {
                ShortcutRunner.Cancel(tile.ShortcutId);
                return;
            }

            var shortcut = viewModel.FindShortcut(tile.ShortcutId);
            if (shortcut == null) return;

            if (model.ConfirmBeforeRun || shortcut.ConfirmBeforeRun)
            {
                var owner = TopLevel.GetTopLevel(this) as Window;
                if (owner != null)
                {
                    var ok = await ConfirmDialog.ConfirmAsync(
                        owner,
                        Loc.Format("Shortcuts_ConfirmRunMessage", shortcut.Name),
                        Loc.Get("Shortcuts_ConfirmRunOk"),
                        Loc.Get("Shortcuts_Cancel"));
                    if (!ok) return;
                }
            }

            await ShortcutRunner.RunAsync(shortcut, ShortcutsRuntime.CreateRunServices(this));
        }
        catch
        {
            // Run failures are surfaced through run events, never through a crash.
        }
    }

    private void OnTileMenuRun(object? sender, RoutedEventArgs e)
    {
        if ((sender as MenuItem)?.DataContext is not ShortcutTileViewModel tile || tile.IsCreate) return;
        var shortcut = viewModel.FindShortcut(tile.ShortcutId);
        if (shortcut == null) return;

        _ = RunFromMenuAsync(shortcut);
    }

    private async Task RunFromMenuAsync(ShortcutDefinition shortcut)
    {
        try
        {
            await ShortcutRunner.RunAsync(shortcut, ShortcutsRuntime.CreateRunServices(this));
        }
        catch
        {
            // Handled through run events.
        }
    }

    private void OnTileMenuEdit(object? sender, RoutedEventArgs e)
    {
        if ((sender as MenuItem)?.DataContext is not ShortcutTileViewModel tile || tile.IsCreate) return;
        ShortcutsEditorWindow.ShowEditor(tile.ShortcutId, TopLevel.GetTopLevel(this) as Window);
    }

    private void OnTileMenuDuplicate(object? sender, RoutedEventArgs e)
    {
        if ((sender as MenuItem)?.DataContext is not ShortcutTileViewModel tile || tile.IsCreate) return;
        ShortcutsStore.Duplicate(tile.ShortcutId, this);
    }

    private async void OnTileMenuDelete(object? sender, RoutedEventArgs e)
    {
        if ((sender as MenuItem)?.DataContext is not ShortcutTileViewModel tile || tile.IsCreate) return;

        try
        {
            var owner = TopLevel.GetTopLevel(this) as Window;
            if (owner != null)
            {
                var ok = await ConfirmDialog.ConfirmAsync(
                    owner,
                    Loc.Format("Shortcuts_Delete_Message", tile.Name),
                    Loc.Get("Shortcuts_Delete_Ok"),
                    Loc.Get("Shortcuts_Cancel"));
                if (!ok) return;
            }

            ShortcutsStore.Delete(tile.ShortcutId, this);
        }
        catch
        {
            // A failed confirm must never crash the card.
        }
    }

    private void ShowCreateMenu(Control? anchor)
    {
        if (anchor == null) return;

        var flyout = new MenuFlyout();
        foreach (var (labelKey, create) in ShortcutTemplates.Menu)
        {
            var item = new MenuItem { Header = Loc.Get(labelKey) };
            item.Click += async (_, _) =>
            {
                try
                {
                    var shortcut = create();
                    ShortcutsStore.Upsert(shortcut, this);
                    ShortcutsEditorWindow.ShowEditor(shortcut.Id, TopLevel.GetTopLevel(this) as Window);
                }
                catch
                {
                    // Creation failures surface via the store's SaveFailed diagnostics.
                }
            };
            flyout.Items.Add(item);
        }

        flyout.ShowAt(anchor);
    }

    // ---- helpers -----------------------------------------------------------------

    private Point? ScreenCenter()
    {
        if (TopLevel.GetTopLevel(this) is not TopLevel topLevel) return null;
        var centerLocal = new Point(Bounds.Width / 2, Bounds.Height / 2);
        var rootPoint = this.TranslatePoint(centerLocal, topLevel);
        if (rootPoint == null) return null;
        var screenPoint = topLevel.PointToScreen(rootPoint.Value);
        return new Point(screenPoint.X, screenPoint.Y);
    }

    /// <summary>The host card's corner radius, so the panel's corners match exactly.</summary>
    private double? SpawnCornerRadius
    {
        get
        {
            var radius = (TopLevel.GetTopLevel(this) as Widget)?.Radius.TopLeft ?? 0;
            return radius > 0 ? radius : null;
        }
    }
}
