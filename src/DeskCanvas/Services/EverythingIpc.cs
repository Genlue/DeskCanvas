using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace DeskCanvas.Services;

/// <summary>One row Everything returned for a query.</summary>
/// <param name="Name">File name with extension.</param>
/// <param name="Folder">
/// Parent folder, <b>without</b> a trailing separator — Everything's IPC reply carries the folder
/// and the name as two independent strings, and the folder is exactly what
/// <c>Everything_GetResultPath</c> returns. (The SDK's full-path helper joins them; the raw reply
/// does not.)
/// </param>
/// <param name="IsFolder">True for a directory hit.</param>
/// <param name="Size">Bytes; 0 for folders (Everything reports the folder's own entry size).</param>
public sealed record EverythingHit(string Name, string Folder, bool IsFolder, long Size)
{
    /// <summary>
    /// The absolute path, assembled the way Explorer shows it.
    /// <para>
    /// <see cref="Path.Combine"/> rather than <c>Folder + Name</c>: the reply's folder has no
    /// trailing separator, so concatenation produced <c>…\其它相册\文件屏幕截图.png</c> — a path
    /// that does not exist. Every downstream consumer then failed silently (no thumbnail, no
    /// text preview, and "打开" doing nothing), which read as a preview-pane bug.
    /// </para>
    /// </summary>
    public string FullPath => string.IsNullOrEmpty(Folder) ? Name : Path.Combine(Folder, Name);
}

/// <summary>Why a query produced what it produced. Drives the settings page's status line.</summary>
public enum EverythingStatus
{
    /// <summary>No query has run yet.</summary>
    Unknown,

    /// <summary>Answered by the running Everything client.</summary>
    Connected,

    /// <summary>Everything is installed but its client process is not running.</summary>
    NotRunning,

    /// <summary>Everything could not be found on this machine.</summary>
    NotInstalled,

    /// <summary>
    /// The client refused the IPC call — the classic cause is Everything running elevated while
    /// DeskCanvas does not, because UIPI blocks WM_COPYDATA from a lower integrity level.
    /// </summary>
    AccessDenied,

    /// <summary>The call was attempted and failed for another reason (timeout, bad reply).</summary>
    Failed,
}

/// <summary>
/// A DLL-free client for Everything's WM_COPYDATA IPC (the protocol the official SDK wraps).
/// <para>
/// <b>Why not the SDK DLL:</b> shipping <c>Everything64.dll</c> would put a third-party binary in
/// the installer and in the single-file bundle for one feature. The protocol is public
/// (<c>everything_ipc.h</c>): a packed <c>EVERYTHING_IPC_QUERY2</c> struct sent as WM_COPYDATA to
/// the window class <c>EVERYTHING_TASKBAR_NOTIFICATION</c>, answered by a WM_COPYDATA to the reply
/// window named in that struct.
/// </para>
/// <para>
/// <b>Threading:</b> the reply window and its message pump live on one dedicated thread. Everything
/// answers <i>during</i> our SendMessage (it sends the result back re-entrantly), so running the
/// call on that same thread means the reply is dispatched into a window that is pumping — no
/// cross-thread deadlock, no "the result arrived before anyone was listening".
/// </para>
/// <para>
/// <b>Everything must be running:</b> the service indexes the disk but owns no IPC window. Only the
/// client process answers queries, so a summon with no client shows "not running" and the launcher
/// falls back to its own index (or starts the client, see <see cref="TryStartClient"/>).
/// </para>
/// </summary>
public sealed class EverythingIpc : IDisposable
{
    private const int WmCopyData = 0x004A;
    private const int WmAppQuery = 0x8000 + 0x51;

    /// <summary>Everything's IPC window class (the taskbar notification window).</summary>
    private const string IpcWindowClass = "EVERYTHING_TASKBAR_NOTIFICATION";

