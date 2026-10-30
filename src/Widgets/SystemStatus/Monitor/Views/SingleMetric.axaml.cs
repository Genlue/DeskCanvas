using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using DeskCanvas.Contracts;
using DeskCanvas.Core.Interfaces;
using DeskCanvas.Runtime;
using Monitor.Models;
using Monitor.ViewModels;

namespace Monitor.Views;

public partial class SingleMetric : UserControl, IWidgetSurfaceHints, IWidgetActivityConsumer
{
    /// <inheritdoc />
    public string? ColorfulBackgroundResourceKey => "MonitorCardBackground";
    private readonly SingleMetricModel model;
    private readonly IWidgetSession? session;
    private SingleMetricViewModel? viewModel;

    public SingleMetric() :
        this(new SingleMetricModel(MetricType.CpuUsage)) {}

    /// <summary>
    /// <paramref name="session"/> is injected by WidgetFactory when the host runs the session
    /// pipeline: the view model then gates its polling subscription on the session's activity.
    /// Null keeps the legacy always-on behaviour (preview, legacy layout entries).
    /// </summary>
    public SingleMetric(SingleMetricModel model, IWidgetSession? session = null)
    {
        this.model = model;
        this.session = session;
        viewModel = new SingleMetricViewModel(model, session);
        DataContext = viewModel;
        SizeChanged += OnSizeChanged;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        InitializeComponent();
        Ring.ApplyMetricType(model.Metric);
        LandscapeRing.ApplyMetricType(model.Metric);
    }

    /// <inheritdoc />
    public void OnSessionActivity(ActivitySnapshot activity) => viewModel?.OnSessionActivity(activity);

    /// <summary>
    /// The view can be detached and re-added later (the settings window caches pages and the
    /// Gallery keeps a live preview control), so everything <see cref="OnUnloaded"/> released
    /// is restarted here — on a *new* view model, because the released one was disposed.
    /// </summary>
    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        SizeChanged -= OnSizeChanged;
        SizeChanged += OnSizeChanged;

        if (viewModel != null) return;
        viewModel = new SingleMetricViewModel(model, session);
        DataContext = viewModel;
    }

    /// <summary>
    /// Release the view model (its one-second timer subscription, which otherwise keeps the
    /// WMI polling alive forever) without detaching this handler: the control stays usable
    /// and is unloaded again on every later removal from the visual tree.
    /// </summary>
    private void OnUnloaded(object? sender, RoutedEventArgs e)
    {
        SizeChanged -= OnSizeChanged;
        viewModel?.Dispose();
        viewModel = null;
    }

    private void OnSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        var size = e.NewSize;

        // S tier (1×1): a bare ring carries no number — put the percentage in the
        // center (replacing the metric icon) instead of hiding all text.
        var tiny = !IsLandscape(size) && size.Height < 80;

        // Landscape (2×1, 3×2, …): gauge left, percentage right, whole canvas
        // scales to the card so the proportions stay right.
        var landscape = IsLandscape(size);

        Landscape.IsVisible = landscape && !tiny;

        if (tiny)
        {
            Stacked.IsVisible = true;
            Stacked.RowDefinitions = RowDefinitions.Parse("*");
            Grid.SetRow(Ring, 0);
            Grid.SetRow(Text, 0);
            Text.IsVisible = false;
            Ring.ShowCenterValue = true;
            Margin = new(6);
        }
        else if (landscape)
        {
            Stacked.IsVisible = false;
            Ring.ShowCenterValue = false;
            Margin = new(12);
        }
        else
        {
            Stacked.IsVisible = true;
            Stacked.RowDefinitions = RowDefinitions.Parse("*,*");
            Grid.SetRow(Ring, 0);
            Grid.SetRow(Text, 1);
            Text.IsVisible = true;
            Ring.ShowCenterValue = false;
            Margin = new(12);
        }
    }

    private static bool IsLandscape(Size size) => size.Width > size.Height * 1.25;
}
