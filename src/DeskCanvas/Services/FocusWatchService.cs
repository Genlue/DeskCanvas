using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using DeskCanvas.Core.Interfaces;
using DeskCanvas.Core.Models.Settings;

namespace DeskCanvas.Services;

/// <summary>What kind of window is currently in the foreground. A display concern only (the
/// 「当前状态」 line) — switching is decided by window <i>occupancy</i>, not by activation.</summary>
public enum ForegroundKind
{
    /// <summary>No foreground window at all (or the observation failed).</summary>
    None,

    /// <summary>The shell: desktop (Progman / WorkerW), taskbar, or a shell surface.</summary>
    Shell,

    /// <summary>One of DeskCanvas' own windows (widgets, panels, the settings window).</summary>
    OwnFurniture,

    /// <summary>A third-party process whose name is on the user's allowlist.</summary>
    Whitelisted,

    /// <summary>An ordinary application window.</summary>
    Application,
}

/// <summary>
/// One evaluation result of <see cref="FocusWatchService"/>. Immutable; compared structurally
/// so a real change can be told from noise.
/// <para>
/// <see cref="Kind"/> / <see cref="ProcessName"/> are display fields: while applications are
/// around they name the <i>leading</i> application (the topmost window that keeps the widgets
/// unfocused — stable across clicks); with the desktop clear they name the momentary foreground.
/// They never drive switching.
/// </para>
/// </summary>
public sealed record FocusStateSnapshot(
    ForegroundKind Kind,
    string? ProcessName,
    bool AnyApplicationFocused,
    IReadOnlySet<ScreenRect> UnfocusedScreens)
{
    /// <summary>The all-focused default (used before the first evaluation and during the startup grace).</summary>
    public static FocusStateSnapshot Focused { get; } =
        new(ForegroundKind.None, null, false, new HashSet<ScreenRect>());

    /// <summary>Structural equality, including set comparison of <see cref="UnfocusedScreens"/>.</summary>
    public bool SameAs(FocusStateSnapshot other) =>
        Kind == other.Kind
        && string.Equals(ProcessName, other.ProcessName, StringComparison.OrdinalIgnoreCase)
        && AnyApplicationFocused == other.AnyApplicationFocused
        && UnfocusedScreens.Count == other.UnfocusedScreens.Count
        && UnfocusedScreens.All(other.UnfocusedScreens.Contains);
}

/// <summary>One running process that owns at least one visible, titled top-level window — the
/// candidates the process picker offers.</summary>
public readonly record struct WindowedProcess(string Name, string? Title);

/// <summary>One top-level window as seen by the focus watcher's z-order walk — metadata only.
/// Gathered on the UI thread; all decisions are made by the pure
/// <see cref="FocusWatchService.ResolveUnfocusedScreens"/>.</summary>
public sealed record FocusWindow(
    string ClassName,
    int ProcessId,
    bool IsOwnProcess,
    string? ProcessName,
    string Title,
    ScreenRect Bounds,
    bool IsVisible = true,
    bool IsMinimized = false,
    bool IsCloaked = false,
    bool IsToolWindow = false);

/// <summary>The outcome of the z-order walk: which screens are kept unfocused, and the leading
/// application (topmost application window; for the status line — <c>null</c> when none is left).</summary>
public sealed record FocusWalkOutcome(IReadOnlyList<int> UnfocusedScreenIndices, string? LeadingApplication);

