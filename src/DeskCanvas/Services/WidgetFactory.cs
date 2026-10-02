using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Avalonia.Controls;
using DeskCanvas.Core.Interfaces;
using DeskCanvas.Core.Models;
using DeskCanvas.Core.Models.Attributes;
using DeskCanvas.Core.Services;
using DeskCanvas.Views;

namespace DeskCanvas.Services;

/// <summary>
/// Creates the widget windows from the stored layout.
/// </summary>
/// <param name="settingsWindow">
/// Resolves the one shared settings window (DI singleton). Every widget opens that same instance
/// instead of building its own, so the app never accumulates settings windows.
/// </param>
public class WidgetFactory(IAssemblyProvider assemblyProvider, ILayoutProvider layoutProvider,
    DisplayMonitorService displayMonitor, Func<Settings> settingsWindow)
    : IWidgetFactory<Window, UserControl>
{
    /// <summary>Shared widget-construction seam; the sidebar host uses the same instance type.</summary>
    private readonly WidgetRuntimeFactory runtime = new(assemblyProvider);

    /// <summary>The shared widget runtime factory (assembly loading, content/edit-window creation).</summary>
    public WidgetRuntimeFactory Runtime => runtime;

    private readonly Dictionary<string, List<Widget>> activeWidgets = [];

    /// <summary>
    /// True while at least one widget window is alive. The settings window asks this to decide
    /// whether closing it should only hide the shared window or really let the app exit.
    /// </summary>
    public bool HasWidgets => activeWidgets.Values.Any(list => list.Count > 0);

    /// <summary>True while the tray menu hides every widget (see <see cref="SetWidgetsHidden"/>).</summary>
    public bool WidgetsHidden { get; private set; }

    /// <summary>
    /// Hide or bring back every widget window (the tray's 隐藏组件 toggle). Unlike
    /// <see cref="SuspendAll"/> this keeps the content and the timers alive — it is a visibility
    /// switch, not a resource-saving suspension.
    /// </summary>
    public void SetWidgetsHidden(bool hidden)
    {
        if (WidgetsHidden == hidden) return;
        WidgetsHidden = hidden;

        foreach (var list in activeWidgets.Values)
        {
            foreach (var widget in list.ToList())
            {
                try
                {
                    if (hidden) widget.Hide();
                    else widget.Show();
                }
                catch
                {
                    // Never let one misbehaving widget abort the whole pass.
                }
            }
        }
    }

    /// <summary>True while <see cref="SuspendAll"/> is in effect (widgets hidden, timers paused).</summary>
    private bool suspended;

    /// <summary>
    /// Create widget windows for every attached screen that has a stored configuration
    /// (widgets of screens that are currently unplugged stay in their config, hidden).
    /// </summary>
    public IEnumerable<Window> Create()
    {
        // Split legacy primary widgets onto their real screens before creation
        // (idempotent; the imported/migrated v2 file then renders widgets on the
        // correct per-screen entries with correct per-screen grids).
        displayMonitor.MigrateLegacyWidgets();

        return displayMonitor.Attached
            .Where(screen => screen.Config != null)
            .SelectMany(screen => activeWidgets.ContainsKey(screen.Config!.Id)
                ? [] // already created (e.g. by a hot-plug pass during startup) — never twice
                : screen.Config!.Layout.Select(layout => CreateInternal(screen.Config!, layout)))
            .ToList();
    }

    /// <summary>
    /// Add a widget to a screen configuration and create its window.
    /// </summary>
    /// <param name="screen">The target screen configuration (may be a new entry — it is upserted).</param>
    /// <param name="widgetLayout">The widget layout to add.</param>
    public Window Add(ScreenLayout screen, WidgetLayout widgetLayout)
    {
        var withWidget = screen with { Layout = [.. screen.Layout, widgetLayout] };
        layoutProvider.Save(layoutProvider.Get().UpsertScreen(withWidget));
        return CreateInternal(withWidget, widgetLayout);
    }

    /// <summary>
    /// Legacy single-argument add (interface member): places the widget on the
    /// "primary" entry (v1 semantics — absolute desktop coordinates). Kept as the
    /// interface contract for pre-multi-screen callers.
    /// </summary>
    public Window Add(WidgetLayout widgetLayout)
    {
        var screens = layoutProvider.Get();
        var primary = screens.FindById(ScreensLayout.LegacyPrimaryId)
                      ?? screens.Screens.FirstOrDefault()
                      ?? new ScreenLayout(ScreensLayout.LegacyPrimaryId, null, null, null, null, null, []);
        return Add(primary, widgetLayout);
    }

    public UserControl CreateControl(Type type)
    {
        var previewScreenId = layoutProvider.Get().Screens.FirstOrDefault()?.Id ?? ScreensLayout.LegacyPrimaryId;
        var defaultLayout = new WidgetLayout(type.Assembly.GetName().Name ?? "", type.Name, 0, 0, 200, 200, null);
        var widgetLayoutProvider = new WidgetLayoutProvider(layoutProvider, previewScreenId, defaultLayout);
        return CreateWidgetControl(type, widgetLayoutProvider, null);
    }

    private Widget CreateInternal(ScreenLayout screen, WidgetLayout widgetLayout)
    {
        var widgetLayoutProvider = new WidgetLayoutProvider(layoutProvider, screen.Id, widgetLayout);

        var assembly = assemblyProvider.LoadAssembly(widgetLayout.Type);
        var widgetInfo = GetWidgetInfo(assembly, widgetLayout.SubType);
        var widgetControl = () => CreateWidgetControl(widgetInfo.ViewType, widgetLayoutProvider, widgetLayoutProvider.Get().GetModel(widgetInfo.ModelType));

        var editWidgetWindow = widgetInfo.EditModelViewType != null
            ? () => CreateEditWidgetWindow(widgetLayoutProvider, widgetInfo.EditModelViewType)
            : (Func<EditWidget>?) null;

        var widget = editWidgetWindow != null
            ? (Widget) assemblyProvider.Activate(typeof(Widget), widgetLayoutProvider, widgetControl, settingsWindow, editWidgetWindow)
            : (Widget) assemblyProvider.Activate(typeof(Widget), widgetLayoutProvider, widgetControl, settingsWindow);

        if (!activeWidgets.TryGetValue(screen.Id, out var list))
            activeWidgets[screen.Id] = list = [];
        list.Add(widget);
        widget.Closed += (_, _) => list.Remove(widget);

        // A widget added while the tray toggle hides the desktop (Gallery "add", profile switch)
        // must not pop up on its own.
        widget.Opened += (_, _) =>
        {
            if (WidgetsHidden) widget.Hide();
        };
        return widget;
    }

    /// <summary>
    /// Close every widget of a screen configuration (used when the screen is unplugged;
    /// the layout stays on disk, so replugging recreates them in place).
    /// </summary>
    public void CloseScreen(string screenId)
    {
        if (!activeWidgets.TryGetValue(screenId, out var list)) return;
        foreach (var widget in list.ToList())
            widget.Close();
        activeWidgets.Remove(screenId);
    }

    /// <summary>
    /// Close every active widget across all screens and recreate them from the current layout.
    /// </summary>
    public void RecreateAll()
    {
        CloseAll();
        CreateFromLayout();
    }

    /// <summary>
    /// Close every active widget window and forget it, without creating replacements.
    /// <para>
    /// Windows are hidden <b>before</b> being closed: a close request is only fully
    /// processed once the dispatcher gets to it, so the outgoing set would otherwise
    /// still be on screen while the incoming set is shown (the "two sets of widgets"
    /// flash when switching profiles). Each window also stops listening to layout
    /// changes first, so a window that is mid-teardown can never write its entry back
    /// into the layout that is being replaced.
    /// </para>
    /// </summary>
    public void CloseAll()
    {
        foreach (var list in activeWidgets.Values)
        {
            foreach (var widget in list.ToList())
            {
                try
                {
                    widget.PrepareForTeardown();
                    widget.Hide();
                    widget.Close();
                }
                catch
                {
                    // A window that already closed itself is not an error here.
                }
            }
        }

        activeWidgets.Clear();
    }

    /// <summary>
    /// Create and show the widget windows for every screen in the current layout.
    /// </summary>
    public void CreateFromLayout()
    {
        displayMonitor.Refresh();

        foreach (var win in Create())
        {
            win.Show();
        }
    }

    /// <summary>
    /// Release the caches the widget views opt into releasing and pause the shared timers —
    /// every attached screen is covered by a fullscreen or maximized application, so nothing
    /// here would be visible and every megabyte counts.
    /// <para>
    /// The windows are deliberately <b>not</b> hidden: they already sit at the bottom of the
    /// z-order behind whatever covers the screen, and hiding then re-showing every window on
    /// each transition was what made the widgets come back late (and flicker) after leaving a
    /// game. Only invisible memory is given up — the material caches, which are rebuilt in the
    /// background on resume.
    /// </para>
    /// </summary>
    public void SuspendAll()
    {
        if (suspended) return;
        suspended = true;

        // Shared widget timers (clock ticks, monitor sampling, weather refresh …) are
        // driven by one DispatcherTimer per interval, so pausing them pauses every
        // widget at once.
        TimerService.PauseAll();

        foreach (var list in activeWidgets.Values)
        {
            foreach (var widget in list.ToList())
            {
                try
                {
                    widget.SuspendContent();
                }
                catch
                {
                    // Never let one misbehaving widget abort the whole suspend pass.
                }
            }
        }

        // The desktop capture is the largest single allocation in the process and nothing is
        // sampling glass behind a fullscreen application, so it is given up as well (it is
        // re-captured on demand the moment a widget renders again). Live sampling stops too:
        // otherwise the timer would immediately re-capture what was just released, every tick.
        LiquidGlassWallpaper.SuspendLiveSampling();
        LiquidGlassWallpaper.Release();

        // The shared blurred backdrops pin the released captures alive through their reference
        // count — without this they would survive the whole fullscreen period (up to three
        // full-desktop bitmaps, tens of MB each on a large desktop).
        LiquidGlassSourceCache.Clear();

        // Off the UI thread: this is a double compacting GC; synchronously here it stalled the
        // fullscreen transition for as long as the heap took to compact (MemoryTrimmerService
        // runs the same trim on a worker for the same reason).
        Task.Run(InteropService.TrimProcessMemory);
    }

    /// <summary>Resume the timers and let the widgets rebuild the released caches.</summary>
    public void ResumeAll()
    {
        if (!suspended) return;
        suspended = false;
        LiquidGlassWallpaper.ResumeLiveSampling();

        foreach (var list in activeWidgets.Values)
        {
            foreach (var widget in list.ToList())
            {
                try
                {
                    widget.ResumeContent();
                }
                catch
                {
                    // See SuspendAll.
                }
            }
        }

        TimerService.ResumeAll();
    }

    /// <summary>
    /// React to display changes: hide widgets of unplugged screens, recreate widgets
    /// of screens that just came back (their config is still on disk), and snap windows
    /// that were stranded on a vanished display back onto their own screen.
    /// </summary>
    public void OnScreensChanged()
    {
        // A screen just arrived: legacy primary widgets whose absolute position
        // now falls on it are moved into its per-screen entry first (idempotent).
        displayMonitor.MigrateLegacyWidgets();

        var attached = displayMonitor.Attached;

        foreach (var screenId in activeWidgets.Keys.ToList())
            if (attached.All(screen => screen.Config?.Id != screenId))
                CloseScreen(screenId);

        foreach (var screen in attached)
        {
            if (screen.Config == null || activeWidgets.ContainsKey(screen.Config.Id)) continue;

            foreach (var layout in screen.Config.Layout)
                CreateInternal(screen.Config, layout).Show();
        }

        // Windows stay wherever they were when a display disappeared (a remote-control
        // tool's virtual screen, an unplugged monitor) — possibly on dead desktop space
        // no attached screen covers. Everything that is still on a real screen keeps its
        // exact position; only stranded windows snap back to their stored slot.
        foreach (var widget in activeWidgets.Values.SelectMany(list => list.ToList()))
        {
            try
            {
                widget.EnsureOnScreen();
            }
            catch
            {
                // Never let one misbehaving widget abort the whole pass.
            }
        }
    }

    private UserControl CreateWidgetControl(Type type, WidgetLayoutProvider? widgetLayoutProvider, object? model)
    {
        return runtime.CreateWidgetControl(type, widgetLayoutProvider, model);
    }

    private EditWidget CreateEditWidgetWindow(IWidgetLayoutProvider widgetLayoutProvider, Type type)
    {
        return runtime.CreateEditWidgetWindow(widgetLayoutProvider, type);
    }

    private bool NeedsWidgetLayoutProvider(Type type) => WidgetRuntimeFactory.NeedsWidgetLayoutProvider(type);

    private static WidgetInfoAttribute GetWidgetInfo(Assembly assembly, string typeName) =>
        WidgetRuntimeFactory.GetWidgetInfo(assembly, typeName);
}