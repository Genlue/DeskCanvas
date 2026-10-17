using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using Avalonia.Threading;
using Countdown.Models;
using Countdown.Services;
using DeskCanvas.Contracts;
using DeskCanvas.Services;

namespace Countdown.ViewModels;

public class CountdownViewModel : INotifyPropertyChanged, IDisposable
{
    private CountdownModel settings;
    private CountdownItemViewModel? selectedItem;
    private readonly ObservableCollection<CountdownItemViewModel> items = [];
    private readonly IWidgetSession? session;
    private bool isDisposed;
    private bool isRunning;
    private bool renderActive = true;
    private int tickSubscriptions;

    /// <summary>The shared timer currently subscribed (null while gated off). The tick tier
    /// moves between the 1-second and the 1-minute shared timer as the granularity and the
    /// 24-hour window demand — the old private DispatcherTimer's dynamic interval.</summary>
    private UpdateTimer? activeTimer;

    /// <summary>
    /// <paramref name="session"/> is injected by WidgetFactory when the host runs the session
    /// pipeline: the tick subscription is then gated on the session's
    /// <see cref="ActivitySnapshot.RenderActive"/>. Null keeps the legacy always-on behaviour
    /// (preview, legacy layout entries) — those hosts never push activity either.
    /// </summary>
    public CountdownViewModel(CountdownModel settings, IWidgetSession? session = null)
    {
        this.settings = settings;
        this.session = session;
        renderActive = session?.Activity.RenderActive ?? true;
        LoadEvents();
    }

    public CountdownModel Settings => settings;

    public bool IsDisposed => isDisposed;
    public bool IsRunning => isRunning;

    public CountdownCardStyle Style => settings.Style;

    public CountdownGranularity Granularity => settings.Granularity;

    public bool IsMinimalNumberStyle => settings.Style == CountdownCardStyle.MinimalNumber;
    public bool IsRingProgressStyle => settings.Style == CountdownCardStyle.RingProgress;
    public bool IsEventListStyle => settings.Style == CountdownCardStyle.EventList;

    public CountdownItemViewModel? SelectedItem
    {
        get => selectedItem;
        private set
        {
            if (selectedItem != value)
            {
                selectedItem = value;
                OnPropertyChanged();
            }
        }
    }

    public ObservableCollection<CountdownItemViewModel> Items => items;

    public void Start()
    {
        if (isDisposed || isRunning) return;
        isRunning = true;

        CountdownStore.ModelChanged -= OnStoreModelChanged;
        CountdownStore.ModelChanged += OnStoreModelChanged;

        RefreshItems();
    }

    public void Stop()
    {
        if (!isRunning) return;
        isRunning = false;

        CountdownStore.ModelChanged -= OnStoreModelChanged;
        UpdateTickSubscription();
    }

    /// <summary>
    /// Host activity push (the <see cref="DeskCanvas.Runtime.IWidgetActivityConsumer"/> path,
    /// invoked on the UI thread): ticking follows the snapshot's
    /// <see cref="ActivitySnapshot.RenderActive"/>. Session-less view models ignore the push
    /// entirely (the host never sends one to them).
    /// </summary>
    public void OnSessionActivity(ActivitySnapshot activity)
    {
        if (session == null) return;
        renderActive = activity.RenderActive;
        UpdateTickSubscription();
    }

    /// <summary>Check seam: the live subscription count over the shared timers (0 or 1).</summary>
    public int TickSubscriptionCountForChecks => tickSubscriptions;

    /// <summary>Check seam: the injected session (null on the legacy path).</summary>
    public IWidgetSession? SessionForChecks => session;

    /// <summary>Check seam: whether the high-precision (1-second) tier is currently live.</summary>
    public bool IsHighPrecisionTickForChecks => activeTimer == TimerService.Timer1Second;

    public void UpdateSettings(CountdownModel newSettings)
    {
        settings = newSettings;
        OnPropertyChanged(nameof(Settings));
        OnPropertyChanged(nameof(Style));
        OnPropertyChanged(nameof(Granularity));
        OnPropertyChanged(nameof(IsMinimalNumberStyle));
        OnPropertyChanged(nameof(IsRingProgressStyle));
        OnPropertyChanged(nameof(IsEventListStyle));

        RefreshItems();
    }

    private void OnStoreModelChanged(List<CountdownItem> events, object? sender)
    {
        if (isDisposed) return;
        if (Dispatcher.UIThread.CheckAccess())
        {
            RefreshItems();
        }
        else
        {
            Dispatcher.UIThread.Post(RefreshItems);
        }
    }

    private void LoadEvents()
    {
        var rawEvents = CountdownStore.Get();
        var now = DateTime.Now;

        items.Clear();
        foreach (var evt in rawEvents)
        {
            items.Add(new CountdownItemViewModel(evt, settings.Granularity, now));
        }

        UpdateSelectedItem();
    }

