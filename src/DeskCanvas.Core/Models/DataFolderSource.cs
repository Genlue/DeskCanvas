namespace DeskCanvas.Core.Models;

/// <summary>
/// How the current <see cref="Const.DataFolder"/> was chosen. Surfaced in the settings UI so a
/// user who "lost" their widgets can see at a glance which rule won — the folder location is
/// otherwise invisible and depends on the build, the exe location and the environment.
/// </summary>
public enum DataFolderSource
{
    /// <summary>
    /// Default packaged location: <c>%LocalAppData%\DeskCanvas</c>.
    /// </summary>
    Default,

    /// <summary>
    /// The folder of the exe, because a <c>Widgets</c> folder sits next to it (classic portable
    /// layout, official zip). Release builds only — see <see cref="PortableDev"/>.
    /// </summary>
    Portable,

    /// <summary>
    /// A Debug build running in the classic portable layout: the widget DLLs still hot-load from
    /// the exe-adjacent <c>Widgets</c> folder, but user data is redirected to
    /// <c>%LocalAppData%\DeskCanvas-dev</c> so a Clean/rebuild cannot wipe it.
    /// </summary>
    PortableDev,

    /// <summary>
    /// A user-chosen folder, pinned by a <c>dataFolder.json</c> pointer written next to the exe.
    /// </summary>
    ExePointer,

    /// <summary>
    /// A user-chosen folder, pinned by a <c>dataFolder.json</c> pointer in the default packaged
    /// folder. Used when the exe folder itself is not writable (installed under Program Files).
    /// </summary>
    AnchorPointer,

    /// <summary>
    /// The <c>DESCANVAS_DATA_FOLDER</c> environment variable (highest priority; pinned by the
    /// process that launched the app or by a shortcut).
    /// </summary>
    EnvironmentVariable,

    /// <summary>
    /// A check/repro harness: the process path carries a <c>tests</c> segment, so its data goes
    /// to a disposable folder under <c>%TEMP%</c> and never touches the user's real data.
    /// </summary>
    TestHarness
}
