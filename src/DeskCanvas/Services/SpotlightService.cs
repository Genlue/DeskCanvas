using System;
using DeskCanvas.Core.Interfaces;
using DeskCanvas.ViewModels;
using DeskCanvas.Views;

namespace DeskCanvas.Services;

/// <summary>
/// Owns the 聚焦搜索 feature: the global hotkey, the (lazily created) overlay window, and the
/// shortcut catalogue.
/// <para>
/// The service is the only thing that knows the feature is on: the window stays a dumb view, the
/// catalogue stays a folder index, and the settings decide whether a hotkey exists at all. It
/// lives for the whole process (registered as a DI singleton and started from
/// <c>App.OnFrameworkInitializationCompleted</c>), because the hotkey must work even when no
/// widget and no settings window exist.
/// </para>
/// <para>
/// <b>Registration is best-effort by design.</b> <c>RegisterHotKey</c> fails when another process
/// owns the gesture (PowerToys Run, Flow Launcher, …) or Windows reserves it. That is reported
/// through <see cref="Status"/>, never thrown — the rest of the app keeps working and the settings
/// page can explain the conflict.
/// </para>
/// </summary>
public sealed class SpotlightService : IDisposable
{
    /// <summary>Registration id for the launcher's own gesture (unique within this process).</summary>
    private const int HotkeyId = 0x5C01;

    /// <summary>Registration id for the search-bar gesture (the second summon mode).</summary>
    private const int SearchHotkeyId = 0x5C02;

    private readonly IAppSettingsProvider settings;
    private readonly LauncherCatalog catalog;
    private readonly ShellIconService icons;
    private readonly FileSearchService files;
    private readonly FullscreenWatcherService fullscreenWatcher;
    private readonly SpotlightQueryRouter router;
    private readonly SpotlightUsageStore usage;
    private readonly SpotlightClipboardService clipboard;
    private readonly SpotlightExternalResults external;
    private readonly SpotlightWebResults webResults;
    private readonly SpotlightRecentLaunchStore recentLaunches;

    private GlobalHotkeyRegistrar? registrar;
    private SpotlightWindow? window;
    private SpotlightViewModel? viewModel;
    private bool started;
    private bool disposed;
    private int captureDepth;

    public SpotlightService(
        IAppSettingsProvider settings,
        LauncherCatalog catalog,
        ShellIconService icons,
        FileSearchService files,
        FullscreenWatcherService fullscreenWatcher,
        SpotlightQueryRouter router,
        SpotlightUsageStore usage,
        SpotlightClipboardService clipboard,
        SpotlightExternalResults external,
        SpotlightWebResults webResults,
        SpotlightRecentLaunchStore? recentLaunches = null)
    {
        this.settings = settings;
        this.catalog = catalog;
        this.icons = icons;
        this.files = files;
        this.fullscreenWatcher = fullscreenWatcher;
        this.router = router;
        this.usage = usage;
        this.clipboard = clipboard;
        this.external = external;
        this.webResults = webResults;
        this.recentLaunches = recentLaunches ?? new SpotlightRecentLaunchStore();
    }

    /// <summary>Persistent store of items opened through search results.</summary>
    public SpotlightRecentLaunchStore RecentLaunches => recentLaunches;

    /// <summary>The per-entry usage store (settings page: quick keys, pinned cleanup).</summary>
    public SpotlightUsageStore Usage => usage;

    /// <summary>The clipboard history recorder (settings page: clear / retention controls).</summary>
    public SpotlightClipboardService Clipboard => clipboard;

    /// <summary>Registry for plugin-provided external result providers.</summary>
    public SpotlightExternalResults External => external;

    /// <summary>Why the hotkey is (not) live. Drives the settings page's status line.</summary>
    public enum HotkeyStatus
    {
        /// <summary>The feature is switched off.</summary>
        Disabled,

        /// <summary>Registered and ready.</summary>
        Registered,

        /// <summary>The stored gesture text could not be parsed.</summary>
        InvalidGesture,

        /// <summary>Another process owns the gesture, or Windows reserves it.</summary>
        Conflict,

        /// <summary>The Win32 message window could not be created.</summary>
        Unavailable,
    }