/// <summary>
/// Watches the desktop's <b>occupancy</b> for the 失焦自动切换 feature: a screen counts as
/// <b>unfocused</b> while it still carries a non-whitelist application window — any visible,
/// non-minimized application window that meaningfully lives on that screen.
/// <para>
/// The rule is deliberately about <i>what occupies the desktop</i>, not about which window is
/// momentarily active; the foreground window is never consulted for the decision. Once an
/// application is on the desktop, clicking the desktop / taskbar / widgets changes nothing
/// (“怎么点都是失焦”); the moment the last application window is minimized, closed or hidden,
/// the screens it occupied snap back to <b>focused</b>. Minimized / cloaked / tool / titleless
/// windows do not occupy the desktop, whitelisted processes are desktop furniture, shell
/// surfaces (desktop, taskbar, start menu, task switchers) never count, and DeskCanvas' own
/// windows (widgets, panels, the settings window) are invisible to the walk.
/// </para>
/// <para>
/// Anti-flicker dwell: a switch is only applied once the new state has held for the
/// user-configured dwell (<c>FocusLinkSettings.SwitchDelayMs</c>, 0–1000 ms; 0 = immediately).
/// A transient window — one flashing open and shut, a fast Alt+Tab round trip — never outlasts
/// the dwell, so the theme no longer flickers.
/// </para>
/// <para>
/// The decision is a z-order walk (<c>EnumWindows</c> yields top-level windows from top to
/// bottom) evaluated per screen. Reaction time: a 150 ms poll plus <c>SetWinEventHook</c> kicks
/// (minimize / restore / show / destroy / cloaked), so going back to the desktop lands within a
/// frame of the window actually going away.
/// </para>
/// <para>
/// Startup grace: the first seconds of a session are spent building widget surfaces, and
/// flipping the theme inside that window would fight the initialization; the state is reported
/// as focused until the grace period elapses.
/// </para>
/// <para>
/// The decision core (<see cref="IsWhitelisted"/>, <see cref="MeaningfullyOnScreen"/>,
/// <see cref="ResolveUnfocusedScreens"/>) is pure and exercised by <c>tests/FocusSwitchChecks</c>.
/// </para>
/// </summary>
public class FocusWatchService : IDisposable
{
    /// <summary>Poll interval of the occupancy observation (the event hooks cover the gaps).</summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(150);

    /// <summary>Initialization grace: widgets finish their first render before the state can flip.</summary>
    private static readonly TimeSpan StartupGrace = TimeSpan.FromSeconds(5);

    /// <summary>How long a PID → process-name lookup is trusted before the cache is dropped
    /// (the walk resolves names for every candidate window on every pass).</summary>
    private static readonly TimeSpan ProcessNameCacheLifetime = TimeSpan.FromSeconds(5);

    /// <summary>Fraction of the window that must lie on a screen for that screen to count as
    /// occupied by it (the center-containing screen always counts).</summary>
    private const double ScreenOverlapRatio = 0.25;

    private readonly IAppSettingsProvider appSettingsProvider;
    private readonly Dictionary<int, string?> processNameCache = [];
    private DateTime processNameCacheStamp;
    private DispatcherTimer? poller;
    private Window? anchor;
    private DateTime attachedAt;
    private FocusStateSnapshot current = FocusStateSnapshot.Focused;

    // Anti-flicker dwell: a candidate that differs from the current state is only applied
    // once it has been observed steadily for the configured duration (0 ms = immediately).
    private DispatcherTimer? pendingSwitchTimer;
    private FocusStateSnapshot? pendingCandidate;

    private IntPtr foregroundHook;
    private IntPtr minimizeHook;
    private IntPtr objectHook;
    private IntPtr cloakHook;
    private WinEventProc? winEventCallback;
    private bool kickQueued;

    public FocusWatchService(IAppSettingsProvider appSettingsProvider)
    {
        this.appSettingsProvider = appSettingsProvider;
    }

    /// <summary>Raised on the UI thread whenever the occupancy state actually switches (after the
    /// configured anti-flicker dwell — immediately when the dwell is 0).</summary>
    public event EventHandler<FocusStateSnapshot>? StateChanged;

    /// <summary>The last reported state; the all-focused default before the first evaluation.</summary>
    public FocusStateSnapshot Current => current;

