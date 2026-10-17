using Monitor.Models;
using Monitor.Services;
using ReactiveUI;
using DeskCanvas.Contracts;
using DeskCanvas.Services;

namespace Monitor.ViewModels;

public class MultiDashboardViewModel : ReactiveObject, IDisposable
{
    private readonly MetricType[] types;
    private readonly Action updateAction;
    private readonly IWidgetSession? session;
    private readonly string owner = $"monitor-dashboard-{Guid.NewGuid():N}";
    private readonly CancellationTokenSource lifetime = new();
    private bool renderActive = true;
    private int tickSubscriptions;
    private bool disposed;

    /// <summary>
    /// In-flight latch: the tick fires every second, but a round is up to four WMI queries
    /// (Win32_NetworkAdapter alone can take hundreds of milliseconds on a busy machine). Without
    /// the latch every slow round stacked another one behind it — "慢速 WMI 未完成时跳过下一轮".
    /// The next tick simply finds the previous round still running and skips.
    /// </summary>
    private int updateInFlight;

    public MultiDashboardItemViewModel[] Items { get; }

    /// <summary>
    /// <paramref name="session"/> is injected by WidgetFactory when the host runs the session
    /// pipeline: the one-second subscription is then gated on the session's activity. Null
    /// keeps the legacy always-on behaviour (preview, legacy layout entries) — those hosts
    /// never push activity either.
    /// </summary>
    public MultiDashboardViewModel(MultiDashboardModel model, IWidgetSession? session = null)
    {
        this.session = session;
        renderActive = session?.Activity.RenderActive ?? true;
        types = Normalize(model.Metrics);
        Items = types
            .Select(type => new MultiDashboardItemViewModel
            {
                Type = type,
                Metric = new MetricViewModel(0d, MetricService.GetMetricIcon(type))
            })
            .ToArray();

        updateAction = Update;
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

    private static MetricType[] Normalize(MetricType[]? metrics)
    {
        var result = (metrics ?? []).ToList();
        while (result.Count < 4)
            result.Add(MultiDashboardModel.DefaultMetrics[result.Count % MultiDashboardModel.DefaultMetrics.Length]);
        return result.Take(4).ToArray();
    }

    private void Update()
    {
        if (disposed) return;
        if (Interlocked.CompareExchange(ref updateInFlight, 1, 0) != 0) return;
        _ = UpdateAsync();
    }

    private async Task UpdateAsync()
    {
        try
        {
            for (var i = 0; i < types.Length; i++)
            {
                var type = types[i];
                var value = await MetricService.GetMetricValue(type, owner, lifetime.Token);
                if (disposed) return;
                if (value.HasValue)
                    Items[i].Metric = new MetricViewModel(value.Value, MetricService.GetMetricIcon(type));
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            Interlocked.Exchange(ref updateInFlight, 0);
        }
    }

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
            TimerService.Timer1Second.Subscribe(updateAction);
        }
        else if (!wanted && tickSubscriptions > 0)
        {
            tickSubscriptions--;
            TimerService.Timer1Second.Unsubscribe(updateAction);
        }
    }
}