    public HotkeyStatus Status { get; private set; } = HotkeyStatus.Disabled;

    /// <summary>Registration state of the <b>search-bar</b> gesture (the second summon mode).</summary>
    public HotkeyStatus SearchStatus { get; private set; } = HotkeyStatus.Disabled;

    /// <summary>Raised whenever <see cref="Status"/> / <see cref="Gesture"/> change (settings page).</summary>
    public event Action? StateChanged;

    /// <summary>
    /// Raised when the overlay asks for the settings window (forwarded from the view model, which
    /// only exists once the overlay was first summoned — App subscribes to this service-level
    /// event so the wiring does not depend on the lazy window).
    /// </summary>
    public event Action? OpenSettingsRequested;

    /// <summary>
    /// True while a hotkey recorder is capturing. The gesture is unregistered for the duration:
    /// otherwise pressing the very combination being recorded would fire the launcher and steal
    /// focus from the recorder (the same reason the Shortcuts editor suspends its bindings).
    /// </summary>
    public bool IsCapturing
    {
        get { lock (gate) return captureDepth > 0; }
    }

    private readonly object gate = new();

    /// <summary>The gesture currently registered (null when nothing is registered).</summary>
    public GlobalHotkeyGesture? Gesture { get; private set; }

    /// <summary>The search-bar gesture currently registered (null when nothing is registered).</summary>
    public GlobalHotkeyGesture? SearchGesture { get; private set; }

    /// <summary>The overlay window, created on first use. Exposed for the tray menu / checks.</summary>
    public SpotlightWindow? Window => window;

    /// <summary>
    /// Wire the feature up: index the folder, then register the hotkey if the user enabled it.
    /// Safe to call once from application start-up.
    /// </summary>
    public void Start()
    {
        if (disposed || started) return;
        started = true;

        var launcher = settings.Get().EffectiveLauncher;

        // Index now so the first summon paints immediately. The catalogue creates the folder when
        // it is missing, which is also what makes the empty state's "open folder" button useful.
        catalog.Start(launcher.EffectiveFolder);

        // The machine-wide clipboard listener is a privacy decision (opt-in setting): it only
        // runs when the user enabled clipboard history.
        if (launcher.ClipboardHistory) clipboard.Start();

        settings.DataChanged += OnSettingsChanged;
        ApplyHotkey();
    }

    /// <summary>Toggle the overlay (used by the hotkey and, later, by a tray entry).</summary>
    /// <param name="bypassBlacklist">
    /// Skip the blacklist check. Used by the <c>--spotlight</c> development flag and by the
    /// settings page's preview button — an explicit user action is not "the hotkey fired while a
    /// blacklisted app was in front".
    /// </param>
    /// <param name="mode">Which surface the gesture opens (app list or bare search bar).</param>
    public void Toggle(bool bypassBlacklist = false, Views.SpotlightSummonMode mode = Views.SpotlightSummonMode.AppList)
    {
        if (disposed) return;

        var launcher = settings.Get().EffectiveLauncher;

        // When "屏蔽全屏进程" (BlockFullscreen) is enabled, suppress summon if a true fullscreen app/game owns the screen.
        // Ordinary maximized windows (e.g. browser, editor) never suppress the launcher.
        if (launcher.BlockFullscreen && !bypassBlacklist && window is not { IsVisible: true })
        {
            if (FullscreenWatcherService.IsForegroundFullscreen(out var fsProcess))
            {
                BlockedProcess = fsProcess;
                GlassDiagnostics.Event($"[spotlight] {mode} summon skipped — a fullscreen app ('{fsProcess ?? "unknown"}') owns the screen");
                return;
            }
        }

        if (!bypassBlacklist && IsForegroundBlacklisted(out var blocked))
        {
            BlockedProcess = blocked;
            GlassDiagnostics.Event($"[spotlight] {mode} summon skipped — '{blocked}' is on the blacklist");
            return;
        }

        BlockedProcess = null;
        EnsureWindow().Toggle(mode);
    }