    /// <summary>Start watching. Any window can serve as the dispatcher / screens anchor.</summary>
    public void Attach(Window anchor)
    {
        this.anchor ??= anchor;
        if (attachedAt == default) attachedAt = DateTime.UtcNow;
        if (poller != null) return;
        poller = new DispatcherTimer(PollInterval, DispatcherPriority.Background, (_, _) => Evaluate());
        poller.Start();
        InstallKickHooks();
    }

    /// <summary>Run one observation pass; a changed state is reported once it holds.</summary>
    public void Evaluate()
    {
        if (!OperatingSystem.IsWindows()) return;

        if (attachedAt != default && DateTime.UtcNow - attachedAt < StartupGrace)
        {
            Report(FocusStateSnapshot.Focused, immediate: true);
            return;
        }

        Report(Observe());
    }

    /// <summary>
    /// Report a candidate that differs from the current state — but only after it has held for
    /// the anti-flicker dwell (<see cref="FocusLinkSettings.SwitchDelayMs"/>): the switch is
    /// scheduled for the dwell's end, and a candidate that re-observes identically keeps its
    /// original deadline instead of being postponed forever. If the desktop falls back to the
    /// current state before the dwell elapses the pending switch is cancelled and nothing
    /// happens — that cancellation is what kills the flicker of transient windows.
    /// </summary>
    private void Report(FocusStateSnapshot candidate, bool immediate = false)
    {
        if (candidate.SameAs(current))
        {
            StopPendingSwitch();
            return;
        }

        var delay = immediate ? TimeSpan.Zero : SwitchDelay();
        if (delay <= TimeSpan.Zero)
        {
            Commit(candidate);
            return;
        }

        // Same candidate already waiting: keep the original deadline (every poll pass
        // re-observes the same state — restarting would push the switch out indefinitely).
        if (pendingCandidate is { } waiting && candidate.SameAs(waiting)) return;

        pendingCandidate = candidate;
        StartPendingSwitch(delay);
    }

    /// <summary>Apply a candidate — the switch the whole feature exists for.</summary>
    private void Commit(FocusStateSnapshot candidate)
    {
        StopPendingSwitch();
        current = candidate;
        try
        {
            StateChanged?.Invoke(this, candidate);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[FocusWatch] handler failed: {ex.Message}");
        }
    }

    private void StartPendingSwitch(TimeSpan delay)
    {
        if (pendingSwitchTimer == null)
        {
            pendingSwitchTimer = new DispatcherTimer(DispatcherPriority.Background);
            pendingSwitchTimer.Tick += (_, _) => CommitPendingSwitch();
        }

        pendingSwitchTimer.Stop();
        pendingSwitchTimer.Interval = delay;
        pendingSwitchTimer.Start();
    }

    private void CommitPendingSwitch()
    {
        if (pendingCandidate is { } candidate) Commit(candidate);
        else pendingSwitchTimer?.Stop();
    }

    private void StopPendingSwitch()
    {
        pendingSwitchTimer?.Stop();
        pendingCandidate = null;
    }

    /// <summary>The configured anti-flicker dwell, clamped to 0–1000 ms (0 = switch immediately).</summary>
    private TimeSpan SwitchDelay()
    {
        var ms = appSettingsProvider.Get().FocusLink?.EffectiveSwitchDelayMs ?? FocusLinkSettings.DefaultSwitchDelayMs;
        return TimeSpan.FromMilliseconds(ms);
    }

    // ---------- Observation (Win32) ----------

    private FocusStateSnapshot Observe()
    {
        try
        {
            var windows = CollectTopLevelWindows();
            var screens = AttachedScreens();
            var walk = ResolveUnfocusedScreens(windows, screens, CurrentWhitelist());

            var unfocused = new HashSet<ScreenRect>();
            foreach (var index in walk.UnfocusedScreenIndices)
                if (index >= 0 && index < screens.Count) unfocused.Add(screens[index]);

            // Display fields — see FocusStateSnapshot's docs. While an application is around the
            // line names the leading application; clicking around must not make it flap.
            var (kind, processName) = walk.LeadingApplication is { Length: > 0 } leading
                ? (ForegroundKind.Application, (string?)leading)
                : ObserveForeground();

            return new FocusStateSnapshot(kind, processName, unfocused.Count > 0, unfocused);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[FocusWatch] observation failed: {ex.Message}");
            return current;
        }
    }

