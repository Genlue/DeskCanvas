using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using DeskCanvas.Services;
using DeskCanvas.Views;
using Shortcuts.Models;

namespace Shortcuts.Services;

/// <summary>How a shortcut's hotkey text ended up in the OS registration table.</summary>
public enum HotkeyRegistrationStatus
{
    /// <summary>No hotkey configured.</summary>
    Unset,

    /// <summary>The service is not running (or is disabled for a harness), so nothing is registered.</summary>
    Inactive,

    /// <summary>Registration is temporarily removed while the editor records a new key.</summary>
    Suspended,

    /// <summary>Registered and ready.</summary>
    Registered,

    /// <summary>RegisterHotKey rejected the gesture — another process owns it, or the OS reserves it.</summary>
    Conflict,

    /// <summary>Two shortcuts in the document use the same gesture; the later one was not registered.</summary>
    Duplicate,

    /// <summary>The stored text cannot be parsed into a gesture.</summary>
    Invalid,
}

/// <summary>Per-shortcut registration result. <see cref="Detail"/> carries the conflicting name.</summary>
public sealed record HotkeyBinding(
    string ShortcutId,
    string? Hotkey,
    HotkeyRegistrationStatus Status,
    string? Detail = null);

/// <summary>
/// Win32 hotkey registration seam. Implementations own the message target; the service only
/// maps registration ids to shortcut ids. Tests inject a fake to exercise registration,
/// conflicts, re-registration and recording suspension without a real keyboard.
/// </summary>
public interface IHotkeyRegistrar : IDisposable
{
    /// <summary>Raised with the registration id when the OS fires the hotkey.</summary>
    event Action<int>? Pressed;

    bool Register(int registrationId, HotkeyModifiers modifiers, uint virtualKey);
    void Unregister(int registrationId);
}

/// <summary>
/// Process-wide global-hotkey host for the Shortcuts plugin. One message-only window receives
/// WM_HOTKEY; the document is re-registered wholesale whenever it changes. Registration work
/// must run on the UI thread (the Avalonia Win32 loop dispatches the window messages).
/// </summary>
public static class ShortcutHotkeyService
{
    private static readonly object Gate = new();
    private static readonly Dictionary<string, HotkeyBinding> BindingsById = new(StringComparer.Ordinal);
    private static readonly Dictionary<int, string> OwnersByRegistrationId = new();

    private static IHotkeyRegistrar? registrar;
    private static ShortcutsDocument? pendingDocument;
    private static bool started;
    private static bool suspended;
    private static bool exitHooked;
    private static int captureDepth;
    private static int nextRegistrationId = 1;

    /// <summary>Test seam: replace the Win32 registrar with a fake before <see cref="EnsureStarted"/>.</summary>
    public static Func<IHotkeyRegistrar>? RegistrarFactory { get; set; }

    /// <summary>Raised after any registration state change (always on the caller's thread).</summary>
    public static event Action? StateChanged;

    public static bool IsStarted
    {
        get { lock (Gate) return started; }
    }

    public static bool IsSuspended
    {
        get { lock (Gate) return suspended; }
    }

    /// <summary>True while any <c>HotkeyCaptureBox</c> is recording (sendHotkey must not run).</summary>
    public static bool IsCapturing
    {
        get { lock (Gate) return captureDepth > 0; }
    }

    /// <summary>Current snapshot, in document order.</summary>
    public static IReadOnlyList<HotkeyBinding> Bindings
    {
        get { lock (Gate) return BindingsById.Values.ToList(); }
    }

    public static bool TryGetBinding(string shortcutId, out HotkeyBinding binding)
    {
        lock (Gate)
        {
            if (BindingsById.TryGetValue(shortcutId, out var found))
            {
                binding = found;
                return true;
            }
        }

        binding = null!;
        return false;
    }

    /// <summary>
    /// Start the service once. Safe to call from every view/editor opening; a no-op when the
    /// harness asked to keep the real OS table untouched.
    /// </summary>
    public static void EnsureStarted()
    {
        if (HotkeysDisabled)
            return;

        IHotkeyRegistrar created;
        lock (Gate)
        {
            if (started) return;

            try
            {
                created = RegistrarFactory?.Invoke() ?? new Win32HotkeyRegistrar();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ShortcutHotkey] registrar creation failed: {ex.Message}");
                return;
            }

            registrar = created;
            started = true;
            nextRegistrationId = 1;
            created.Pressed += OnRegistrarPressed;
        }

