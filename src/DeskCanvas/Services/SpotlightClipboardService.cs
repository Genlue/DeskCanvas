using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using DeskCanvas.Core;
using DeskCanvas.Core.Interfaces;
using DeskCanvas.Core.Models.Settings;

namespace DeskCanvas.Services;

/// <summary>
/// One captured clipboard text, newest-first inside <see cref="SpotlightClipboardService.Entries"/>.
/// </summary>
/// <param name="Id">Stable identifier (<c>Guid.ToString("N")</c>), survives persistence.</param>
/// <param name="Text">The captured clipboard text (plain text / link only — images are P2).</param>
/// <param name="IsLink">True when the text looks like a URL or a bare domain.</param>
/// <param name="CapturedUtc">When the text was captured from the system clipboard (or refreshed
/// by a <see cref="SpotlightClipboardService.CopyBack"/>).</param>
/// <param name="SourceProcess">Process name of the clipboard owner at capture time, when it could
/// be resolved; <c>null</c> otherwise (unreadable owners are recorded, not dropped).</param>
public sealed record ClipboardEntry(string Id, string Text, bool IsLink, DateTime CapturedUtc, string? SourceProcess);

/// <summary>
/// Local clipboard history for the Spotlight launcher: records copied text into memory and a
/// JSON file under <see cref="Const.DataFolder"/>, serves it newest-first, and can copy an entry
/// back to the system clipboard.
/// <para>
/// <b>Why Win32 and not Avalonia's Clipboard API:</b> Avalonia clipboard access requires a
/// <c>TopLevel</c> window, and this service is a background recorder that must exist (and keep
/// listening) even when no launcher window is open. A message-only window on a dedicated STA
/// thread with <c>AddClipboardFormatListener</c> is the standard machine-wide listener pattern.
/// </para>
/// <para>
/// <b>Privacy contract:</b> this is an opt-in (<see cref="LauncherSettings.ClipboardHistory"/>,
/// default off), machine-local feature. Clipboard text is never written to any log —
/// <see cref="GlassDiagnostics"/> calls in this file carry entry counts and state transitions
/// only. The history file is the feature itself (local history), holds plaintext by design, and
/// lives only under the user's data folder.
/// </para>
/// </summary>
public sealed class SpotlightClipboardService : IDisposable
{
    private const string FileName = "launcher-clipboard.json";
    private const int CopyBackTimeoutMs = 2000;
    private const int ClipboardOpenRetryCount = 10;
    private const int ClipboardOpenRetryDelayMs = 10;
    private const int StopJoinTimeoutMs = 3000;

    private const uint WmClipboardUpdate = 0x031D;
    private const uint WmQuit = 0x0012;
    // WM_APP+1 posted straight to the listener thread's queue (hwnd == 0) to wake the message
    // loop for queued work — there is no second window to PostMessage to, and the work (CopyBack
    // writes) must run on the listener's STA thread.
    private const uint WmServiceWork = 0x8000 + 1;
    private const int ErrorClassAlreadyExists = 1410;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        // The file format is fixed by the design doc ("version"/"entries"/camelCase keys);
        // explicit [JsonPropertyName] on the DTOs pins it regardless of this policy.
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    // The unmanaged window class points at a delegate; a static root keeps it alive for the
    // process lifetime so the class never outlives its callback. At most one listener runs per
    // process (Start is idempotent), so a single static instance slot is the whole dispatch table.
    private static readonly NativeMethods.WndProcCallback WindowProcedure = WndProcBridge;
    private static readonly string WindowClassName = "DeskCanvas_SpotlightClipboard";
    private static SpotlightClipboardService? activeInstance;

    private readonly IAppSettingsProvider settings;
    private readonly object gate = new();
    private readonly List<ClipboardEntry> entries = new();
    private readonly ConcurrentQueue<Action> work = new();

    private Thread? listenerThread;
    private volatile IntPtr hwnd;
    private volatile uint threadId;
    private volatile bool running;
    private bool stopRequested;
    private Timer? retentionTimer;

    /// <summary>
    /// Creates the service. <paramref name="settings"/> is re-read on every capture, so setting
    /// changes (exclusion list, retention) apply without a restart.
    /// </summary>
    public SpotlightClipboardService(IAppSettingsProvider settings)
    {
        // A null provider is a wiring bug, not an environment failure — fail fast here (the only
        // throwing member) so every later member can honour the "degrade, never throw" contract.
        this.settings = settings ?? throw new ArgumentNullException(nameof(settings));
    }

