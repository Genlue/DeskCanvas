using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using DeskCanvas.Core;
using DeskCanvas.Core.Interfaces;
using DeskCanvas.Core.Models.Settings;
using SkiaSharp;

namespace DeskCanvas.Services;

/// <summary>
/// What a clipboard entry <b>is</b>. The three the Windows clipboard actually carries between apps,
/// and the same three the 剪贴板组件 records (文本 / 图片 / 文件) — a launcher history that only kept
/// text would silently drop half of what a user copies.
/// </summary>
public enum ClipboardContentKind
{
    /// <summary>Plain text or a link (<see cref="ClipboardEntry.Text"/>).</summary>
    Text,

    /// <summary>A bitmap, stored as a PNG under the data folder (<see cref="ClipboardEntry.ImagePath"/>).</summary>
    Image,

    /// <summary>One or more files / folders copied from Explorer (<see cref="ClipboardEntry.FilePaths"/>).</summary>
    Files,
}

/// <summary>
/// One captured clipboard entry, newest-first inside <see cref="SpotlightClipboardService.Entries"/>.
/// </summary>
/// <param name="Id">Stable identifier (<c>Guid.ToString("N")</c>), survives persistence.</param>
/// <param name="Kind">Which of the three formats this entry is.</param>
/// <param name="Text">The captured text; empty for <see cref="ClipboardContentKind.Image"/> and
/// <see cref="ClipboardContentKind.Files"/>.</param>
/// <param name="IsLink">True when the text looks like a URL or a bare domain.</param>
/// <param name="CapturedUtc">When the entry was captured from the system clipboard (or refreshed
/// by a <see cref="SpotlightClipboardService.CopyBack"/>).</param>
/// <param name="SourceProcess">Process name of the clipboard owner at capture time, when it could
/// be resolved; <c>null</c> otherwise (unreadable owners are recorded, not dropped).</param>
/// <param name="FilePaths">The copied files / folders (<see cref="ClipboardContentKind.Files"/>) —
/// absolute paths, in the order Explorer handed them over.</param>
/// <param name="ImagePath">The PNG this image was stored as (<see cref="ClipboardContentKind.Image"/>),
/// under <c>&lt;data folder&gt;\ClipboardImages</c>. Null when the file was already gone.</param>
/// <param name="ImageWidth">Pixel width of the captured bitmap (0 for other kinds).</param>
/// <param name="ImageHeight">Pixel height of the captured bitmap (0 for other kinds).</param>
/// <param name="ImageHash">
/// SHA-256 of the clipboard's DIB bytes. It is what makes "copy the same picture twice" a no-op
/// instead of a second identical entry, the way text compares by its own content — comparing the
/// decoded pixels would cost more than hashing the buffer we already hold.
/// </param>
public sealed record ClipboardEntry(
    string Id,
    ClipboardContentKind Kind,
    string Text,
    bool IsLink,
    DateTime CapturedUtc,
    string? SourceProcess,
    IReadOnlyList<string>? FilePaths = null,
    string? ImagePath = null,
    int ImageWidth = 0,
    int ImageHeight = 0,
    string? ImageHash = null);

/// <summary>
/// A bitmap just lifted off the clipboard, with the hash that identifies it.
/// <para>
/// The decoded <see cref="Bitmap"/> is owned by the caller, which encodes it to the entry's PNG and
/// disposes it — the history never holds decoded pixels, only the file. Full-screen captures are
/// scaled down to <see cref="SpotlightClipboardService.MaxImageSide"/> first, so one 8K screenshot
/// cannot turn a 100-entry history into a gigabyte folder.
/// </para>
/// </summary>
/// <param name="Bitmap">The pixels, already within the size ceiling.</param>
/// <param name="Hash">SHA-256 of the source DIB bytes (see <see cref="ClipboardEntry.ImageHash"/>).</param>
public readonly record struct CapturedClipboardImage(SKBitmap Bitmap, string Hash)
{
    public int Width => Bitmap.Width;

    public int Height => Bitmap.Height;
}


