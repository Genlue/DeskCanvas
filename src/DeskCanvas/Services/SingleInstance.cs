using System;
using System.Threading;

namespace DeskCanvas.Services;

/// <summary>
/// Process-level single-instance guard.
/// <para>
/// The first launch owns the named kernel objects of a scope. A later launch detects the owner,
/// asks it to surface its UI (see <see cref="Signal"/>) and then exits, so two copies of the app
/// can never fight over the same data folder, widget windows or auto-start entry.
/// </para>
/// <para>
/// The default scope is the current Windows session, so a second user signed in through fast user
/// switching still gets their own instance — each user has their own data folder.
/// </para>
/// </summary>
public sealed class SingleInstance : IDisposable
{
    /// <summary>One instance per signed-in user, not per machine.</summary>
    public const string DefaultScope = @"Local\DeskCanvas.SingleInstance";

    private const string MutexSuffix = ".Mutex";
    private const string SignalSuffix = ".Show";

    /// <summary>
    /// The guard owned by this process, or <c>null</c> when this is a secondary launch.
    /// <see cref="App"/> uses it to listen for hand-over requests.
    /// </summary>
    public static SingleInstance? Current { get; set; }

    private readonly string scope;

    // Deliberately NOT owned: presence of the named object is what marks the scope as taken, so
    // there is no thread affinity and releasing is a plain Dispose from any thread.
    private Mutex? mutex;
    private EventWaitHandle? signal;
    private CancellationTokenSource? cancellation;
    private Thread? listener;

    private SingleInstance(string scope) => this.scope = scope;

    /// <summary>
    /// Become the primary instance of <paramref name="scope"/>.
    /// </summary>
    /// <returns>
    /// The guard when this process is the primary instance, or <c>null</c> when another live
    /// process already owns the scope. The guard fails open: if it cannot be set up at all the
    /// app still starts rather than refusing to run.
    /// </returns>
    public static SingleInstance? Acquire(string scope = DefaultScope)
    {
        Mutex mutex;
        try
        {
            mutex = new Mutex(false, scope + MutexSuffix, out var createdNew);
            if (!createdNew)
            {
                // A live process already holds the scope (its handle keeps the object alive, so a
                // crashed instance leaves nothing behind).
                mutex.Dispose();
                return null;
            }
        }
        catch
        {
            return new SingleInstance(scope);
        }

        var instance = new SingleInstance(scope) { mutex = mutex };
        try
        {
            instance.signal = new EventWaitHandle(false, EventResetMode.AutoReset, scope + SignalSuffix);
        }
        catch
        {
            // Without the signal a later launch still refuses to start; it just cannot ask this
            // instance to come to the front.
        }

        return instance;
    }

    /// <summary>
    /// Ask the primary instance of <paramref name="scope"/> to surface its UI. Best effort —
    /// called by a secondary launch right before it exits.
    /// </summary>
    public static void Signal(string scope = DefaultScope)
    {
        // This process was started by the shell, so it is still allowed to hand the foreground
        // over; the primary instance is not, and would otherwise only flash its taskbar button.
        InteropService.AllowOtherProcessToTakeForeground();

        try
        {
            using var signal = new EventWaitHandle(false, EventResetMode.AutoReset, scope + SignalSuffix);
            signal.Set();
        }
        catch
        {
            // The primary instance is already gone or unreachable: nothing to hand over to.
        }
    }

    /// <summary>
    /// Start listening for hand-over requests. <paramref name="onSignal"/> runs on a background
    /// thread every time a secondary launch asks this instance to show itself.
    /// </summary>
    public void Listen(Action onSignal)
    {
        if (signal == null || listener != null) return;

        cancellation = new CancellationTokenSource();
        var handles = new WaitHandle[] { signal, cancellation.Token.WaitHandle };

        listener = new Thread(() =>
        {
            while (true)
            {
                int signaled;
                try
                {
                    signaled = WaitHandle.WaitAny(handles);
                }
                catch (ObjectDisposedException)
                {
                    return;
                }

                if (signaled != 0) return;   // cancelled
                try
                {
                    onSignal();
                }
                catch
                {
                    // A failed hand-over must not take the listener down.
                }
            }
        })
        {
            IsBackground = true,
            Name = "DeskCanvas single-instance listener"
        };

        listener.Start();
    }

    /// <summary>
    /// Give the scope up so a successor process can take over. Used by the restart hand-over,
    /// which must release <b>before</b> spawning the successor — otherwise the successor reads the
    /// still-held scope as "another instance is running" and exits immediately.
    /// </summary>
    public void Release()
    {
        cancellation?.Cancel();

        var thread = listener;
        listener = null;
        if (thread != null && thread.IsAlive && thread != Thread.CurrentThread)
            thread.Join(TimeSpan.FromSeconds(1));

        cancellation?.Dispose();
        cancellation = null;

        signal?.Dispose();
        signal = null;

        mutex?.Dispose();
        mutex = null;
    }

    /// <summary>Re-take the scope after a hand-over that never happened (successor failed to start).</summary>
    public void TryReacquire()
    {
        if (mutex != null) return;

        try
        {
            var candidate = new Mutex(false, scope + MutexSuffix, out var createdNew);
            if (createdNew) mutex = candidate;
            else candidate.Dispose();
        }
        catch
        {
            // Best effort: the app keeps running, only the guard would be missing.
        }
    }

    public void Dispose() => Release();
}