    private void RefreshItems()
    {
        if (isDisposed) return;
        var rawEvents = CountdownStore.Get();
        var now = DateTime.Now;

        // Sync existing items in place to avoid churning ViewModel wrappers and visual tree
        var currentDict = items.ToDictionary(i => i.Id);
        var newIds = new HashSet<string>(rawEvents.Select(e => e.Id));

        // Remove deleted
        for (int i = items.Count - 1; i >= 0; i--)
        {
            if (!newIds.Contains(items[i].Id))
            {
                items.RemoveAt(i);
            }
        }

        // Update existing or add new in order
        int targetIndex = 0;
        foreach (var evt in rawEvents)
        {
            if (currentDict.TryGetValue(evt.Id, out var existing))
            {
                existing.Update(evt, settings.Granularity, now);
                int currentIndex = items.IndexOf(existing);
                if (currentIndex != targetIndex)
                {
                    items.Move(currentIndex, targetIndex);
                }
            }
            else
            {
                items.Insert(targetIndex, new CountdownItemViewModel(evt, settings.Granularity, now));
            }
            targetIndex++;
        }

        UpdateSelectedItem();
        UpdateTickSubscription();
    }

    private void UpdateSelectedItem()
    {
        if (items.Count == 0)
        {
            SelectedItem = null;
            return;
        }

        if (!string.IsNullOrEmpty(settings.TargetEventId))
        {
            var target = items.FirstOrDefault(i => i.Id == settings.TargetEventId);
            if (target != null)
            {
                SelectedItem = target;
                return;
            }
        }

        // Default: pick pinned first, then nearest upcoming
        var pinned = items.FirstOrDefault(i => i.IsPinned);
        if (pinned != null)
        {
            SelectedItem = pinned;
            return;
        }

        // Nearest upcoming
        var upcoming = items
            .Where(i => !i.IsCountup)
            .OrderBy(i => i.Computed.Days)
            .FirstOrDefault();

        SelectedItem = upcoming ?? items.FirstOrDefault();
    }

    private void OnTick()
    {
        if (isDisposed || !isRunning || !renderActive) return;
        TickItems();
        UpdateTickSubscription();
    }

    private void TickItems()
    {
        var now = DateTime.Now;
        foreach (var item in items)
        {
            item.Tick(now);
        }
    }

    public void Dispose()
    {
        if (isDisposed) return;
        isDisposed = true;
        Stop();
    }

    /// <summary>
    /// Single subscription state machine over the shared timers: exactly one subscription
    /// exists while the view model is running AND the session renders, and its tier (the
    /// 1-second or the 1-minute shared timer) follows the granularity and the 24-hour window.
    /// This replaces the old private DispatcherTimer and its dynamic interval: the shared
    /// subscription is reconciled from the inputs on every change, so overlapping transitions
    /// (a covered screen, a granularity flip, Start/Stop) can neither double-subscribe nor
    /// double-unsubscribe, and the host-wide timer pause covers the countdown too.
    /// </summary>
    private void UpdateTickSubscription()
    {
        var wanted = isRunning && !isDisposed && renderActive;
        var target = wanted && NeedsHighPrecisionTick()
            ? TimerService.Timer1Second
            : TimerService.Timer1Minute;

        if (wanted)
        {
            if (activeTimer == target) return;
            if (activeTimer != null)
            {
                activeTimer.Unsubscribe(OnTick);
                tickSubscriptions--;
            }
            activeTimer = target;
            activeTimer.Subscribe(OnTick);
            tickSubscriptions++;
            // Fresh values right after (re)subscribing: while the subscription was gated off
            // (covered screen, stopped) the displayed countdowns went stale.
            TickItems();
        }
        else if (activeTimer != null)
        {
            activeTimer.Unsubscribe(OnTick);
            activeTimer = null;
            tickSubscriptions--;
        }
    }

    /// <summary>
    /// Whether the 1-second tier is needed: <see cref="CountdownGranularity.AlwaysDetailed"/>
    /// always, <see cref="CountdownGranularity.Auto24Hours"/> while at least one countdown
    /// event is inside its 24-hour window. The former minute-alignment scheduling (a private
    /// tick exactly on the next whole-minute / 24-hour boundary) collapses into the shared
    /// 1-minute tier, which notices the same boundaries at most one minute later.
    /// </summary>
    private bool NeedsHighPrecisionTick()
    {
        if (settings.Granularity == CountdownGranularity.AlwaysDetailed)
        {
            return true;
        }

        if (settings.Granularity != CountdownGranularity.Auto24Hours)
        {
            return false;
        }

        var now = DateTime.Now;
        foreach (var item in items)
        {
            if (item.IsCountup) continue;
            var diff = item.Computed.EffectiveTargetDate - now;
            if (diff.TotalSeconds > 0 && diff.TotalHours <= 24)
            {
                return true;
            }
        }

        return false;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
