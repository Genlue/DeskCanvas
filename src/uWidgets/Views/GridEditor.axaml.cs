using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform;
using uWidgets.Core.Interfaces;
using uWidgets.Core.Models;
using uWidgets.Core.Models.Settings;
using uWidgets.Locales;
using uWidgets.Services;
using GridSettings = uWidgets.Core.Models.Settings.Grid;

namespace uWidgets.Views;

/// <summary>
/// Full-screen editor for the manual grid (<see cref="GridMode.Manual"/>).
/// <para>
/// Covers the target screen (the screen whose per-screen grid is being edited,
/// or the primary screen when <paramref name="screenId"/> is null / legacy).
/// The highlighted grid area shows the saved grid and can be dragged to move the
/// whole grid on that screen;<br/>
/// Right-click the grid area opens the parameters panel: rows / columns / cell
/// size, exact X/Y position inputs, X and Y centering and fine ±1% nudge buttons.<br/>
/// Every change is saved immediately — into the per-screen configuration, or
/// into the global <see cref="AppSettings.Grid"/> when editing the legacy entry.
/// </para>
/// </summary>
public partial class GridEditor : Window
{
    private readonly IAppSettingsProvider appSettingsProvider;
    private readonly ILayoutProvider layoutProvider;
    private readonly DisplayMonitorService displayMonitor;
    private readonly string? screenId;
    private readonly Screen? targetScreen;
    private bool dragging;
    private Point dragStart;
    private double startLeft;
    private double startTop;

    /// <summary>True while the parameter inputs are being populated from the model.</summary>
    private bool syncingInputs;

    public GridEditor(IAppSettingsProvider appSettingsProvider, ILayoutProvider layoutProvider,
        DisplayMonitorService displayMonitor, string? screenId = null, Screen? targetScreen = null)
    {
        this.appSettingsProvider = appSettingsProvider;
        this.layoutProvider = layoutProvider;
        this.displayMonitor = displayMonitor;
        this.screenId = screenId;
        this.targetScreen = targetScreen;
        InitializeComponent();

        Opened += OnOpened;
        KeyDown += OnKeyDown;
        Closed += OnClosed;
        ColumnsInput.ValueChanged += OnCellParameterChanged;
        RowsInput.ValueChanged += OnCellParameterChanged;
        CellInput.ValueChanged += OnCellParameterChanged;
        XInput.ValueChanged += OnPositionInputChanged;
        YInput.ValueChanged += OnPositionInputChanged;

        HintText.Text = Locale.Settings_Advanced_GridEditorHint;
        SaveButton.Content = Locale.Settings_Advanced_GridEditorSave;
        // The dismiss button used to be left without content, so it rendered as a blank chip.
        CloseButton.Content = Locale.Settings_Appearance_Glass_Align_Close;
        CenterButton.Content = Locale.Settings_Advanced_GridEditorCenterX;
        CenterYButton.Content = Locale.Settings_Advanced_GridEditorCenterY;
        NudgeLeftButton.Content = Locale.Settings_Advanced_GridEditorNudgeLeft;
        NudgeRightButton.Content = Locale.Settings_Advanced_GridEditorNudgeRight;
        NudgeUpButton.Content = Locale.Settings_Advanced_GridEditorNudgeUp;
        NudgeDownButton.Content = Locale.Settings_Advanced_GridEditorNudgeDown;

        // Full screen, DPI-correct: geometry is set BEFORE the window is shown so
        // the editor opens as a full-screen window every time (position in physical
        // pixels, size in DIPs = physical / scaling).
        var screen = ResolveTargetScreen();
        if (screen != null)
        {
            Position = new PixelPoint(screen.Bounds.X, screen.Bounds.Y);
            Width = screen.Bounds.Width / ScreenScaling;
            Height = screen.Bounds.Height / ScreenScaling;
        }
        else
        {
            Width = 1920;
            Height = 1080;
        }
    }

