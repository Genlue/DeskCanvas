using DeskCanvas.Contracts;

namespace DeskCanvas.Runtime;

/// <summary>Serializes lifecycle commands for one instance and owns its cancellation scope.</summary>
public sealed class WidgetSession : IWidgetSession
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly CancellationTokenSource lifetime = new();
    private readonly Func<CancellationToken, ValueTask>? initialize;
    private readonly Func<object?, CancellationToken, ValueTask>? applySettings;
    private readonly Func<ActivitySnapshot, CancellationToken, ValueTask>? setActivity;
    private readonly Func<CancellationToken, ValueTask>? dispose;
    private ActivitySnapshot activity = new();
    private Exception? fault;
    private bool disposed;
    private long generation;

    public WidgetSession(WidgetInstanceId instanceId,
        Func<CancellationToken, ValueTask>? initialize = null,
        Func<object?, CancellationToken, ValueTask>? applySettings = null,
        Func<ActivitySnapshot, CancellationToken, ValueTask>? setActivity = null,
        Func<CancellationToken, ValueTask>? dispose = null)
    {
        InstanceId = instanceId.IsEmpty ? throw new ArgumentException("InstanceId must not be empty.", nameof(instanceId)) : instanceId;
        this.initialize = initialize;
        this.applySettings = applySettings;
        this.setActivity = setActivity;
        this.dispose = dispose;
    }

    public WidgetInstanceId InstanceId { get; }
    public WidgetLifecycleState State { get; private set; } = WidgetLifecycleState.Created;
    public long Generation => Interlocked.Read(ref generation);
    public ActivitySnapshot Activity => activity;
    public Exception? Fault => fault;

    public async ValueTask InitializeAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (State == WidgetLifecycleState.Ready) return;
            if (State is WidgetLifecycleState.Disposing or WidgetLifecycleState.Disposed)
                throw new ObjectDisposedException(nameof(WidgetSession));
            State = WidgetLifecycleState.Initializing;
            try
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
                if (initialize is not null) await initialize(linked.Token).ConfigureAwait(false);
                State = WidgetLifecycleState.Ready;
                fault = null;
                Interlocked.Increment(ref generation);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                fault = ex;
                State = WidgetLifecycleState.Faulted;
                throw;
            }
        }
        finally { gate.Release(); }
    }

    public async ValueTask ApplySettingsAsync(object? settings, CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureReady();
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
            if (applySettings is not null) await applySettings(settings, linked.Token).ConfigureAwait(false);
            Interlocked.Increment(ref generation);
        }
        finally { gate.Release(); }
    }

    public async ValueTask SetActivityAsync(ActivitySnapshot next, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(next);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureReady();
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
            activity = next;
            if (setActivity is not null) await setActivity(next, linked.Token).ConfigureAwait(false);
            Interlocked.Increment(ref generation);
        }
        finally { gate.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (disposed || State == WidgetLifecycleState.Disposed) return;
            disposed = true;
            State = WidgetLifecycleState.Disposing;
            Interlocked.Increment(ref generation);
            lifetime.Cancel();
            try { if (dispose is not null) await dispose(CancellationToken.None).ConfigureAwait(false); }
            catch (Exception ex) { fault ??= ex; }
            State = WidgetLifecycleState.Disposed;
        }
        finally
        {
            gate.Release();
            lifetime.Dispose();
            // Keep the gate alive so a repeated DisposeAsync can observe the idempotent
            // disposed flag instead of racing an ObjectDisposedException.
        }
    }

    private void EnsureReady()
    {
        if (State == WidgetLifecycleState.Faulted) throw new InvalidOperationException("Widget session is faulted; initialize again or recreate it.", fault);
        if (State != WidgetLifecycleState.Ready) throw new InvalidOperationException($"Widget session is {State}; operation requires Ready.");
    }
}
