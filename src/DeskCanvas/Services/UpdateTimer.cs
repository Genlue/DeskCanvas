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
    /// Snapshot the ticks are delivered from. A subscriber may (un)subscribe from inside its own
    /// callback — the lock is re-entrant on that thread — so the tick cannot iterate the live
    /// list; the snapshot is rebuilt only when the subscription set actually changes instead of
    /// allocating a fresh list on every tick (this runs 10×/s just for the 100 ms timer).
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
            snapshotDirty = true;
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
        lock (subscribers)
        {
            if (paused) return;
            if (snapshotDirty)
            {
                tickSnapshot = subscribers.ToArray();
                snapshotDirty = false;
            }

            foreach (var action in tickSnapshot)
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