    /// <summary>True while the listener thread is alive and the message window is listening.</summary>
    public bool IsRunning => running;

    /// <summary>The history, newest first, capped at <see cref="LauncherSettings.MaxClipboardEntries"/>.
    /// Returns a snapshot; safe from any thread.</summary>
    public IReadOnlyList<ClipboardEntry> Entries
    {
        get
        {
            lock (gate) return entries.ToArray();
        }
    }

    /// <summary>Raised after the history changed (entry captured, copy-back, trim, clear). May
    /// fire on any thread — consumers marshal to the UI thread themselves. A throwing handler is
    /// isolated so it can never kill the listener thread or block other handlers.</summary>
    public event Action? Changed;

    /// <summary>
    /// Starts listening. Idempotent. A no-op while <see cref="LauncherSettings.ClipboardHistory"/>
    /// is off — the machine-wide listener is a privacy decision and must not run uninvited.
    /// </summary>
    public void Start()
    {
        try
        {
            lock (gate)
            {
                if (listenerThread is { IsAlive: true }) return; // idempotent: already starting / running
                listenerThread = null; // a crashed listener must not block a restart
                running = false;
                stopRequested = false;
                if (!settings.Get().EffectiveLauncher.ClipboardHistory) return; // off → no-op
            }

            // Load before the thread starts so the first Changed consumer sees a full history.
            LoadFromDisk();

            var thread = new Thread(ListenerLoop)
            {
                IsBackground = true, // never block process exit; Stop() normally joins it first
                Name = "SpotlightClipboardListener",
            };
            thread.SetApartmentState(ApartmentState.STA); // clipboard / window APIs want STA
            lock (gate) listenerThread = thread;
            thread.Start();
        }
        catch (Exception ex)
        {
            GlassDiagnostics.Event($"clipboard history: start failed ({ex.GetType().Name})");
            lock (gate)
            {
                listenerThread = null;
                running = false;
            }
        }
    }

    /// <summary>
    /// Stops the listener thread (posts <c>WM_QUIT</c> and joins). Idempotent. Memory entries are
    /// kept — dropping them here is <see cref="RefreshSettings"/>' decision when the feature was
    /// switched off.
    /// </summary>
    public void Stop()
    {
        Thread? thread;
        lock (gate)
        {
            thread = listenerThread;
            if (thread == null) return;
            if (!stopRequested)
            {
                stopRequested = true;
                if (threadId != 0)
                    NativeMethods.PostThreadMessage(threadId, WmQuit, IntPtr.Zero, IntPtr.Zero);
            }
        }

        thread.Join(StopJoinTimeoutMs);

        lock (gate)
        {
            if (!ReferenceEquals(listenerThread, thread)) return; // a newer listener took over
            listenerThread = null;
            running = false;
        }
    }

    /// <summary>Wipes the in-memory history <b>and</b> the persisted file — a user-initiated
    /// "clear history" must survive a restart.</summary>
    public void Clear()
    {
        bool had;
        lock (gate)
        {
            had = entries.Count > 0;
            entries.Clear();
            SaveUnderGate();
        }
        GlassDiagnostics.Event("clipboard history cleared");
        if (had) RaiseChanged();
    }

