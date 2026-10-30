using ReactiveUI;
using DeskCanvas.Contracts;
using DeskCanvas.Services;
using Weather.Models;
using Weather.Services;

namespace Weather.ViewModels;

public class AirQualityViewModel : ReactiveObject, IDisposable
{
    private readonly ForecastModel model;
    private readonly OpenMeteoWeatherProvider provider;
    private readonly IWidgetSession? session;
    private readonly FetchRetry retry = new();
    private bool renderActive = true;
    private bool hasReading;
    private int tickSubscriptions;

    /// <summary>Set once <see cref="Dispose"/> ran; keeps disposal (and late replies) harmless.</summary>
    private bool disposed;

    /// <summary>
    /// <paramref name="session"/> is injected by WidgetFactory when the host runs the session
    /// pipeline: the hourly refresh subscription is then gated on the session's activity. Null
    /// keeps the legacy always-on behaviour (preview, legacy layout entries) — those hosts
    /// never push activity either.
    /// </summary>
    public AirQualityViewModel(ForecastModel model, IWidgetSession? session = null)
    {
        provider = new OpenMeteoWeatherProvider();
        this.model = model;
        this.session = session;
        renderActive = session?.Activity.RenderActive ?? true;

        UpdateTickSubscription();
        UpdateForecast();
    }

    /// <summary>
    /// Host activity push (the <see cref="DeskCanvas.Runtime.IWidgetActivityConsumer"/> path,
    /// invoked on the UI thread): the hourly refresh follows the snapshot's RenderActive.
    /// Session-less view models ignore the push entirely (the host never sends one to them).
    /// </summary>
    public void OnSessionActivity(ActivitySnapshot activity)
    {
        if (session == null) return;
        renderActive = activity.RenderActive;
        UpdateTickSubscription();

        // Same boot-race rule as the forecast view model: ask again as soon as the card renders
        // if the first attempt (made before the network was up) failed.
        if (renderActive && !hasReading) retry.Schedule(UpdateForecast);
        else if (!renderActive) retry.Stop();
    }

    /// <summary>Check seam: the live subscription count over the shared timer (0 or 1).</summary>
    public int TickSubscriptionCountForChecks => tickSubscriptions;

    /// <summary>Check seam: the injected session (null on the legacy path).</summary>
    public IWidgetSession? SessionForChecks => session;

    /// <summary>Check seam: the latest session gate value.</summary>
    public bool RenderActiveForChecks => renderActive;

    /// <summary>Check seam: true once a reading actually arrived (the retry stops there).</summary>
    public bool HasReadingForChecks => hasReading;

    /// <summary>Check seam: true while a boot-offline retry is queued.</summary>
    public bool RetryPendingForChecks => retry.IsPendingForChecks;

    private void UpdateForecast()
    {
        if (disposed) return;
        _ = UpdateForecastAsync();
    }

    private async Task UpdateForecastAsync()
    {
        var forecast = await provider.GetAirQualityAsync(model.Latitude, model.Longitude);
        // A reply that arrives after disposal (the view was unloaded mid-request) must not
        // touch this view model any more.
        if (disposed) return;

        if (forecast?.Current == null)
        {
            if (renderActive) retry.Schedule(UpdateForecast);
            return;
        }

        retry.Reset();
        hasReading = true;
        Metric = new(0, 100, forecast.Current.AirQualityIndex, WeatherIcon.Wind.Value);
    }

    private MetricViewModel metric = new(0, 100, 0, null);
    public MetricViewModel Metric
    {
        get => metric;
        private set => this.RaiseAndSetIfChanged(ref metric, value);
    }

    /// <summary>
    /// Detach from the process-lifetime hourly timer and release this owner's
    /// in-flight provider waits. The view rebuilds a fresh owner before reuse.
    /// </summary>
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        retry.Dispose();
        UpdateTickSubscription();
        provider.Dispose();
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
            TimerService.Timer1Hour.Subscribe(UpdateForecast);
        }
        else if (!wanted && tickSubscriptions > 0)
        {
            tickSubscriptions--;
            TimerService.Timer1Hour.Unsubscribe(UpdateForecast);
        }
    }
}