        try
        {
            ShortcutsStore.ModelChanged += OnStoreChanged;
            HookAppExit();
            Refresh(ShortcutsStore.Get());
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ShortcutHotkey] start failed: {ex.Message}");
        }
    }

    /// <summary>Best-effort window cleanup on normal app exit (the service otherwise lives for the process).</summary>
    private static void HookAppExit()
    {
        if (exitHooked) return;

        try
        {
            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                exitHooked = true;
                desktop.Exit += (_, _) => Shutdown();
            }
        }
        catch
        {
            // Headless harnesses have no desktop lifetime.
        }
    }

    /// <summary>Unregister everything, release the window/registrar and unsubscribe. Idempotent.</summary>
    public static void Shutdown()
    {
        IHotkeyRegistrar? released;
        lock (Gate)
        {
            UnregisterAllLocked();
            released = registrar;
            if (released != null) released.Pressed -= OnRegistrarPressed;
            registrar = null;
            started = false;
            suspended = false;
            captureDepth = 0;
            pendingDocument = null;
            BindingsById.Clear();
            OwnersByRegistrationId.Clear();
        }

        try { ShortcutsStore.ModelChanged -= OnStoreChanged; } catch { }

        try { released?.Dispose(); } catch { }

        RaiseStateChanged();
    }

    /// <summary>Drop all OS registrations while the editor records; they come back on <see cref="Resume"/>.</summary>
    public static void Suspend()
    {
        bool changed;
        lock (Gate)
        {
            changed = !suspended;
            suspended = true;
            UnregisterAllLocked();
            if (pendingDocument != null)
                RebuildLocked(pendingDocument, register: false, inactiveStatus: HotkeyRegistrationStatus.Suspended);
        }

        if (changed) RaiseStateChanged();
    }

    /// <summary>
    /// Begin a UI key-capture session. Reference-counted: the first capture suspends the OS
    /// table, the last one resumes it. This also flags <see cref="IsCapturing"/> so a
    /// <c>sendHotkey</c> step refuses to inject keys into the capture box.
    /// </summary>
    public static void BeginCapture()
    {
        var first = false;
        lock (Gate)
        {
            captureDepth++;
            first = captureDepth == 1;
        }

        if (first)
        {
            // Synthesized keystrokes must not be injected while the box is recording: they
            // would be swallowed by the capture and the shortcut would do nothing.
            KeyboardInput.IsRecording = true;
            Suspend();
        }
    }

    /// <summary>End a capture session; resumes registrations when the last one closes.</summary>
    public static void EndCapture()
    {
        var last = false;
        lock (Gate)
        {
            if (captureDepth == 0) return;
            captureDepth--;
            last = captureDepth == 0;
        }

        if (last)
        {
            KeyboardInput.IsRecording = false;
            Resume();
        }
    }

    /// <summary>Re-register the most recent document (or the store's current one) after recording.</summary>
    public static void Resume()
    {
        ShortcutsDocument? document;
        lock (Gate)
        {
            suspended = false;
            document = pendingDocument;
        }

        if (document != null)
        {
            Refresh(document);
            return;
        }

        try
        {
            Refresh(ShortcutsStore.Get());
        }
        catch
        {
            RaiseStateChanged();
        }
    }

    /// <summary>
    /// Re-register the whole table. Unregisters first, so a gesture can move from one shortcut
    /// to another without a transient "already in use" conflict.
    /// </summary>
    public static void Refresh(ShortcutsDocument document)
    {
        if (document == null) return;

        lock (Gate)
        {
            pendingDocument = document;
            var register = started && registrar != null && !suspended;
            var inactive = suspended
                ? HotkeyRegistrationStatus.Suspended
                : HotkeyRegistrationStatus.Inactive;
            RebuildLocked(document, register, inactive);
        }

        RaiseStateChanged();
    }

    /// <summary>
    /// Run a shortcut by id from the hotkey path. Confirmation is decided once, inside the single
    /// run entry point, so the hotkey behaves exactly like the card and the panel; a shortcut that
    /// needs a dialog and has no owner window fails instead of running silently.
    /// </summary>
    public static async Task<bool> ExecuteShortcutAsync(string shortcutId)
    {
        ShortcutDefinition? shortcut;
        try
        {
            shortcut = ShortcutsStore.Get().Shortcuts.FirstOrDefault(s => s.Id == shortcutId);
        }
        catch
        {
            return false;
        }

        if (shortcut == null) return false;

        var owner = ResolveOwnerWindow();

        try
        {
            var result = await ShortcutRunner.RunAsync(shortcut, ShortcutsRuntime.CreateRunServices((Control?)owner));
            return result.Success;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Test helper: stop, forget the injected factory and clear all state.</summary>
    public static void ResetForTesting()
    {
        Shutdown();
        RegistrarFactory = null;
    }

    // ---- internals ---------------------------------------------------------------

    private static bool HotkeysDisabled =>
        Environment.GetEnvironmentVariable("DESKCANVAS_SHORTCUTS_DISABLE_HOTKEYS") == "1";

    private static void OnStoreChanged(ShortcutsDocument document, object? sender)
    {
        lock (Gate)
        {
            pendingDocument = document;
            if (suspended) return;
        }

        Refresh(document);
    }

    private static void OnRegistrarPressed(int registrationId)
    {
        string? shortcutId;
        lock (Gate) OwnersByRegistrationId.TryGetValue(registrationId, out shortcutId);
        if (shortcutId == null) return;

        try
        {
            Dispatcher.UIThread.Post(() => { _ = ExecuteShortcutAsync(shortcutId); });
        }
        catch
        {
            _ = ExecuteShortcutAsync(shortcutId);
        }
    }

    /// <summary>Rebuild the table; caller holds <see cref="Gate"/>.</summary>
    private static void RebuildLocked(ShortcutsDocument document, bool register,
        HotkeyRegistrationStatus inactiveStatus)
    {
        if (register) UnregisterAllLocked();

        BindingsById.Clear();
        if (register) OwnersByRegistrationId.Clear();

        var claimed = new Dictionary<string, ShortcutDefinition>(StringComparer.OrdinalIgnoreCase);

        foreach (var shortcut in document.Shortcuts)
        {
            var text = shortcut.Hotkey?.Trim();
            if (string.IsNullOrEmpty(text))
            {
                BindingsById[shortcut.Id] = new HotkeyBinding(shortcut.Id, null,
                    HotkeyRegistrationStatus.Unset);
                continue;
            }

            if (!HotkeyGesture.TryParse(text, requireModifier: true, out var gesture, out _))
            {
                BindingsById[shortcut.Id] = new HotkeyBinding(shortcut.Id, text,
                    HotkeyRegistrationStatus.Invalid);
                continue;
            }

            if (!register)
            {
                BindingsById[shortcut.Id] = new HotkeyBinding(shortcut.Id, gesture.Text,
                    inactiveStatus);
                continue;
            }

            if (claimed.TryGetValue(gesture.Text, out var first))
            {
                BindingsById[shortcut.Id] = new HotkeyBinding(shortcut.Id, gesture.Text,
                    HotkeyRegistrationStatus.Duplicate, first.Name);
                continue;
            }

            var registrationId = nextRegistrationId++;
            bool ok;
            try
            {
                ok = registrar!.Register(registrationId, gesture.Modifiers, gesture.VirtualKey);
            }
            catch
            {
                ok = false;
            }

            if (ok)
            {
                claimed[gesture.Text] = shortcut;
                OwnersByRegistrationId[registrationId] = shortcut.Id;
                BindingsById[shortcut.Id] = new HotkeyBinding(shortcut.Id, gesture.Text,
                    HotkeyRegistrationStatus.Registered);
            }
            else
            {
                BindingsById[shortcut.Id] = new HotkeyBinding(shortcut.Id, gesture.Text,
                    HotkeyRegistrationStatus.Conflict);
            }
        }
    }

    private static void UnregisterAllLocked()
    {
        if (registrar == null) return;

        foreach (var registrationId in OwnersByRegistrationId.Keys.ToList())
        {
            try { registrar.Unregister(registrationId); } catch { }
        }

        OwnersByRegistrationId.Clear();
    }

    private static Window? ResolveOwnerWindow()
    {
        if (ShortcutsRuntime.LastTopLevel is Window tracked && tracked.IsVisible)
            return tracked;

        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            return desktop.Windows.FirstOrDefault(w => w.IsVisible) ?? desktop.MainWindow;
        }

        return null;
    }

    private static void RaiseStateChanged()
    {
        try { StateChanged?.Invoke(); } catch { }
    }
}

/// <summary>
/// Adapter over the host's <see cref="GlobalHotkeyRegistrar"/>.
/// <para>
/// The Win32 message-only window used to live here. It moved to the host when 聚焦搜索 became a
/// second consumer: one process must own exactly one <c>RegisterHotKey</c> host, not one per
/// feature (开发规范 §1.2). This class now only maps the plugin's <see cref="HotkeyModifiers"/>
/// onto the host's flags — the numeric values are identical by design, so the cast is exact.
/// </para>
/// </summary>
internal sealed class Win32HotkeyRegistrar : IHotkeyRegistrar
{
    private readonly GlobalHotkeyRegistrar inner = new();
    private bool disposed;

    public Win32HotkeyRegistrar()
    {
        inner.Pressed += id => Pressed?.Invoke(id);
    }

    public event Action<int>? Pressed;

    public bool Register(int registrationId, HotkeyModifiers modifiers, uint virtualKey)
        => inner.Register(registrationId, (GlobalHotkeyModifiers)(uint)modifiers, virtualKey);

    public void Unregister(int registrationId) => inner.Unregister(registrationId);

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        inner.Dispose();
    }
}