    /// <summary>
    /// The process that suppressed the most recent summon, or <c>null</c>. The settings page reads
    /// this to explain why the hotkey "did nothing" instead of leaving the user guessing.
    /// </summary>
    public string? BlockedProcess { get; private set; }

    /// <summary>
    /// True when the foreground window belongs to a blacklisted process. An unreadable foreground
    /// process (elevated window, access denied) counts as "not blacklisted" — suppressing the
    /// launcher on a guess would be worse than showing it.
    /// </summary>
    private bool IsForegroundBlacklisted(out string? processName)
    {
        processName = ForegroundProcess.GetProcessName();
        if (processName == null) return false;

        return settings.Get().EffectiveLauncher.IsBlacklisted(processName);
    }

    /// <summary>Re-register the hotkey after the user changed it (or after a conflict cleared).</summary>
    public void Reapply() => ApplyHotkey();

    /// <summary>
    /// Stop listening while a recorder captures a new gesture. Nesting is reference-counted; the
    /// registration comes back on the last <see cref="EndCapture"/>.
    /// </summary>
    public void BeginCapture()
    {
        lock (gate)
        {
            if (captureDepth++ != 0) return;
        }

        UnregisterHotkey();
    }

    /// <summary>Resume listening and re-register the (possibly new) gesture.</summary>
    public void EndCapture()
    {
        lock (gate)
        {
            if (captureDepth == 0) return;
            if (--captureDepth != 0) return;
        }

        ApplyHotkey();
    }

    private void ApplyHotkey()
    {
        UnregisterHotkey();

        var launcher = settings.Get().EffectiveLauncher;
        GlassDiagnostics.Event(
            $"[spotlight] apply gestures: enabled={launcher.Enabled} " +
            $"list='{launcher.EffectiveHotkey}' search='{launcher.EffectiveSearchHotkey}'");

        if (!launcher.Enabled)
        {
            Status = HotkeyStatus.Disabled;
            SearchStatus = HotkeyStatus.Disabled;
            StateChanged?.Invoke();
            return;
        }

        if (!GlobalHotkeyGesture.TryParse(launcher.EffectiveHotkey, out var gesture, out _))
        {
            GlassDiagnostics.Event($"[spotlight] list gesture '{launcher.EffectiveHotkey}' does not parse");
            Status = HotkeyStatus.InvalidGesture;
            SearchStatus = HotkeyStatus.InvalidGesture;
            StateChanged?.Invoke();
            return;
        }

        registrar ??= CreateRegistrar();
        if (registrar == null)
        {
            GlassDiagnostics.Event("[spotlight] no hotkey window — the OS refused the message-only window");
            Status = HotkeyStatus.Unavailable;
            SearchStatus = HotkeyStatus.Unavailable;
            StateChanged?.Invoke();
            return;
        }

        if (!registrar.Register(HotkeyId, gesture.Modifiers, gesture.VirtualKey))
        {
            GlassDiagnostics.Event(
                $"[spotlight] RegisterHotKey('{gesture.Text}') failed (win32 {registrar.LastError}) — " +
                "another process owns it");
            Status = HotkeyStatus.Conflict;
            Gesture = null;
            SearchStatus = HotkeyStatus.Conflict;
            StateChanged?.Invoke();
            return;
        }

        Gesture = gesture;
        Status = HotkeyStatus.Registered;

        // The second gesture is independent: one combination cannot mean two modes, so an equal
        // gesture is reported as a conflict instead of silently registering the same key twice
        // (the registrar would refuse it anyway, with no explanation for the user).
        SearchGesture = null;
        if (launcher.SearchHotkeyCollidesWithList)
        {
            SearchStatus = HotkeyStatus.Conflict;
        }
        else if (!GlobalHotkeyGesture.TryParse(launcher.EffectiveSearchHotkey, out var searchGesture, out _))
        {
            SearchStatus = HotkeyStatus.InvalidGesture;
        }
        else if (registrar.Register(SearchHotkeyId, searchGesture.Modifiers, searchGesture.VirtualKey))
        {
            SearchGesture = searchGesture;
            SearchStatus = HotkeyStatus.Registered;
        }
        else
        {
            SearchStatus = HotkeyStatus.Conflict;
        }

        // One line per (re)registration: which gesture opens which surface, and whether Windows
        // took it. A conflicting gesture is the other silent "nothing happens".
        GlassDiagnostics.Event(
            $"[spotlight] gestures: list='{launcher.EffectiveHotkey}' {Status}, " +
            $"search='{launcher.EffectiveSearchHotkey}' {SearchStatus}");

        StateChanged?.Invoke();
    }

