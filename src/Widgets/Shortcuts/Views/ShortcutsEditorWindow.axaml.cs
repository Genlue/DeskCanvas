using System.Text.Json;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using DeskCanvas.Services;
using DeskCanvas.Views;
using Shortcuts.Models;
using Shortcuts.Services;

namespace Shortcuts.Views;

/// <summary>
/// The standalone shortcut editor window (P2/P3 of the design): a single action canvas with
/// inline parameter chips, control-flow containers, drag-to-reorder, undo/redo, a bottom
/// action-library drawer and live run feedback. One instance at a time; edits are committed
/// to <see cref="ShortcutsStore"/> on every change (history gives undo/redo).
/// </summary>
public partial class ShortcutsEditorWindow : Window
{
    private const string GeometryKey = "Shortcuts:EditorWindow";

    private ShortcutDefinition shortcut = new();
    private readonly List<string> history = [];
    private int historyIndex = -1;
    private string? selectedActionId;
    private bool geometryRestored;
    private bool runButtonRunning;
    private int runStateVersion;
    private bool eventsAttached;

    private static ShortcutsEditorWindow? current;

    internal ShortcutDefinition Shortcut => shortcut;

    // ---- construction -------------------------------------------------------------

    public ShortcutsEditorWindow() : this(null)
    {
    }

    public ShortcutsEditorWindow(string? shortcutId)
    {
        InitializeComponent();
        WindowRoles.Register(this, WindowRole.Dialog);

        var existing = string.IsNullOrEmpty(shortcutId)
            ? null
            : ShortcutsStore.Get().Shortcuts.FirstOrDefault(s => s.Id == shortcutId);
        if (existing != null)
        {
            shortcut = existing.Clone();
        }
        else
        {
            shortcut = ShortcutTemplates.Blank();
            ShortcutsStore.Upsert(shortcut, this);
        }

        history.Add(Snapshot());
        historyIndex = 0;

        ShortcutsStore.SetLastEdited(shortcut.Id);

        Opened += OnWindowOpened;
        Closing += (_, _) => SaveGeometry();

        KeyDown += OnWindowKeyDown;
        Closed += OnWindowClosed;

        UpdateToolbar();
        RebuildRows();
        UpdateUndoRedo();
    }

    /// <summary>
    /// Subscribe only once the window is actually shown: a window that is never opened never
    /// fires <see cref="Window.Closed"/>, so a constructor subscription to the static store/runner
    /// events would leak if the editor is abandoned before it is displayed.
    /// </summary>
    private void OnWindowOpened(object? sender, EventArgs e)
    {
        TryRestoreGeometry();
        if (eventsAttached) return;
        eventsAttached = true;
        ShortcutsStore.ModelChanged += OnStoreChanged;
        ShortcutRunner.RunEvent += OnRunEvent;
    }

    public static void ShowEditor(string shortcutId, Window? owner)
    {
        if (current is { } existing)
        {
            try
            {
                existing.SelectShortcut(shortcutId);
                existing.Activate();
                return;
            }
            catch
            {
                current = null;
            }
        }

        var window = new ShortcutsEditorWindow(shortcutId);
        window.Closed += (_, _) =>
        {
            if (ReferenceEquals(current, window)) current = null;
        };
        current = window;

        if (owner != null) window.Show(owner);
        else window.Show();
    }

    /// <summary>Switch the open editor to another shortcut (from a card/panel "Edit" entry).</summary>
    private void SelectShortcut(string shortcutId)
    {
        if (shortcut.Id == shortcutId) return;
        var target = ShortcutsStore.Get().Shortcuts.FirstOrDefault(s => s.Id == shortcutId);
        if (target == null) return;

        shortcut = target.Clone();
        ShortcutsStore.SetLastEdited(shortcut.Id);
        history.Clear();
        history.Add(Snapshot());
        historyIndex = 0;
        selectedActionId = null;
        UpdateToolbar();
        RebuildRows();
        UpdateUndoRedo();
    }

