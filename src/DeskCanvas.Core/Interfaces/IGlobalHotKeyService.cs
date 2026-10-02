namespace DeskCanvas.Core.Interfaces;

/// <summary>
/// A system-wide hotkey backed by Win32 <c>RegisterHotKey</c>.
/// <para>
/// Registration is fail-safe: a rejected or already-taken combination never leaves the app
/// without a hotkey — the previous one stays registered — and a failure to register at startup
/// must never stop the application from running.
/// </para>
/// </summary>
public interface IGlobalHotKeyService
{
    /// <summary>The normalized hotkey currently in effect (e.g. <c>"Ctrl+Alt+Space"</c>).</summary>
    string HotKey { get; }

    /// <summary>Whether a hotkey is currently registered with the OS.</summary>
    bool IsRegistered { get; }

    /// <summary>Raised on the thread that owns the message window when the hotkey fires.</summary>
    event EventHandler? HotKeyPressed;

    /// <summary>
    /// Create the message window and register the current hotkey. Call once, on the UI thread.
    /// </summary>
    void Start();

    /// <summary>
    /// Register <paramref name="hotKey"/>; the previous combination stays registered when the new
    /// one cannot be taken.
    /// </summary>
    /// <param name="hotKey">User input such as <c>"Ctrl+Shift+A"</c>.</param>
    /// <param name="error">Localized reason when registration fails.</param>
    /// <returns><c>true</c> when the new combination is now in effect.</returns>
    bool TrySetHotKey(string? hotKey, out string? error);

    /// <summary>Unregister and destroy the message window.</summary>
    void Stop();
}
