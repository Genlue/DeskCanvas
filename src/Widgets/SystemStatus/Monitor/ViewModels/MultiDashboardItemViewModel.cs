using ReactiveUI;

namespace Monitor.ViewModels;

/// <summary>
/// One gauge of the multi-dashboard widget. Wraps the per-tick metric snapshot in
/// a reactive object so binding <c>Items[i].Metric</c> updates the ring in place.
/// </summary>
public class MultiDashboardItemViewModel : ReactiveObject
{
    public Monitor.Models.MetricType Type { get; init; }
    private MetricViewModel? metric;

    public MetricViewModel? Metric
    {
        get => metric;
        set => this.RaiseAndSetIfChanged(ref metric, value);
    }
}
