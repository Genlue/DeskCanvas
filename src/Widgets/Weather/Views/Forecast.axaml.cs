using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Weather.Models;
using Weather.ViewModels;
using Weather.Views.Controls;
using DeskCanvas.Services;

namespace Weather.Views;

public partial class Forecast : UserControl
{
    private readonly ForecastModel model;
    private ForecastViewModel? viewModel;

    public Forecast() : this(new ForecastModel("Beijing", 39.9042, 116.4074, "celsius")) {}
    
    public Forecast(ForecastModel model)
    {
        this.model = model;
        viewModel = new ForecastViewModel(model);
        Content = new ForecastSmall(viewModel, this);
        SizeChanged += OnSizeChanged;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        DoubleTapped += OnCardDoubleTapped;
        InitializeComponent();
    }

    private void OnCardDoubleTapped(object? sender, RoutedEventArgs e)
    {
        OpenPopupWindow();
    }

    public void OpenPopupWindow()
    {
        if (viewModel == null) return;
        var (screenCenter, _) = GetScreenCenterAndTopLevel();
        var owner = VisualRoot as Window;
        WeatherPopupWindow.ShowPopup(viewModel, screenCenter, owner, SpawnCornerRadius);
    }

    /// <summary>
    /// The host widget's visual corner radius, so the popup's corners and its glass match the
    /// widget card exactly. Null keeps the popup's own default radius.
    /// </summary>
    private double? SpawnCornerRadius
    {
        get
        {
            var r = (VisualRoot as DeskCanvas.Views.Widget)?.Radius.TopLeft ?? 0;
            return r > 0 ? r : null;
        }
    }

    private (Point? ScreenCenter, TopLevel? TopLevel) GetScreenCenterAndTopLevel()
    {
        if (VisualRoot is Visual rootVisual && VisualRoot is TopLevel topLevel)
        {
            var bounds = Bounds;
            var centerLocal = new Point(bounds.Width / 2, bounds.Height / 2);
            var rootPoint = this.TranslatePoint(centerLocal, rootVisual);
            if (rootPoint.HasValue)
            {
                var screenPoint = topLevel.PointToScreen(rootPoint.Value);
                return (new Point(screenPoint.X, screenPoint.Y), topLevel);
            }
        }
        return (null, null);
    }

    private WidgetTier? currentTier;

    /// <summary>
    /// The view can be detached and re-added later (the settings window caches pages and the
    /// Gallery keeps a live preview control), so everything <see cref="OnUnloaded"/> released
    /// is rebuilt here — on a *new* view model, because the released one was disposed.
    /// </summary>
    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        SizeChanged -= OnSizeChanged;
        SizeChanged += OnSizeChanged;

        if (viewModel != null) return;

        var vm = new ForecastViewModel(model);
        viewModel = vm;

        currentTier = null;
        // A re-added view keeps the size it already had, so SizeChanged will not fire again
        // to pick the tier content: resolve it from the size the card has right now.
        Content = CreateTierContent(Bounds.Size, vm);
    }

    /// <summary>
    /// Release the view model (its hourly timer subscription and HTTP client) without
    /// detaching this handler: the control stays usable and is unloaded again on every
    /// later removal from the visual tree.
    /// </summary>
    private void OnUnloaded(object? sender, RoutedEventArgs e)
    {
        SizeChanged -= OnSizeChanged;
        DoubleTapped -= OnCardDoubleTapped;
        currentTier = null;
        viewModel?.Dispose();
        viewModel = null;
    }

    private void OnSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        // Detached (unloaded) views have no view model to bind the tier content to.
        var current = viewModel;
        if (current == null) return;
        if (e.NewSize.Width <= 0 || e.NewSize.Height <= 0) return;

        var tier = SizeTiers.ResolveTier(this, e.NewSize);
        if (tier != currentTier || Content == null)
        {
            currentTier = tier;
            Content = CreateTierContent(e.NewSize, current);
        }
    }

    /// <summary>
    /// The content for one card size: phone-style tiers resolved from the grid span
    /// (2×2 = compact card, 4×2 = wide hourly strip, 4×4 = full daily forecast, 1×1 = the
    /// temperature only), with the historic pixel thresholds for spans that have no tier.
    /// </summary>
    private Control CreateTierContent(Size size, ForecastViewModel vm)
    {
        return SizeTiers.ResolveTier(this, size) switch
        {
            WidgetTier.Cell => new ForecastTiny(vm),
            WidgetTier.Small => new ForecastSmall(vm, this),
            WidgetTier.Medium => new ForecastWide(vm, this),
            WidgetTier.Large => new ForecastLarge(vm, this),
            _ => size switch
            {
                { Width: > 230, Height: > 230 } => new ForecastLarge(vm, this),
                { Width: > 230, Height: > 140 } => new ForecastWide(vm, this),
                { Width: > 75, Height: > 75 } => new ForecastSmall(vm, this),
                _ => new ForecastTiny(vm)
            }
        };
    }
}