    /// <summary>EVERYTHING_IPC_COPYDATA_QUERY2W — the Unicode QUERY2 payload.</summary>
    private const int CopyDataQuery2W = 18;

    /// <summary>Value we stamp on the reply so a stray WM_COPYDATA cannot be mistaken for a result.</summary>
    private const int ReplyToken = 0x0DCA;

    // EVERYTHING_IPC_MATCH* flags.
    private const int MatchCase = 0x00000001;
    private const int MatchPath = 0x00000004;
    private const int RegexFlag = 0x00000008;

    // EVERYTHING_IPC_QUERY2_REQUEST_* flags, in the order the reply encodes them.
    private const int RequestName = 0x00000001;
    private const int RequestPath = 0x00000002;
    private const int RequestSize = 0x00000010;

    /// <summary>EVERYTHING_IPC_SORT_NAME_ASCENDING — the only sort Everything guarantees to be free.</summary>
    private const int SortNameAscending = 1;

    private const int MsgfltAllow = 1;

    private static readonly object Gate = new();
    private static EverythingIpc? shared;

    /// <summary>The process-wide client (created on first use).</summary>
    public static EverythingIpc Shared => shared ??= new EverythingIpc();

    private readonly object queueGate = new();
    private readonly Queue<Request> queue = new();
    private readonly ManualResetEventSlim threadReady = new(false);

    private Thread? thread;
    private IntPtr replyWindow;
    private string? className;
    private WndProc? wndProc;          // rooted: the native side keeps a raw function pointer
    private List<EverythingHit>? pendingHits;
    private bool replyReceived;
    private bool disposed;

    /// <summary>Where the last query stood. Read by the settings page.</summary>
    public EverythingStatus Status { get; private set; } = EverythingStatus.Unknown;

    /// <summary>Human-readable detail for <see cref="Status"/> (null when there is nothing to say).</summary>
    public string? LastError { get; private set; }

    /// <summary>The client executable the launcher would start, or null when Everything is absent.</summary>
    public string? ClientPath { get; private set; }

    /// <summary>True when Everything's IPC window exists right now.</summary>
    public bool IsClientRunning => FindWindowW(IpcWindowClass, null) != IntPtr.Zero;

    /// <summary>
    /// Run one query. Returns an empty list (never throws) when Everything cannot answer; the
    /// reason lands in <see cref="Status"/>.
    /// </summary>
    /// <param name="search">Everything search syntax (already translated by <see cref="FileSearchQuery"/>).</param>
    /// <param name="regex">Interpret <paramref name="search"/> as a regular expression.</param>
    /// <param name="matchCase">Case-sensitive match.</param>
    /// <param name="max">Result cap.</param>
    public IReadOnlyList<EverythingHit> Query(string search, bool regex, bool matchCase, int max)
    {
        if (string.IsNullOrWhiteSpace(search)) return [];
        if (disposed) return [];

        if (!EnsureThread())
        {
            Status = EverythingStatus.Failed;
            LastError = "ipc-thread";
            return [];
        }

        var request = new Request(search, regex, matchCase, Math.Max(1, max));
        lock (queueGate) queue.Enqueue(request);

        if (!PostMessageW(replyWindow, WmAppQuery, IntPtr.Zero, IntPtr.Zero))
        {
            lock (queueGate) queue.Clear();
            Status = EverythingStatus.Failed;
            LastError = "post-failed";
            return [];
        }

        // Everything answers in single-digit milliseconds for a warm index; a cold index or a
        // busy client is still well inside this budget. Timing out is not fatal — the caller
        // falls back — so the wait is bounded rather than the call being abandoned.
        if (!request.Done.Wait(TimeSpan.FromMilliseconds(2500)))
        {
            Status = EverythingStatus.Failed;
            LastError = "timeout";
            return [];
        }

        return request.Hits ?? [];
    }