    /// <summary>
    /// Copies the entry with <paramref name="id"/> back to the system clipboard. On success the
    /// returned entry is the same one with <see cref="ClipboardEntry.CapturedUtc"/> refreshed to
    /// this copy and moved to the front; <c>null</c> when the id is unknown, the listener is not
    /// running, or the clipboard could not be written. Never throws.
    /// </summary>
    public ClipboardEntry? CopyBack(string id)
    {
        if (string.IsNullOrEmpty(id) || !running || hwnd == IntPtr.Zero) return null;

        // Called from the listener thread itself would deadlock on the wait below — run inline.
        if (threadId != 0 && NativeMethods.GetCurrentThreadId() == threadId)
        {
            try { return CopyBackCore(id); } catch { return null; }
        }

        var completion = new TaskCompletionSource<ClipboardEntry?>(TaskCreationOptions.RunContinuationsAsynchronously);
        work.Enqueue(() =>
        {
            ClipboardEntry? result = null;
            try { result = CopyBackCore(id); }
            catch (Exception ex)
            {
                GlassDiagnostics.Event($"clipboard history: copy-back failed ({ex.GetType().Name})");
            }
            completion.TrySetResult(result);
        });

        // The wake-up races nothing fatal: if the listener died between the running check and
        // this post, the wait below simply times out and we degrade to "copy-back failed".
        if (!NativeMethods.PostThreadMessage(threadId, WmServiceWork, IntPtr.Zero, IntPtr.Zero))
            return null;

        try
        {
            return completion.Task.Wait(CopyBackTimeoutMs) ? completion.Task.Result : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Re-reads the launcher settings after a change: starts when the feature was turned on,
    /// stops-and-clears when turned off, and re-applies retention. The exclusion list is read on
    /// every capture already, so there is nothing cached to refresh.
    /// </summary>
    public void RefreshSettings()
    {
        try
        {
            var launcher = settings.Get().EffectiveLauncher;
            if (!launcher.ClipboardHistory)
            {
                // Switched off while listening: stop and drop memory, but KEEP the disk file.
                // Trade-off: wiping the file would turn a toggle into permanent data loss (one
                // accidental settings change destroys the whole history), while keeping plaintext
                // on disk after "off" is bounded by the retention trim that runs on next Start.
                if (running || listenerThread != null) Stop();
                lock (gate) entries.Clear();
                return;
            }

            if (!running && listenerThread == null)
            {
                Start();
                return;
            }

            TrimByRetention();
        }
        catch (Exception ex)
        {
            GlassDiagnostics.Event($"clipboard history: settings refresh failed ({ex.GetType().Name})");
        }
    }

    /// <summary>Stops the listener. The history file is intentionally left as-is.</summary>
    public void Dispose()
    {
        try { Stop(); }
        catch
        {
            // Dispose must never throw even if the join path failed unexpectedly.
        }
        lock (gate)
        {
            retentionTimer?.Dispose();
            retentionTimer = null;
            work.Clear();
        }
    }

    // ---- listener thread -----------------------------------------------------------

    private void ListenerLoop()
    {
        try
        {
            activeInstance = this;
            if (!NativeMethods.EnsureWindowClassRegistered(WindowClassName, WindowProcedure) ||
                !NativeMethods.TryCreateMessageWindow(WindowClassName, Marshal.GetHINSTANCE(typeof(SpotlightClipboardService).Module), out var window))
            {
                GlassDiagnostics.Event("clipboard history: message window creation failed");
                return;
            }
            hwnd = window;

            NativeMethods.AddClipboardFormatListener(hwnd);
            threadId = NativeMethods.GetCurrentThreadId();
            running = true;
            GlassDiagnostics.Event($"clipboard history listener started, entries={CountEntries()}");
            TrimByRetention(); // covers entries that aged out while the feature was off
            StartRetentionTimer();

            try
            {
                while (NativeMethods.GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
                {
                    if (msg.message == WmServiceWork && msg.hwnd == IntPtr.Zero)
                    {
                        DrainWork();
                        continue;
                    }
                    NativeMethods.TranslateMessage(ref msg);
                    NativeMethods.DispatchMessage(ref msg);
                }
            }
            finally
            {
                StopRetentionTimer();
                NativeMethods.RemoveClipboardFormatListener(hwnd);
                NativeMethods.DestroyWindow(hwnd);
            }
        }
        catch (Exception ex)
        {
            GlassDiagnostics.Event($"clipboard history: listener crashed ({ex.GetType().Name})");
        }
        finally
        {
            running = false;
            hwnd = IntPtr.Zero;
            threadId = 0;
            if (activeInstance == this) activeInstance = null;
            GlassDiagnostics.Event("clipboard history listener stopped");
        }
    }

    private static IntPtr WndProcBridge(IntPtr hWnd, uint message, IntPtr wParam, IntPtr lParam)
    {
        var instance = activeInstance;
        if (instance == null) return NativeMethods.DefWindowProc(hWnd, message, wParam, lParam);
        return instance.HandleWindowMessage(hWnd, message, wParam, lParam);
    }

    private IntPtr HandleWindowMessage(IntPtr hWnd, uint message, IntPtr wParam, IntPtr lParam)
    {
        if (message == WmClipboardUpdate)
        {
            TryCaptureClipboardText();
            return IntPtr.Zero;
        }
        return NativeMethods.DefWindowProc(hWnd, message, wParam, lParam);
    }

    private void DrainWork()
    {
        while (work.TryDequeue(out var action))
        {
            try { action(); }
            catch
            {
                // The queued action reports its own failure; never let it kill the loop.
            }
        }
    }

    // ---- capture -------------------------------------------------------------------

    private void TryCaptureClipboardText()
    {
        try
        {
            var owner = NativeClipboard.GetClipboardOwner();
            // Self-write detection: CopyBack opens/empties/sets with the listener window, so the
            // owner IS us. Boundary trade-off: if the user re-copies the identical text from
            // another app, the owner is their window — that hits the consecutive-duplicate rule
            // below instead, which keeps the older timestamp. Preferring the owner check over
            // text comparison is what prevents a self-echo write loop.
            if (owner == hwnd) return;

            if (!settings.Get().EffectiveLauncher.ClipboardHistory) return;

            var source = ResolveOwnerProcess(owner);
            if (settings.Get().EffectiveLauncher.IsClipboardExcluded(source)) return;

            var text = NativeClipboard.ReadUnicodeText(hwnd);
            if (string.IsNullOrEmpty(text)) return;

            Record(text, source);
        }
        catch (Exception ex)
        {
            GlassDiagnostics.Event($"clipboard history: capture failed ({ex.GetType().Name})");
        }
    }

    /// <summary>Owner process name, or null when it cannot be resolved. Unreadable must NOT
    /// count as excluded (spec): a process racing its own exit would otherwise silently drop
    /// perfectly recordable copies.</summary>
    private static string? ResolveOwnerProcess(IntPtr owner)
    {
        if (owner == IntPtr.Zero) return null;
        try
        {
            NativeMethods.GetWindowThreadProcessId(owner, out var processId);
            if (processId == 0) return null;
            return Process.GetProcessById((int)processId).ProcessName;
        }
        catch
        {
            return null;
        }
    }

    private void Record(string text, string? sourceProcess)
    {
        bool recorded = false;
        lock (gate)
        {
            // Consecutive-duplicate suppression: re-copying what is already newest neither
            // re-records nor refreshes the timestamp — a copy burst in the source app must not
            // churn order and must not rewrite the file.
            if (entries.Count > 0 && string.Equals(entries[0].Text, text, StringComparison.Ordinal))
                return;

            var entry = new ClipboardEntry(
                Guid.NewGuid().ToString("N"),
                text,
                LooksLikeLink(text),
                DateTime.UtcNow,
                sourceProcess);
            entries.Insert(0, entry); // newest first
            if (entries.Count > LauncherSettings.MaxClipboardEntries)
                entries.RemoveAt(entries.Count - 1); // evict oldest
            SaveUnderGate();
            recorded = true;
        }

        if (recorded)
        {
            // Counts only — clipboard text must never reach a log.
            GlassDiagnostics.Event($"clipboard history: entry captured, total={CountEntries()}");
            RaiseChanged();
        }
    }

    private ClipboardEntry? CopyBackCore(string id)
    {
        ClipboardEntry source;
        lock (gate)
        {
            var index = entries.FindIndex(e => string.Equals(e.Id, id, StringComparison.Ordinal));
            if (index < 0) return null;
            source = entries[index];
        }

        if (!NativeClipboard.WriteUnicodeText(hwnd, source.Text)) return null;

        ClipboardEntry refreshed;
        lock (gate)
        {
            // The id is kept so a consumer that captured the entry can still match it; the
            // timestamp refresh plus front-position is what makes "recently copied back" behave
            // like a real copy in the history's newest-first order.
            refreshed = source with { CapturedUtc = DateTime.UtcNow };
            entries.Remove(source);
            entries.Insert(0, refreshed);
            SaveUnderGate();
        }
        GlassDiagnostics.Event("clipboard history: entry copied back");
        RaiseChanged();
        return refreshed;
    }

    // ---- retention -----------------------------------------------------------------

    private void StartRetentionTimer()
    {
        var timer = new Timer(
            _ => TrimByRetention(),
            null,
            TimeSpan.FromMinutes(15),
            TimeSpan.FromMinutes(15));
        lock (gate)
        {
            retentionTimer?.Dispose();
            retentionTimer = timer;
        }
    }

    private void StopRetentionTimer()
    {
        lock (gate)
        {
            retentionTimer?.Dispose();
            retentionTimer = null;
        }
    }

    private void TrimByRetention()
    {
        try
        {
            var days = settings.Get().EffectiveLauncher.ClipboardRetentionDays;
            var cutoff = DateTime.UtcNow.AddDays(-days);
            bool removed;
            lock (gate)
            {
                removed = entries.RemoveAll(e => e.CapturedUtc < cutoff) > 0;
                if (removed) SaveUnderGate();
            }
            if (removed)
            {
                GlassDiagnostics.Event($"clipboard history: expired entries trimmed, total={CountEntries()}");
                RaiseChanged();
            }
        }
        catch (Exception ex)
        {
            GlassDiagnostics.Event($"clipboard history: retention trim failed ({ex.GetType().Name})");
        }
    }

    private int CountEntries()
    {
        lock (gate) return entries.Count;
    }

    // ---- persistence ---------------------------------------------------------------

    private static string FilePath => Path.Combine(Const.DataFolder, FileName);

    /// <summary>Loads the history file, dropping blank entries. Runs on Start before the listener
    /// goes live; every failure degrades to whatever is already in memory (or empty).</summary>
    private void LoadFromDisk()
    {
        try
        {
            var file = FilePath;
            if (!File.Exists(file)) return;

            var payload = JsonSerializer.Deserialize<ClipboardFile>(File.ReadAllText(file), JsonOptions);
            if (payload?.Entries == null) return;

            var retentionCutoff = DateTime.UtcNow.AddDays(-settings.Get().EffectiveLauncher.ClipboardRetentionDays);
            lock (gate)
            {
                entries.Clear();
                foreach (var dto in payload.Entries)
                {
                    if (string.IsNullOrWhiteSpace(dto.Id) || string.IsNullOrWhiteSpace(dto.Text)) continue;
                    entries.Add(new ClipboardEntry(dto.Id, dto.Text, dto.IsLink, dto.CapturedUtc, dto.Source));
                }
                // The file is newest-first (the order we write); trim and cap defensively in
                // case it was hand-edited or written by an older build with different limits.
                entries.RemoveAll(e => e.CapturedUtc < retentionCutoff);
                if (entries.Count > LauncherSettings.MaxClipboardEntries)
                    entries.RemoveRange(LauncherSettings.MaxClipboardEntries,
                        entries.Count - LauncherSettings.MaxClipboardEntries);
            }
        }
        catch (Exception ex)
        {
            // A corrupt/locked history file must not disable the feature — start empty instead.
            GlassDiagnostics.Event($"clipboard history: load failed ({ex.GetType().Name})");
        }
    }

    /// <summary>Writes the whole history atomically (temp file + <c>File.Move</c>) so a crash can
    /// never leave a half-written history. Must be called while holding the <c>gate</c> lock —
    /// writes are serialized there and every failure is silent: the
    /// in-memory history stays usable and the next change retries a full rewrite. Intentionally
    /// logs nothing on failure paths with content anywhere near the message.</summary>
    private void SaveUnderGate()
    {
        var tempFile = string.Empty;
        try
        {
            var file = FilePath;
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            var payload = new ClipboardFile
            {
                Version = 1,
                Entries = entries.Select(e => new ClipboardEntryDto
                {
                    Id = e.Id,
                    Text = e.Text,
                    IsLink = e.IsLink,
                    CapturedUtc = e.CapturedUtc,
                    Source = e.SourceProcess,
                }).ToList(),
            };
            tempFile = $"{file}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
            File.WriteAllText(tempFile, JsonSerializer.Serialize(payload, JsonOptions));
            File.Move(tempFile, file, overwrite: true);
            tempFile = string.Empty;
        }
        catch
        {
            // Silent by design (spec): persistence is best-effort, history lives on in memory.
            try { if (tempFile.Length > 0) File.Delete(tempFile); } catch { }
        }
    }

    private sealed class ClipboardFile
    {
        public int Version { get; set; }

        public List<ClipboardEntryDto> Entries { get; set; } = new();
    }

    private sealed class ClipboardEntryDto
    {
        [System.Text.Json.Serialization.JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        [System.Text.Json.Serialization.JsonPropertyName("text")]
        public string Text { get; set; } = string.Empty;

        [System.Text.Json.Serialization.JsonPropertyName("isLink")]
        public bool IsLink { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("capturedUtc")]
        public DateTime CapturedUtc { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("source")]
        public string? Source { get; set; }
    }

    // ---- misc ----------------------------------------------------------------------

    private void RaiseChanged()
    {
        var handlers = Changed;
        if (handlers == null) return;
        foreach (var handler in handlers.GetInvocationList().Cast<Action>())
        {
            try { handler(); }
            catch
            {
                // One bad consumer must not kill the listener thread or starve the others;
                // consumers own their dispatch to the UI thread (contract).
            }
        }
    }

    /// <summary>Link detection: an explicit scheme / <c>www.</c> prefix, or a bare domain such as
    /// <c>example.com</c> / <c>a.b.co/path</c> (letters-only TLD, no spaces, optional port).</summary>
    private static bool LooksLikeLink(string text)
    {
        var candidate = text.Trim();
        if (candidate.Length == 0) return false;
        if (candidate.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            candidate.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
            candidate.StartsWith("www.", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Bare domain: whitespace disqualifies; strip path/query/fragment and :port first.
        if (candidate.IndexOfAny([' ', '\t', '\n', '\r']) >= 0) return false;
        var host = candidate;
        var separator = host.IndexOfAny(['/', '?', '#']);
        if (separator >= 0) host = host[..separator];
        var colon = host.IndexOf(':');
        if (colon >= 0) host = host[..colon];

        var labels = host.Split('.');
        if (labels.Length < 2) return false;
        for (var i = 0; i < labels.Length; i++)
        {
            var label = labels[i];
            if (label.Length == 0 || label.Length > 63) return false;
            if (i == labels.Length - 1)
            {
                // TLD: at least two ASCII letters — this is what separates "example.com" from
                // "1.2" or a file name like "report.v2".
                if (label.Length < 2) return false;
                foreach (var ch in label)
                    if (!char.IsAsciiLetter(ch)) return false;
                continue;
            }
            foreach (var ch in label)
                if (!char.IsAsciiLetterOrDigit(ch) && ch != '-') return false;
        }
        return true;
    }

    // ---- Win32 ---------------------------------------------------------------------

    /// <summary>
    /// Window / thread plumbing for the message-only listener window. Private to this file on
    /// purpose: the WindowRoleChecks gate counts <c>[DllImport]</c>s, and every new import of
    /// this feature must live in this one file.
    /// </summary>
    private static class NativeMethods
    {
        private const int HwndMessageParent = -3; // HWND_MESSAGE: a message-only window

        public delegate IntPtr WndProcCallback(IntPtr hWnd, uint message, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        private struct WndClass
        {
            public uint style;
            public WndProcCallback lpfnWndProc;
            public int cbClsExtra;
            public int cbWndExtra;
            public IntPtr hInstance;
            public IntPtr hIcon;
            public IntPtr hCursor;
            public IntPtr hbrBackground;
            [MarshalAs(UnmanagedType.LPWStr)] public string? lpszMenuName;
            [MarshalAs(UnmanagedType.LPWStr)] public string lpszClassName;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct Msg
        {
            public IntPtr hwnd;
            public uint message;
            public UIntPtr wParam;
            public IntPtr lParam;
            public uint time;
            public int ptX;
            public int ptY;
        }

        private static bool windowClassRegistered;

        public static bool EnsureWindowClassRegistered(string className, WndProcCallback callback)
        {
            if (windowClassRegistered) return true;
            var wndClass = new WndClass
            {
                lpfnWndProc = callback,
                hInstance = Marshal.GetHINSTANCE(typeof(SpotlightClipboardService).Module),
                lpszClassName = className,
            };
            var atom = RegisterClassW(ref wndClass);
            if (atom != 0)
            {
                windowClassRegistered = true;
                return true;
            }
            // A previous service lifetime already registered this exact class name — reuse it.
            return Marshal.GetLastWin32Error() == ErrorClassAlreadyExists;
        }

        public static bool TryCreateMessageWindow(string className, IntPtr instance, out IntPtr window)
        {
            window = CreateWindowExW(0, className, null, 0, 0, 0, 0, 0,
                (IntPtr)HwndMessageParent, IntPtr.Zero, instance, IntPtr.Zero);
            return window != IntPtr.Zero;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern ushort RegisterClassW(ref WndClass wndClass);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr CreateWindowExW(uint exStyle,
            [MarshalAs(UnmanagedType.LPWStr)] string className,
            [MarshalAs(UnmanagedType.LPWStr)] string? windowName,
            uint style, int x, int y, int width, int height,
            IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool DestroyWindow(IntPtr window);

        [DllImport("user32.dll")]
        public static extern int GetMessage(out Msg message, IntPtr window, uint min, uint max);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool TranslateMessage(ref Msg message);

        [DllImport("user32.dll")]
        public static extern IntPtr DispatchMessage(ref Msg message);

        [DllImport("user32.dll")]
        public static extern IntPtr DefWindowProc(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool PostThreadMessage(uint threadId, uint message, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool AddClipboardFormatListener(IntPtr window);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool RemoveClipboardFormatListener(IntPtr window);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

        [DllImport("kernel32.dll")]
        public static extern uint GetCurrentThreadId();
    }

    /// <summary>
    /// Clipboard read/write primitives. Private to this file on purpose: the WindowRoleChecks
    /// gate counts <c>[DllImport]</c>s, and every new import of this feature must live in this
    /// one file.
    /// </summary>
    private static class NativeClipboard
    {
        private const uint CfUnicodeText = 13;
        private const uint GlobalMemMoveable = 0x0002;

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool OpenClipboard(IntPtr ownerWindow);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseClipboard();

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool EmptyClipboard();

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetClipboardData(uint format, IntPtr handle);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr GetClipboardData(uint format);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr GetClipboardOwner();

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GlobalAlloc(uint flags, UIntPtr bytes);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GlobalLock(IntPtr handle);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GlobalUnlock(IntPtr handle);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GlobalFree(IntPtr handle);

        /// <summary>
        /// Reads <c>CF_UNICODETEXT</c>, opening the clipboard against <paramref name="ownerWindow"/>.
        /// Retries on open failures — the clipboard is a machine-global resource that browsers,
        /// clipboard managers and shell extensions hold for brief moments. Returns null (never
        /// throws) when the clipboard holds no text or stays busy.
        /// </summary>
        public static string? ReadUnicodeText(IntPtr ownerWindow)
        {
            if (!TryOpen(ownerWindow)) return null;
            try
            {
                var handle = GetClipboardData(CfUnicodeText);
                if (handle == IntPtr.Zero) return null;
                var pointer = GlobalLock(handle);
                if (pointer == IntPtr.Zero) return null;
                try
                {
                    return Marshal.PtrToStringUni(pointer); // reads up to the NUL terminator
                }
                finally
                {
                    GlobalUnlock(handle);
                }
            }
            finally
            {
                CloseClipboard();
            }
        }

        /// <summary>
        /// Writes <paramref name="text"/> as <c>CF_UNICODETEXT</c>, empting and re-owning the
        /// clipboard through the listener window so the echo of this write can be recognized by
        /// owner comparison. On success the allocated handle belongs to the clipboard and must
        /// not be freed. Returns false (never throws) on failure.
        /// </summary>
        public static bool WriteUnicodeText(IntPtr ownerWindow, string text)
        {
            if (!TryOpen(ownerWindow)) return false;
            IntPtr handle = IntPtr.Zero;
            try
            {
                if (!EmptyClipboard()) return false;

                var bytes = (UIntPtr)((ulong)(text.Length + 1) * 2); // UTF-16 + NUL
                handle = GlobalAlloc(GlobalMemMoveable, bytes);
                if (handle == IntPtr.Zero) return false;

                var pointer = GlobalLock(handle);
                if (pointer == IntPtr.Zero)
                {
                    GlobalFree(handle);
                    handle = IntPtr.Zero;
                    return false;
                }

                try
                {
                    var chars = new char[text.Length + 1];
                    text.CopyTo(0, chars, 0, text.Length); // trailing element stays NUL
                    Marshal.Copy(chars, 0, pointer, chars.Length);
                }
                finally
                {
                    GlobalUnlock(handle);
                }

                if (SetClipboardData(CfUnicodeText, handle) == IntPtr.Zero)
                {
                    // Ownership did not transfer — free our allocation.
                    GlobalFree(handle);
                    handle = IntPtr.Zero;
                    return false;
                }
                handle = IntPtr.Zero;
                return true;
            }
            finally
            {
                if (handle != IntPtr.Zero) GlobalFree(handle);
                CloseClipboard();
            }
        }

        private static bool TryOpen(IntPtr ownerWindow)
        {
            for (var attempt = 0; attempt < ClipboardOpenRetryCount; attempt++)
            {
                if (OpenClipboard(ownerWindow)) return true;
                Thread.Sleep(ClipboardOpenRetryDelayMs);
            }
            return false;
        }
    }
}
