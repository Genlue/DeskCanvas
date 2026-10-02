using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using Avalonia.Threading;
using DeskCanvas.Core.Interfaces;
using DeskCanvas.Core.Models;
using DeskCanvas.Core.Models.Settings;
using DeskCanvas.Core.Services;
using DeskCanvas.Views;

namespace DeskCanvas.Services;

/// <summary>
/// Lifecycle and state of the right-hand widget sidebar.
/// <para>
/// One sidebar window per screen at most. The service selects the target screen from the mouse
/// position, persists the per-screen width and widget list, and closes/unbinds every sidebar
/// widget before a profile switch so an outgoing profile can never write into the incoming one.
/// </para>
/// </summary>
public interface ISidebarService
{
    /// <summary>Is a sidebar window currently visible?</summary>
    bool IsOpen { get; }

    /// <summary>Toggle the sidebar on the screen the mouse is on.</summary>
    void ToggleForCursorScreen();

    /// <summary>Show the sidebar on <paramref name="screen"/> (creating it if needed).</summary>
    void Show(Screen screen);

    /// <summary>Hide every sidebar window (focus loss, Esc, hotkey).</summary>
    void Hide(string reason);

    /// <summary>Add a widget to the sidebar of <paramref name="screen"/> with a fresh instance id.</summary>
    /// <param name="screen">Target screen.</param>
    /// <param name="layout">The widget's layout (settings, content scale).</param>
    /// <param name="columns">Grid columns the widget should occupy (the gallery passes the widget's
    /// own declared default span).</param>
    /// <param name="rows">Grid rows the widget should occupy.</param>
    void AddWidget(Screen screen, WidgetLayout layout, int columns, int rows);

    /// <summary>Remove a sidebar widget by its stable instance id.</summary>
    void RemoveWidget(string screenId, string instanceId);

    /// <summary>Move a sidebar widget to the final index <paramref name="targetIndex"/>.</summary>
    void Reorder(string screenId, string instanceId, int targetIndex);

    /// <summary>Persist the sidebar width for a screen.</summary>
    void UpdateWidth(string screenId, double widthDip);

    /// <summary>Persist the sidebar's grid column count for a screen.</summary>
    void UpdateColumns(string screenId, int columns);

    /// <summary>Close every sidebar window immediately (profile switch / shutdown).</summary>
    void CloseAll();
}

/// <inheritdoc cref="ISidebarService" />
public sealed class SidebarService : ISidebarService, ISidebarHostCallbacks
{
    private readonly IAppSettingsProvider appSettingsProvider;
    private readonly DisplayMonitorService displayMonitor;
    private readonly WidgetFactory widgetFactory;
    private readonly ILayoutProvider? layoutProvider;
    private readonly Func<ProfileService>? profileService;
    private readonly Func<Settings>? settingsWindow;

    private readonly Dictionary<string, SidebarWindow> windows = new(StringComparer.Ordinal);
    private readonly DispatcherTimer policyTimer;
    private int policyHits;

    public SidebarService(IAppSettingsProvider appSettingsProvider, DisplayMonitorService displayMonitor,
        WidgetFactory widgetFactory, ILayoutProvider? layoutProvider = null, Func<ProfileService>? profileService = null,
        Func<Settings>? settingsWindow = null,
        FullscreenWatcherService? fullscreenWatcher = null)
    {
        this.appSettingsProvider = appSettingsProvider;
        this.displayMonitor = displayMonitor;
        this.widgetFactory = widgetFactory;
        this.layoutProvider = layoutProvider;
        this.profileService = profileService;
        this.settingsWindow = settingsWindow;

        // The sidebar's glass samples the live screen through the shared sampling service, which
        // discovers its consumers from the glass surfaces themselves (the sidebar window's cards
        // and the desktop widgets both register — see ScreenCaptureService.RefreshDemand). The
        // sidebar's own rectangle is kept out of the image by the OS-level capture exclusion
        // (SidebarZOrder.TryExcludeFromCapture), so the glass never refracts itself.

        // A true fullscreen app (exclusive or borderless) or a blocked process coming to the
        // foreground pulls an already-open sidebar back — ordinary maximized windows do not.
        policyTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(1200), DispatcherPriority.Background,
            (_, _) => EnforcePolicy());

