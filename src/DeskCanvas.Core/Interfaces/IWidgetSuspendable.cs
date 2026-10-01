namespace DeskCanvas.Core.Interfaces;

/// <summary>
/// Opt-in contract for widget views that hold expensive, reconstructible resources
/// (pre-rendered material caches, decoded images, background render queues).
/// <para>
/// <see cref="Suspend"/> is called when the desktop is covered by a fullscreen
/// application: the view must cancel pending background work and release every cached
/// resource it can rebuild, so the process gives the memory back to the fullscreen app.
/// The view is expected to stay usable — <see cref="Resume"/> re-creates the caches.
/// </para>
/// <para>
/// Widgets that do not implement this interface are simply hidden while a fullscreen
/// application is active; their timers are paused by the host regardless.
/// </para>
/// </summary>
public interface IWidgetSuspendable
{
    /// <summary>Release caches and stop background work (idempotent).</summary>
    void Suspend();

    /// <summary>Rebuild whatever <see cref="Suspend"/> released (idempotent).</summary>
    void Resume();
}
