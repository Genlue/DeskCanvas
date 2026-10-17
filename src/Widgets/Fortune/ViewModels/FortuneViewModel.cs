using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Avalonia.Threading;
using DeskCanvas.Contracts;
using DeskCanvas.Services;
using Fortune.Models;
using Fortune.Services;

namespace Fortune.ViewModels;

public class FortuneViewModel : INotifyPropertyChanged, IDisposable
{
    private bool hasDrawnToday;
    private bool isDrawing;
    private FortuneItem? currentFortune;
    private FortuneModel model;
    private readonly IWidgetSession? session;
    private bool isRunning;
    private bool renderActive = true;
    private int tickSubscriptions;
    private DateTime lastSeenDate = DateTime.Today;

    public event PropertyChangedEventHandler? PropertyChanged;

    public bool HasDrawnToday
    {
        get => hasDrawnToday;
        set => SetField(ref hasDrawnToday, value);
    }

    public bool IsDrawing
    {
        get => isDrawing;
        set => SetField(ref isDrawing, value);
    }

    public FortuneItem? CurrentFortune
    {
        get => currentFortune;
        set => SetField(ref currentFortune, value);
    }

    public FortuneModel Model
    {
        get => model;
        set => SetField(ref model, value);
    }

    public string DateDisplay => DateTime.Today.ToString("M月d日 · 今日运气");

    public ObservableCollection<DailyFortuneRecord> History { get; } = [];

    /// <summary>
    /// <paramref name="session"/> is injected by the host (WidgetFactory probes the view
    /// constructor) and forwarded here by the view. With a session the daily-rollover watch
    /// runs on the shared 5-minute timer, gated by the activity snapshot's RenderActive —
    /// a covered screen never polls. Null keeps the historic behaviour: no timer at all,
    /// the drawn state is only evaluated at construction (the preview and legacy layout
    /// entries never receive activity pushes either).
    /// </summary>
    public FortuneViewModel(FortuneModel? initialModel = null, IWidgetSession? session = null)
    {
        model = initialModel ?? new FortuneModel();
        this.session = session;
        renderActive = session?.Activity.RenderActive ?? true;
        LoadCurrentState();
        FortuneStore.FortuneDrawn += OnFortuneDrawn;
        Start();
    }

    public void Start()
    {
        if (isRunning) return;
        isRunning = true;
        UpdateTickSubscription();
    }

    public void Stop()
    {
        if (!isRunning) return;
        isRunning = false;
        UpdateTickSubscription();
    }

    /// <summary>
    /// Host activity push (the <see cref="DeskCanvas.Runtime.IWidgetActivityConsumer"/> path,
    /// invoked on the UI thread): the daily-rollover watch follows the snapshot's
    /// <see cref="ActivitySnapshot.RenderActive"/>. Session-less view models ignore the push
    /// entirely (the host never sends one to them).
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

    public void Dispose()
    {
        Stop();
        FortuneStore.FortuneDrawn -= OnFortuneDrawn;
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Single subscription state machine: the shared timer subscription exists exactly while
    /// the widget is running, the session renders AND a session was injected at all. The
    /// session-less path keeps the historic no-timer behaviour, so it never subscribes.
    /// </summary>
    private void UpdateTickSubscription()
    {
        var wanted = isRunning && renderActive && session != null;
        if (wanted && tickSubscriptions == 0)
        {
            tickSubscriptions++;
            TimerService.Timer5Minutes.Subscribe(OnDayTick);
        }
        else if (!wanted && tickSubscriptions > 0)
        {
            tickSubscriptions--;
            TimerService.Timer5Minutes.Unsubscribe(OnDayTick);
        }
    }

    /// <summary>
    /// The daily-rollover watch: a long-lived widget must release yesterday's drawn state at
    /// midnight so the "draw today" affordance returns. The tick is a cheap date comparison
    /// until the day actually changes.
    /// </summary>
    private void OnDayTick()
    {
        var today = DateTime.Today;
        if (today == lastSeenDate) return;
        lastSeenDate = today;
        LoadCurrentState();
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DateDisplay)));
    }

    public void LoadCurrentState()
    {
        lastSeenDate = DateTime.Today;
        var today = FortuneStore.GetTodayFortune();
        if (today != null)
        {
            HasDrawnToday = true;
            CurrentFortune = today.Fortune;
        }
        else
        {
            HasDrawnToday = false;
            CurrentFortune = null;
        }

        History.Clear();
        var data = FortuneStore.GetData();
        foreach (var r in data.History)
        {
            History.Add(r);
        }
    }

    public async Task DrawTodayAsync()
    {
        if (IsDrawing) return;
        IsDrawing = true;

        // 600ms ritual vibration / shaking time
        await Task.Delay(600);

        var record = FortuneStore.DrawToday(Model.PoolType);
        CurrentFortune = record.Fortune;
        HasDrawnToday = true;
        IsDrawing = false;
    }

    public async Task<FortuneItem> FreeDrawAsync()
    {
        if (IsDrawing) return CurrentFortune ?? FortuneRepository.GetRandomFortune(Model.PoolType);
        IsDrawing = true;

        await Task.Delay(500);

        var record = FortuneStore.FreeDraw(Model.PoolType);
        IsDrawing = false;
        return record.Fortune;
    }

    private void OnFortuneDrawn(DailyFortuneRecord record)
    {
        Dispatcher.UIThread.Post(() =>
        {
            History.Insert(0, record);
            if (History.Count > 30) History.RemoveAt(History.Count - 1);
        });
    }

    protected bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }
}