    /// <summary>
    /// Start Everything minimised so its IPC window exists. Returns false when no client
    /// executable could be located or the launch failed.
    /// </summary>
    public bool TryStartClient()
    {
        if (IsClientRunning) return true;

        var exe = ClientPath ??= FindClientExecutable();
        if (exe == null) return false;

        try
        {
            Process.Start(new ProcessStartInfo(exe, "-minimized") { UseShellExecute = true });
        }
        catch
        {
            return false;
        }

        // Everything creates its notification window early, but a cold start also loads the
        // index; 4 s covers the observed worst case on a spinning disk.
        var deadline = Environment.TickCount64 + 4000;
        while (Environment.TickCount64 < deadline)
        {
            if (IsClientRunning) return true;
            Thread.Sleep(120);
        }

        return false;
    }

    /// <summary>
    /// Locate <c>Everything.exe</c>: the running process first (it knows its own path), then the
    /// installer's default locations. Deliberately registry-free — the launcher only needs a path
    /// to <i>start</i> a client, and every installed copy lives in one of these places.
    /// </summary>
    public static string? FindClientExecutable()
    {
        try
        {
            foreach (var process in Process.GetProcessesByName("Everything"))
            {
                using (process)
                {
                    try
                    {
                        var path = process.MainModule?.FileName;
                        if (!string.IsNullOrEmpty(path) && File.Exists(path)) return path;
                    }
                    catch
                    {
                        // An elevated client's module list is unreadable from here; fall through.
                    }
                }
            }
        }
        catch { }

        var candidates = new List<string>();
        foreach (var root in new[]
                 {
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                     Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                 })
        {
            if (string.IsNullOrEmpty(root)) continue;
            candidates.Add(Path.Combine(root, "Everything", "Everything.exe"));
            candidates.Add(Path.Combine(root, "Programs", "Everything", "Everything.exe"));
        }

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate)) return candidate;
        }

        return null;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;

        var handle = replyWindow;
        replyWindow = IntPtr.Zero;
        if (handle != IntPtr.Zero) PostMessageW(handle, 0x0010 /* WM_CLOSE */, IntPtr.Zero, IntPtr.Zero);
        threadReady.Dispose();
    }

    // ---- message thread -----------------------------------------------------------

    private bool EnsureThread()
    {
        lock (Gate)
        {
            if (disposed) return false;
            if (thread != null) return replyWindow != IntPtr.Zero;

            thread = new Thread(ThreadMain)
            {
                IsBackground = true,
                Name = "EverythingIpc",
            };
            thread.Start();

            // The window is created before the pump starts; a slow machine gets 2 s before we
            // give up and report the failure instead of blocking a summon.
            return threadReady.Wait(2000) && replyWindow != IntPtr.Zero;
        }
    }

    private void ThreadMain()
    {
        className = "DeskCanvasEverythingIpc";
        wndProc = WndProcImpl;

        var wc = new WNDCLASSW
        {
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(wndProc),
            hInstance = GetModuleHandleW(null),
            lpszClassName = className,
        };
        RegisterClassW(ref wc);

        // A hidden *top-level* window, exactly like the official SDK creates: a message-only
        // (HWND_MESSAGE) window does not receive Everything's cross-process reply.
        replyWindow = CreateWindowExW(0, className, string.Empty, WsPopup, 0, 0, 0, 0,
            IntPtr.Zero, IntPtr.Zero, wc.hInstance, IntPtr.Zero);

        if (replyWindow != IntPtr.Zero)
        {
            // Everything may run at a higher integrity level (installed "as administrator");
            // without this filter its reply WM_COPYDATA would be blocked by UIPI.
            ChangeWindowMessageFilterEx(replyWindow, WmCopyData, MsgfltAllow, IntPtr.Zero);
        }

        threadReady.Set();
        if (replyWindow == IntPtr.Zero) return;

        while (GetMessageW(out var message, IntPtr.Zero, 0, 0))
        {
            TranslateMessage(ref message);
            DispatchMessageW(ref message);
        }
    }

    private IntPtr WndProcImpl(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam)
    {
        switch (message)
        {
            case WmCopyData:
                ReadReply(lParam);
                return new IntPtr(1);

            case WmAppQuery:
                DrainQueue();
                return IntPtr.Zero;
        }

        return DefWindowProcW(hwnd, message, wParam, lParam);
    }

    /// <summary>Run every queued request on the message thread (Everything allows one at a time).</summary>
    private void DrainQueue()
    {
        while (true)
        {
            Request request;
            lock (queueGate)
            {
                if (queue.Count == 0) return;
                request = queue.Dequeue();
            }

            try
            {
                RunQuery(request);
            }
            catch (Exception ex)
            {
                Status = EverythingStatus.Failed;
                LastError = ex.GetType().Name;
            }
            finally
            {
                request.Done.Set();
            }
        }
    }

    private void RunQuery(Request request)
    {
        var everything = FindWindowW(IpcWindowClass, null);
        if (everything == IntPtr.Zero)
        {
            Status = EverythingStatus.NotRunning;
            LastError = null;
            return;
        }

        pendingHits = new List<EverythingHit>();
        replyReceived = false;

        var flags = MatchPath
                    | (request.Regex ? RegexFlag : 0)
                    | (request.MatchCase ? MatchCase : 0);

        // EVERYTHING_IPC_QUERY2 is #pragma pack(1): seven DWORDs, then the search string.
        const int header = 7 * 4;
        var bytes = header + (request.Search.Length + 1) * 2;
        var buffer = Marshal.AllocHGlobal(bytes);
        try
        {
            Marshal.WriteInt32(buffer, 0, (int)(replyWindow.ToInt64() & 0xFFFFFFFF));
            Marshal.WriteInt32(buffer, 4, ReplyToken);
            Marshal.WriteInt32(buffer, 8, flags);
            Marshal.WriteInt32(buffer, 12, 0);                 // offset
            Marshal.WriteInt32(buffer, 16, (int)request.Max);
            Marshal.WriteInt32(buffer, 20, RequestName | RequestPath | RequestSize);
            Marshal.WriteInt32(buffer, 24, SortNameAscending);
            for (var i = 0; i < request.Search.Length; i++)
                Marshal.WriteInt16(buffer, header + i * 2, (short)request.Search[i]);
            Marshal.WriteInt16(buffer, header + request.Search.Length * 2, 0);

            var cds = new CopyDataStruct
            {
                dwData = new IntPtr(CopyDataQuery2W),
                cbData = bytes,
                lpData = buffer,
            };

            // SendMessageTimeout, not SendMessage: a hung client must not freeze the launcher.
            var sent = SendMessageTimeoutW(everything, WmCopyData, replyWindow, ref cds,
                SmtoAbortIfHung | SmtoNormal, 1500, out _);

            if (sent == IntPtr.Zero)
            {
                var error = Marshal.GetLastWin32Error();
                Status = error == 5 ? EverythingStatus.AccessDenied : EverythingStatus.Failed;
                LastError = error == 5
                    ? "access-denied (Everything is running elevated)"
                    : $"send-failed:{error}";
                return;
            }

            // Everything answers by sending WM_COPYDATA to the reply window from its own thread,
            // and that reply is *not* guaranteed to land inside the SendMessage call (the official
            // SDK runs a WaitMessage/PeekMessage loop for exactly this reason). Pump until it does,
            // with a bound so a silent client cannot hang the launcher.
            var deadline = Environment.TickCount64 + ReplyTimeoutMs;
            while (!replyReceived && Environment.TickCount64 < deadline)
            {
                if (PeekMessageW(out var message, IntPtr.Zero, 0, 0, PmRemove))
                {
                    TranslateMessage(ref message);
                    DispatchMessageW(ref message);
                }
                else
                {
                    Thread.Sleep(2);
                }
            }

            request.Hits = pendingHits;
            if (replyReceived)
            {
                Status = EverythingStatus.Connected;
                LastError = null;
            }
            else
            {
                Status = EverythingStatus.Failed;
                LastError = "no-reply";
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
            pendingHits = null;
        }
    }

    /// <summary>
    /// Parse <c>EVERYTHING_IPC_LIST2</c>: a 5-DWORD header, <c>numitems</c> 2-DWORD items, then the
    /// variable-length data blobs each item points at.
    /// </summary>
    private void ReadReply(IntPtr lParam)
    {
        if (lParam == IntPtr.Zero) return;

        var cds = Marshal.PtrToStructure<CopyDataStruct>(lParam);
        if (cds.dwData.ToInt64() != ReplyToken || cds.lpData == IntPtr.Zero) return;

        var count = Marshal.ReadInt32(cds.lpData, 4);

        var hits = pendingHits ??= new List<EverythingHit>();
        replyReceived = true;
        for (var i = 0; i < count; i++)
        {
            var item = cds.lpData + 20 + i * 8;
            var flags = Marshal.ReadInt32(item, 0);
            var dataOffset = Marshal.ReadInt32(item, 4);
            var cursor = cds.lpData + dataOffset;

            var name = ReadLengthPrefixed(cursor, out var afterName);
            var folder = ReadLengthPrefixed(afterName, out var afterPath);
            var size = Marshal.ReadInt64(afterPath);

            if (string.IsNullOrEmpty(name)) continue;
            hits.Add(new EverythingHit(name, folder, (flags & 1) != 0, size));
        }
    }

    /// <summary>Read a <c>[DWORD length][WCHAR text][NUL]</c> blob; <paramref name="next"/> is past it.</summary>
    private static string ReadLengthPrefixed(IntPtr cursor, out IntPtr next)
    {
        var length = Marshal.ReadInt32(cursor);
        var text = length > 0 ? Marshal.PtrToStringUni(cursor + 4, length) : string.Empty;
        next = cursor + 4 + (length + 1) * 2;
        return text ?? string.Empty;
    }

    private sealed class Request(string search, bool regex, bool matchCase, int max)
    {
        public string Search { get; } = search;
        public bool Regex { get; } = regex;
        public bool MatchCase { get; } = matchCase;
        public int Max { get; } = max;
        public ManualResetEventSlim Done { get; } = new(false);
        public IReadOnlyList<EverythingHit>? Hits { get; set; }
    }

    // ---- interop -----------------------------------------------------------------

    /// <summary>Window style of the hidden reply window (WS_POPUP, no border, never shown).</summary>
    private const int WsPopup = unchecked((int)0x80000000);

    /// <summary>How long a reply may take before the query is abandoned.</summary>
    private const int ReplyTimeoutMs = 1200;

    private const uint PmRemove = 0x0001;

    private const uint SmtoAbortIfHung = 0x0002;
    private const uint SmtoNormal = 0x0000;

    private delegate IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct CopyDataStruct
    {
        public IntPtr dwData;
        public int cbData;
        public IntPtr lpData;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int ptX;
        public int ptY;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSW
    {
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string? lpszMenuName;
        public string? lpszClassName;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowW(string? className, string? windowName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SendMessageTimeoutW(IntPtr hwnd, int msg, IntPtr wParam,
        ref CopyDataStruct lParam, uint flags, uint timeout, out IntPtr result);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassW(ref WNDCLASSW wc);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowExW(int exStyle, string className, string? windowName,
        int style, int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hwnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr DefWindowProcW(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool PostMessageW(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool GetMessageW(out MSG msg, IntPtr hwnd, uint filterMin, uint filterMax);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool PeekMessageW(out MSG msg, IntPtr hwnd, uint filterMin, uint filterMax, uint remove);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref MSG msg);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessageW(ref MSG msg);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool ChangeWindowMessageFilterEx(IntPtr hwnd, int msg, int action, IntPtr changeFilterStruct);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandleW(string? moduleName);
}
