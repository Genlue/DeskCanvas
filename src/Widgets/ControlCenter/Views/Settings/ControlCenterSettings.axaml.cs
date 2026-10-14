using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using ControlCenter.Models;
using DeskCanvas.Core.Interfaces;
using DeskCanvas.Core.Models;

namespace ControlCenter.Views.Settings;

public partial class ControlCenterSettings : UserControl
{
    private readonly IWidgetLayoutProvider? widgetLayoutProvider;
    private ControlCenterModel model;
    private int activeGridCols = 4;
    private int activeGridRows = 2;
    private List<ControlTilePlacement> currentPlacements = [];
    private ControlItemType? selectedType;
    private bool isInitializing = true;

    // Drag / Resize state on ComposerCanvas
    private enum InteractionMode
    {
        None,
        MoveExisting,
        ResizeExisting,
        DragFromDock
    }

    private InteractionMode interactionMode = InteractionMode.None;
    private ControlTilePlacement? interactingTile;
    private ControlItemType dockDragType;
    private Point dragStartCanvasPt;
    private Point pointerOffsetInTile;
    private Border? activeDragVisual;
    private Border? previewGhost;
    private int previewCol;
    private int previewRow;
    private int previewColSpan = 1;
    private int previewRowSpan = 1;
    private bool previewValid;

    private const double CellGap = 6.0;

    private double CellSize => activeGridRows >= 4 ? 72.0 : 84.0;

    public ControlCenterSettings() : this(null) { }

    public ControlCenterSettings(IWidgetLayoutProvider? widgetLayoutProvider)
    {
        this.widgetLayoutProvider = widgetLayoutProvider;
        model = ReadModel(widgetLayoutProvider?.Get()) ?? new ControlCenterModel();

        InitializeComponent();

        DetectInitialTierFromWidget();
        InitWheelStepCombo();

        isInitializing = false;
        LoadTierLayout(activeGridCols, activeGridRows);
    }

    private void DetectInitialTierFromWidget()
    {
        if (widgetLayoutProvider?.Get() is { } layout && layout.Width > 0 && layout.Height > 0)
        {
            double aspect = (double)layout.Width / layout.Height;
            if (aspect >= 1.45)
            {
                activeGridCols = 4;
                activeGridRows = 2;
            }
            else if (layout.Width >= 280 && layout.Height >= 280)
            {
                activeGridCols = 4;
                activeGridRows = 4;
            }
            else
            {
                activeGridCols = 2;
                activeGridRows = 2;
            }
        }
    }

    private void InitWheelStepCombo()
    {
        int step = model.WheelStep;
        int idx = step switch
        {
            1 => 0,
            2 => 1,
            5 => 2,
            10 => 3,
            _ => 1
        };
        WheelStepCombo.SelectedIndex = idx;
    }

    private void LoadTierLayout(int cols, int rows)
    {
        activeGridCols = cols;
        activeGridRows = rows;
        currentPlacements = model.GetLayoutForGrid(cols, rows).Select(p => p.Clone()).ToList();

        if (selectedType.HasValue && currentPlacements.All(p => p.Type != selectedType.Value))
            selectedType = currentPlacements.FirstOrDefault()?.Type;
        else if (!selectedType.HasValue)
            selectedType = currentPlacements.FirstOrDefault()?.Type;

        Tab2x2Btn.Classes.Set("active", cols == 2 && rows == 2);
        Tab4x2Btn.Classes.Set("active", cols == 4 && rows == 2);
        Tab4x4Btn.Classes.Set("active", cols == 4 && rows == 4);
        PresetVerticalBtn.IsVisible = cols == 4 && rows == 2;

        RenderComposer();
    }