    /// <summary>
    /// Walk the top-level windows from the top of the z-order down and collect their metadata.
    /// <c>EnumWindows</c> yields windows in z-order; cheap gates (visibility, minimized,
    /// cloaked, tool window, title, bounds) run here purely for cost — the pure resolver
    /// re-checks every one of them.
    /// </summary>
    private List<FocusWindow> CollectTopLevelWindows()
    {
        var list = new List<FocusWindow>();
        var ownPid = Environment.ProcessId;

        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd)) return true;
            if (IsIconic(hwnd)) return true;
            if (IsCloaked(hwnd)) return true;
            if ((GetWindowLongW(hwnd, GWL_EXSTYLE) & WS_EX_TOOLWINDOW) != 0) return true;

            var title = GetWindowTitle(hwnd);
            if (string.IsNullOrWhiteSpace(title)) return true;
            if (!TryGetWindowBounds(hwnd, out var bounds)) return true;

            var className = GetClassNameString(hwnd);
            GetWindowThreadProcessId(hwnd, out var pid);
            if (pid <= 0) return true;

            var isOwn = pid == ownPid;
            var processName = isOwn ? null : GetProcessNameCached(pid);
            list.Add(new FocusWindow(className, pid, isOwn, processName, title, bounds));
            return true;
        }, IntPtr.Zero);

        return list;
    }

    /// <summary>Classify the momentary foreground window (display only — never drives switching).</summary>
    private (ForegroundKind Kind, string? ProcessName) ObserveForeground()
    {
        var hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return (ForegroundKind.None, null);

        var className = GetClassNameString(hwnd);
        GetWindowThreadProcessId(hwnd, out var pid);
        var isOwn = pid == Environment.ProcessId;

        // The generic CoreWindow class needs the process to decide — see IsShellWindow.
        var shellProcessName = className == CoreWindowClassName && !isOwn ? GetProcessNameCached(pid) : null;

        if (IsShellWindow(className, shellProcessName)) return (ForegroundKind.Shell, null);

        // Every DeskCanvas window (widgets, panels, the settings window) is the app itself —
        // it neither pins nor prevents any state; the walk skips the whole process.
        if (isOwn) return (ForegroundKind.OwnFurniture, null);

        var processName = shellProcessName ?? GetProcessNameCached(pid);
        return IsWhitelisted(processName, CurrentWhitelist())
            ? (ForegroundKind.Whitelisted, processName)
            : (ForegroundKind.Application, processName);
    }

    private string[] CurrentWhitelist() => appSettingsProvider.Get().FocusLink?.EffectiveWhitelist ?? [];

    /// <summary>The attached screens, in physical pixels (Avalonia reports physical rects).</summary>
    private IReadOnlyList<ScreenRect> AttachedScreens()
    {
        if (anchor?.Screens.All is not { Count: > 0 } all) return [];
        var screens = new List<ScreenRect>(all.Count);
        foreach (var screen in all)
            screens.Add(new ScreenRect(screen.Bounds.X, screen.Bounds.Y, screen.Bounds.Right, screen.Bounds.Bottom));
        return screens;
    }

    /// <summary>Cached <see cref="GetProcessName"/> — the walk resolves a name per candidate
    /// window on every pass and PIDs cannot be recycled within the cache's lifetime.</summary>
    private string? GetProcessNameCached(int pid)
    {
        var now = DateTime.UtcNow;
        if (now - processNameCacheStamp > ProcessNameCacheLifetime)
        {
            processNameCache.Clear();
            processNameCacheStamp = now;
        }

        if (processNameCache.TryGetValue(pid, out var cached)) return cached;
        var name = GetProcessName(pid);
        processNameCache[pid] = name;
        return name;
    }

    // ---------- Decision (pure, testable) ----------

    /// <summary>
    /// Is the foreground process on the user's allowlist? Entries match by process name,
    /// case-insensitively, with or without the <c>.exe</c> suffix.
    /// </summary>
    public static bool IsWhitelisted(string? processName, IReadOnlyList<string>? whitelist)
    {
        if (string.IsNullOrWhiteSpace(processName) || whitelist is null || whitelist.Count == 0)
            return false;

        var candidate = StripExeSuffix(processName.AsSpan().Trim());
        foreach (var entry in whitelist)
        {
            if (string.IsNullOrWhiteSpace(entry)) continue;
            var span = StripExeSuffix(entry.AsSpan().Trim());
            if (span.Equals(candidate, StringComparison.OrdinalIgnoreCase)) return true;
        }

        return false;
    }

    private static ReadOnlySpan<char> StripExeSuffix(ReadOnlySpan<char> name) =>
        name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;

    /// <summary>
    /// Does the window meaningfully occupy this screen? A screen counts when the window overlaps
    /// at least <paramref name="minOverlapRatio"/> of the window's area, or when it contains the
    /// window's center — so a window straddling two screens covers both, while a window whose
    /// corner barely pokes onto the neighbouring screen does not.
    /// </summary>
    public static bool MeaningfullyOnScreen(
        ScreenRect windowBounds,
        ScreenRect screen,
        double minOverlapRatio = ScreenOverlapRatio)
    {
        var overlapWidth = Math.Min(windowBounds.Right, screen.Right) - Math.Max(windowBounds.Left, screen.Left);
        var overlapHeight = Math.Min(windowBounds.Bottom, screen.Bottom) - Math.Max(windowBounds.Top, screen.Top);
        if (overlapWidth > 0 && overlapHeight > 0)
        {
            var windowArea = (double)windowBounds.Width * windowBounds.Height;
            if (windowArea <= 0 || (double)overlapWidth * overlapHeight / windowArea >= minOverlapRatio)
                return true;
        }

        var centerX = (windowBounds.Left + windowBounds.Right) / 2.0;
        var centerY = (windowBounds.Top + windowBounds.Bottom) / 2.0;
        return centerX >= screen.Left && centerX < screen.Right && centerY >= screen.Top && centerY < screen.Bottom;
    }

    /// <summary>
    /// The heart of the feature — walk the top-level windows from the top of the z-order down
    /// and decide, per screen, whether an application window still occupies it.
    /// <list type="bullet">
    /// <item>An <b>application window</b> (not a shell surface, not on the allowlist, not
    /// DeskCanvas itself) keeps every screen it meaningfully covers unfocused.</item>
    /// <item>Everything else is skipped. The walk is about <i>what occupies the desktop</i>,
    /// not about which window is active — clicking the desktop, the taskbar or a widget never
    /// changes the outcome (“怎么点都是失焦”).</item>
    /// <item>Screens no application occupies come out focused — minimizing / closing the last
    /// application flips back immediately.</item>
    /// </list>
    /// The outcome also names the <i>leading</i> application (the first application window met
    /// on the way down) for the status line — not the active window.
    /// </summary>
    public static FocusWalkOutcome ResolveUnfocusedScreens(
        IReadOnlyList<FocusWindow> windowsTopToBottom,
        IReadOnlyList<ScreenRect> screens,
        IReadOnlyList<string>? whitelist)
    {
        var unfocused = new List<int>();
        var marked = new bool[screens.Count];
        string? leading = null;

        foreach (var window in windowsTopToBottom)
        {
            if (!window.IsVisible || window.IsMinimized || window.IsCloaked || window.IsToolWindow) continue;
            if (string.IsNullOrWhiteSpace(window.Title)) continue;

            // Desktop / taskbar / start menu / task switchers: shell surfaces never occupy.
            if (IsShellWindow(window.ClassName, window.ProcessName)) continue;
            // DeskCanvas itself (widgets, panels, the settings window) is invisible to the walk.
            if (window.IsOwnProcess) continue;
            // The allowlist is desktop furniture: it neither pins nor prevents any state.
            if (IsWhitelisted(window.ProcessName, whitelist)) continue;

            var occupiesScreen = false;
            for (var i = 0; i < screens.Count; i++)
            {
                if (!MeaningfullyOnScreen(window.Bounds, screens[i])) continue;
                occupiesScreen = true;
                if (marked[i]) continue;
                marked[i] = true;
                unfocused.Add(i);
            }

            if (occupiesScreen && leading is null && window.ProcessName is { Length: > 0 } name)
                leading = name;
        }

        unfocused.Sort();
        return new FocusWalkOutcome(unfocused, leading);
    }

    /// <summary>Hard shell classes that are always a shell surface — the desktop, both taskbars,
    /// the Win11/Mica shell hosts (task view, alt-tab flyout, tray overflow) and the legacy task
    /// switcher windows.</summary>
    public static bool IsShellWindowClass(string className) => className is
        "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd"
        or "XamlExplorerHostIslandWindow" or "TaskSwitcherWnd" or "ForegroundStaging"
        or "MultitaskingViewFrame" or "TopLevelWindowForOverflowXamlIsland";

    /// <summary>The generic Windows class shared by UWP windows and several shell surfaces.</summary>
    public const string CoreWindowClassName = "Windows.UI.Core.CoreWindow";

    /// <summary>
    /// Is the window a shell surface (desktop / taskbar / start / search)? The generic
    /// <c>Windows.UI.Core.CoreWindow</c> class only counts when the owning process really is a
    /// shell host — regular applications also use that class, and counting them as the desktop
    /// flipped the state "focused" while an app was clearly in front.
    /// </summary>
    public static bool IsShellWindow(string className, string? processName) =>
        IsShellWindowClass(className)
        || (className == CoreWindowClassName
            && processName is not null
            && ShellProcessNames.Contains(processName));

    /// <summary>Processes whose CoreWindow-class windows are shell surfaces (start menu, search,
    /// shell experience hosts, the desktop host).</summary>
    private static readonly HashSet<string> ShellProcessNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "StartMenuExperienceHost", "SearchHost", "ShellExperienceHost", "TextInputHost", "Explorer"
    };

    /// <summary>
    /// Real visual bounds of a window in physical pixels (DWM extended frame excludes the
    /// invisible resize border); falls back to <c>GetWindowRect</c> when DWM cannot answer.
    /// </summary>
    private static bool TryGetWindowBounds(IntPtr hwnd, out ScreenRect rect)
    {
        if (DwmGetWindowAttribute(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, out var native, Marshal.SizeOf<NATIVE_RECT>()) == 0)
        {
            rect = ToRect(native);
            if (rect.Width > 0 && rect.Height > 0) return true;
        }

        if (GetWindowRect(hwnd, out native))
        {
            rect = ToRect(native);
            return rect.Width > 0 && rect.Height > 0;
        }

        rect = default;
        return false;
    }

    private static ScreenRect ToRect(NATIVE_RECT rect) => new(rect.Left, rect.Top, rect.Right, rect.Bottom);

    private static string GetClassNameString(IntPtr hwnd)
    {
        var buffer = new StringBuilder(256);
        return GetClassName(hwnd, buffer, buffer.Capacity) > 0 ? buffer.ToString() : string.Empty;
    }

    private static string GetWindowTitle(IntPtr hwnd)
    {
        var length = GetWindowTextLength(hwnd);
        if (length <= 0) return string.Empty;
        var buffer = new StringBuilder(length + 1);
        GetWindowText(hwnd, buffer, buffer.Capacity);
        return buffer.ToString();
    }

    /// <summary>
    /// The process name (without path or extension) of a PID. The managed query is tried first;
    /// elevated / protected processes that deny it fall back to
    /// <c>QueryFullProcessImageName</c> with <c>PROCESS_QUERY_LIMITED_INFORMATION</c>, which
    /// works for most of them.
    /// </summary>
    private static string? GetProcessName(int pid)
    {
        if (pid <= 0) return null;

        try
        {
            using var process = Process.GetProcessById(pid);
            if (!string.IsNullOrEmpty(process.ProcessName)) return process.ProcessName;
        }
        catch
        {
            // Fall through to the Win32 path.
        }

        var handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (handle == IntPtr.Zero) return null;
        try
        {
            var buffer = new StringBuilder(1024);
            var size = buffer.Capacity;
            if (QueryFullProcessImageName(handle, 0, buffer, ref size))
            {
                var name = Path.GetFileNameWithoutExtension(buffer.ToString());
                if (!string.IsNullOrEmpty(name)) return name;
            }
        }
        catch
        {
            // Ignore — the caller treats a missing name as "not whitelisted".
        }
        finally
        {
            CloseHandle(handle);
        }

        return null;
    }

    /// <summary>
    /// Every running process that owns at least one visible, titled top-level window — the
    /// candidates for the process picker. Shell surfaces and DeskCanvas' own windows are
    /// excluded; windowless processes can never be an application window, so they are not offered.
    /// </summary>
    public static IReadOnlyList<WindowedProcess> EnumerateWindowedProcesses()
    {
        var map = new Dictionary<int, WindowedProcess>();
        var ownPid = Environment.ProcessId;

        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd)) return true;
            if (IsCloaked(hwnd)) return true;

            var className = GetClassNameString(hwnd);
            if (IsShellWindowClass(className)) return true;

            GetWindowThreadProcessId(hwnd, out var pid);
            if (pid <= 0 || pid == ownPid) return true;

            var title = GetWindowTitle(hwnd);
            if (string.IsNullOrWhiteSpace(title)) return true;

            var name = GetProcessName(pid);
            if (string.IsNullOrEmpty(name)) return true;
            if (IsShellWindow(className, name)) return true;

            if (map.TryGetValue(pid, out var existing))
            {
                // Keep the first non-empty window title as the representative one.
                if (string.IsNullOrEmpty(existing.Title))
                    map[pid] = existing with { Title = title };
            }
            else
            {
                map[pid] = new WindowedProcess(name, title);
            }

            return true;
        }, IntPtr.Zero);

        return map.Values.OrderBy(process => process.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static bool IsCloaked(IntPtr hwnd) =>
        DwmGetWindowAttributeInt(hwnd, DWMWA_CLOAKED, out var cloaked, sizeof(int)) == 0 && cloaked != 0;

    // ---------- Event kicks (immediate reaction) ----------

    /// <summary>
    /// Install the WinEvent hooks that make “回到桌面马上聚焦” land within a frame of the
    /// window actually going away (minimize / restore / show / destroy / cloaked). Best effort —
    /// the 150 ms poll covers everything should the hooks ever fail or not be delivered.
    /// </summary>
    private void InstallKickHooks()
    {
        if (!OperatingSystem.IsWindows() || winEventCallback is not null) return;

        winEventCallback = OnWinEvent;
        const uint flags = WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS;
        try
        {
            foregroundHook = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, IntPtr.Zero, winEventCallback, 0, 0, flags);
            minimizeHook = SetWinEventHook(EVENT_SYSTEM_MINIMIZESTART, EVENT_SYSTEM_MINIMIZEEND, IntPtr.Zero, winEventCallback, 0, 0, flags);
            objectHook = SetWinEventHook(EVENT_OBJECT_DESTROY, EVENT_OBJECT_HIDE, IntPtr.Zero, winEventCallback, 0, 0, flags);
            cloakHook = SetWinEventHook(EVENT_OBJECT_CLOAKED, EVENT_OBJECT_UNCLOAKED, IntPtr.Zero, winEventCallback, 0, 0, flags);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[FocusWatch] event hooks unavailable: {ex.Message}");
        }
    }

    /// <summary>
    /// A tracked window changed — evaluate now instead of waiting for the next poll. The kick is
    /// coalesced (a Win+D storm is one evaluation) and marshalled to the UI thread.
    /// </summary>
    private void OnWinEvent(IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint threadId, uint time)
    {
        if (idObject != OBJID_WINDOW || idChild != 0) return;
        if (kickQueued) return;
        kickQueued = true;
        try
        {
            Dispatcher.UIThread.Post(() =>
            {
                kickQueued = false;
                Evaluate();
            }, DispatcherPriority.Normal);
        }
        catch
        {
            kickQueued = false;
        }
    }

    public void Dispose()
    {
        poller?.Stop();
        poller = null;
        StopPendingSwitch();
        pendingSwitchTimer = null;

        // Unhook before dropping the delegate — a live hook with a collected callback would
        // crash on the next event.
        if (foregroundHook != IntPtr.Zero) { UnhookWinEvent(foregroundHook); foregroundHook = IntPtr.Zero; }
        if (minimizeHook != IntPtr.Zero) { UnhookWinEvent(minimizeHook); minimizeHook = IntPtr.Zero; }
        if (objectHook != IntPtr.Zero) { UnhookWinEvent(objectHook); objectHook = IntPtr.Zero; }
        if (cloakHook != IntPtr.Zero) { UnhookWinEvent(cloakHook); cloakHook = IntPtr.Zero; }
        winEventCallback = null;

        GC.SuppressFinalize(this);
    }

    // ---------- Win32 ----------

    private const uint DWMWA_EXTENDED_FRAME_BOUNDS = 9;
    private const uint DWMWA_CLOAKED = 14;
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TOOLWINDOW = 0x00000080;

    private const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    private const uint EVENT_SYSTEM_MINIMIZESTART = 0x0016;
    private const uint EVENT_SYSTEM_MINIMIZEEND = 0x0017;
    private const uint EVENT_OBJECT_DESTROY = 0x8001;
    private const uint EVENT_OBJECT_SHOW = 0x8002;
    private const uint EVENT_OBJECT_HIDE = 0x8003;
    private const uint EVENT_OBJECT_CLOAKED = 0x8017;
    private const uint EVENT_OBJECT_UNCLOAKED = 0x8018;
    private const uint WINEVENT_OUTOFCONTEXT = 0x0000;
    private const uint WINEVENT_SKIPOWNPROCESS = 0x0002;
    private const int OBJID_WINDOW = 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct NATIVE_RECT
    {
        public int Left, Top, Right, Bottom;
    }

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr extraData);

    private delegate void WinEventProc(IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint threadId, uint time);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr extraData);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hwnd);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLongW(IntPtr hwnd, int index);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out NATIVE_RECT rect);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out int processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hwnd, StringBuilder className, int maxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int maxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern IntPtr SetWinEventHook(
        uint eventMin, uint eventMax, IntPtr eventHookModule, WinEventProc callback,
        uint processId, uint threadId, uint flags);

    [DllImport("user32.dll")]
    private static extern bool UnhookWinEvent(IntPtr hook);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint desiredAccess, bool inheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool QueryFullProcessImageName(IntPtr process, int flags, StringBuilder exeName, ref int size);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, uint attribute, out NATIVE_RECT value, int size);

    [DllImport("dwmapi.dll", EntryPoint = "DwmGetWindowAttribute")]
    private static extern int DwmGetWindowAttributeInt(IntPtr hwnd, uint attribute, out int value, int size);
}
