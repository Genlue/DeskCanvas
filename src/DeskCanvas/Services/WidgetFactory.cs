using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Avalonia.Controls;
using DeskCanvas.Contracts;
using DeskCanvas.Core.Interfaces;
using DeskCanvas.Core.Models;
using DeskCanvas.Core.Models.Attributes;
using DeskCanvas.Core.Models.Settings;
using DeskCanvas.Core.Services;
using DeskCanvas.Runtime;
using DeskCanvas.Views;

namespace DeskCanvas.Services;

/// <summary>
/// Creates the widget windows from the stored layout.
/// </summary>
/// <param name="settingsWindow">
/// Resolves the one shared settings window (DI singleton). Every widget opens that same instance
/// instead of building its own, so the app never accumulates settings windows.
/// </param>
/// <param name="appSettingsProvider">
/// App settings, for <see cref="ApplyFullscreen"/>: the 最大化或全屏时 behaviour is read at
/// every cover transition so a settings save lands on the next one (and the settings-window
/// wiring re-applies it immediately while the desktop is covered).
/// </param>
public class WidgetFactory(IAssemblyProvider assemblyProvider, ILayoutProvider layoutProvider,
    DisplayMonitorService displayMonitor, Func<Settings> settingsWindow,
    IAppSettingsProvider appSettingsProvider)
    : IWidgetFactory<Window, UserControl>
{
    private readonly Dictionary<string, List<Widget>> activeWidgets = [];

    /// <summary>
    /// 存活组件 → 它自己的 <see cref="WidgetLayoutProvider"/>。供新 WinUI 编辑窗口复用同一个
    /// Provider（保存后桌面组件立即刷新，存档仍是原模型/JSON）。组件关闭时移除。
    /// </summary>
    private readonly Dictionary<Widget, WidgetLayoutProvider> editorProviders = [];

    /// <summary>
    /// True while at least one widget window is alive. The settings window asks this to decide
    /// whether closing it should only hide the shared window or really let the app exit.
    /// </summary>
    public bool HasWidgets => activeWidgets.Values.Any(list => list.Count > 0);

    /// <summary>True while the tray menu hides every widget (see <see cref="SetWidgetsHidden"/>).</summary>
    public bool WidgetsHidden { get; private set; }

    private bool displayChangePending;
    private bool reconcilingScreens;

    private IEnumerable<WidgetLayout> VisibleLayouts(ScreenLayout config)
    {
        if (config.Key != null) return config.Layout;
        var owner = displayMonitor.FindByConfigId(config.Id)?.Screen;
        if (owner == null) return [];
        // Unconverted v1 entries still carry absolute desktop coordinates. An offline
        // widget must remain in storage until its monitor returns, rather than creating
        // a native window Windows would clamp onto the surviving display.
        return config.Layout.Where(widget => owner.WorkingArea.Contains(new Avalonia.PixelPoint(
            widget.X + widget.Width / 2, widget.Y + widget.Height / 2)));
    }

    private void MigrateBeforeCreating()
    {
        // Migration changes both ownership and absolute-vs-relative coordinates. A live
        // provider caches its old WidgetLayout, so retire that bucket before converting it.
        var legacy = layoutProvider.Get().FindById(ScreensLayout.LegacyPrimaryId);
        if (legacy?.Key == null && legacy != null)
        {
            CloseScreen(legacy.Id);
            foreach (var attached in displayMonitor.Attached.Where(screen => screen.Config == null).ToList())
                if (legacy.Layout.Any(widget => attached.Screen.WorkingArea.Contains(new Avalonia.PixelPoint(
                        widget.X + widget.Width / 2, widget.Y + widget.Height / 2))))
                    displayMonitor.EnsureConfig(attached);
        }
        displayMonitor.MigrateLegacyWidgets();
    }

    private void ReindexLiveWidgets()
    {
        var widgets = activeWidgets.Values.SelectMany(list => list).Distinct().ToList();
        activeWidgets.Clear();
        foreach (var widget in widgets)
        {
            if (!activeWidgets.TryGetValue(widget.ScreenId, out var list))
                activeWidgets[widget.ScreenId] = list = [];
            list.Add(widget);
        }
    }

    /// <summary>Keep native windows out of Windows' monitor-removal relocation pass.</summary>
    public void SuspendForDisplayChange()
    {
        if (displayChangePending) return;
        displayChangePending = true;
        SecondaryPanelWindow.CloseForDisplayChange();
        foreach (var widget in activeWidgets.Values.SelectMany(list => list.ToList()))
        {
            try { widget.Hide(); }
            catch { /* An independently closed window must not interrupt reconciliation. */ }
        }
    }

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
                    else if (!displayChangePending && !displayMonitor.IsTopologyChanging) widget.Show();
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

    /// <summary>True while <see cref="PauseAll"/> is in effect (widgets visible, timers paused, nothing released).</summary>
    private bool fullscreenPaused;

    /// <summary>
    /// Create widget windows for every attached screen that has a stored configuration
    /// (widgets of screens that are currently unplugged stay in their config, hidden).
    /// </summary>
    public IEnumerable<Window> Create()
    {
        if (displayMonitor.IsTopologyChanging || reconcilingScreens) return [];
        reconcilingScreens = true;
        try
        {
            // Split legacy primary widgets onto their real screens before creation
            // (idempotent; the imported/migrated v2 file then renders widgets on the
            // correct per-screen entries with correct per-screen grids).
            MigrateBeforeCreating();

            ReindexLiveWidgets();
            var stored = layoutProvider.Get();

            return displayMonitor.Attached
                .Where(screen => screen.Config != null)
                .Select(screen => stored.FindById(screen.Config!.Id) ?? screen.Config!)
                .SelectMany(screen => activeWidgets.ContainsKey(screen.Id)
                    ? [] // already created (e.g. by a hot-plug pass during startup) — never twice
                    : VisibleLayouts(screen).Select(layout => CreateInternal(screen, layout)))
                .ToList();
        }
        finally { reconcilingScreens = false; }
    }

    /// <summary>
    /// Add a widget to a screen configuration and create its window.
    /// </summary>
    /// <param name="screen">The target screen configuration (may be a new entry — it is upserted).</param>
    /// <param name="widgetLayout">The widget layout to add.</param>
    public Window Add(ScreenLayout screen, WidgetLayout widgetLayout)
    {
        widgetLayout = widgetLayout.EnsureIdentity();
        var stored = layoutProvider.Get();
        var current = stored.FindById(screen.Id) ?? screen;
        var withWidget = current with { Layout = [.. current.Layout, widgetLayout] };
        layoutProvider.Save(stored.UpsertScreen(withWidget));
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

    /// <summary>
    /// Create a widget control for the 组件库 preview. This is the preview's only host path, so
    /// the host contract (<see cref="WidgetContentHost"/>) has to be applied right here: the
    /// desktop host applies it in the <see cref="Widget"/> constructor, and skipping it for the
    /// preview leaves a widget without the class markers the content-inset styles key off.
    /// </summary>
    public UserControl CreateControl(Type type)
    {
        // The preview gets no session on purpose: every preview selection builds a fresh
        // control under a throwaway Guid.NewGuid() identity and there is no teardown pass to
        // release registry entries, so a session here would only leak per preview. A
        // preview-capable consumer stays session-less (the pilot ignores activity without a
        // session) and the preview's no-write semantics stay untouched.
        var previewScreenId = layoutProvider.Get().Screens.FirstOrDefault()?.Id ?? ScreensLayout.LegacyPrimaryId;
        var defaultLayout = new WidgetLayout(type.Assembly.GetName().Name ?? "", type.Name, 0, 0, 200, 200, null,
            InstanceId: Guid.NewGuid(), Generation: 1);
        var widgetLayoutProvider = new WidgetLayoutProvider(layoutProvider, previewScreenId, defaultLayout);
        var control = CreateWidgetControl(type, widgetLayoutProvider, null);
        WidgetContentHost.Prepare(control);
        return control;
    }

    private Widget CreateInternal(ScreenLayout screen, WidgetLayout widgetLayout)
    {
        var widgetLayoutProvider = new WidgetLayoutProvider(layoutProvider, screen.Id, widgetLayout);

        // One session per widget instance for the window's whole lifetime (recreations of the
        // content reuse it through the same lambda). Legacy layout entries without a stable
        // identity keep the session-less behaviour, exactly like the preview below.
        var session = widgetLayout.HasStableIdentity
            ? WidgetSessionRegistry.GetOrCreate(new WidgetInstanceId(widgetLayout.InstanceId))
            : null;

        var assembly = assemblyProvider.LoadAssembly(widgetLayout.Type);
        var widgetInfo = GetWidgetInfo(assembly, widgetLayout.SubType);
        var widgetControl = () => CreateWidgetControl(widgetInfo.ViewType, widgetLayoutProvider, widgetLayoutProvider.Get().GetModel(widgetInfo.ModelType), session);

        // 编辑能力（报告 §3 第三步）：不再把 Func<EditWidget> 交给 Widget。这里只声明
        // “这个组件可编辑”并给出纯数据请求；新入口由 ISoftwareUi 打开 WinUI 编辑窗口，
        // 没有新入口（旧入口/检查工程）时才用 Binding 里的旧窗口委托维持原 Avalonia 行为。
        var editor = CreateEditorBinding(widgetLayoutProvider, widgetLayout, widgetInfo);

        var widget = editor != null
            ? (Widget) assemblyProvider.Activate(typeof(Widget), widgetLayoutProvider, widgetControl, settingsWindow, editor)
            : (Widget) assemblyProvider.Activate(typeof(Widget), widgetLayoutProvider, widgetControl, settingsWindow);

        if (!activeWidgets.TryGetValue(screen.Id, out var list))
            activeWidgets[screen.Id] = list = [];
        list.Add(widget);
        editorProviders[widget] = widgetLayoutProvider;
        widget.Closed += (_, _) =>
        {
            // A user drag may have changed ownership since this window was created.
            foreach (var currentList in activeWidgets.Values)
                currentList.Remove(widget);

            editorProviders.Remove(widget);
            OnWidgetClosed();
        };

        // A widget added while the tray toggle hides the desktop (Gallery "add", profile switch)
        // must not pop up on its own.
        widget.Opened += (_, _) =>
        {
            if (WidgetsHidden || displayChangePending || displayMonitor.IsTopologyChanging) widget.Hide();
        };
        return widget;
    }

    /// <summary>
    /// Close every widget of a screen configuration (used when the screen is unplugged;
    /// the layout stays on disk, so replugging recreates them in place).
    /// </summary>
    public void CloseScreen(string screenId)
    {
        ReindexLiveWidgets();
        if (!activeWidgets.TryGetValue(screenId, out var list)) return;
        foreach (var widget in list.ToList())
        {
            // The owner component is going away — its secondary panels must not outlive it.
            PanelService.CloseForOwner(widget.InstanceId);
            widget.PrepareForTeardown();
            widget.Hide();
            widget.Close();
        }
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
    /// A widget window has closed.
    /// <para>
    /// The closed widget has just given up its native resources (render surfaces, decoded
    /// pictures, per-widget caches), so the guarded maintenance trim is requested — the service
    /// coalesces a burst of closes into a single trim, so removing a whole layout cannot turn
    /// into a trim per widget. When the <b>last</b> widget is gone, the shared, rebuildable
    /// material caches — the full-desktop capture and the blurred backdrops, tens of megabytes
    /// each on a large desktop — are released with it; they are re-created on demand. Without
    /// this, removing a layout's widgets left those caches and their heap resident with nothing
    /// on screen to justify them.
    /// </para>
    /// </summary>
    private void OnWidgetClosed()
    {
        MemoryTrimmerService.RequestTrim();

        if (HasWidgets) return;

        LiquidGlassSourceCache.Clear();
        LiquidGlassWallpaper.Release();
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
    public virtual void CloseAll()
    {
        foreach (var list in activeWidgets.Values)
        {
            foreach (var widget in list.ToList())
            {
                try
                {
                    // The owner component is going away — its secondary panels must not outlive it.
                    PanelService.CloseForOwner(widget.InstanceId);
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
    public virtual void CreateFromLayout()
    {
        displayMonitor.Refresh();

        foreach (var win in Create())
        {
            win.Show();
        }
    }

    /// <summary>
    /// Hide every widget and release the caches the widget views opt into releasing, pausing
    /// the shared timers — the 最大化或全屏时 → 隐藏 behaviour: every attached screen is
    /// covered by a fullscreen or maximized application, so nothing here would be visible and
    /// every megabyte counts.
    /// <para>
    /// The windows are hidden (<see cref="Widget.Hide"/>) and brought back on resume. They
    /// already sit at the bottom of the z-order behind whatever covers the screen, but hiding
    /// also drops them out of the compositor — on top of the released caches that hands the
    /// most memory back to the covering application.
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
                    widget.Hide();
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

        // Large native blocks were just released (the capture, the shared backdrops); hand the
        // freed pages back promptly. Force trim bypasses the floor and cooldown so working set
        // and managed heap drop immediately when the desktop is covered.
        MemoryTrimmerService.RequestTrim(TrimPriority.Force);
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
                    widget.Show();
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
    /// Freeze-in-place suspension (最大化或全屏时 → 暂停): the windows stay visible with their
    /// last frame, but the shared timers stop and every hosted content is told the desktop is
    /// covered so its own ticks and data polling gate themselves. Unlike <see cref="SuspendAll"/>
    /// nothing is hidden, no cache is given up and no trim runs — the desktop reappears exactly
    /// as it was left, at the cost of keeping every allocation alive behind the covering window.
    /// </summary>
    public void PauseAll()
    {
        if (suspended || fullscreenPaused) return;
        fullscreenPaused = true;

        // Shared widget timers (clock ticks, monitor sampling, weather refresh …) are
        // driven by one DispatcherTimer per interval, so pausing them pauses every
        // widget at once — and with them every timer-driven data request.
        TimerService.PauseAll();

        foreach (var list in activeWidgets.Values)
        {
            foreach (var widget in list.ToList())
            {
                try
                {
                    widget.PauseTicks();
                }
                catch
                {
                    // Never let one misbehaving widget abort the whole pause pass.
                }
            }
        }
    }

    /// <summary>Undo <see cref="PauseAll"/>: the timers resume and the widgets tick again.</summary>
    public void ResumePausedWidgets()
    {
        if (!fullscreenPaused) return;
        fullscreenPaused = false;

        foreach (var list in activeWidgets.Values)
        {
            foreach (var widget in list.ToList())
            {
                try
                {
                    widget.ResumeTicks();
                }
                catch
                {
                    // See PauseAll.
                }
            }
        }

        TimerService.ResumeAll();
    }

    /// <summary>
    /// Apply the 最大化或全屏时 behaviour for one cover transition. Undo-then-apply: whatever
    /// the previous state suspended is restored first (both resume paths are no-ops when
    /// inactive), so changing the option while the desktop is covered lands immediately —
    /// and a <c>false</c> transition only ever restores.
    /// </summary>
    public void ApplyFullscreen(bool covered)
    {
        if (!covered)
        {
            ResumeAll();
            ResumePausedWidgets();
            return;
        }

        switch (appSettingsProvider.Get().FullscreenBehavior ?? FullscreenBehavior.Hide)
        {
            case FullscreenBehavior.Hide:
                ResumePausedWidgets(); // a mode switch while covered (暂停 → 隐藏)
                SuspendAll();
                break;
            case FullscreenBehavior.Pause:
                ResumeAll(); // a mode switch while covered (隐藏 → 暂停)
                PauseAll();
                break;
            // FullscreenBehavior.None: leave everything running.
        }
    }

    /// <summary>
    /// React to display changes: hide widgets of unplugged screens, recreate widgets
    /// of screens that just came back (their config is still on disk), and snap windows
    /// that were stranded on a vanished display back onto their own screen.
    /// </summary>
    public void OnScreensChanged()
    {
        if (displayMonitor.IsTopologyChanging || reconcilingScreens) return;
        reconcilingScreens = true;
        try
        {
            // A screen just arrived: legacy primary widgets whose absolute position
            // now falls on it are moved into its per-screen entry first (idempotent).
            MigrateBeforeCreating();

            ReindexLiveWidgets();

            var attached = displayMonitor.Attached;

            foreach (var screenId in activeWidgets.Keys.ToList())
                if (attached.All(screen => screen.Config?.Id != screenId))
                    CloseScreen(screenId);

            foreach (var screen in attached)
            {
                if (screen.Config == null || activeWidgets.ContainsKey(screen.Config.Id)) continue;

                var config = layoutProvider.Get().FindById(screen.Config.Id) ?? screen.Config;
                foreach (var layout in VisibleLayouts(config))
                {
                    var widget = CreateInternal(config, layout);
                    // Opening creates the native handle; the Opened guard keeps it hidden until
                    // every surviving window has restored its own monitor's geometry and style.
                    widget.Show();
                    if (suspended) widget.SuspendContent();
                }
            }

            // Windows may have already moved a window onto another attached display. Always
            // restore from its configuration owner, including DPI, card insets and native clipping.
            foreach (var widget in activeWidgets.Values.SelectMany(list => list.ToList()))
            {
                try
                {
                    widget.RefreshDisplayMetrics();
                }
                catch
                {
                    // Never let one misbehaving widget abort the whole pass.
                }
            }

            displayChangePending = false;
            if (!WidgetsHidden)
                foreach (var widget in activeWidgets.Values.SelectMany(list => list.ToList()))
                {
                    try { widget.Show(); }
                    catch { /* Keep the remaining screens usable if one widget has closed. */ }
                }
        }
        finally { reconcilingScreens = false; }
    }

    private UserControl CreateWidgetControl(Type type, WidgetLayoutProvider? widgetLayoutProvider, object? model, IWidgetSession? session = null)
    {
        List<object> args = [];

        if (NeedsWidgetLayoutProvider(type) && widgetLayoutProvider != null)
            args.Add(widgetLayoutProvider);

        // Widgets that declare an IWidgetSession constructor parameter receive the host
        // session (the P8 A1 pilot contract, probed like IWidgetLayoutProvider above);
        // every other widget's activation is untouched.
        if (session != null && NeedsSession(type))
            args.Add(session);

        // A widget whose layout entry has no stored Settings yet (freshly added) — and every
        // 组件库 preview — arrives with model == null. Widgets whose constructors take the model
        // FIRST (FramelessDigital, MapView, ProgressView, …) then fall back to a shorter
        // constructor that leaves the IAppSettingsProvider parameter unfilled: the widget runs
        // without a settings provider, never hears DataChanged and is locked to its null-theme
        // fallback (the frameless clock rendered 毛玻璃 no matter what the global theme was).
        // Building the model's default instance here makes the full constructor reachable, and
        // ActivatorUtilities fills the remaining parameter from DI — the preview and the freshly
        // added widget are wired exactly like a widget restored from a stored layout.
        if (model == null && TryCreateDefaultModel(type) is { } defaultModel)
            args.Add(defaultModel);
        else if (model != null)
            args.Add(model);

        return (assemblyProvider.Activate(type, args.ToArray()) as UserControl)!;
    }

    /// <summary>
    /// The default instance of the widget model declared by <paramref name="viewType"/>'s
    /// <see cref="WidgetInfoAttribute.ModelType"/>, or null when the widget declares no model or
    /// the model cannot be default-constructed. Exposed as a pure function so
    /// <c>tests/ClockThemeChecks</c> can pin the fresh-add / preview activation contract.
    /// </summary>
    public static object? TryCreateDefaultModel(Type viewType)
    {
        try
        {
            var modelType = viewType.Assembly
                .GetCustomAttributes<WidgetInfoAttribute>()
                .SingleOrDefault(attribute => attribute.ViewType == viewType)
                ?.ModelType;

            return modelType == null ? null : Activator.CreateInstance(modelType);
        }
        catch
        {
            // A model without a usable default constructor keeps the old behaviour
            // (no model argument) instead of breaking widget creation outright.
            return null;
        }
    }

    /// <summary>
    /// 组件的“可编辑能力 + 打开编辑器委托”。没有 <c>EditModelViewType</c> 的组件返回 null
    /// （右键菜单沿用 <c>ShowEditButton</c> 的可见性规则）。
    /// <para>
    /// 生产路径只把 <see cref="WidgetEditorRequest"/> 转给 <see cref="ISoftwareUi"/>；
    /// 旧 Avalonia 编辑窗口的构造委托只有在新入口缺席时才会被 <see cref="WidgetEditorBinding.Open"/>
    /// 调用（报告 §3 第三步：生产路径不得再创建旧窗口）。
    /// </para>
    /// </summary>
    private WidgetEditorBinding? CreateEditorBinding(WidgetLayoutProvider widgetLayoutProvider,
        WidgetLayout widgetLayout, WidgetInfoAttribute widgetInfo)
    {
        if (widgetInfo.EditModelViewType is not { } editType) return null;

        var request = new WidgetEditorRequest(
            widgetLayout.InstanceId,
            widgetLayout.Generation,
            widgetLayout.Type,
            widgetLayout.SubType,
            widgetLayoutProvider.ScreenId,
            editType.Assembly.GetName().Name,
            editType.FullName ?? editType.Name,
            // 旧槽位键 "assembly:settingsViewFullName"：用 EditModelViewType 原类型算，
            // 新 WinUI 页面换程序集/类名也不会让窗口几何记忆全部丢失（报告 §3 第三步）。
            WindowGeometryStore.ComposeKey(editType));

        return new WidgetEditorBinding(request, () =>
        {
            CreateLegacyEditWidgetWindow(widgetLayoutProvider, editType);
            return WidgetEditorOpenResult.Started;
        });
    }

    /// <summary>
    /// 旧 Avalonia 编辑窗口：<b>只</b>在没有 <see cref="ISoftwareUi"/> 的旧入口/检查工程里创建。
    /// 有 WinUI 入口时 <see cref="WidgetEditorBinding.Open"/> 直接转发请求，走不到这里。
    /// </summary>
    private void CreateLegacyEditWidgetWindow(IWidgetLayoutProvider widgetLayoutProvider, Type type)
    {
        var control = (UserControl) assemblyProvider.Activate(type, widgetLayoutProvider);

        new EditWidget(widgetLayoutProvider, control).Show();
    }

    /// <summary>
    /// 新编辑窗口的上下文来源：按稳定身份找存活组件，返回它<b>自己</b>的
    /// <see cref="IWidgetLayoutProvider"/>（同一实例）。
    /// <para>
    /// 复用同一个 Provider 而不是按存档重建一个：页面保存会触发该 Provider 的
    /// <c>DataChanged</c>，桌面组件随即按新模型刷新；存档仍是原来的 layout.json/Settings JSON
    /// （报告 §4.2）。组件已关闭或换代时返回 null，调用方必须拒绝打开。
    /// </para>
    /// </summary>
    public IWidgetLayoutProvider? TryGetLiveLayoutProvider(Guid instanceId, string type, string subType)
    {
        foreach (var (widget, provider) in editorProviders.ToList())
        {
            var layout = provider.Get();
            if (layout == null) continue;
            if (!string.Equals(layout.Type, type, StringComparison.Ordinal)) continue;
            if (!string.Equals(layout.SubType, subType, StringComparison.Ordinal)) continue;
            if (instanceId != Guid.Empty && layout.InstanceId != instanceId) continue;
            if (!activeWidgets.Values.Any(list => list.Contains(widget))) continue;
            return provider;
        }

        return null;
    }

    /// <summary>
    /// 开发/冒烟诊断（<c>--edit-widget=Type/SubType</c>）：找第一个可编辑的存活组件。
    /// 正常功能一律由组件自己的右键菜单触发，这里不是生产路径。
    /// </summary>
    public Widget? FindLiveWidget(string? type, string? subType)
    {
        foreach (var (widget, provider) in editorProviders.ToList())
        {
            var layout = provider.Get();
            if (layout == null) continue;
            if (!string.IsNullOrEmpty(type)
                && !string.Equals(layout.Type, type, StringComparison.OrdinalIgnoreCase)) continue;
            if (!string.IsNullOrEmpty(subType)
                && !string.Equals(layout.SubType, subType, StringComparison.OrdinalIgnoreCase)) continue;
            if (!activeWidgets.Values.Any(list => list.Contains(widget))) continue;
            return widget;
        }

        return null;
    }

    private bool NeedsWidgetLayoutProvider(Type type)
    {
        return type
            .GetConstructors()
            .Any(constructor => constructor
                .GetParameters()
                .Any(param => param.ParameterType == typeof(IWidgetLayoutProvider)));
    }

    /// <summary>
    /// Whether the view declares an <see cref="IWidgetSession"/> constructor parameter and
    /// therefore wants the host session injected. Views without the parameter are created
    /// exactly as before.
    /// </summary>
    private static bool NeedsSession(Type type)
    {
        return type
            .GetConstructors()
            .Any(constructor => constructor
                .GetParameters()
                .Any(param => param.ParameterType == typeof(IWidgetSession)));
    }

    private static WidgetInfoAttribute GetWidgetInfo(Assembly assembly, string typeName)
    {
        var attributes = assembly.GetCustomAttributes<WidgetInfoAttribute>().ToList();
        var widgetInfo = attributes
            .SingleOrDefault(attribute => attribute.ViewType.Name == typeName);

        // A renamed widget keeps loading layouts written under its old SubType. Match the
        // comma-separated compatibility list only when no current type owns the saved name.
        widgetInfo ??= attributes.FirstOrDefault(attribute =>
            attribute.LegacySubTypes?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Any(name => string.Equals(name, typeName, StringComparison.Ordinal)) == true);

        if (widgetInfo == null)
            throw new ArgumentException($"No suitable WidgetInfoAttribute found for {typeName}");

        return widgetInfo;
    }
}