    private void RenderComposer()
    {
        double cell = CellSize;
        ComposerCanvas.Width = activeGridCols * cell;
        ComposerCanvas.Height = activeGridRows * cell;
        ComposerCanvas.Children.Clear();

        // 1. Draw empty background grid cells
        for (int r = 0; r < activeGridRows; r++)
        {
            for (int c = 0; c < activeGridCols; c++)
            {
                var cellBorder = new Border
                {
                    Width = cell - CellGap,
                    Height = cell - CellGap,
                    CornerRadius = new CornerRadius(8),
                    Background = new SolidColorBrush(Color.Parse("#0D808080")),
                    BorderBrush = new SolidColorBrush(Color.Parse("#22808080")),
                    BorderThickness = new Thickness(1),
                    Child = new TextBlock
                    {
                        Text = $"{c + 1},{r + 1}",
                        FontSize = 9.5,
                        Opacity = 0.28,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center
                    }
                };
                Canvas.SetLeft(cellBorder, c * cell + CellGap / 2);
                Canvas.SetTop(cellBorder, r * cell + CellGap / 2);
                ComposerCanvas.Children.Add(cellBorder);
            }
        }

        // 2. Preview ghost (for drag & resize feedback)
        previewGhost = new Border
        {
            CornerRadius = new CornerRadius(9),
            BorderThickness = new Thickness(2),
            IsVisible = false,
            IsHitTestVisible = false,
            ZIndex = 50
        };
        ComposerCanvas.Children.Add(previewGhost);

        // 3. Draw placed tiles
        foreach (var tile in currentPlacements)
        {
            var card = CreatePlacedTileCard(tile, cell);
            ComposerCanvas.Children.Add(card);
        }

        // 4. Update Selection Toolbar & Unplaced Dock
        UpdateSelectionToolbar();
        RebuildDock();
    }

    private Border CreatePlacedTileCard(ControlTilePlacement tile, double cell)
    {
        bool isSelected = selectedType == tile.Type;
        var accentBrush = GetBrush("SystemControlForegroundAccentBrush", Color.Parse("#007AFF"));
        var highFgBrush = GetBrush("SystemControlForegroundBaseHighBrush", Colors.White);

        double w = tile.ColumnSpan * cell - CellGap;
        double h = tile.RowSpan * cell - CellGap;

        var card = new Border
        {
            Width = w,
            Height = h,
            CornerRadius = new CornerRadius(9),
            Background = new SolidColorBrush(isSelected ? Color.Parse("#2E007AFF") : Color.Parse("#26808080")),
            BorderBrush = isSelected ? accentBrush : new SolidColorBrush(Color.Parse("#38808080")),
            BorderThickness = new Thickness(isSelected ? 1.8 : 1),
            Cursor = new Cursor(StandardCursorType.SizeAll),
            ClipToBounds = true,
            ZIndex = isSelected ? 20 : 10
        };

        var rootGrid = new Grid();

        // Center content: Icon + Name + Span badge
        var centerStack = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Spacing = 2,
            Margin = new Thickness(6),
            IsHitTestVisible = false
        };

        var icon = new PathIcon
        {
            Data = SafeParseGeometry(ControlCenterModel.GetItemIconPath(tile.Type, true)),
            Width = 18,
            Height = 18,
            Foreground = isSelected ? accentBrush : highFgBrush,
            HorizontalAlignment = HorizontalAlignment.Center
        };

        var title = new TextBlock
        {
            Text = ControlCenterModel.GetItemShortName(tile.Type),
            FontSize = 11.5,
            FontWeight = FontWeight.SemiBold,
            Foreground = highFgBrush,
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        };

        var badge = new TextBlock
        {
            Text = FormatSpanBadge(tile.ColumnSpan, tile.RowSpan),
            FontSize = 9.5,
            Opacity = 0.62,
            Foreground = highFgBrush,
            HorizontalAlignment = HorizontalAlignment.Center
        };

        centerStack.Children.Add(icon);
        centerStack.Children.Add(title);
        centerStack.Children.Add(badge);
        rootGrid.Children.Add(centerStack);

