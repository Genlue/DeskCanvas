using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using Avalonia.Threading;
using Countdown.Models;
using Countdown.Services;

namespace Countdown.ViewModels;

public class CountdownViewModel : INotifyPropertyChanged, IDisposable
{
    private CountdownModel settings;
    private DispatcherTimer? timer;
    private CountdownItemViewModel? selectedItem;
    private readonly ObservableCollection<CountdownItemViewModel> items = [];
    private bool isDisposed;
    private bool isRunning;

    public CountdownViewModel(CountdownModel settings)
    {
        this.settings = settings;
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
        StartTimer();
    }

    public void Stop()
    {
        if (!isRunning) return;
        isRunning = false;

        CountdownStore.ModelChanged -= OnStoreModelChanged;
        StopTimer();
    }

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
        AdjustTimerInterval();
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
        AdjustTimerInterval();
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

    private void StartTimer()
    {
        StopTimer();
        timer = new DispatcherTimer();
        timer.Tick += (s, e) => OnTick();
        AdjustTimerInterval();
        timer.Start();
    }

    private void StopTimer()
    {
        if (timer != null)
        {
            timer.Stop();
            timer = null;
        }
    }

    private void AdjustTimerInterval()
    {
        if (timer == null || isDisposed) return;

        var now = DateTime.Now;
        bool needsHighPrecision = false;

        if (settings.Granularity == CountdownGranularity.AlwaysDetailed)
        {
            needsHighPrecision = true;
        }
        else if (settings.Granularity == CountdownGranularity.Auto24Hours)
        {
            // Only need 1-second ticks when at least one countdown event has entered the 24-hour window
            foreach (var item in items)
            {
                if (!item.IsCountup)
                {
                    var diff = item.Computed.EffectiveTargetDate - now;
                    if (diff.TotalSeconds > 0 && diff.TotalHours <= 24)
                    {
                        needsHighPrecision = true;
                        break;
                    }
                }
            }
        }

        TimeSpan newInterval;
        if (needsHighPrecision)
        {
            newInterval = TimeSpan.FromSeconds(1);
        }
        else
        {
            // Align to whole minute boundary (e.g. 1..60s)
            int secondsToNext = 60 - now.Second;
            if (secondsToNext <= 0) secondsToNext = 60;

            if (settings.Granularity == CountdownGranularity.Auto24Hours)
            {
                foreach (var item in items)
                {
                    if (!item.IsCountup)
                    {
                        var timeUntil24h = (item.Computed.EffectiveTargetDate - TimeSpan.FromHours(24)) - now;
                        if (timeUntil24h > TimeSpan.Zero && timeUntil24h.TotalSeconds < secondsToNext)
                        {
                            secondsToNext = Math.Max(1, (int)Math.Ceiling(timeUntil24h.TotalSeconds));
                        }
                    }
                }
            }

            newInterval = TimeSpan.FromSeconds(secondsToNext);
        }

        if (timer.Interval != newInterval)
        {
            timer.Interval = newInterval;
            if (timer.IsEnabled)
            {
                timer.Stop();
                timer.Start();
            }
        }
    }

    private void OnTick()
    {
        if (isDisposed || !isRunning) return;
        var now = DateTime.Now;

        foreach (var item in items)
        {
            item.Tick(now);
        }

        AdjustTimerInterval();
    }

    public void Dispose()
    {
        if (isDisposed) return;
        isDisposed = true;
        Stop();
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
