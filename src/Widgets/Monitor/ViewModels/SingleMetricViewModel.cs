using Monitor.Models;
using Monitor.Services;
using ReactiveUI;
using DeskCanvas.Services;

namespace Monitor.ViewModels;

public class SingleMetricViewModel : ReactiveObject, IDisposable
{
    private readonly SingleMetricModel model;

    /// <summary>Set once <see cref="Dispose"/> ran; keeps disposal (and late results) harmless.</summary>
    private bool disposed;

    public SingleMetricViewModel(SingleMetricModel model)
    {
        this.model = model;
        metric = new MetricViewModel(0d);
        TimerService.Timer1Second.Subscribe(Update);
    }
    
    private MetricViewModel? metric;
    public MetricViewModel? Metric 
    {
        get => metric;
        private set => this.RaiseAndSetIfChanged(ref metric, value);
    }

    private void Update()
    {
        if (disposed) return;
        _ = UpdateAsync();
    }

    private async Task UpdateAsync()
    {
        var value = await MetricService.GetMetricValue(model.Metric);
        // A WMI query that returns after disposal (the view was unloaded meanwhile) must not
        // touch this view model any more, and must not revive it through its bindings.
        if (disposed) return;

        var icon = MetricService.GetMetricIcon(model.Metric);
        if (value.HasValue)
            Metric = new MetricViewModel(value.Value, icon);
    }

    /// <summary>
    /// Detach from the process-lifetime one-second timer, which otherwise holds this view
    /// model (and its WMI query workload) alive for the whole process — every abandoned
    /// instance would keep querying the system once a second forever. The view must build a
    /// fresh view model before using this one again. Idempotent.
    /// </summary>
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        TimerService.Timer1Second.Unsubscribe(Update);
        GC.SuppressFinalize(this);
    }
}
