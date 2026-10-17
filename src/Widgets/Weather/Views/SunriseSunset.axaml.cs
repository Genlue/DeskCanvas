using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using DeskCanvas.Contracts;
using DeskCanvas.Core.Interfaces;
using DeskCanvas.Runtime;
using Weather.Models;
using Weather.ViewModels;

namespace Weather.Views;

public partial class SunriseSunset : UserControl, IWidgetActivityConsumer
{
    private readonly ForecastModel model;
    private readonly IWidgetSession? session;
    private ForecastViewModel? viewModel;

    public SunriseSunset() : this(new ForecastModel("Beijing", 39.9042, 116.4074, "celsius")) {}

    /// <summary>
    /// <paramref name="session"/> is injected by WidgetFactory when the host runs the session
    /// pipeline: the view model then gates its hourly refresh on the session's activity. Null
    /// keeps the legacy always-on behaviour (preview, legacy layout entries).
    /// </summary>
    public SunriseSunset(ForecastModel model, IWidgetSession? session = null)
    {
        this.model = model;
        this.session = session;
        viewModel = new ForecastViewModel(model, session);
        DataContext = viewModel;
        SizeChanged += OnSizeChanged;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        InitializeComponent();
    }

    /// <inheritdoc />
    public void OnSessionActivity(ActivitySnapshot activity) => viewModel?.OnSessionActivity(activity);

    /// <summary>
    /// The view can be detached and re-added later (the settings window caches pages and the
    /// Gallery keeps a live preview control), so everything <see cref="OnUnloaded"/> released
    /// is rebuilt here — including a *new* view model, because the released one was disposed.
    /// </summary>
    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        SizeChanged -= OnSizeChanged;
        SizeChanged += OnSizeChanged;

        if (viewModel != null) return;
        viewModel = new ForecastViewModel(model, session);
        DataContext = viewModel;
    }

    /// <summary>
    /// Release the view model (its hourly timer subscription and HTTP client) without
    /// detaching this handler: the control stays usable and is unloaded again on every
    /// later removal from the visual tree.
    /// </summary>
    private void OnUnloaded(object? sender, RoutedEventArgs e)
    {
        SizeChanged -= OnSizeChanged;
        viewModel?.Dispose();
        viewModel = null;
    }

    private void OnSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        var size = Math.Min(DesiredSize.Width, DesiredSize.Height);
        var margin = size >= 150 ? size * 0.1 : 6;
        Margin = new Thickness(margin, margin);
    }
}