    private GlobalHotkeyRegistrar? CreateRegistrar()
    {
        try
        {
            var created = new GlobalHotkeyRegistrar();
            created.Pressed += OnHotkeyPressed;
            return created;
        }
        catch
        {
            return null;
        }
    }

    private void OnHotkeyPressed(int registrationId)
    {
        try
        {
            if (registrationId == HotkeyId) Toggle(mode: Views.SpotlightSummonMode.AppList);
            else if (registrationId == SearchHotkeyId) Toggle(mode: Views.SpotlightSummonMode.Search);
        }
        catch (Exception ex)
        {
            // The registrar's WndProc cannot let an exception unwind into native code, so it
            // swallows them — which turns a failed summon into "the hotkey does nothing", with no
            // trace anywhere. Record it instead: this is the only line that explains it.
            GlassDiagnostics.Event(
                $"[spotlight] hotkey {registrationId} threw {ex.GetType().Name}: {ex.Message}");
        }
    }

    private void UnregisterHotkey()
    {
        if (registrar == null) return;
        registrar.Unregister(HotkeyId);
        registrar.Unregister(SearchHotkeyId);
        Gesture = null;
        SearchGesture = null;
    }

    /// <summary>Build the overlay on first use so start-up stays cheap.</summary>
    private SpotlightWindow EnsureWindow()
    {
        if (window != null) return window;

        viewModel = new SpotlightViewModel(router, catalog, icons, usage, clipboard, files, settings,
            new SpotlightPreviewService(icons), webResults, recentLaunches);
        viewModel.Start();
        viewModel.OpenSettingsRequested += () => OpenSettingsRequested?.Invoke();

        // Alt-Tab removal happens in the window's Opened handler: the native handle does not
        // exist before the first Show, so calling it here would silently do nothing.
        window = new SpotlightWindow(viewModel, settings);
        return window;
    }

    private void OnSettingsChanged(object? sender, Core.Models.Settings.AppSettings? oldData,
        Core.Models.Settings.AppSettings newData)
    {
        var previous = oldData?.EffectiveLauncher;
        var current = newData.EffectiveLauncher;

        // Folder changes re-index; everything else only needs the hotkey re-applied.
        if (previous == null || !string.Equals(previous.EffectiveFolder, current.EffectiveFolder, StringComparison.OrdinalIgnoreCase))
        {
            catalog.Start(current.EffectiveFolder);
        }

        if (previous == null
            || previous.Enabled != current.Enabled
            || !string.Equals(previous.EffectiveHotkey, current.EffectiveHotkey, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(previous.EffectiveSearchHotkey, current.EffectiveSearchHotkey, StringComparison.OrdinalIgnoreCase))
        {
            ApplyHotkey();
        }

        // 剪贴板历史: the machine-wide listener follows its opt-in toggle; retention and the
        // exclusion list are re-read by RefreshSettings without touching the listener.
        if (previous != null && previous.ClipboardHistory != current.ClipboardHistory)
        {
            if (current.ClipboardHistory) clipboard.Start();
            else clipboard.Stop();
            clipboard.RefreshSettings();
        }
        else if (previous != null
            && (previous.ClipboardRetentionDays != current.ClipboardRetentionDays
                || !previous.EffectiveClipboardExcluded.SequenceEqual(
                    current.EffectiveClipboardExcluded, StringComparer.OrdinalIgnoreCase)))
        {
            clipboard.RefreshSettings();
        }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;

        settings.DataChanged -= OnSettingsChanged;
        UnregisterHotkey();

        if (registrar != null)
        {
            registrar.Pressed -= OnHotkeyPressed;
            registrar.Dispose();
            registrar = null;
        }

        viewModel?.Dispose();
        viewModel = null;

        window?.Close();
        window = null;
    }
}