/// <summary>
/// Local clipboard history for the Spotlight launcher: records what the user copies — <b>text,
/// images and files</b> — into memory and a JSON file under <see cref="Const.DataFolder"/>, serves it
/// newest-first, and can put any of the three back on the system clipboard.
/// <para>
/// <b>Formats:</b> the same three the 剪贴板组件 collects, in the same priority order
/// (files → image → text): a picture copied out of a browser usually also carries a text flavour
/// (its URL) and an Explorer copy carries the file names as text, so the richest format wins and
/// the entry is what the user thinks they copied.
/// </para>
/// <para>
/// <b>Why Win32 and not Avalonia's Clipboard API:</b> Avalonia clipboard access requires a
/// <c>TopLevel</c> window, and this service is a background recorder that must exist (and keep
/// listening) even when no launcher window is open. A message-only window on a dedicated STA
/// thread with <c>AddClipboardFormatListener</c> is the standard machine-wide listener pattern.
/// </para>
/// <para>
/// <b>Storage:</b> text and file paths live in the JSON history; a bitmap is written once as a PNG
/// under <c>&lt;data folder&gt;\ClipboardImages</c> and only its path is kept in the file. Retention,
/// the 100-entry cap and "clear history" all delete the PNGs they orphan, so the folder never grows
/// past the history it belongs to.
/// </para>
/// <para>
/// <b>Privacy contract:</b> this is an opt-in (<see cref="LauncherSettings.ClipboardHistory"/>,
/// default off), machine-local feature. Clipboard content is never written to any log —
/// <see cref="GlassDiagnostics"/> calls in this file carry entry counts and state transitions
/// only. The history file and the image folder are the feature itself (local history), hold
/// plaintext / raw pixels by design, and live only under the user's data folder.
/// </para>
/// </summary>
public sealed class SpotlightClipboardService : IDisposable
{
    private const string FileName = "launcher-clipboard.json";
    private const string ImagesFolderName = "ClipboardImages";
    /// <summary>Largest bitmap the history keeps, in pixels on the long side. A 4K screenshot stays
    /// lossless; anything bigger (a full-screen capture at 200 % scaling) is scaled down before it
    /// is written, because 100 entries of 30 MB PNGs is not a history, it is a disk leak.</summary>
    private const int MaxImageSide = 4096;