        // Top-right Remove ('✕') button
        var removeBtn = new Button
        {
            Content = "✕",
            Width = 20,
            Height = 20,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(10),
            Background = new SolidColorBrush(Color.Parse("#28000000")),
            BorderThickness = new Thickness(0),
            FontSize = 10,
            Foreground = highFgBrush,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 3, 3, 0),
            Cursor = new Cursor(StandardCursorType.Hand)
        };
        ToolTip.SetTip(removeBtn, Locales.Locale.ControlCenter_TileRemoveTip);
        removeBtn.Click += (_, e) =>
        {
            e.Handled = true;
            currentPlacements.Remove(tile);
            if (selectedType == tile.Type)
                selectedType = currentPlacements.FirstOrDefault()?.Type;
            CommitAndRefresh();
        };
        rootGrid.Children.Add(removeBtn);

        // Bottom-right Resize Handle ('◢')
        var resizeGrip = new Border
        {
            Width = 18,
            Height = 18,
            Background = Brushes.Transparent,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Cursor = new Cursor(StandardCursorType.BottomRightCorner),
            Child = new TextBlock
            {
                Text = "◢",
                FontSize = 10,
                Opacity = 0.55,
                Foreground = isSelected ? accentBrush : highFgBrush,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(0, 0, 3, 1)
            }
        };
        ToolTip.SetTip(resizeGrip, Locales.Locale.ControlCenter_TileResizeTip);
        resizeGrip.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
            e.Handled = true;
            selectedType = tile.Type;
            StartResizeInteraction(tile, card, e);
        };
        rootGrid.Children.Add(resizeGrip);

        card.Child = rootGrid;

        // Dragging the tile body moves it
        card.PointerPressed += (_, e) =>
        {
            if (e.Handled || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
            e.Handled = true;
            selectedType = tile.Type;
            StartMoveInteraction(tile, card, e);
        };

        Canvas.SetLeft(card, tile.Column * cell + CellGap / 2);
        Canvas.SetTop(card, tile.Row * cell + CellGap / 2);
        return card;
    }

    private void RebuildDock()
    {
        DockWrapPanel.Children.Clear();
        var placedSet = currentPlacements.Select(p => p.Type).ToHashSet();
        var allTypes = Enum.GetValues<ControlItemType>();
        var highFgBrush = GetBrush("SystemControlForegroundBaseHighBrush", Colors.White);
        var accentBrush = GetBrush("SystemControlForegroundAccentBrush", Color.Parse("#007AFF"));

        bool anyUnplaced = false;
        foreach (var type in allTypes)
        {
            if (placedSet.Contains(type)) continue;
            anyUnplaced = true;

            var chip = new Border
            {
                CornerRadius = new CornerRadius(7),
                Background = new SolidColorBrush(Color.Parse("#1C808080")),
                BorderBrush = new SolidColorBrush(Color.Parse("#30808080")),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(9, 5),
                Margin = new Thickness(0, 0, 6, 6),
                Cursor = new Cursor(StandardCursorType.Hand)
            };

            var row = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6
            };
            row.Children.Add(new PathIcon
            {
                Data = SafeParseGeometry(ControlCenterModel.GetItemIconPath(type, true)),
                Width = 14,
                Height = 14,
                Foreground = accentBrush,
                VerticalAlignment = VerticalAlignment.Center
            });
            row.Children.Add(new TextBlock
            {
                Text = $"+ {ControlCenterModel.GetItemDisplayName(type)}",
                FontSize = 11.5,
                FontWeight = FontWeight.Medium,
                Foreground = highFgBrush,
                VerticalAlignment = VerticalAlignment.Center
            });
            chip.Child = row;

            chip.PointerPressed += (_, e) =>
            {
                if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
                e.Handled = true;
                StartDockDragOrClick(type, e);
            };

            DockWrapPanel.Children.Add(chip);
        }

        if (!anyUnplaced)
        {
            DockWrapPanel.Children.Add(new TextBlock
            {
                Text = Locales.Locale.ControlCenter_DockEmpty,
                FontSize = 11,
                Opacity = 0.55,
                Margin = new Thickness(0, 2)
            });
        }
    }

    private void UpdateSelectionToolbar()
    {
        var tile = selectedType.HasValue
            ? currentPlacements.FirstOrDefault(p => p.Type == selectedType.Value)
            : null;

        if (tile == null)
        {
            SelectedTileLabel.Text = Locales.Locale.ControlCenter_SelectionNone;
            SpanButtonsPanel.IsEnabled = false;
            Span1x1Btn.Classes.Set("active", false);
            Span2x1Btn.Classes.Set("active", false);
            Span1x2Btn.Classes.Set("active", false);
            Span2x2Btn.Classes.Set("active", false);
            return;
        }

        SelectedTileLabel.Text = string.Format(
            Locales.Locale.ControlCenter_SelectionFormat,
            ControlCenterModel.GetItemDisplayName(tile.Type),
            FormatSpanBadge(tile.ColumnSpan, tile.RowSpan));
        SpanButtonsPanel.IsEnabled = true;

        SetSpanBtnActive(Span1x1Btn, tile.ColumnSpan == 1 && tile.RowSpan == 1);
        SetSpanBtnActive(Span2x1Btn, tile.ColumnSpan == 2 && tile.RowSpan == 1);
        SetSpanBtnActive(Span1x2Btn, tile.ColumnSpan == 1 && tile.RowSpan == 2);
        SetSpanBtnActive(Span2x2Btn, tile.ColumnSpan == 2 && tile.RowSpan == 2);
    }

    private static void SetSpanBtnActive(Button btn, bool active)
    {
        btn.FontWeight = active ? FontWeight.Bold : FontWeight.Normal;
        btn.BorderColorIfActive(active);
    }

    private void StartMoveInteraction(ControlTilePlacement tile, Border visual, PointerPressedEventArgs e)
    {
        interactionMode = InteractionMode.MoveExisting;
        interactingTile = tile;
        activeDragVisual = visual;
        dragStartCanvasPt = e.GetPosition(ComposerCanvas);
        pointerOffsetInTile = e.GetPosition(visual);
        activeDragVisual.ZIndex = 100;
        activeDragVisual.Opacity = 0.85;

        previewCol = tile.Column;
        previewRow = tile.Row;
        previewColSpan = tile.ColumnSpan;
        previewRowSpan = tile.RowSpan;
        previewValid = true;

        e.Pointer.Capture(ComposerCanvas);
        UpdateSelectionToolbar();
    }

    private void StartResizeInteraction(ControlTilePlacement tile, Border visual, PointerPressedEventArgs e)
    {
        interactionMode = InteractionMode.ResizeExisting;
        interactingTile = tile;
        activeDragVisual = visual;
        dragStartCanvasPt = e.GetPosition(ComposerCanvas);

        previewCol = tile.Column;
        previewRow = tile.Row;
        previewColSpan = tile.ColumnSpan;
        previewRowSpan = tile.RowSpan;
        previewValid = true;

        UpdatePreviewGhost(previewCol, previewRow, previewColSpan, previewRowSpan, true);
        e.Pointer.Capture(ComposerCanvas);
        UpdateSelectionToolbar();
    }

    private void StartDockDragOrClick(ControlItemType type, PointerPressedEventArgs e)
    {
        interactionMode = InteractionMode.DragFromDock;
        dockDragType = type;
        dragStartCanvasPt = e.GetPosition(ComposerCanvas);

        int defaultColSpan = type is ControlItemType.Brightness or ControlItemType.Volume && activeGridCols >= 4 ? 2 : 1;
        previewColSpan = defaultColSpan;
        previewRowSpan = 1;
        previewValid = false;

        e.Pointer.Capture(ComposerCanvas);
    }

    private void OnCanvasPointerMoved(object? sender, PointerEventArgs e)
    {
        if (interactionMode == InteractionMode.None) return;

        double cell = CellSize;
        var pt = e.GetPosition(ComposerCanvas);

        if (interactionMode == InteractionMode.MoveExisting && interactingTile != null && activeDragVisual != null)
        {
            double newLeft = pt.X - pointerOffsetInTile.X;
            double newTop = pt.Y - pointerOffsetInTile.Y;
            Canvas.SetLeft(activeDragVisual, newLeft);
            Canvas.SetTop(activeDragVisual, newTop);

            int targetCol = (int)Math.Round(newLeft / cell);
            int targetRow = (int)Math.Round(newTop / cell);
            targetCol = Math.Clamp(targetCol, 0, activeGridCols - interactingTile.ColumnSpan);
            targetRow = Math.Clamp(targetRow, 0, activeGridRows - interactingTile.RowSpan);

            previewCol = targetCol;
            previewRow = targetRow;
            previewColSpan = interactingTile.ColumnSpan;
            previewRowSpan = interactingTile.RowSpan;
            previewValid = CanMoveOrSwap(interactingTile, targetCol, targetRow);

            UpdatePreviewGhost(previewCol, previewRow, previewColSpan, previewRowSpan, previewValid);
            e.Handled = true;
        }
        else if (interactionMode == InteractionMode.ResizeExisting && interactingTile != null)
        {
            double tileLeft = interactingTile.Column * cell;
            double tileTop = interactingTile.Row * cell;
            int targetColSpan = (int)Math.Ceiling(Math.Max(cell * 0.5, pt.X - tileLeft) / cell);
            int targetRowSpan = (int)Math.Ceiling(Math.Max(cell * 0.5, pt.Y - tileTop) / cell);

            targetColSpan = Math.Clamp(targetColSpan, 1, Math.Min(2, activeGridCols - interactingTile.Column));
            targetRowSpan = Math.Clamp(targetRowSpan, 1, Math.Min(2, activeGridRows - interactingTile.Row));

            previewCol = interactingTile.Column;
            previewRow = interactingTile.Row;
            previewColSpan = targetColSpan;
            previewRowSpan = targetRowSpan;
            previewValid = CanResizeTile(interactingTile, interactingTile.Column, interactingTile.Row, targetColSpan, targetRowSpan);

            UpdatePreviewGhost(previewCol, previewRow, previewColSpan, previewRowSpan, previewValid);
            e.Handled = true;
        }
        else if (interactionMode == InteractionMode.DragFromDock)
        {
            int targetCol = (int)Math.Floor(pt.X / cell);
            int targetRow = (int)Math.Floor(pt.Y / cell);

            if (targetCol >= 0 && targetCol < activeGridCols && targetRow >= 0 && targetRow < activeGridRows)
            {
                int colSpan = Math.Min(previewColSpan, activeGridCols - targetCol);
                int rowSpan = Math.Min(previewRowSpan, activeGridRows - targetRow);
                var occupied = BuildOccupiedMatrix(exclude: null);

                if (!ControlCenterModel.CanFit(occupied, activeGridCols, activeGridRows, targetCol, targetRow, colSpan, rowSpan)
                    && ControlCenterModel.CanFit(occupied, activeGridCols, activeGridRows, targetCol, targetRow, 1, 1))
                {
                    colSpan = 1;
                    rowSpan = 1;
                }

                previewCol = targetCol;
                previewRow = targetRow;
                previewColSpan = colSpan;
                previewRowSpan = rowSpan;
                previewValid = ControlCenterModel.CanFit(occupied, activeGridCols, activeGridRows, targetCol, targetRow, colSpan, rowSpan);
                UpdatePreviewGhost(previewCol, previewRow, previewColSpan, previewRowSpan, previewValid);
            }
            else if (previewGhost != null)
            {
                previewGhost.IsVisible = false;
                previewValid = false;
            }
            e.Handled = true;
        }
    }

    private void OnCanvasPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (interactionMode == InteractionMode.None) return;

        var mode = interactionMode;
        interactionMode = InteractionMode.None;
        e.Pointer.Capture(null);

        if (previewGhost != null)
            previewGhost.IsVisible = false;

        var endPt = e.GetPosition(ComposerCanvas);
        bool wasClick = Point.Distance(dragStartCanvasPt, endPt) < 5.0;

        if (mode == InteractionMode.MoveExisting && interactingTile != null)
        {
            if (!wasClick && previewValid)
            {
                ApplyMoveOrSwap(interactingTile, previewCol, previewRow);
                CommitAndRefresh();
            }
            else
            {
                RenderComposer();
            }
        }
        else if (mode == InteractionMode.ResizeExisting && interactingTile != null)
        {
            if (previewValid && (previewColSpan != interactingTile.ColumnSpan || previewRowSpan != interactingTile.RowSpan))
            {
                interactingTile.ColumnSpan = previewColSpan;
                interactingTile.RowSpan = previewRowSpan;
                CommitAndRefresh();
            }
            else
            {
                RenderComposer();
            }
        }
        else if (mode == InteractionMode.DragFromDock)
        {
            if (!wasClick && previewValid)
            {
                currentPlacements.Add(new ControlTilePlacement
                {
                    Type = dockDragType,
                    Column = previewCol,
                    Row = previewRow,
                    ColumnSpan = previewColSpan,
                    RowSpan = previewRowSpan
                });
                selectedType = dockDragType;
                CommitAndRefresh();
            }
            else if (wasClick)
            {
                TryAutoPlaceFromDock(dockDragType);
            }
            else
            {
                RenderComposer();
            }
        }

        interactingTile = null;
        activeDragVisual = null;
        e.Handled = true;
    }

    private void TryAutoPlaceFromDock(ControlItemType type)
    {
        var occupied = BuildOccupiedMatrix(exclude: null);
        int prefColSpan = type is ControlItemType.Brightness or ControlItemType.Volume && activeGridCols >= 4 ? 2 : 1;

        if (ControlCenterModel.TryFindFirstFreeSlot(occupied, activeGridCols, activeGridRows, prefColSpan, 1, out int c, out int r))
        {
            currentPlacements.Add(new ControlTilePlacement
            {
                Type = type,
                Column = c,
                Row = r,
                ColumnSpan = prefColSpan,
                RowSpan = 1
            });
            selectedType = type;
            CommitAndRefresh();
            return;
        }

        if (ControlCenterModel.TryFindFirstFreeSlot(occupied, activeGridCols, activeGridRows, 1, 1, out int c1, out int r1))
        {
            currentPlacements.Add(new ControlTilePlacement
            {
                Type = type,
                Column = c1,
                Row = r1,
                ColumnSpan = 1,
                RowSpan = 1
            });
            selectedType = type;
            CommitAndRefresh();
        }
    }

    private bool[,] BuildOccupiedMatrix(ControlTilePlacement? exclude)
    {
        var occupied = new bool[activeGridCols, activeGridRows];
        foreach (var item in currentPlacements)
        {
            if (ReferenceEquals(item, exclude)) continue;
            ControlCenterModel.MarkOccupied(occupied, item.Column, item.Row, item.ColumnSpan, item.RowSpan, true);
        }
        return occupied;
    }

    private bool CanMoveOrSwap(ControlTilePlacement moving, int targetCol, int targetRow)
    {
        var occupied = BuildOccupiedMatrix(exclude: moving);
        if (ControlCenterModel.CanFit(occupied, activeGridCols, activeGridRows, targetCol, targetRow, moving.ColumnSpan, moving.RowSpan))
            return true;

        // Check if there is a single colliding tile of the exact same span that we can swap with
        var colliding = GetCollidingTiles(moving, targetCol, targetRow, moving.ColumnSpan, moving.RowSpan);
        if (colliding.Count == 1)
        {
            var other = colliding[0];
            if (other.ColumnSpan == moving.ColumnSpan && other.RowSpan == moving.RowSpan)
                return true;
        }
        return false;
    }

    private void ApplyMoveOrSwap(ControlTilePlacement moving, int targetCol, int targetRow)
    {
        var occupied = BuildOccupiedMatrix(exclude: moving);
        if (ControlCenterModel.CanFit(occupied, activeGridCols, activeGridRows, targetCol, targetRow, moving.ColumnSpan, moving.RowSpan))
        {
            moving.Column = targetCol;
            moving.Row = targetRow;
            return;
        }

        var colliding = GetCollidingTiles(moving, targetCol, targetRow, moving.ColumnSpan, moving.RowSpan);
        if (colliding.Count == 1)
        {
            var other = colliding[0];
            if (other.ColumnSpan == moving.ColumnSpan && other.RowSpan == moving.RowSpan)
            {
                (other.Column, moving.Column) = (moving.Column, targetCol);
                (other.Row, moving.Row) = (moving.Row, targetRow);
            }
        }
    }

    private bool CanResizeTile(ControlTilePlacement tile, int col, int row, int newColSpan, int newRowSpan)
    {
        var occupied = BuildOccupiedMatrix(exclude: tile);
        return ControlCenterModel.CanFit(occupied, activeGridCols, activeGridRows, col, row, newColSpan, newRowSpan);
    }

    private List<ControlTilePlacement> GetCollidingTiles(ControlTilePlacement exclude, int col, int row, int colSpan, int rowSpan)
    {
        var list = new List<ControlTilePlacement>();
        foreach (var item in currentPlacements)
        {
            if (ReferenceEquals(item, exclude)) continue;
            bool overlapsX = col < item.Column + item.ColumnSpan && col + colSpan > item.Column;
            bool overlapsY = row < item.Row + item.RowSpan && row + rowSpan > item.Row;
            if (overlapsX && overlapsY)
                list.Add(item);
        }
        return list;
    }

    private void UpdatePreviewGhost(int col, int row, int colSpan, int rowSpan, bool valid)
    {
        if (previewGhost == null) return;
        double cell = CellSize;
        previewGhost.IsVisible = true;
        previewGhost.Width = colSpan * cell - CellGap;
        previewGhost.Height = rowSpan * cell - CellGap;
        previewGhost.Background = new SolidColorBrush(valid ? Color.Parse("#2830D158") : Color.Parse("#28FF453A"));
        previewGhost.BorderBrush = new SolidColorBrush(valid ? Color.Parse("#CC30D158") : Color.Parse("#CCFF453A"));
        Canvas.SetLeft(previewGhost, col * cell + CellGap / 2);
        Canvas.SetTop(previewGhost, row * cell + CellGap / 2);
    }

    private void OnSpanButtonClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string spanTag } || !selectedType.HasValue) return;
        var tile = currentPlacements.FirstOrDefault(p => p.Type == selectedType.Value);
        if (tile == null) return;

        var parts = spanTag.Split('x');
        if (parts.Length != 2 || !int.TryParse(parts[0], out int newColSpan) || !int.TryParse(parts[1], out int newRowSpan))
            return;

        newColSpan = Math.Clamp(newColSpan, 1, activeGridCols);
        newRowSpan = Math.Clamp(newRowSpan, 1, activeGridRows);

        int testCol = Math.Min(tile.Column, activeGridCols - newColSpan);
        int testRow = Math.Min(tile.Row, activeGridRows - newRowSpan);

        var occupied = BuildOccupiedMatrix(exclude: tile);
        if (ControlCenterModel.CanFit(occupied, activeGridCols, activeGridRows, testCol, testRow, newColSpan, newRowSpan))
        {
            tile.Column = testCol;
            tile.Row = testRow;
            tile.ColumnSpan = newColSpan;
            tile.RowSpan = newRowSpan;
            CommitAndRefresh();
        }
        else if (ControlCenterModel.TryFindFirstFreeSlot(occupied, activeGridCols, activeGridRows, newColSpan, newRowSpan, out int freeC, out int freeR))
        {
            tile.Column = freeC;
            tile.Row = freeR;
            tile.ColumnSpan = newColSpan;
            tile.RowSpan = newRowSpan;
            CommitAndRefresh();
        }
    }

    private void OnTier2x2Clicked(object? sender, RoutedEventArgs e) => LoadTierLayout(2, 2);
    private void OnTier4x2Clicked(object? sender, RoutedEventArgs e) => LoadTierLayout(4, 2);
    private void OnTier4x4Clicked(object? sender, RoutedEventArgs e) => LoadTierLayout(4, 4);

    private void OnPresetVerticalClicked(object? sender, RoutedEventArgs e)
    {
        if (activeGridCols == 4 && activeGridRows == 2)
        {
            currentPlacements = ControlCenterModel.CreateDefault4x2Vertical();
            CommitAndRefresh();
        }
    }

    private void OnResetDefaultClicked(object? sender, RoutedEventArgs e)
    {
        currentPlacements = (activeGridCols, activeGridRows) switch
        {
            (2, 2) => ControlCenterModel.CreateDefault2x2(),
            (4, 4) => ControlCenterModel.CreateDefault4x4(),
            _ => ControlCenterModel.CreateDefault4x2Horizontal()
        };
        CommitAndRefresh();
    }

    private void OnWheelStepChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (isInitializing) return;
        if (WheelStepCombo.SelectedItem is ComboBoxItem { Tag: string tag } && int.TryParse(tag, out int step))
        {
            model.WheelStep = step;
            Save();
        }
    }

    private void CommitAndRefresh()
    {
        model.SetLayoutForGrid(activeGridCols, activeGridRows, currentPlacements);
        RenderComposer();
        Save();
    }

    private void Save()
    {
        if (widgetLayoutProvider == null) return;
        var current = widgetLayoutProvider.Get();
        var element = JsonSerializer.SerializeToElement(model);
        widgetLayoutProvider.Save(current with { Settings = element });
    }

    private static ControlCenterModel? ReadModel(WidgetLayout? layout)
    {
        if (layout?.Settings is not { ValueKind: JsonValueKind.Object } settings)
            return null;
        try
        {
            return settings.Deserialize<ControlCenterModel>();
        }
        catch
        {
            return null;
        }
    }

    private static string FormatSpanBadge(int colSpan, int rowSpan) => (colSpan, rowSpan) switch
    {
        (1, 1) => Locales.Locale.ControlCenter_Span1x1,
        (2, 1) => Locales.Locale.ControlCenter_Span2x1,
        (1, 2) => Locales.Locale.ControlCenter_Span1x2,
        (2, 2) => Locales.Locale.ControlCenter_Span2x2,
        _ => $"{colSpan}×{rowSpan}"
    };

    private IBrush GetBrush(string key, Color fallback)
    {
        if (this.TryFindResource(key, ActualThemeVariant, out var res) && res is IBrush b)
            return b;
        if (Application.Current?.TryFindResource(key, ActualThemeVariant, out var appRes) == true && appRes is IBrush ab)
            return ab;
        return new SolidColorBrush(fallback);
    }

    private static StreamGeometry? SafeParseGeometry(string path)
    {
        try { return StreamGeometry.Parse(path); }
        catch { return null; }
    }
}

internal static class ButtonStyleExtensions
{
    public static void BorderColorIfActive(this Button btn, bool active)
    {
        btn.BorderBrush = new SolidColorBrush(active ? Color.Parse("#007AFF") : Color.Parse("#30808080"));
        btn.Background = new SolidColorBrush(active ? Color.Parse("#22007AFF") : Color.Parse("#00000000"));
    }
}
