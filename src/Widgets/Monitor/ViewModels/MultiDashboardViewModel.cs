using Monitor.Models;
using Monitor.Services;
using ReactiveUI;
using DeskCanvas.Services;

namespace Monitor.ViewModels;

public class MultiDashboardViewModel : ReactiveObject, IDisposable
{
    private readonly MetricType[] types;
    private readonly Action updateAction;

    /// <summary>
    /// In-flight latch: the tick fires every second, but a round is up to four WMI queries
    /// (Win32_NetworkAdapter alone can take hundreds of milliseconds on a busy machine). Without
    /// the latch every slow round stacked another one behind it — "慢速 WMI 未完成时跳过下一轮".
    /// The next tick simply finds the previous round still running and skips.
    /// </summary>
    private int updateInFlight;

    public MultiDashboardItemViewModel[] Items { get; }

    public MultiDashboardViewModel(MultiDashboardModel model)
    {
        types = Normalize(model.Metrics);
        Items = types
            .Select(type => new MultiDashboardItemViewModel
            {
                Type = type,
                Metric = new MetricViewModel(0d, MetricService.GetMetricIcon(type))
            })
            .ToArray();

        updateAction = Update;
        TimerService.Timer1Second.Subscribe(updateAction);
    }

    private static MetricType[] Normalize(MetricType[]? metrics)
    {
        var result = (metrics ?? []).ToList();
        while (result.Count < 4)
            result.Add(MultiDashboardModel.DefaultMetrics[result.Count % MultiDashboardModel.DefaultMetrics.Length]);
        return result.Take(4).ToArray();
    }

    private void Update()
    {
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
                var value = await MetricService.GetMetricValue(type);
                if (value.HasValue)
                    Items[i].Metric = new MetricViewModel(value.Value, MetricService.GetMetricIcon(type));
            }
        }
        finally
        {
            Interlocked.Exchange(ref updateInFlight, 0);
        }
    }

    public void Dispose()
    {
        TimerService.Timer1Second.Unsubscribe(updateAction);
        GC.SuppressFinalize(this);
    }
}