    /// <summary>
    /// How many <b>image</b> entries the history keeps, regardless of the 100-entry cap. A PNG is
    /// orders of magnitude heavier than a text entry, so the shared cap alone would let a morning of
    /// screenshots fill a gigabyte. Beyond this the oldest picture is evicted (and its file deleted);
    /// text and files never count towards it.
    /// </summary>
    private const int MaxImageEntries = 20;
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
    /// "clear history" must survive a restart. Captured bitmaps are deleted with it: pixels the
    /// user asked to forget cannot stay behind under a name nothing lists.</summary>
    public void Clear()
    {
        bool had;
        List<ClipboardEntry> images;
        lock (gate)
        {
            had = entries.Count > 0;
            images = entries.Where(e => e.Kind == ClipboardContentKind.Image).ToList();
            entries.Clear();
            SaveUnderGate();
        }
        DeleteImageFiles(images);
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
            TryCaptureClipboard();
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

    /// <summary>
    /// One clipboard update, richest format first. The order matters more than it looks: a browser
    /// image copy also publishes text (the image URL), and an Explorer copy publishes the file
    /// names as <c>CF_UNICODETEXT</c> alongside <c>CF_HDROP</c> — reading text first would record
    /// every screenshot as a URL and every file copy as a list of names.
    /// </summary>
    private void TryCaptureClipboard()
    {
        try
        {
            var owner = NativeClipboard.GetClipboardOwner();
            // Self-write detection: CopyBack opens/empties/sets with the listener window, so the
            // owner IS us. Boundary trade-off: if the user re-copies the identical text from
            // another app, the owner is their window — that hits the consecutive-duplicate rule
            // below instead, which keeps the older timestamp. Preferring the owner check over
            // content comparison is what prevents a self-echo write loop.
            if (owner == hwnd) return;

            if (!settings.Get().EffectiveLauncher.ClipboardHistory) return;

            var source = ResolveOwnerProcess(owner);
            if (settings.Get().EffectiveLauncher.IsClipboardExcluded(source)) return;

            if (NativeClipboard.TryReadFiles(hwnd, out var files))
            {
                RecordFiles(files, source);
                return;
            }

            if (NativeClipboard.TryReadImage(hwnd, out var image))
            {
                RecordImage(image, source);
                return;
            }

            var text = NativeClipboard.ReadUnicodeText(hwnd);
            if (string.IsNullOrEmpty(text)) return;

            RecordText(text, source);
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

    private void RecordText(string text, string? sourceProcess)
        => Record(
            new ClipboardEntry(
                Guid.NewGuid().ToString("N"),
                ClipboardContentKind.Text,
                text,
                LooksLikeLink(text),
                DateTime.UtcNow,
                sourceProcess),
            isConsecutiveDuplicate: newest => newest.Kind == ClipboardContentKind.Text
                                              && string.Equals(newest.Text, text, StringComparison.Ordinal));

    private void RecordFiles(IReadOnlyList<string> filePaths, string? sourceProcess)
        => Record(
            new ClipboardEntry(
                Guid.NewGuid().ToString("N"),
                ClipboardContentKind.Files,
                string.Empty,
                IsLink: false,
                DateTime.UtcNow,
                sourceProcess,
                FilePaths: filePaths),
            isConsecutiveDuplicate: newest => newest.Kind == ClipboardContentKind.Files
                                              && SamePaths(newest.FilePaths, filePaths));

    private void RecordImage(CapturedClipboardImage image, string? sourceProcess)
    {
        // The PNG is written before the entry exists, because the entry is what names the file:
        // one id, one file, and a failed write simply drops the capture (nothing points at it).
        var id = Guid.NewGuid().ToString("N");
        var path = Path.Combine(ImagesFolder, id + ".png");
        if (!WriteImageFile(path, image)) return;

        Record(
            new ClipboardEntry(
                id,
                ClipboardContentKind.Image,
                string.Empty,
                IsLink: false,
                DateTime.UtcNow,
                sourceProcess,
                ImagePath: path,
                ImageWidth: image.Width,
                ImageHeight: image.Height,
                ImageHash: image.Hash),
            isConsecutiveDuplicate: newest => newest.Kind == ClipboardContentKind.Image
                                              && string.Equals(newest.ImageHash, image.Hash, StringComparison.Ordinal),
            onDuplicate: () => TryDeleteFile(path));
    }

    /// <summary>
    /// Insert one entry, honouring the consecutive-duplicate rule and the cap.
    /// <para>
    /// Suppression is per-kind: re-copying what is already newest neither re-records nor refreshes
    /// the timestamp — a copy burst in the source app must not churn order and must not rewrite
    /// the file. <paramref name="onDuplicate"/> is the kind-specific cleanup (an image writes its
    /// PNG before it can ask this question).
    /// </para>
    /// </summary>
    private void Record(ClipboardEntry entry, Func<ClipboardEntry, bool> isConsecutiveDuplicate, Action? onDuplicate = null)
    {
        var duplicate = false;
        List<ClipboardEntry> dropped = [];
        lock (gate)
        {
            if (entries.Count > 0 && isConsecutiveDuplicate(entries[0]))
            {
                duplicate = true;
            }
            else
            {
                entries.Insert(0, entry); // newest first
                if (entries.Count > LauncherSettings.MaxClipboardEntries)
                {
                    dropped.Add(entries[^1]);
                    entries.RemoveAt(entries.Count - 1); // evict oldest
                }

                // The image budget, on top of the entry cap (see MaxImageEntries).
                if (entry.Kind == ClipboardContentKind.Image)
                {
                    var images = entries.Count(e => e.Kind == ClipboardContentKind.Image);
                    for (var i = entries.Count - 1; images > MaxImageEntries && i > 0; i--)
                    {
                        if (entries[i].Kind != ClipboardContentKind.Image) continue;
                        dropped.Add(entries[i]);
                        entries.RemoveAt(i);
                        images--;
                    }
                }

                SaveUnderGate();
            }
        }

        // Both cleanups are IO and stay outside the lock: the history must never be blocked by a
        // disk delete.
        if (duplicate)
        {
            onDuplicate?.Invoke();
            return;
        }

        DeleteImageFiles(dropped);

        // Counts only — clipboard content must never reach a log.
        GlassDiagnostics.Event($"clipboard history: entry captured ({entry.Kind}), total={CountEntries()}");
        RaiseChanged();
    }

    private static bool SamePaths(IReadOnlyList<string>? left, IReadOnlyList<string>? right)
    {
        if (left == null || right == null) return left == null && right == null;
        if (left.Count != right.Count) return false;
        for (var i = 0; i < left.Count; i++)
            if (!string.Equals(left[i], right[i], StringComparison.OrdinalIgnoreCase)) return false;
        return true;
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

        var written = source.Kind switch
        {
            ClipboardContentKind.Image => source.ImagePath is { Length: > 0 } imagePath
                                          && NativeClipboard.WriteImage(hwnd, imagePath),
            ClipboardContentKind.Files => source.FilePaths is { Count: > 0 } files
                                          && NativeClipboard.WriteFiles(hwnd, files),
            _ => NativeClipboard.WriteUnicodeText(hwnd, source.Text),
        };
        if (!written) return null;

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
            List<ClipboardEntry> expired = [];
            bool removed;
            lock (gate)
            {
                expired = entries.Where(e => e.CapturedUtc < cutoff).ToList();
                removed = expired.Count > 0;
                if (removed)
                {
                    entries.RemoveAll(e => e.CapturedUtc < cutoff);
                    SaveUnderGate();
                }
            }

            if (!removed) return;

            DeleteImageFiles(expired);
            GlassDiagnostics.Event($"clipboard history: expired entries trimmed, total={CountEntries()}");
            RaiseChanged();
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

    /// <summary>Where captured bitmaps live: one PNG per image entry, named after the entry.</summary>
    private static string ImagesFolder => Path.Combine(Const.DataFolder, ImagesFolderName);

    /// <summary>Writes <paramref name="image"/> as the entry's PNG. False on any IO failure —
    /// the capture is then simply dropped rather than recorded pointing at a missing file.</summary>
    private static bool WriteImageFile(string path, CapturedClipboardImage image)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
            using var encoded = image.Bitmap.Encode(SKEncodedImageFormat.Png, 100);
            encoded.SaveTo(stream);
            return true;
        }
        catch (Exception ex)
        {
            GlassDiagnostics.Event($"clipboard history: image write failed ({ex.GetType().Name})");
            TryDeleteFile(path);
            return false;
        }
    }

    private static void TryDeleteFile(string? path)
    {
        if (string.IsNullOrEmpty(path)) return;
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // A locked PNG is retried by the next orphan sweep; never fatal.
        }
    }

    /// <summary>Deletes the PNGs that belong to entries leaving the history. Must be called
    /// <b>outside</b> the <c>gate</c> lock: the delete is IO, and holding the history lock across
    /// it would stall every reader for the duration of a disk write.</summary>
    private static void DeleteImageFiles(IEnumerable<ClipboardEntry> removed)
    {
        foreach (var entry in removed)
        {
            if (entry.Kind == ClipboardContentKind.Image) TryDeleteFile(entry.ImagePath);
        }
    }

    /// <summary>
    /// Delete PNGs no entry points at any more (an interrupted write, a hand-edited history file,
    /// a crash between capture and save). Cheap — the folder only ever holds one file per image
    /// entry — and it keeps the privacy promise honest: "clear history" must not leave pixels on
    /// disk under a name nothing lists.
    /// </summary>
    private void SweepOrphanImages()
    {
        try
        {
            var folder = ImagesFolder;
            if (!Directory.Exists(folder)) return;

            HashSet<string> live;
            lock (gate)
            {
                live = new HashSet<string>(
                    entries.Where(e => e.Kind == ClipboardContentKind.Image && e.ImagePath is { Length: > 0 })
                        .Select(e => Path.GetFileName(e.ImagePath!)),
                    StringComparer.OrdinalIgnoreCase);
            }

            foreach (var file in Directory.EnumerateFiles(folder, "*.png"))
            {
                if (live.Contains(Path.GetFileName(file))) continue;
                TryDeleteFile(file);
            }
        }
        catch (Exception ex)
        {
            GlassDiagnostics.Event($"clipboard history: image sweep failed ({ex.GetType().Name})");
        }
    }

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
                    if (string.IsNullOrWhiteSpace(dto.Id)) continue;

                    var kind = dto.Kind ?? ClipboardContentKind.Text;
                    var text = dto.Text ?? string.Empty;
                    var files = dto.FilePaths;
                    var imagePath = dto.ImagePath;

                    // A text entry with no text, a file entry with no paths, or an image whose PNG
                    // is gone (the user cleaned %LocalAppData%, an old build never wrote one) is
                    // not a row: drop it here rather than render an entry that cannot be copied.
                    var usable = kind switch
                    {
                        ClipboardContentKind.Image => !string.IsNullOrEmpty(imagePath) && File.Exists(imagePath),
                        ClipboardContentKind.Files => files is { Count: > 0 },
                        _ => !string.IsNullOrWhiteSpace(text),
                    };
                    if (!usable) continue;

                    entries.Add(new ClipboardEntry(
                        dto.Id,
                        kind,
                        text,
                        dto.IsLink,
                        dto.CapturedUtc,
                        dto.Source,
                        files,
                        imagePath,
                        dto.ImageWidth,
                        dto.ImageHeight,
                        dto.ImageHash));
                }
                // The file is newest-first (the order we write); trim and cap defensively in
                // case it was hand-edited or written by an older build with different limits.
                entries.RemoveAll(e => e.CapturedUtc < retentionCutoff);
                if (entries.Count > LauncherSettings.MaxClipboardEntries)
                    entries.RemoveRange(LauncherSettings.MaxClipboardEntries,
                        entries.Count - LauncherSettings.MaxClipboardEntries);