        // A fullscreen app is about to take the whole screen: release what the sidebar widgets
        // hold (timers, decoded frames, pre-rendered material) exactly like the desktop widgets,
        // and rebuild it when the desktop comes back. Purely a resource reaction — whether the
        // sidebar is *visible* is the overlay policy's business (see EnforcePolicy).
        if (fullscreenWatcher != null) fullscreenWatcher.FullscreenChanged += OnFullscreenChanged;
    }

    /// <summary>The effective sidebar settings (hotkey, width range, blocking policy).</summary>
    public SidebarSettings SidebarSettings => appSettingsProvider.Get().EffectiveSidebar;

    /// <summary>
    /// The live app settings. The sidebar window reads the active material through this (毛玻璃
    /// needs the OS acrylic backdrop, the rendered materials must stay per-pixel transparent) and
    /// follows it while it is open.
    /// </summary>
    public IAppSettingsProvider AppSettings => appSettingsProvider;

    /// <inheritdoc />
    public bool IsOpen => windows.Values.Any(window => window.IsVisible);

    /// <summary>The window currently on top, if any (used by diagnostics/settings).</summary>
    public SidebarWindow? Current => windows.Values.LastOrDefault(window => window.IsVisible);

    /// <inheritdoc />
    public void ToggleForCursorScreen()
    {
        try
        {
            ToggleForCursorScreenCore();
        }
        catch (Exception ex)
        {
            // Never let a summon failure vanish silently — it is written to the glass trace.
            GlassDiagnostics.Failure(ex);
        }
    }

    private void ToggleForCursorScreenCore()
    {
        var attached = ResolveSummonScreen(CursorAttached());
        if (attached?.Config == null)
        {
            GlassDiagnostics.Event("[Sidebar] toggle ignored: no attached screen with a configuration");
            return;
        }

        GlassDiagnostics.Event($"[Sidebar] toggle on screen '{attached.Config.Id}' (open={IsOpen})");

        // A window that is still sliding out counts as "reopen", not as "hide": otherwise the
        // second press of a double press cancels what the first one just did.
        if (windows.TryGetValue(attached.Config.Id, out var open) && open.IsVisible && !open.IsClosing)
        {
            Hide("toggle");
            return;
        }

        ShowAttached(attached, attached.Config.Id);
    }

    /// <inheritdoc />
    public void Show(Screen screen)
    {
        var attached = ResolveSummonScreen(displayMonitor.Find(screen) ?? CursorAttached());
        if (attached?.Config == null) return;
        ShowAttached(attached, attached.Config.Id);
    }

    /// <summary>Show the sidebar on the screen the mouse is currently on (tray / settings).</summary>
    public void ShowForCursorScreen()
    {
        var attached = ResolveSummonScreen(CursorAttached());
        if (attached?.Config == null) return;
        ShowAttached(attached, attached.Config.Id);
    }

    /// <summary>Empty the sidebar of the screen the mouse is currently on (tray / settings).</summary>
    public void ClearForCursorScreen()
    {
        var attached = CursorAttached();
        if (attached?.Config == null) return;
        Mutate(attached.Config.Id, sidebar => sidebar with { Widgets = [] }, rebuild: true);
    }

    /// <summary>The screen id the mouse is currently on (settings page).</summary>
    public string? CursorScreenId() => CursorAttached()?.Config?.Id;

    private void ShowAttached(AttachedScreen attached, string screenId)
    {
        if (windows.TryGetValue(screenId, out var existing))
        {
            if (!existing.IsVisible) existing.Show();
            existing.PlayOpen();
            SidebarZOrder.MakeToolWindow(existing);
            existing.ApplyCaptureExclusion();
            ScreenCaptureService.RefreshDemand();
            RefreshCaptureTargets();
            return;
        }

        var widthDip = SidebarRules.ResolveWidth(
            layoutProvider?.Get().FindById(screenId)?.EffectiveSidebar,
            SidebarSettings,
            ScreenWidthDip(attached));

        var window = new SidebarWindow(this, attached, widthDip);
        windows[screenId] = window;

        window.Closed += (_, _) =>
        {
            if (windows.TryGetValue(screenId, out var tracked) && ReferenceEquals(tracked, window))
                windows.Remove(screenId);
            window.DetachHosts();
            RefreshCaptureTargets();
            if (!IsOpen) ScreenCaptureService.RefreshDemand();
        };

        GlassDiagnostics.Event($"[Sidebar] showing on '{screenId}', width={widthDip:0.##} DIP, entries={EntriesFor(screenId).Count}");
        window.Show();
        window.PlayOpen();
        window.ApplyCaptureExclusion();
        ScreenCaptureService.RefreshDemand();
        RefreshCaptureTargets();
        policyTimer.Start();
    }

    /// <inheritdoc />
    public void Hide(string reason)
    {
        foreach (var (screenId, window) in windows.ToList())
        {
            var closing = window;
            closing.PlayClose(() =>
            {
                if (windows.TryGetValue(screenId, out var tracked) && ReferenceEquals(tracked, closing))
                    windows.Remove(screenId);
                closing.DetachHosts();
                if (!IsOpen) { ScreenCaptureService.RefreshDemand(); policyTimer.Stop(); }
            });
        }
    }

    /// <inheritdoc />
    public void CloseAll()
    {
        foreach (var window in windows.Values.ToList())
        {
            try
            {
                window.DetachHosts();
                window.Close();
            }
            catch
            {
                // A window that already closed itself is not an error here.
            }
        }

        windows.Clear();
        policyTimer.Stop();
        RefreshCaptureTargets();
        ScreenCaptureService.RefreshDemand();
    }

    /// <summary>
    /// Close sidebars whose screen is no longer attached (unplugged display). Their stored layout
    /// stays on disk, so replugging recreates them in place.
    /// </summary>
    public void ValidateScreens()
    {
        var attachedIds = displayMonitor.Attached
            .Select(attached => attached.Config?.Id)
            .Where(id => id != null)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var (screenId, window) in windows.ToList())
        {
            if (attachedIds.Contains(screenId)) continue;
            window.DetachHosts();
            window.Close();
            windows.Remove(screenId);
        }

        if (!IsOpen)
        {
            policyTimer.Stop();
            RefreshCaptureTargets();
            ScreenCaptureService.RefreshDemand();
        }
    }

    /// <inheritdoc />
    public void AddWidget(Screen screen, WidgetLayout layout, int columns, int rows)
    {
        if (layoutProvider == null) return;

        var attached = EnsureScreen(displayMonitor.Find(screen) ?? CursorAttached());
        if (attached?.Config == null) return;
        var screenId = attached.Config.Id;

        var screens = layoutProvider.Get();
        var screenLayout = screens.FindById(screenId);
        if (screenLayout == null) return;

        var sidebar = screenLayout.EffectiveSidebar;

        // The span is stored explicitly, so the sidebar never has to guess it back out of a pixel
        // size — guessing was what turned a 4×2 aggregate into an 11×6 (the size came from the
        // desktop's manual grid cell, which is several times the widget margin the span was
        // recovered with). The pixel size is still written, made coherent with the span, so any
        // consumer that derives a span from pixels lands on the same numbers.
        var fitted = SidebarRules.FitSpan((columns, rows), [], sidebar.EffectiveColumns);

        var entry = new SidebarWidgetEntry(
            Guid.NewGuid().ToString("N"),
            Normalize(layout, fitted.Columns, fitted.Rows),
            sidebar.Items.Count,
            fitted.Columns,
            fitted.Rows);

        var entries = SidebarRules.Renumber([.. sidebar.Items, entry]);

        layoutProvider.Save(screens.WithScreen(screenLayout with
        {
            Sidebar = sidebar with { Widgets = entries }
        }));

        if (!windows.TryGetValue(screenId, out var window))
        {
            ShowAttached(attached, screenId);
            return;
        }

        window.Rebuild();
        if (!window.IsVisible) { window.Show(); window.PlayOpen(); }
    }

    /// <inheritdoc />
    public void RemoveWidget(string screenId, string instanceId) => Mutate(screenId, sidebar =>
    {
        var entries = sidebar.Items.Where(entry => entry.InstanceId != instanceId).ToList();
        return entries.Count == sidebar.Items.Count ? sidebar : sidebar with { Widgets = SidebarRules.Renumber(entries) };
    }, rebuild: true);

    /// <inheritdoc />
    public void Reorder(string screenId, string instanceId, int targetIndex) => Mutate(screenId, sidebar =>
    {
        var next = SidebarRules.Reorder(sidebar.Ordered, instanceId, targetIndex);
        return sidebar with { Widgets = next };
    }, rebuild: false, reorder: (screenId, instanceId, targetIndex));

    /// <inheritdoc />
    public void UpdateWidth(string screenId, double widthDip) => Mutate(screenId, sidebar =>
    {
        var attached = displayMonitor.Attached.FirstOrDefault(a => a.Config?.Id == screenId);
        var max = attached != null ? ScreenWidthDip(attached) : double.MaxValue;
        return sidebar with { WidthDip = SidebarRules.ClampWidth(widthDip, SidebarSettings, max) };
    }, rebuild: false);

    /// <inheritdoc />
    public void UpdateColumns(string screenId, int columns) => Mutate(screenId, sidebar =>
        sidebar with { Columns = Math.Clamp(columns, SidebarLayout.MinColumns, SidebarLayout.MaxColumns) },
        rebuild: false, relayout: true);

    private void Mutate(string screenId, Func<SidebarLayout, SidebarLayout> edit, bool rebuild,
        (string ScreenId, string InstanceId, int TargetIndex)? reorder = null, bool relayout = false)
    {
        if (layoutProvider == null) return;

        var screens = layoutProvider.Get();
        var screen = screens.FindById(screenId);
        if (screen == null) return;

        var updated = edit(screen.EffectiveSidebar);
        layoutProvider.Save(screens.WithScreen(screen with { Sidebar = updated }));

        if (!windows.TryGetValue(screenId, out var window)) return;

        if (reorder is { } target)
            window.ApplyReorder(target.InstanceId, target.TargetIndex);
        if (rebuild)
            window.Rebuild();
        else if (relayout)
            window.RelayoutGrid();
    }

    /// <summary>
    /// The sidebar entries of a screen, read <b>live</b> from the layout provider.
    /// <para>
    /// <see cref="AttachedScreen.Config"/> is a snapshot taken at the last monitor poll, so a
    /// rebuild driven by it re-creates the entries that were just added/removed — which is why a
    /// removal only became visible after closing and reopening the sidebar.
    /// </para>
    /// </summary>
    public IReadOnlyList<SidebarWidgetEntry> EntriesFor(string screenId) =>
        layoutProvider?.Get().FindById(screenId)?.EffectiveSidebar.Ordered ?? [];

    /// <summary>The screen's stored configuration, read live from the layout provider.</summary>
    public ScreenLayout? ScreenConfigFor(string screenId) => layoutProvider?.Get().FindById(screenId);

    /// <summary>Create the host card for a sidebar entry (used by <see cref="SidebarWindow"/>).</summary>
    public SidebarWidgetHost CreateHost(string screenId, SidebarWidgetEntry entry, SidebarWindow window) =>
        new(entry,
            new SidebarWidgetLayoutProvider(layoutProvider!, screenId, entry.InstanceId, entry.Layout),
            widgetFactory.Runtime,
            appSettingsProvider,
            profileService?.Invoke(),
            settingsWindow ?? (() => throw new InvalidOperationException("No settings window available")),
            this,
            window);

    /// <inheritdoc />
    public void RequestRemove(string screenId, string instanceId) => RemoveWidget(screenId, instanceId);

    // ---------- Helpers ----------

    /// <summary>
    /// The stored layout of a new sidebar entry. X/Y are meaningless in the sidebar (the grid
    /// arranges it), and the pixel size is derived from the span with the same unit
    /// <c>WidgetSurfaceMetrics.SpanFromSize</c> reads it back with — so the pixels and the span
    /// agree instead of encoding two different sizes.
    /// </summary>
    private WidgetLayout Normalize(WidgetLayout layout, int columns, int rows)
    {
        var dimensions = appSettingsProvider.Get().Dimensions;
        var unit = Math.Max(1.0, (double)dimensions.Size + dimensions.Margin);
        return layout with
        {
            X = 0,
            Y = 0,
            Width = (int)Math.Round(Math.Max(1, columns) * unit),
            Height = (int)Math.Round(Math.Max(1, rows) * unit)
        };
    }

    private static double ScreenWidthDip(AttachedScreen attached)
    {
        var scaling = attached.Screen.Scaling <= 0 ? 1.0 : attached.Screen.Scaling;
        return attached.Screen.Bounds.Width / scaling;
    }

    /// <summary>
    /// Return an <see cref="AttachedScreen"/> that is guaranteed to carry a stored configuration,
    /// creating one for a brand-new screen. The original record is stale after
    /// <see cref="DisplayMonitorService.EnsureConfig"/> (it still has a null Config), so the screen
    /// is re-resolved — passing the stale record to a sidebar window would null-deref on creation.
    /// </summary>
    private AttachedScreen? EnsureScreen(AttachedScreen? attached)
    {
        if (attached == null) return null;
        if (attached.Config != null) return attached;
        if (layoutProvider == null) return null;
        displayMonitor.EnsureConfig(attached);
        return displayMonitor.Find(attached.Screen) ?? attached;
    }

    private AttachedScreen? CursorAttached()
    {
        // The attached list is refreshed on a 1.5 s poll; refresh it eagerly so the very first
        // hotkey press after launch (or right after a monitor change) still finds its screen.
        displayMonitor.Refresh();
        if (displayMonitor.Attached.Count == 0) return null;

        if (!OperatingSystem.IsWindows() || !GetCursorPos(out var point))
            return BestScreen(displayMonitor.Attached);

        var pixel = new PixelPoint(point.X, point.Y);

        // A remote-desktop / virtual-display driver can mirror a real screen, and both then report
        // the cursor's rectangle — the first match used to win, which summoned the sidebar onto the
        // invisible mirror and read as "the hotkey stopped working". Every candidate under the
        // cursor is ranked instead of taken in list order.
        AttachedScreen? best = null;
        foreach (var attached in displayMonitor.Attached)
        {
            if (!attached.Screen.Bounds.Contains(pixel)) continue;
            best = BetterScreen(best, attached);
        }

        return best ?? BestScreen(displayMonitor.Attached);
    }

    /// <summary>
    /// The best screen to host a sidebar: one the user has <b>configured</b>, that is primary, and
    /// that has a work area to be pinned to — in that order. A virtual/mirror screen a remote-desktop
    /// driver added fails all three, which is what keeps a summon from landing on it.
    /// </summary>
    private static int ScreenRank(AttachedScreen attached)
    {
        var area = attached.Screen.WorkingArea;
        var rank = 0;
        if (attached.Config != null) rank += 4;
        if (attached.Screen.IsPrimary) rank += 2;
        if (area.Width > 0 && area.Height > 0) rank += 1;
        // Tie-break on size, so two equally unconfigured screens do not resolve by list order alone.
        return rank * 1_000_000 + Math.Clamp(area.Width, 0, 999_999);
    }

    private static AttachedScreen? BetterScreen(AttachedScreen? best, AttachedScreen candidate) =>
        best == null || ScreenRank(candidate) > ScreenRank(best) ? candidate : best;

    private static AttachedScreen? BestScreen(IEnumerable<AttachedScreen> candidates)
    {
        AttachedScreen? best = null;
        foreach (var attached in candidates) best = BetterScreen(best, attached);
        return best;
    }

    /// <summary>
    /// Resolve the screen a summon lands on: create a configuration for a brand-new one, and fall
    /// back to the best <i>configured</i> screen when that is not possible.
    /// </summary>
    private AttachedScreen? ResolveSummonScreen(AttachedScreen? preferred)
    {
        var attached = EnsureScreen(preferred);
        if (attached?.Config != null) return attached;

        // A screen that just appeared (a remote-desktop driver's virtual display, a monitor mid
        // hot-plug) may not be configurable yet. Swallowing the hotkey here read as "the sidebar
        // stopped coming up"; a configured screen is always a better answer than silence.
        return BestScreen(displayMonitor.Attached.Where(candidate => candidate.Config != null));
    }

    /// <summary>
    /// Release (or rebuild) what every sidebar widget's content holds while a fullscreen
    /// application owns the screen.
    /// <para>
    /// This is a <b>resource</b> reaction, not a visibility one: suspending is what hands the
    /// memory back to the game/video, and it happens whether or not the sidebar is on screen.
    /// Whether the overlay may stay visible is decided separately by <see cref="EnforcePolicy"/>
    /// (true fullscreen + the user's process list) — an ordinary maximized window suspends
    /// nothing here, because the watcher only reports a fullscreen <i>cover</i>.
    /// </para>
    /// </summary>
    private void OnFullscreenChanged(object? sender, bool isFullscreen)
    {
        foreach (var window in windows.Values)
            window.SetContentSuspended(isFullscreen);
    }

    /// <summary>
    /// Pull the sidebar back when the overlay policy no longer allows it: a true fullscreen
    /// foreground app, or a process in the user's block list coming to the foreground.
    /// </summary>
    private void EnforcePolicy()
    {
        if (!IsOpen)
        {
            policyHits = 0;
            return;
        }

        var policy = SidebarOverlayPolicy.From(SidebarSettings);
        var fullscreen = FullscreenWatcherService.IsForegroundTrueFullscreen();
        var process = FullscreenWatcherService.ForegroundProcessName();

        if (policy.Allows(fullscreen, process))
        {
            policyHits = 0;
            return;
        }

        // Two consecutive positive polls: a transient foreground change (a menu, a tooltip owner,
        // an overlay appearing for one tick) must not pull the sidebar back.
        if (++policyHits < 2) return;
        policyHits = 0;

        GlassDiagnostics.Event($"[Sidebar] policy pulled the sidebar back (fullscreen={fullscreen}, foreground='{process}')");
        Hide("policy");
    }

    /// <summary>
    /// Re-read the sampling demand: the sidebar moved, resized or changed which monitor it is on, so
    /// the rectangles the glass sampler has to keep out of its own frame are stale.
    /// <para>
    /// The list of monitors to sample is no longer published from here — the sampling service
    /// discovers its consumers from the live glass surfaces themselves, so a screen with desktop
    /// widgets but no sidebar is sampled too (see <see cref="ScreenCaptureService.RefreshDemand"/>).
    /// This is just the nudge for the changed geometry.
    /// </para>
    /// </summary>
    public void RefreshCaptureTargets() => ScreenCaptureService.RefreshDemand();

    /// <summary>
    /// The rectangles occupied by the open sidebars (physical px, virtual-desktop space). Kept for
    /// diagnostics.
    /// </summary>
    public IReadOnlyList<PixelRect> ExcludedRects()
    {
        var rects = new List<PixelRect>(windows.Count);
        foreach (var window in windows.Values)
        {
            if (!window.IsVisible) continue;
            var scaling = window.Scaling <= 0 ? 1.0 : window.Scaling;
            rects.Add(new PixelRect(window.Position,
                new PixelSize((int)Math.Round(window.Bounds.Width * scaling), (int)Math.Round(window.Bounds.Height * scaling))));
        }
        return rects;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT point);
}
