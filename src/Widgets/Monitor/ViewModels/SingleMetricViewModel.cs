using Monitor.Models;
using Monitor.Services;
using ReactiveUI;
using DeskCanvas.Contracts;
using DeskCanvas.Services;

namespace Monitor.ViewModels;

public class SingleMetricViewModel : ReactiveObject, IDisposable
{
    private readonly SingleMetricModel model;
    private readonly IWidgetSession? session;
    private readonly string owner = $"monitor-single-{Guid.NewGuid():N}";
    private readonly CancellationTokenSource lifetime = new();
    private bool renderActive = true;
    private int tickSubscriptions;

    /// <summary>Set once <see cref="Dispose"/> ran; keeps disposal (and late results) harmless.</summary>
    private bool disposed;

    /// <summary>
    /// <paramref name="session"/> is injected by WidgetFactory when the host runs the session
    /// pipeline: the one-second subscription is then gated on the session's activity. Null
    /// keeps the legacy always-on behaviour (preview, legacy layout entries) — those hosts
    /// never push activity either.
    /// </summary>
    public SingleMetricViewModel(SingleMetricModel model, IWidgetSession? session = null)
    {
        this.model = model;
        this.session = session;
        renderActive = session?.Activity.RenderActive ?? true;
        metric = new MetricViewModel(0d);
        UpdateTickSubscription();
    }

    /// <summary>
    /// Host activity push (the <see cref="DeskCanvas.Runtime.IWidgetActivityConsumer"/> path,
    /// invoked on the UI thread): polling follows the snapshot's RenderActive. Session-less
    /// view models ignore the push entirely (the host never sends one to them).
    /// </summary>
    public void OnSessionActivity(ActivitySnapshot activity)
    {
        if (session == null) return;
        renderActive = activity.RenderActive;
        UpdateTickSubscription();
    }

    /// <summary>Check seam: the live subscription count over the shared timer (0 or 1).</summary>
    public int TickSubscriptionCountForChecks => tickSubscriptions;

    /// <summary>Check seam: the injected session (null on the legacy path).</summary>
    public IWidgetSession? SessionForChecks => session;

    /// <summary>Check seam: the latest session gate value.</summary>
    public bool RenderActiveForChecks => renderActive;

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
        double? value;
        try { value = await MetricService.GetMetricValue(model.Metric, owner, lifetime.Token); }
        catch (OperationCanceledException) { return; }
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
        lifetime.Cancel();
        lifetime.Dispose();
        UpdateTickSubscription();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Single subscription state machine: the shared timer subscription exists exactly while
    /// the session renders AND the view model is not disposed. Session-less view models render
    /// always (<paramref name="renderActive"/> starts true and nothing flips it). Overlapping
    /// transitions cannot double-subscribe or double-unsubscribe — the count is reconciled
    /// from both inputs on every change instead of toggled from each.
    /// </summary>
    private void UpdateTickSubscription()
    {
        var wanted = renderActive && !disposed;
        if (wanted && tickSubscriptions == 0)
        {
            tickSubscriptions++;
            TimerService.Timer1Second.Subscribe(Update);
        }
        else if (!wanted && tickSubscriptions > 0)
        {
            tickSubscriptions--;
            TimerService.Timer1Second.Unsubscribe(Update);
        }
    }
}