                // Same image budget as a live capture: a hand-edited file (or a v1 history that
                // somehow grew) must not load more pictures than a capture would keep.
                var images = entries.Count(e => e.Kind == ClipboardContentKind.Image);
                for (var i = entries.Count - 1; images > MaxImageEntries && i > 0; i--)
                {
                    if (entries[i].Kind != ClipboardContentKind.Image) continue;
                    entries.RemoveAt(i);
                    images--;
                }
            }

            SweepOrphanImages();
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
                // v2: entries carry a kind and a payload (text / image file / file list). The
                // reader is tolerant — v1 files simply have no "kind" and load as text.
                Version = 2,
                Entries = entries.Select(e => new ClipboardEntryDto
                {
                    Id = e.Id,
                    Kind = e.Kind,
                    Text = e.Text,
                    IsLink = e.IsLink,
                    CapturedUtc = e.CapturedUtc,
                    Source = e.SourceProcess,
                    FilePaths = e.FilePaths?.ToList(),
                    ImagePath = e.ImagePath,
                    ImageWidth = e.ImageWidth,
                    ImageHeight = e.ImageHeight,
                    ImageHash = e.ImageHash,
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

        /// <summary>Null in a v1 file (written before formats existed) → text.</summary>
        [System.Text.Json.Serialization.JsonPropertyName("kind")]
        public ClipboardContentKind? Kind { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("text")]
        public string? Text { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("isLink")]
        public bool IsLink { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("capturedUtc")]
        public DateTime CapturedUtc { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("source")]
        public string? Source { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("filePaths")]
        public List<string>? FilePaths { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("imagePath")]
        public string? ImagePath { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("imageWidth")]
        public int ImageWidth { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("imageHeight")]
        public int ImageHeight { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("imageHash")]
        public string? ImageHash { get; set; }
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
        private const uint CfHdrop = 15;
        private const uint CfDib = 8;
        private const uint CfDibV5 = 17;
        private const uint GlobalMemMoveable = 0x0002;
        private const uint GlobalZeroInit = 0x0040;

        /// <summary>The registered "PNG" format (the name browsers and chat clients paste from).</summary>
        private static readonly uint CfPng = RegisterClipboardFormat("PNG");

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern uint RegisterClipboardFormat(string format);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool OpenClipboard(IntPtr ownerWindow);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseClipboard();

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool EmptyClipboard();

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsClipboardFormatAvailable(uint format);

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

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern UIntPtr GlobalSize(IntPtr handle);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern uint DragQueryFileW(IntPtr drop, uint index, [Out] StringBuilder? file, uint length);

        [StructLayout(LayoutKind.Sequential)]
        private struct DropFiles
        {
            public uint pFiles;
            public int ptX;
            public int ptY;
            [MarshalAs(UnmanagedType.Bool)] public bool fNC;
            [MarshalAs(UnmanagedType.Bool)] public bool fWide;
        }

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
        /// Reads <c>CF_HDROP</c> — the files Explorer (or any other app) put on the clipboard.
        /// False when the format is absent, the list is empty, or the clipboard stays busy.
        /// </summary>
        public static bool TryReadFiles(IntPtr ownerWindow, out IReadOnlyList<string> files)
        {
            files = Array.Empty<string>();
            if (!IsClipboardFormatAvailable(CfHdrop)) return false;
            if (!TryOpen(ownerWindow)) return false;

            try
            {
                var drop = GetClipboardData(CfHdrop);
                if (drop == IntPtr.Zero) return false;

                var count = DragQueryFileW(drop, 0xFFFFFFFF, null, 0);
                if (count == 0) return false;

                var list = new List<string>((int)count);
                for (uint i = 0; i < count; i++)
                {
                    var length = DragQueryFileW(drop, i, null, 0);
                    if (length == 0) continue;
                    var buffer = new StringBuilder((int)length + 1);
                    if (DragQueryFileW(drop, i, buffer, (uint)buffer.Capacity) == 0) continue;
                    var path = buffer.ToString();
                    if (path.Length > 0) list.Add(path);
                }

                if (list.Count == 0) return false;
                files = list;
                return true;
            }
            catch
            {
                return false;
            }
            finally
            {
                CloseClipboard();
            }
        }

        /// <summary>
        /// Reads a bitmap (<c>CF_DIB</c> / <c>CF_DIBV5</c>) by wrapping the device-independent
        /// bitmap in a BMP header and decoding it with Skia — the same route the 剪贴板组件 uses,
        /// because the clipboard hands over a headerless DIB rather than an image file.
        /// <para>
        /// The DIB bytes are hashed while the memory is locked: that hash is what lets the history
        /// tell "copied the same picture again" from "copied another picture", without keeping a
        /// full copy of the pixels around.
        /// </para>
        /// </summary>
        public static bool TryReadImage(IntPtr ownerWindow, out CapturedClipboardImage image)
        {
            image = default;
            if (!IsClipboardFormatAvailable(CfDib) && !IsClipboardFormatAvailable(CfDibV5)) return false;
            if (!TryOpen(ownerWindow)) return false;

            try
            {
                var format = IsClipboardFormatAvailable(CfDib) ? CfDib : CfDibV5;
                var handle = GetClipboardData(format);
                if (handle == IntPtr.Zero) return false;

                var size = (int)GlobalSize(handle);
                if (size < 40) return false; // smaller than a BITMAPINFOHEADER

                var pointer = GlobalLock(handle);
                if (pointer == IntPtr.Zero) return false;

                try
                {
                    var biSize = Marshal.ReadInt32(pointer, 0);
                    var biBitCount = Marshal.ReadInt16(pointer, 14);
                    var biCompression = Marshal.ReadInt32(pointer, 16);
                    var biClrUsed = Marshal.ReadInt32(pointer, 32);

                    var colorCount = biBitCount <= 8
                        ? (biClrUsed == 0 ? 1 << biBitCount : biClrUsed)
                        : biBitCount >= 16 && biCompression == 3 ? 3 : 0; // BI_BITFIELDS
                    var offBits = 14 + biSize + colorCount * 4;
                    var totalFileSize = 14 + size;

                    string hash;
                    using (var sha = System.Security.Cryptography.SHA256.Create())
                    {
                        var header = new byte[14];
                        header[0] = 0x42; // 'B'
                        header[1] = 0x4D; // 'M'
                        BitConverter.GetBytes(totalFileSize).CopyTo(header, 2);
                        BitConverter.GetBytes(offBits).CopyTo(header, 10);
                        sha.TransformBlock(header, 0, header.Length, null, 0);

                        var body = new byte[size];
                        Marshal.Copy(pointer, body, 0, size);
                        sha.TransformFinalBlock(body, 0, body.Length);
                        hash = Convert.ToHexString(sha.Hash!);
                    }

                    using var stream = new MemoryStream(totalFileSize);
                    using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
                    {
                        writer.Write((ushort)0x4D42);          // 'BM'
                        writer.Write(totalFileSize);
                        writer.Write((ushort)0);
                        writer.Write((ushort)0);
                        writer.Write(offBits);
                    }

                    stream.SetLength(totalFileSize);
                    Marshal.Copy(pointer, stream.GetBuffer()!, 14, size);
                    stream.Position = 0;

                    var bitmap = SKBitmap.Decode(stream);
                    if (bitmap is not { Width: > 0, Height: > 0 })
                    {
                        bitmap?.Dispose();
                        return false;
                    }

                    // Ceiling on the long side: a 200 %-scaled 4K screenshot decodes to 7680 px
                    // and ~90 MB of pixels. Scaling here keeps the PNG (and every later decode of
                    // it for a thumbnail) inside a size a history can afford.
                    var longest = Math.Max(bitmap.Width, bitmap.Height);
                    if (longest > MaxImageSide)
                    {
                        var scale = (double)MaxImageSide / longest;
                        var target = new SKImageInfo(
                            Math.Max(1, (int)Math.Round(bitmap.Width * scale)),
                            Math.Max(1, (int)Math.Round(bitmap.Height * scale)));
                        var reduced = bitmap.Resize(target, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
                        bitmap.Dispose();
                        if (reduced is not { Width: > 0, Height: > 0 })
                        {
                            reduced?.Dispose();
                            return false;
                        }
                        bitmap = reduced;
                    }

                    image = new CapturedClipboardImage(bitmap, hash);
                    return true;
                }
                finally
                {
                    GlobalUnlock(handle);
                }
            }
            catch
            {
                return false;
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

        /// <summary>
        /// Re-publishes <paramref name="paths"/> as <c>CF_HDROP</c>: a <c>DROPFILES</c> header
        /// followed by the double-NUL-terminated UTF-16 path list, so Explorer pastes the files
        /// (and every app that accepts a file drop sees the same list it saw the first time).
        /// </summary>
        public static bool WriteFiles(IntPtr ownerWindow, IReadOnlyList<string> paths)
        {
            if (paths.Count == 0) return false;
            if (!TryOpen(ownerWindow)) return false;

            IntPtr handle = IntPtr.Zero;
            try
            {
                if (!EmptyClipboard()) return false;

                var headerSize = Marshal.SizeOf<DropFiles>();
                var totalBytes = headerSize + paths.Sum(path => (path.Length + 1) * 2) + 2;
                handle = GlobalAlloc(GlobalMemMoveable | GlobalZeroInit, (UIntPtr)totalBytes);
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
                    var drop = new DropFiles
                    {
                        pFiles = (uint)headerSize,
                        ptX = 0,
                        ptY = 0,
                        fNC = false,
                        fWide = true,
                    };
                    Marshal.StructureToPtr(drop, pointer, false);

                    var cursor = pointer + headerSize;
                    foreach (var path in paths)
                    {
                        var bytes = Encoding.Unicode.GetBytes(path + "\0");
                        Marshal.Copy(bytes, 0, cursor, bytes.Length);
                        cursor += bytes.Length;
                    }
                    Marshal.WriteInt16(cursor, 0); // the list's second NUL
                }
                finally
                {
                    GlobalUnlock(handle);
                }

                if (SetClipboardData(CfHdrop, handle) == IntPtr.Zero)
                {
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

        /// <summary>
        /// Re-publishes a stored PNG as <c>CF_DIB</c> (32 bpp, bottom-up): the format every Windows
        /// app accepts a bitmap on. PNG itself is published too — apps that paste "image/png"
        /// (browsers, chat clients) read that flavour, and it costs one more clipboard copy.
        /// </summary>
        public static bool WriteImage(IntPtr ownerWindow, string pngPath)
        {
            byte[] png;
            SKBitmap? decoded;
            try
            {
                png = File.ReadAllBytes(pngPath);
                decoded = SKBitmap.Decode(png);
            }
            catch
            {
                return false;
            }

            if (decoded is not { Width: > 0, Height: > 0 })
            {
                decoded?.Dispose();
                return false;
            }

            try
            {
                if (!TryOpen(ownerWindow)) return false;

                IntPtr dibHandle = IntPtr.Zero;
                IntPtr pngHandle = IntPtr.Zero;
                try
                {
                    if (!EmptyClipboard()) return false;

                    var width = decoded.Width;
                    var height = decoded.Height;
                    var stride = ((width * 32 + 31) / 32) * 4;
                    var imageSize = stride * height;
                    const int headerSize = 40; // BITMAPINFOHEADER
                    var dibSize = headerSize + imageSize;

                    dibHandle = GlobalAlloc(GlobalMemMoveable | GlobalZeroInit, (UIntPtr)dibSize);
                    if (dibHandle == IntPtr.Zero) return false;

                    var pointer = GlobalLock(dibHandle);
                    if (pointer == IntPtr.Zero)
                    {
                        GlobalFree(dibHandle);
                        dibHandle = IntPtr.Zero;
                        return false;
                    }

                    try
                    {
                        Marshal.WriteInt32(pointer, 0, headerSize); // biSize
                        Marshal.WriteInt32(pointer, 4, width);      // biWidth
                        Marshal.WriteInt32(pointer, 8, height);     // biHeight (positive = bottom-up)
                        Marshal.WriteInt16(pointer, 12, 1);         // biPlanes
                        Marshal.WriteInt16(pointer, 14, 32);        // biBitCount
                        Marshal.WriteInt32(pointer, 16, 0);         // BI_RGB
                        Marshal.WriteInt32(pointer, 20, imageSize); // biSizeImage

                        var pixels = pointer + headerSize;
                        var row = new byte[stride];

                        // Row copies, not per-pixel: a 4096² image is 16 M pixels, and GetPixel
                        // for each of them would spend seconds inside the caller's 2 s copy-back
                        // budget. The source is normalised to BGRA first, so one memcpy per row
                        // is the whole conversion.
                        var source = decoded.ColorType == SKColorType.Bgra8888 ? null : decoded.Copy(SKColorType.Bgra8888);
                        var normalized = source ?? decoded;
                        var sourceSpan = normalized.GetPixelSpan();
                        var sourceStride = normalized.RowBytes;

                        for (var y = 0; y < height; y++)
                        {
                            var sourceY = height - 1 - y; // DIB rows run bottom-up
                            if (!sourceSpan.IsEmpty)
                            {
                                sourceSpan.Slice(sourceY * sourceStride, Math.Min(stride, sourceSpan.Length - sourceY * sourceStride))
                                    .CopyTo(row);
                            }
                            else
                            {
                                for (var x = 0; x < width; x++)
                                {
                                    var color = normalized.GetPixel(x, sourceY);
                                    var offset = x * 4;
                                    row[offset] = color.Blue;
                                    row[offset + 1] = color.Green;
                                    row[offset + 2] = color.Red;
                                    row[offset + 3] = color.Alpha;
                                }
                            }
                            Marshal.Copy(row, 0, pixels + y * stride, stride);
                        }

                        source?.Dispose();
                    }
                    finally
                    {
                        GlobalUnlock(dibHandle);
                    }

                    if (SetClipboardData(CfDib, dibHandle) == IntPtr.Zero)
                    {
                        GlobalFree(dibHandle);
                        dibHandle = IntPtr.Zero;
                        return false;
                    }
                    dibHandle = IntPtr.Zero; // the clipboard owns it now

                    // Optional extra flavour: PNG. A failure here keeps the DIB that already
                    // landed — the copy has succeeded either way.
                    pngHandle = GlobalAlloc(GlobalMemMoveable | GlobalZeroInit, (UIntPtr)png.Length);
                    if (pngHandle != IntPtr.Zero)
                    {
                        var pngPointer = GlobalLock(pngHandle);
                        if (pngPointer != IntPtr.Zero)
                        {
                            try
                            {
                                Marshal.Copy(png, 0, pngPointer, png.Length);
                            }
                            finally
                            {
                                GlobalUnlock(pngHandle);
                            }

                            if (SetClipboardData(CfPng, pngHandle) != IntPtr.Zero) pngHandle = IntPtr.Zero;
                        }

                        if (pngHandle != IntPtr.Zero)
                        {
                            GlobalFree(pngHandle);
                            pngHandle = IntPtr.Zero;
                        }
                    }

                    return true;
                }
                finally
                {
                    if (dibHandle != IntPtr.Zero) GlobalFree(dibHandle);
                    if (pngHandle != IntPtr.Zero) GlobalFree(pngHandle);
                }
            }
            finally
            {
                CloseClipboard();
                decoded.Dispose();
            }
        }
    }
}