    /// <summary>Target screen (provided explicitly or resolved from screenId).</summary>
    private Screen? ResolveTargetScreen()
    {
        if (targetScreen != null) return targetScreen;
        var fromAttached = GetTargetScreen()?.Screen;
        if (fromAttached != null) return fromAttached;
        try
        {
            return Screens?.Primary ?? Screens?.All?.FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// DPI scale used to convert the target screen's physical geometry into this window's DIPs.
    /// <para>
    /// The window's own <see cref="Visual.RenderScaling"/> is authoritative once the platform
    /// window exists; the screen model can disagree with it (remote-desktop sessions, mixed-DPI
    /// multi-monitor setups, a monitor list captured before a resolution change). A DIP width
    /// larger than the physical screen is exactly what pushes the right-aligned parameter panel
    /// and the ✕ button outside the visible area, so this prefers the window value.
    /// </para>
    /// </summary>
    private double Scaling => RenderScaling > 0 ? RenderScaling : ScreenScaling;

    /// <summary>DPI scale reported by the target screen model (used before the window is shown).</summary>
    private double ScreenScaling
    {
        get
        {
            var scaling = ResolveTargetScreen()?.Scaling ?? 1.0;
            return scaling > 0 ? scaling : 1.0;
        }
    }

    /// <summary>The attached screen this editor covers (targetScreen → per-screen config → primary fallback).</summary>
    private AttachedScreen? GetTargetScreen()
    {
        if (targetScreen != null)
        {
            var foundByScreen = displayMonitor.Find(targetScreen);
            if (foundByScreen != null) return foundByScreen;
            return new AttachedScreen(targetScreen,
                new ScreenIdentity(string.Empty, "Screen", targetScreen.Bounds.Width, targetScreen.Bounds.Height, targetScreen.IsPrimary, targetScreen.Scaling),
                screenId != null ? layoutProvider.Get().FindById(screenId) : null);
        }

        if (screenId != null)
        {
            var found = displayMonitor.FindByConfigId(screenId);
            if (found != null) return found;

            var config = layoutProvider.Get().FindById(screenId);
            if (config?.Key != null)
            {
                var matched = displayMonitor.Attached.FirstOrDefault(a => a.Identity.Key == config.Key);
                if (matched != null) return matched;
            }
        }

        return displayMonitor.Attached.FirstOrDefault(s => s.Screen.IsPrimary)
               ?? (Screens?.Primary is { } prim ? displayMonitor.Find(prim) : null);
    }

    /// <summary>The grid being edited: per-screen grid → global grid → default.</summary>
    private GridSettings CurrentGrid =>
        screenId != null
            ? layoutProvider.Get().FindById(screenId)?.Grid ?? appSettingsProvider.Get().Grid ?? GridSettings.Default
            : appSettingsProvider.Get().Grid ?? GridSettings.Default;

    /// <summary>Persist the grid: into the per-screen entry or the global settings.</summary>
    private void SaveGrid(GridSettings grid)
    {
        if (screenId != null)
        {
            var screens = layoutProvider.Get();
            var screen = screens.FindById(screenId);
            if (screen != null)
            {
                layoutProvider.Save(screens.WithScreen(screen with { Grid = grid }));
                // Re-match the attached screens right away so widget windows and the
                // screen settings page see the new grid without waiting for the poller.
                displayMonitor.Refresh();
            }
            // Do NOT overwrite global appSettings when editing a specific screen!
            return;
        }

        appSettingsProvider.Save(appSettingsProvider.Get() with { Grid = grid });
    }

    private void OnOpened(object? sender, EventArgs e)
    {
        ApplyScreenPlacement();
        ApplyGrid();
        SyncParamInputs();
        Avalonia.Threading.Dispatcher.UIThread.Post(() => { ApplyScreenPlacement(); SyncParamInputs(); },
            Avalonia.Threading.DispatcherPriority.Loaded);
    }

    /// <summary>
    /// Force native physical window position and dimensions via Win32 SetWindowPosition
    /// to guarantee full coverage of the target display without DPI mismatch or clamping.
    /// The DIP size is then derived from the window's own render scaling so the laid-out
    /// content matches the forced physical rectangle (see <see cref="Scaling"/>).
    /// </summary>
    private void ApplyScreenPlacement()
    {
        var screen = ResolveTargetScreen();
        if (screen == null) return;

        var bounds = screen.Bounds;

        InteropService.SetWindowPosition(this, bounds.X, bounds.Y, bounds.Width, bounds.Height, topmost: true);

        Width = bounds.Width / Scaling;
        Height = bounds.Height / Scaling;
        Position = new PixelPoint(bounds.X, bounds.Y);
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        Opened -= OnOpened;
        KeyDown -= OnKeyDown;
        Closed -= OnClosed;
        ColumnsInput.ValueChanged -= OnCellParameterChanged;
        RowsInput.ValueChanged -= OnCellParameterChanged;
        CellInput.ValueChanged -= OnCellParameterChanged;
        XInput.ValueChanged -= OnPositionInputChanged;
        YInput.ValueChanged -= OnPositionInputChanged;
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) Close();
    }

    private void OnCloseClicked(object? sender, RoutedEventArgs e) => Close();

    private void OnSaveClicked(object? sender, RoutedEventArgs e)
    {
        SaveGeometry();
        Close();
    }

    // ---------- Grid area drag (move the whole grid) ----------

    private void OnGridPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsRightButtonPressed)
        {
            OpenParamPanel();
            e.Handled = true;
            return;
        }