    /// <summary>The toolbar's "switch shortcut" menu: every shortcut plus a blank one.</summary>
    private void OnSwitchShortcutClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control anchor) return;

        CommitName();

        var flyout = new MenuFlyout();
        foreach (var candidate in ShortcutsStore.Get().Shortcuts)
        {
            var item = new MenuItem { Header = candidate.Name };
            if (candidate.Id == shortcut.Id)
            {
                item.Icon = new Shortcuts.Views.Controls.GlyphView
                {
                    Data = GlyphLibrary.GetGeometry("check"),
                    Stroke = new SolidColorBrush(Color.Parse("#3B6D11")),
                    StrokeThickness = 2,
                    Width = 13,
                    Height = 13,
                };
            }

            var id = candidate.Id;
            item.Click += (_, _) => SelectShortcut(id);
            flyout.Items.Add(item);
        }

        flyout.Items.Add(new Separator());
        var newItem = new MenuItem { Header = Loc.Get("Shortcuts_Menu_New") };
        newItem.Click += (_, _) =>
        {
            var blank = ShortcutTemplates.Blank();
            ShortcutsStore.Upsert(blank, this);
            SelectShortcut(blank.Id);
        };
        flyout.Items.Add(newItem);

        flyout.ShowAt(anchor);
    }

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        if (eventsAttached)
        {
            eventsAttached = false;
            ShortcutsStore.ModelChanged -= OnStoreChanged;
            ShortcutRunner.RunEvent -= OnRunEvent;
        }

        Opened -= OnWindowOpened;
        Closed -= OnWindowClosed;
    }

    // ---- document plumbing --------------------------------------------------------

    private string Snapshot() => JsonSerializer.Serialize(shortcut, DocumentJson.Options);

    /// <summary>Push the (already mutated) state to history + store and refresh the UI.</summary>
    private void CommitChange()
    {
        var json = Snapshot();
        if (historyIndex >= 0 && historyIndex < history.Count && history[historyIndex] == json)
            return;

        if (historyIndex < history.Count - 1)
            history.RemoveRange(historyIndex + 1, history.Count - historyIndex - 1);

        history.Add(json);
        historyIndex = history.Count - 1;
        if (history.Count > 80)
        {
            history.RemoveAt(0);
            historyIndex--;
        }

        ShortcutsStore.Upsert(shortcut, this);
        UpdateToolbar();
        RebuildRows();
        UpdateUndoRedo();
    }

    /// <summary>Apply a state without touching history (undo/redo apply their own pointer).</summary>
    private void ApplySnapshot(string json)
    {
        var parsed = JsonSerializer.Deserialize<ShortcutDefinition>(json, DocumentJson.Options);
        if (parsed == null) return;

        shortcut = parsed;
        ShortcutsStore.Upsert(shortcut, this);
        UpdateToolbar();
        RebuildRows();
        UpdateUndoRedo();
    }

    private void Undo()
    {
        if (historyIndex <= 0) return;
        historyIndex--;
        ApplySnapshot(history[historyIndex]);
    }

    private void Redo()
    {
        if (historyIndex >= history.Count - 1) return;
        historyIndex++;
        ApplySnapshot(history[historyIndex]);
    }

    private void UpdateUndoRedo()
    {
        UndoButton.IsEnabled = historyIndex > 0;
        RedoButton.IsEnabled = historyIndex < history.Count - 1;
    }

    // ---- toolbar ------------------------------------------------------------------

    private void UpdateToolbar()
    {
        if (NameBox.Text != shortcut.Name)
            NameBox.Text = shortcut.Name;

        HeaderGlyphPath.Data = GlyphLibrary.GetGeometry(shortcut.Glyph);
        HeaderGlyphSurface.Background = Visuals.Brush(shortcut.Color);

        // ActionCount walks the whole action tree — compute it once instead of twice.
        var actionCount = shortcut.ActionCount;
        ActionCountText.Text = actionCount == 1
            ? Loc.Get("Shortcuts_Actions_CountOne")
            : Loc.Format("Shortcuts_Actions_Count", actionCount);
    }

    private void OnNameCommitted(object? sender, RoutedEventArgs e) => CommitName();

    private void OnNameKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        CommitName();
        e.Handled = true;
    }

    private void CommitName()
    {
        var text = (NameBox.Text ?? string.Empty).Trim();
        if (text.Length == 0)
        {
            NameBox.Text = shortcut.Name;
            return;
        }

        if (text.Length > 60) text = text[..60];
        if (text == shortcut.Name) return;

        shortcut.Name = text;
        CommitChange();
    }

    private void OnDoneClicked(object? sender, RoutedEventArgs e) => Close();

    private void OnUndoClicked(object? sender, RoutedEventArgs e) => Undo();

    private void OnRedoClicked(object? sender, RoutedEventArgs e) => Redo();

    // ---- run ----------------------------------------------------------------------

    private async void OnRunClicked(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (ShortcutRunner.IsRunning(shortcut.Id))
            {
                ShortcutRunner.Cancel(shortcut.Id);
                return;
            }

            await ShortcutRunner.RunAsync(shortcut, ShortcutsRuntime.CreateRunServices(this));
        }
        catch
        {
            // Failures surface through run events.
        }
    }

    private void OnRunEvent(RunEvent runEvent)
    {
        if (runEvent.ShortcutId != shortcut.Id) return;

        if (Dispatcher.UIThread.CheckAccess()) ApplyRunEvent(runEvent);
        else Dispatcher.UIThread.Post(() => ApplyRunEvent(runEvent));
    }

    private void ApplyRunEvent(RunEvent runEvent)
    {
        switch (runEvent.Kind)
        {
            case RunEventKind.Started:
            case RunEventKind.Busy:
                SetRunButtonState(true);
                break;

            case RunEventKind.StepStarted:
                if (runEvent.ActionId != null) SetRowRing(runEvent.ActionId, RunRing.Running);
                break;

            case RunEventKind.StepFinished:
                if (runEvent.ActionId != null)
                {
                    SetRowRing(runEvent.ActionId, RunRing.None);
                    SetRowOutput(runEvent.ActionId, runEvent.Detail, failed: false);
                }
                break;

            case RunEventKind.StepFailed:
                if (runEvent.ActionId != null)
                {
                    SetRowRing(runEvent.ActionId, RunRing.Failed);
                    SetRowOutput(runEvent.ActionId, runEvent.Detail, failed: true);
                }
                break;

            case RunEventKind.Finished:
            case RunEventKind.Cancelled:
                SetRunButtonState(false);
                ScheduleRunCleanup();
                break;
        }
    }

    private void SetRunButtonState(bool running)
    {
        runButtonRunning = running;
        RunGlyphPath.Data = Geometry.Parse(running ? "M7 7h10v10H7z" : "M8 5.5v13l11-6.5z");
        ToolTip.SetTip(RunButton, Loc.Get(running ? "Shortcuts_Menu_Stop" : "Shortcuts_Menu_Run"));
        AutomationProperties.SetName(RunButton, Loc.Get(running ? "Shortcuts_Menu_Stop" : "Shortcuts_Menu_Run"));
    }

    private async void ScheduleRunCleanup()
    {
        runStateVersion++;
        var version = runStateVersion;
        try
        {
            await Task.Delay(1200);
            if (version != runStateVersion) return;
            foreach (var actionId in rowVisuals.Keys.ToList())
            {
                var ring = GetRowRing(actionId);
                if (ring == RunRing.Failed) continue;
                SetRowRing(actionId, RunRing.None);
            }
        }
        catch
        {
            // Cleanup is cosmetic.
        }
    }

    // ---- keyboard -----------------------------------------------------------------

    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        var ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        var shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);

        if (ctrl && e.Key == Key.Z)
        {
            if (shift) Redo(); else Undo();
            e.Handled = true;
        }
        else if (ctrl && e.Key == Key.Y)
        {
            Redo();
            e.Handled = true;
        }
        else if (ctrl && e.Key == Key.F)
        {
            OpenLibrary(focusSearch: true);
            e.Handled = true;
        }
        else if (e.Key == Key.Delete)
        {
            DeleteSelected();
            e.Handled = true;
        }
        else if (ctrl && e.Key == Key.D)
        {
            DuplicateSelected();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && LibraryPanel.IsVisible)
        {
            CloseLibrary();
            e.Handled = true;
        }
    }

    // ---- geometry -----------------------------------------------------------------

    private void TryRestoreGeometry()
    {
        if (geometryRestored) return;

        WindowGeometryEntry? entry;
        Screens? screens;
        try
        {
            entry = WindowGeometryStore.TryGet(GeometryKey);
            if (entry == null) return;
            screens = Screens;
        }
        catch
        {
            return; // platform window not ready yet — retried on the next open
        }

        if (screens == null || screens.All.Count == 0) return;

        var target = screens.Primary ?? screens.All[0];
        var restored = EditWidget.ResolveRestoredGeometry(entry, target.WorkingArea, target.Scaling, MinWidth, MinHeight);

        MinWidth = restored.MinWidth;
        MinHeight = restored.MinHeight;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Width = restored.Width;
        Height = restored.Height;
        Position = restored.Position;
        geometryRestored = true;
    }

    private void SaveGeometry()
    {
        if (WindowState == WindowState.Minimized) return;
        if (!IsVisible) return;
        if (!double.IsFinite(Width) || !double.IsFinite(Height) || Width <= 0 || Height <= 0) return;

        var screens = Screens;
        if (screens == null || screens.All.Count == 0) return;

        var scaling = RenderScaling > 0 ? RenderScaling : 1.0;
        var sizePx = new PixelSize(
            Math.Max(1, (int)Math.Round(Width * scaling)),
            Math.Max(1, (int)Math.Round(Height * scaling)));
        var center = new PixelPoint(Position.X + sizePx.Width / 2, Position.Y + sizePx.Height / 2);
        var screen = screens.Primary ?? screens.All[0];
        var area = screen.WorkingArea;

        WindowGeometryStore.Save(GeometryKey, new WindowGeometryEntry
        {
            WidthDip = Width,
            HeightDip = Height,
            MonitorId = null,
            CenterXRatio = Math.Clamp((center.X - area.X) / (double)area.Width, 0, 1),
            CenterYRatio = Math.Clamp((center.Y - area.Y) / (double)area.Height, 0, 1),
        });
    }

    // ---- store sync ---------------------------------------------------------------

    private void OnStoreChanged(ShortcutsDocument document, object? sender)
    {
        if (ReferenceEquals(sender, this)) return;

        void Handle()
        {
            // The shortcut was deleted elsewhere (card menu / panel): the editor has nothing to edit.
            if (!document.Shortcuts.Any(s => s.Id == shortcut.Id))
            {
                try { Close(); } catch { }
            }
        }

        if (Dispatcher.UIThread.CheckAccess()) Handle();
        else Dispatcher.UIThread.Post(Handle);
    }
}
