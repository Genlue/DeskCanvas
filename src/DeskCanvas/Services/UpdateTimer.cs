using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Threading;
using Microsoft.Win32;

namespace DeskCanvas.Services;

public class UpdateTimer : IDisposable
{
    private readonly DispatcherTimer timer;
    private readonly List<Action> subscribers = [];

    /// <summary>
    /// Snapshot the ticks are delivered from. Subscribers may (un)subscribe from inside their own
    /// callback — including the last one leaving, which stops the timer — so the tick cannot
    /// iterate the live list; the snapshot is rebuilt when the subscription set actually changes
    /// instead of allocating a fresh array on every tick (this runs 10×/s just for the 100 ms
    /// timer). It is also published eagerly on unsubscribe, so a stopped timer cannot pin the
    /// delegates of components that already left.
    /// </summary>
    private Action[] tickSnapshot = [];
    private bool snapshotDirty = true;

    /// <summary>Set while the host paused every widget (fullscreen application active).</summary>
    private bool paused;

    public UpdateTimer(TimeSpan interval)
    {
        timer = new DispatcherTimer { Interval = interval };
        timer.Tick += OnTimerTick;
        
        if (OperatingSystem.IsWindows())
        {
            try { SystemEvents.SessionSwitch += SystemEventsOnSessionSwitch; } catch { }
        }
    }

    public void Subscribe(Action action)
    {
        lock (subscribers)
        {
            if (subscribers.Contains(action)) return;
            subscribers.Add(action);
            snapshotDirty = true;
            if (subscribers.Count > 0 && !paused) timer.Start();
        }
    }

    public void Unsubscribe(Action action)
    {
        lock (subscribers)
        {
            if (!subscribers.Contains(action)) return;
            subscribers.Remove(action);
            // Publish the trimmed snapshot immediately instead of only marking it dirty.
            // While the timer keeps ticking the dirty flag would be consumed on the next tick
            // anyway — but the moment the last subscriber leaves the timer stops, and a stale
            // snapshot would then pin every removed action (and through the closure the whole
            // widget) for as long as nothing subscribes again. Clearing it here releases the
            // removed component's delegate right away.
            tickSnapshot = subscribers.ToArray();
            snapshotDirty = false;
            if (subscribers.Count == 0) timer.Stop();
        }
    }

    /// <summary>
    /// Stop delivering ticks without dropping the subscriptions, so widget timers can be
    /// resumed exactly as they were. Used while a fullscreen application is active.
    /// </summary>
    public void Pause()
    {
        lock (subscribers)
        {
            paused = true;
            timer.Stop();
        }
    }

    /// <summary>Resume tick delivery after <see cref="Pause"/>, refreshing immediately.</summary>
    public void Resume()
    {
        bool hasSubscribers;
        lock (subscribers)
        {
            paused = false;
            hasSubscribers = subscribers.Count > 0;
            if (hasSubscribers) timer.Start();
        }

        // Catch up right away. A paused timer restarts on its interval, so a widget hidden
        // for minutes behind a fullscreen application would otherwise show stale content
        // until its next tick — the clock would be up to a minute behind, the monitor
        // readings up to a second. This mirrors the session-unlock refresh above.
        if (hasSubscribers) OnTimerTick(this, EventArgs.Empty);
    }

    private void OnTimerTick(object? sender, EventArgs e)
    {
        // Ticks always reach subscribers on the UI thread. The DispatcherTimer already ticks
        // there, but the session-unlock catch-up comes from Microsoft.Win32.SystemEvents, which
        // fires on its own thread: a widget whose handler starts an async refresh there resumes
        // on the thread pool, and anything it builds for its view (an icon StreamGeometry, say)
        // then belongs to that pool thread — the Path bound to it throws from its measure pass,
        // inside the render pass, and the process dies. Raw property writes would throw too, and
        // be swallowed by the per-subscriber guard below.
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => OnTimerTick(sender, e));
            return;
        }

        // The tick runs over a local snapshot taken under the lock, then invokes subscribers with
        // no lock held. A subscriber that (un)subscribes from inside its own callback keeps the
        // previous semantics — the set for *this* tick is fixed, a newly added action waits for
        // the next tick, a removed one still sees the current tick — but the callback no longer
        // runs under the subscription lock, so a slow widget cannot delay another one's
        // subscribe/unsubscribe.
        Action[] snapshot;
        lock (subscribers)
        {
            if (paused) return;
            if (snapshotDirty)
            {
                tickSnapshot = subscribers.ToArray();
                snapshotDirty = false;
            }
            snapshot = tickSnapshot;
        }

        foreach (var action in snapshot)
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                // One misbehaving widget must not stop every other widget's tick.
                System.Diagnostics.Debug.WriteLine($"[UpdateTimer] subscriber failed: {ex.Message}");
            }
        }
    }
    
    private void SystemEventsOnSessionSwitch(object sender, SessionSwitchEventArgs e)
    {
        if (OperatingSystem.IsWindows() && e.Reason != SessionSwitchReason.SessionUnlock) return;
        if (subscribers.Count > 0) OnTimerTick(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        timer.Stop();
        timer.Tick -= OnTimerTick;
        if (OperatingSystem.IsWindows())
        {
            try { SystemEvents.SessionSwitch -= SystemEventsOnSessionSwitch; } catch { }
        }
        GC.SuppressFinalize(this);
    }
}