        dragging = true;
        dragStart = e.GetPosition(GridCanvas);
        startLeft = Canvas.GetLeft(GridVisual);
        startTop = Canvas.GetTop(GridVisual);
        e.Pointer.Capture(GridVisual);
        e.Handled = true;
    }

    private void OnGridMoved(object? sender, PointerEventArgs e)
    {
        if (!dragging) return;

        var position = e.GetPosition(GridCanvas);
        var maxLeft = Math.Max(GridCanvas.Bounds.Width - GridVisual.Bounds.Width, 0);
        var maxTop = Math.Max(GridCanvas.Bounds.Height - GridVisual.Bounds.Height, 0);

        Canvas.SetLeft(GridVisual, Math.Clamp(startLeft + position.X - dragStart.X, 0, maxLeft));
        Canvas.SetTop(GridVisual, Math.Clamp(startTop + position.Y - dragStart.Y, 0, maxTop));
        UpdateInfo();
    }

    private void OnGridReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!dragging) return;
        dragging = false;
        e.Pointer.Capture(null);
        SaveGeometry();
        // Keep the X/Y inputs honest after a drag: the saved percentages just changed.
        SyncParamInputs();
    }

    // ---------- Parameters panel ----------

    /// <summary>
    /// Right-click anywhere (grid or surrounding dim area) brings the parameters panel back
    /// if it was hidden. The panel is visible from the start, so the position buttons and
    /// "Save &amp; close" are always reachable — right-clicking the grid used to be the only way
    /// to reveal them, which read as "the buttons disappeared".
    /// </summary>
    private void OnRootPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsRightButtonPressed) return;
        OpenParamPanel();
        e.Handled = true;
    }

    private void OpenParamPanel()
    {
        ParamPanel.IsVisible = true;
        SyncParamInputs();
    }

    /// <summary>
    /// Populate the panel's inputs from the current grid without treating the assignment as a
    /// user edit (otherwise opening the panel would immediately re-save the grid).
    /// </summary>
    private void SyncParamInputs()
    {
        var grid = CurrentGrid;
        syncingInputs = true;
        try
        {
            ColumnsInput.Value = grid.Columns;
            RowsInput.Value = grid.Rows;
            CellInput.Value = (decimal) grid.CellPercent;
            XInput.Value = (decimal) grid.XPercent;
            YInput.Value = (decimal) grid.YPercent;
        }
        finally
        {
            syncingInputs = false;
        }

        ParamTitle.Text = Locale.Settings_Advanced_GridEdit;
    }

    private void OnCellParameterChanged(object? sender, NumericUpDownValueChangedEventArgs e)
    {
        if (syncingInputs) return;
        if (ColumnsInput.Value is not { } columns || RowsInput.Value is not { } rows || CellInput.Value is not { } cell)
            return;

        var newGrid = CurrentGrid with
        {
            Columns = Math.Max(1, (int) columns),
            Rows = Math.Max(1, (int) rows),
            CellPercent = Math.Max(0.01, (double) cell)
        };

        if (newGrid != CurrentGrid)
            SaveGrid(newGrid);

        ApplyGrid();
    }

    // ---------- Position inputs, X/Y centering & nudges ----------

    /// <summary>
    /// Manual numeric fine-tuning of the grid origin (percent of the working area): the drag
    /// and the ±1% nudge buttons only move in whole percents, these inputs set it exactly.
    /// </summary>
    private void OnPositionInputChanged(object? sender, NumericUpDownValueChangedEventArgs e)
    {
        if (syncingInputs) return;
        if (XInput.Value is not { } x || YInput.Value is not { } y) return;

        var grid = CurrentGrid;
        var newGrid = grid with
        {
            XPercent = Math.Clamp((double) x, 0, 100),
            YPercent = Math.Clamp((double) y, 0, 100)
        };

        if (newGrid != grid)
        {
            SaveGrid(newGrid);
            ApplyGrid();
        }
    }

    private void OnCenterClicked(object? sender, RoutedEventArgs e)
    {
        var grid = CurrentGrid;
        var centeredX = Math.Clamp((100 - grid.Columns * grid.CellPercent) / 2.0, 0, 100);
        var newGrid = grid with { XPercent = centeredX };

        if (newGrid != grid)
        {
            SaveGrid(newGrid);
            ApplyGrid();
        }
    }

    private void OnCenterYClicked(object? sender, RoutedEventArgs e)
    {
        var grid = CurrentGrid;
        var newGrid = grid with { YPercent = CenteredYPercent(grid) };

        if (newGrid != grid)
        {
            SaveGrid(newGrid);
            ApplyGrid();
        }
    }

    /// <summary>
    /// The Y that centers the grid vertically in the working area. <see cref="GridSettings.CellPercent"/>
    /// is a percentage of the working-area <b>width</b> while <see cref="GridSettings.YPercent"/> is one
    /// of its <b>height</b>, so the grid's pixel height (rows × cell) must be converted through the
    /// screen's aspect ratio before it can be subtracted from 100% of the height.
    /// </summary>
    private double CenteredYPercent(GridSettings grid)
    {
        var work = ResolveTargetScreen()?.WorkingArea;
        if (work is { } area && area.Height > 0)
        {
            var heightPercent = grid.Rows * grid.CellPercent * area.Width / area.Height;
            return Math.Clamp((100 - heightPercent) / 2.0, 0, 100);
        }
        return Math.Clamp((100 - grid.Rows * grid.CellPercent) / 2.0, 0, 100);
    }

    private void OnNudgeLeftClicked(object? sender, RoutedEventArgs e) => Nudge(-1, 0);
    private void OnNudgeRightClicked(object? sender, RoutedEventArgs e) => Nudge(1, 0);
    private void OnNudgeUpClicked(object? sender, RoutedEventArgs e) => Nudge(0, -1);
    private void OnNudgeDownClicked(object? sender, RoutedEventArgs e) => Nudge(0, 1);

    /// <summary>
    /// Fine-tune the grid origin by ±1% of the working area.
    /// </summary>
    private void Nudge(int dx, int dy)
    {
        var grid = CurrentGrid;
        var newGrid = grid with
        {
            XPercent = Math.Clamp(grid.XPercent + dx, 0, 100),
            YPercent = Math.Clamp(grid.YPercent + dy, 0, 100)
        };

        if (newGrid != grid)
        {
            SaveGrid(newGrid);
            ApplyGrid();
        }
    }

    // ---------- Geometry ----------

    /// <summary>
    /// Position and size the grid visual from the saved percentage settings
    /// (all canvas coordinates in DIPs).
    /// </summary>
    private void ApplyGrid()
    {
        var grid = CurrentGrid;
        var (cellDip, xDip, yDip) = ResolveInWindow(grid);

        Canvas.SetLeft(GridVisual, xDip);
        Canvas.SetTop(GridVisual, yDip);
        GridVisual.Width = grid.Columns * cellDip;
        GridVisual.Height = grid.Rows * cellDip;
        RebuildCells(grid);
        UpdateInfo();
    }

    /// <summary>
    /// Resolve the saved grid into editor-window coordinates (DIPs).
    /// </summary>
    private (double Cell, double X, double Y) ResolveInWindow(GridSettings grid)
    {
        var screen = ResolveTargetScreen();
        if (screen is not { } target)
            return (96, 100, 100);

        var bounds = target.Bounds;
        var area = target.WorkingArea;
        var (cell, gridX, gridY) = GridMetrics.Resolve(grid, area.X, area.Y, area.Width, area.Height);
        var scaling = Scaling;

        // physical → DIP
        return (cell / scaling, (gridX - bounds.X) / scaling, (gridY - bounds.Y) / scaling);
    }

    /// <summary>
    /// Save the current grid visual geometry back as percentages (of the working area).
    /// </summary>
    private void SaveGeometry()
    {
        var grid = CurrentGrid;
        var screen = ResolveTargetScreen();
        if (screen is not { } target) return;

        var bounds = target.Bounds;
        var area = target.WorkingArea;
        if (area.Width <= 0 || area.Height <= 0) return;

        var scaling = Scaling;
        var cellPx = Math.Min(
            GridVisual.Bounds.Width / Math.Max(grid.Columns, 1),
            GridVisual.Bounds.Height / Math.Max(grid.Rows, 1)) * scaling;

        var newGrid = grid with
        {
            XPercent = Math.Clamp((bounds.X + Canvas.GetLeft(GridVisual) * scaling - area.X) * 100.0 / area.Width, 0, 100),
            YPercent = Math.Clamp((bounds.Y + Canvas.GetTop(GridVisual) * scaling - area.Y) * 100.0 / area.Height, 0, 100),
            CellPercent = Math.Max(0.01, cellPx * 100.0 / area.Width)
        };

        if (newGrid != grid)
            SaveGrid(newGrid);
    }

    private void RebuildCells(GridSettings grid)
    {
        CellsGrid.Children.Clear();
        CellsGrid.RowDefinitions.Clear();
        CellsGrid.ColumnDefinitions.Clear();

        if (grid.Columns <= 0 || grid.Rows <= 0) return;

        for (var column = 0; column < grid.Columns; column++)
            CellsGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        for (var row = 0; row < grid.Rows; row++)
            CellsGrid.RowDefinitions.Add(new RowDefinition(GridLength.Star));

        for (var row = 0; row < grid.Rows; row++)
        {
            for (var column = 0; column < grid.Columns; column++)
            {
                var cell = new Border
                {
                    BorderBrush = new SolidColorBrush(Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF)),
                    BorderThickness = new Thickness(0.5),
                    Margin = new Thickness(0.5)
                };
                Avalonia.Controls.Grid.SetRow(cell, row);
                Avalonia.Controls.Grid.SetColumn(cell, column);
                CellsGrid.Children.Add(cell);
            }
        }
    }

    private void UpdateInfo()
    {
        var grid = CurrentGrid;
        var (cellDip, _, _) = ResolveInWindow(grid);
        var target = GetTargetScreen();
        var screenName = target?.Config?.DisplayName
                         ?? (target?.Identity.FriendlyName.Length > 0 ? target.Identity.FriendlyName : null)
                         ?? (target?.Screen.IsPrimary == true ? "Primary" : null);
        var screenHeader = screenName != null && target != null
            ? $"[{screenName} · {target.Screen.Bounds.Width}×{target.Screen.Bounds.Height}] "
            : "";
        InfoText.Text = screenHeader + string.Format(Locale.Settings_Advanced_GridEditorInfo, grid.Columns, grid.Rows, (int) Math.Round(cellDip * Scaling));
    }
}