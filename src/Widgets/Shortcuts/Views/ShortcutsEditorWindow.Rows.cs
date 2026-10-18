using System.Globalization;
using System.Text.Json;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Shortcuts.Models;
using Shortcuts.Services;
using Shortcuts.Views.Controls;

namespace Shortcuts.Views;

/// <summary>Action-row construction, inline parameter chips, drag-to-reorder and row menus.</summary>
public partial class ShortcutsEditorWindow
{
    private enum RunRing
    {
        None,
        Running,
        Failed,
    }

    private enum RowKind
    {
        Action,
        Else,
        End,
    }

    private sealed class RowRef
    {
        public Control Root { get; set; } = null!;
        public RowKind Kind { get; set; }
        public ShortcutAction Action { get; set; } = null!;

        /// <summary>Containing list: the action's siblings, or the list the else/end marker terminates.</summary>
        public List<ShortcutAction> Parent { get; set; } = null!;
    }

    private readonly List<RowRef> rowRefs = [];
    private readonly Dictionary<string, (Border Block, TextBlock Output)> rowVisuals = new(StringComparer.Ordinal);
    private readonly Dictionary<string, RunRing> rowRings = new(StringComparer.Ordinal);
    private string? dropHintId;

    /// <summary>Lazy id→action index for the current rows, built at most once per rebuild.</summary>
    private Dictionary<string, ShortcutAction>? actionsById;

    private static readonly IBrush TransparentRing = Brushes.Transparent;
    private static readonly IBrush RunningRing = new SolidColorBrush(Color.Parse("#E6FFFFFF"));
    private static readonly IBrush SelectedRing = new SolidColorBrush(Color.Parse("#80FFFFFF"));
    private static readonly IBrush FailedRing = new SolidColorBrush(Color.Parse("#FFC9C2"));
    private static readonly IBrush FailedText = new SolidColorBrush(Color.Parse("#A32D2D"));

    private RowRef? dragRow;
    private RowRef? dropRow;
    private bool dropAfter;

    // ---- row construction ---------------------------------------------------------

    private void RebuildRows()
    {
        // A rebuild can tear down the row currently being dragged; drop the drag state and
        // release the pointer capture first so no discarded row keeps the capture (or is kept
        // alive by it) after its visual is gone.
        ResetDragState();

        rowRefs.Clear();
        rowVisuals.Clear();
        rowRings.Clear();
        actionsById = null;
        dropHintId = null;
        RowsHost.Children.Clear();

        BuildListInto(RowsHost, shortcut.Actions);
        EmptyHint.IsVisible = shortcut.Actions.Count == 0;
    }

    private void BuildListInto(Panel host, List<ShortcutAction> list)
    {
        for (var i = 0; i < list.Count; i++)
        {
            var action = list[i];
            var definition = ActionCatalog.Get(action.Type);
            var rowRef = new RowRef
            {
                Kind = RowKind.Action,
                Action = action,
                Parent = list,
            };
            rowRef.Root = BuildActionRow(rowRef, definition);
            rowRefs.Add(rowRef);
            host.Children.Add(rowRef.Root);

            if (definition is not { } container || container.Type is not ("repeat" or "if"))
                continue;

            var color = Visuals.Brush(ActionCatalog.CategoryColor(container.Category));
            var body = new StackPanel { Spacing = 6 };
            var bracket = new Border
            {
                BorderThickness = new Thickness(2, 0, 0, 0),
                BorderBrush = color,
                CornerRadius = new CornerRadius(0, 0, 0, 10),
                Margin = new Thickness(20, 0, 0, 0),
                Padding = new Thickness(10, 2, 0, 6),
                Child = body,
            };
            host.Children.Add(bracket);

            if (action.Collapsed)
            {
                body.IsVisible = false;
                continue;
            }

            BuildListInto(body, action.Children);

            if (container.Type == "if")
            {
                var elseRef = new RowRef
                {
                    Kind = RowKind.Else,
                    Action = action,
                    Parent = action.Children,
                };
                elseRef.Root = BuildMarkerRow(Loc.Get("Shortcuts_Else"), color);
                rowRefs.Add(elseRef);
                body.Children.Add(elseRef.Root);

                BuildListInto(body, action.ElseChildren);
            }

            var endRef = new RowRef
            {
                Kind = RowKind.End,
                Action = action,
                Parent = container.Type == "if" ? action.ElseChildren : action.Children,
            };
            endRef.Root = BuildMarkerRow(
                Loc.Get(container.Type == "repeat" ? "Shortcuts_EndRepeat" : "Shortcuts_EndIf"), color);
            rowRefs.Add(endRef);
            body.Children.Add(endRef.Root);
        }
    }

    private Control BuildActionRow(RowRef rowRef, ActionDefinition? definition)
    {
        var action = rowRef.Action;
        var categoryColor = definition != null
            ? ActionCatalog.CategoryColor(definition.Category)
            : "#5F5E5A";
        var title = definition != null ? Loc.Get(definition.TitleKey) : action.Type;

        var block = new Border
        {
            Background = Visuals.Brush(categoryColor),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(11, 9, 11, 9),
            BorderThickness = new Thickness(2),
            BorderBrush = TransparentRing,
            Cursor = new Cursor(StandardCursorType.Hand),
        };

        var content = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*,Auto") };

        var glyph = new GlyphView
        {
            Data = GlyphLibrary.GetGeometry(definition?.Glyph),
            Stroke = Brushes.White,
            StrokeThickness = 1.7,
            Width = 15,
            Height = 15,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(glyph, 0);

        var titleText = new TextBlock
        {
            Text = title,
            Foreground = Brushes.White,
            FontSize = 13,
            FontWeight = FontWeight.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 8, 0),
        };
        Grid.SetColumn(titleText, 1);

        var chips = new WrapPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(chips, 2);
        BuildChips(chips, action, definition);

        content.Children.Add(glyph);
        content.Children.Add(titleText);
        content.Children.Add(chips);

        if (definition is { Type: "repeat" or "if" })
        {
            var chevron = new GlyphView
            {
                Data = GlyphLibrary.GetGeometry(action.Collapsed ? "chevron-right" : "chevron-down"),
                Stroke = Brushes.White,
                StrokeThickness = 2,
                Width = 12,
                Height = 12,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Opacity = 0.85,
            };
            // Same hit-surface reasoning as the grip: the transparent border receives the press,
            // so collapsing no longer requires hitting the stroke pixels of a 12px glyph.
            var chevronHit = new Border
            {
                Background = Brushes.Transparent,
                Width = 18,
                Height = 22,
                Margin = new Thickness(4, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Cursor = new Cursor(StandardCursorType.Hand),
                Child = chevron,
            };
            Grid.SetColumn(chevronHit, 3);
            chevronHit.PointerPressed += (_, e) =>
            {
                e.Handled = true;
                action.Collapsed = !action.Collapsed;
                ShortcutsStore.Upsert(shortcut, this);
                RebuildRows();
            };
            content.Children.Add(chevronHit);
        }

        block.Child = content;

        block.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
            selectedActionId = action.Id;
            UpdateAllRingVisuals();
        };

        var menu = new ContextMenu { Items = { } };
        foreach (var item in BuildRowMenuItems(rowRef)) menu.Items.Add(item);
        block.ContextMenu = menu;

        // Output line under the block (step result / error).
        var output = new TextBlock
        {
            FontSize = 10.5,
            Opacity = 0.7,
            Margin = new Thickness(4, 3, 4, 0),
            TextTrimming = TextTrimming.CharacterEllipsis,
            IsVisible = false,
        };
        var blockColumn = new StackPanel { Spacing = 0 };
        blockColumn.Children.Add(block);
        blockColumn.Children.Add(output);
        Grid.SetColumn(blockColumn, 1);
        Grid.SetColumnSpan(blockColumn, 2);

        // Grip (drag handle). A bare GlyphView only renders strokes and Avalonia hit-tests what
        // is actually drawn, so the grip could only be grabbed exactly on a dash segment; the
        // transparent hit surface gives it the ~18x26 grab target the drag interaction needs.
        var grip = new GlyphView
        {
            Data = GlyphLibrary.GetGeometry("grip"),
            Stroke = Brushes.Gray,
            StrokeThickness = 1.6,
            Width = 12,
            Height = 14,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6, 0),
            Opacity = 0.45,
        };
        var gripHit = new Border
        {
            Background = Brushes.Transparent,
            Width = 18,
            Height = 26,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 5, 0, 0),
            Cursor = new Cursor(StandardCursorType.SizeAll),
            Child = grip,
        };
        Grid.SetColumn(gripHit, 0);
        gripHit.PointerPressed += (_, e) => BeginDrag(rowRef, e);
        gripHit.PointerMoved += (_, e) => UpdateDrag(e);
        gripHit.PointerReleased += (_, e) => EndDrag(e);
        ToolTip.SetTip(gripHit, Loc.Get("Shortcuts_Grip"));

        // Overflow menu button.
        var moreButton = new Button
        {
            Classes = { "toolbar-icon" },
            Width = 26,
            Height = 26,
            Opacity = 0,
            IsHitTestVisible = false,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(4, 4, 0, 0),
        };
        moreButton.Content = new GlyphView
        {
            Data = Geometry.Parse("M6 12h.01M12 12h.01M18 12h.01"),
            Stroke = Brushes.Gray,
            StrokeThickness = 2.4,
            Width = 14,
            Height = 14,
        };
        AutomationProperties.SetName(moreButton, Loc.Get("Shortcuts_RowMenu"));
        ToolTip.SetTip(moreButton, Loc.Get("Shortcuts_RowMenu"));
        Grid.SetColumn(moreButton, 3);

        // Four columns: grip | block (spans 1-2) | OWN column for the overflow button. With only
        // three columns defined the button's Grid.Column=3 was clamped into the last column and
        // sat on top of the block's right edge — its invisible (opacity 0) hit target swallowed
        // clicks on the last parameter chip (the openUrl URL chip) and covered the container
        // chevron entirely. Keep the fourth definition in sync with Grid.SetColumn(moreButton, 3).
        var rowGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto") };
        rowGrid.Children.Add(gripHit);
        rowGrid.Children.Add(blockColumn);
        rowGrid.Children.Add(moreButton);

        var flyout = new MenuFlyout();
        foreach (var item in BuildRowMenuItems(rowRef)) flyout.Items.Add(item);
        FlyoutBase.SetAttachedFlyout(moreButton, flyout);
        moreButton.Click += (_, _) => FlyoutBase.ShowAttachedFlyout(moreButton);

        // The overflow button reveals on hover — and only then accepts hits; an invisible but
        // hit-testable button must never sit over anything the user wants to click.
        rowGrid.PointerEntered += (_, _) =>
        {
            moreButton.Opacity = 0.7;
            moreButton.IsHitTestVisible = true;
        };
        rowGrid.PointerExited += (_, _) =>
        {
            moreButton.Opacity = 0;
            moreButton.IsHitTestVisible = false;
        };

        rowVisuals[action.Id] = (block, output);
        UpdateRowRingVisual(action.Id);
        return rowGrid;
    }

    private static Control BuildMarkerRow(string label, IBrush color)
    {
        var text = new TextBlock
        {
            Text = label,
            FontSize = 11.5,
            FontWeight = FontWeight.SemiBold,
            Foreground = color,
            Opacity = 0.9,
            Margin = new Thickness(24, 3, 0, 3),
        };
        return new Panel { Children = { text } };
    }

    private List<Control> BuildRowMenuItems(RowRef rowRef)
    {
        var action = rowRef.Action;

        var up = new MenuItem { Header = Loc.Get("Shortcuts_Action_MoveUp") };
        up.Click += (_, _) => MoveAction(rowRef, -1);

        var down = new MenuItem { Header = Loc.Get("Shortcuts_Action_MoveDown") };
        down.Click += (_, _) => MoveAction(rowRef, +1);

        var duplicate = new MenuItem { Header = Loc.Get("Shortcuts_Action_Duplicate") };
        duplicate.Click += (_, _) => DuplicateAction(action);

        var delete = new MenuItem { Header = Loc.Get("Shortcuts_Action_Delete") };
        delete.Click += (_, _) => DeleteAction(action.Id);

        return [up, down, new Separator(), duplicate, delete];
    }

    // ---- row operations -----------------------------------------------------------

    private void MoveAction(RowRef rowRef, int delta)
    {
        var list = rowRef.Parent;
        var index = list.IndexOf(rowRef.Action);
        var target = index + delta;
        if (index < 0 || target < 0 || target >= list.Count) return;

        list.RemoveAt(index);
        list.Insert(target, rowRef.Action);
        selectedActionId = rowRef.Action.Id;
        CommitChange();
    }

    private void DuplicateAction(ShortcutAction action)
    {
        var location = FindParent(shortcut.Actions, action.Id);
        if (location == null) return;

        var copy = CloneAction(action);
        location.Value.List.Insert(location.Value.Index + 1, copy);
        selectedActionId = copy.Id;
        CommitChange();
    }

    private void DeleteAction(string actionId)
    {
        var location = FindParent(shortcut.Actions, actionId);
        if (location == null) return;

        location.Value.List.RemoveAt(location.Value.Index);
        if (selectedActionId == actionId) selectedActionId = null;
        CommitChange();
    }

    private void DeleteSelected()
    {
        if (selectedActionId != null) DeleteAction(selectedActionId);
    }

    private void DuplicateSelected()
    {
        if (selectedActionId == null) return;
        var action = ShortcutTree.Find(shortcut.Actions, selectedActionId);
        if (action != null) DuplicateAction(action);
    }

    private static ShortcutAction CloneAction(ShortcutAction source)
    {
        var json = JsonSerializer.Serialize(source, DocumentJson.Options);
        var copy = JsonSerializer.Deserialize<ShortcutAction>(json, DocumentJson.Options)!;
        ReassignIds(copy);
        return copy;
    }

    private static void ReassignIds(ShortcutAction action)
    {
        action.Id = ShortcutDefinition.NewId();
        foreach (var child in action.Children) ReassignIds(child);
        foreach (var child in action.ElseChildren) ReassignIds(child);
    }

    private (List<ShortcutAction> List, int Index)? FindParent(List<ShortcutAction> roots, string actionId)
    {
        for (var i = 0; i < roots.Count; i++)
        {
            if (roots[i].Id == actionId) return (roots, i);
            var inChildren = FindParent(roots[i].Children, actionId);
            if (inChildren != null) return inChildren;
            var inElse = FindParent(roots[i].ElseChildren, actionId);
            if (inElse != null) return inElse;
        }

        return null;
    }

    // ---- rings / outputs ----------------------------------------------------------

    private void SetRowRing(string actionId, RunRing ring)
    {
        rowRings[actionId] = ring;
        UpdateRowRingVisual(actionId);
    }

    private RunRing GetRowRing(string actionId) =>
        rowRings.GetValueOrDefault(actionId, RunRing.None);

    private void SetRowOutput(string actionId, string? text, bool failed)
    {
        if (!rowVisuals.TryGetValue(actionId, out var visuals)) return;

        visuals.Output.Text = text ?? string.Empty;
        visuals.Output.Foreground = failed ? FailedText : null;
        visuals.Output.IsVisible = !string.IsNullOrEmpty(text);
    }

    private void UpdateAllRingVisuals()
    {
        foreach (var actionId in rowVisuals.Keys.ToList())
            UpdateRowRingVisual(actionId);
    }

    private void UpdateRowRingVisual(string actionId)
    {
        if (!rowVisuals.TryGetValue(actionId, out var visuals)) return;

        var ring = TransparentRing;
        var state = GetRowRing(actionId);
        if (state == RunRing.Failed) ring = FailedRing;
        else if (state == RunRing.Running) ring = RunningRing;
        else if (actionId == dropHintId) ring = RunningRing;
        else if (actionId == selectedActionId) ring = SelectedRing;

        visuals.Block.BorderBrush = ring;
    }

    // ---- drag to reorder ----------------------------------------------------------

    private void BeginDrag(RowRef rowRef, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        dragRow = rowRef;
        dragPointerCapture = e.Pointer;
        e.Pointer.Capture(e.Source as IInputElement);
        rowRef.Root.Opacity = 0.45;
        e.Handled = true;
    }

    private IPointer? dragPointerCapture;

    private void UpdateDrag(PointerEventArgs e)
    {
        if (dragRow == null) return;

        var y = e.GetPosition(RowsHost).Y;
        RowRef? target = null;
        var after = false;

        foreach (var candidate in rowRefs)
        {
            var top = candidate.Root.TranslatePoint(new Point(0, 0), RowsHost)?.Y;
            if (top == null) continue;
            var height = candidate.Root.Bounds.Height;
            if (height <= 0) continue;
            if (y >= top && y <= top + height)
            {
                target = candidate;
                after = y > top + height / 2;
                break;
            }
        }

        dropRow = target;
        dropAfter = after;

        var hint = target is { Kind: RowKind.Action } ? target.Action.Id : null;
        if (hint != dropHintId)
        {
            var previous = dropHintId;
            dropHintId = hint;
            if (previous != null) UpdateRowRingVisual(previous);
            if (hint != null) UpdateRowRingVisual(hint);
        }
    }

    private void EndDrag(PointerReleasedEventArgs e)
    {
        var source = dragRow;
        var target = dropRow;
        var after = dropAfter;
        ResetDragState();

        if (source == null || target == null) return;
        if (source.Kind != RowKind.Action) return;
        if (ReferenceEquals(source, target)) return;
        if (ShortcutTree.Contains(source.Action, target.Action.Id)) return;

        var destination = ResolveDropPosition(target, after);
        if (destination == null) return;

        var (destinationList, destinationIndex) = destination.Value;
        var location = FindParent(shortcut.Actions, source.Action.Id);
        if (location == null) return;

        var sourceList = location.Value.List;
        var sourceIndex = location.Value.Index;
        sourceList.RemoveAt(sourceIndex);
        if (ReferenceEquals(sourceList, destinationList) && sourceIndex < destinationIndex)
            destinationIndex--;

        destinationIndex = Math.Clamp(destinationIndex, 0, destinationList.Count);
        destinationList.Insert(destinationIndex, source.Action);
        selectedActionId = source.Action.Id;
        CommitChange();
    }

    private (List<ShortcutAction> List, int Index)? ResolveDropPosition(RowRef target, bool after)
    {
        switch (target.Kind)
        {
            case RowKind.Action:
            {
                var location = FindParent(shortcut.Actions, target.Action.Id);
                if (location == null) return null;
                return (location.Value.List, location.Value.Index + (after ? 1 : 0));
            }

            case RowKind.Else:
                // Before the marker: end of the then-branch; after it: start of the else-branch.
                return after
                    ? (target.Action.ElseChildren, 0)
                    : (target.Action.Children, target.Action.Children.Count);

            case RowKind.End:
                return (target.Parent, target.Parent.Count);
        }

        return null;
    }

    private void ResetDragState()
    {
        if (dragRow != null) dragRow.Root.Opacity = 1.0;

        // Release the capture explicitly: the field is only a bookkeeping slot, and a drag
        // interrupted before PointerReleased (rebuild, window close) must not leave the grip
        // capturing the pointer.
        try { dragPointerCapture?.Capture(null); } catch { }
        dragPointerCapture = null;
        dragRow = null;
        dropRow = null;

        if (dropHintId != null)
        {
            var previous = dropHintId;
            dropHintId = null;
            UpdateRowRingVisual(previous);
        }
    }

    // ---- parameter chips ----------------------------------------------------------

    private void BuildChips(Panel host, ShortcutAction action, ActionDefinition? definition)
    {
        if (definition == null) return;

        foreach (var spec in definition.Params)
        {
            action.Params.TryGetValue(spec.Key, out var value);
            var hasValue = value != null && (value.IsVariable || !string.IsNullOrEmpty(value.Text));
            if (!spec.Primary && !hasValue) continue;

            // Wrapped chips must not exceed the block's remaining line: the MaxWidth cap keeps a
            // single long value inside the WrapPanel and the ellipsis + tooltip keep it readable
            // instead of the old silent ClipToBounds hard-cut.
            var text = ChipText(action, spec, value);
            var chip = new Button
            {
                Classes = { "chip-btn" },
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 5, 2),
            };
            chip.Content = new TextBlock
            {
                Text = text,
                FontSize = 11,
                MaxWidth = 320,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Foreground = Brushes.White,
                VerticalAlignment = VerticalAlignment.Center,
            };
            chip.Click += (_, _) => OpenParamEditor(action, spec, chip);
            ToolTip.SetTip(chip, text);
            host.Children.Add(chip);
        }
    }

    private string ChipText(ShortcutAction action, ParamSpec spec, ShortcutValue? value)
    {
        if (value == null) return Loc.Get("Shortcuts_Param_Missing");

        if (value.IsVariable)
        {
            if (value.SourceActionId == ValueKinds.LastResult)
                return Loc.Get("Shortcuts_Param_LastResult");

            var source = FindActionById(value.SourceActionId);
            var sourceTitle = source == null
                ? Loc.Get("Shortcuts_Param_Variable")
                : Loc.Get(ActionCatalog.Get(source.Type)?.TitleKey);
            return Loc.Format("Shortcuts_Param_ResultOf", sourceTitle);
        }

        var raw = value.Text ?? string.Empty;
        if (raw.Length == 0) return Loc.Get("Shortcuts_Param_Missing");

        if (spec.Kind == ParamKind.Choice)
        {
            var labelKey = ActionCatalog.OptionLabelKey(spec, raw);
            return string.IsNullOrEmpty(labelKey) ? raw : Loc.Get(labelKey);
        }

        if (spec.Kind == ParamKind.Hotkey &&
            HotkeyGesture.TryParse(raw, requireModifier: false, out var hotkey, out _))
        {
            return hotkey.Text;
        }

        return spec.UnitKey != null ? Loc.Format(spec.UnitKey, raw) : raw;
    }

    /// <summary>
    /// Resolve an action by id for chip labels. The id→action index is built lazily on first use
    /// and discarded on rebuild, so a shortcut without variable chips pays nothing while one with
    /// many pays a single walk instead of one full-tree <c>Find</c> per chip.
    /// </summary>
    private ShortcutAction? FindActionById(string? id)
    {
        if (id == null) return null;

        if (actionsById == null)
        {
            actionsById = new Dictionary<string, ShortcutAction>(StringComparer.Ordinal);
            foreach (var action in ShortcutTree.Flatten(shortcut.Actions))
                actionsById[action.Id] = action;
        }

        return actionsById.GetValueOrDefault(id);
    }

    private void OpenParamEditor(ShortcutAction action, ParamSpec spec, Control anchor)
    {
        var panel = new StackPanel { Spacing = 8, Width = 300, MinWidth = 220 };
        panel.Children.Add(new TextBlock
        {
            Text = Loc.Get(spec.LabelKey),
            FontSize = 12,
            Opacity = 0.7,
        });

        var flyout = new Flyout { Content = panel, Placement = PlacementMode.Bottom };

        // The control the flyout focuses once it is open (assigned per branch below): focusing
        // hands keyboard routing to the flyout's own tree, so the editor window's Delete /
        // Ctrl+D shortcuts can no longer fire while the user is editing a parameter value.
        Control? initialFocus = null;

        void Commit(ShortcutValue newValue, bool close)
        {
            action.Params[spec.Key] = newValue;
            CommitChange();
            if (close) flyout.Hide();
        }

        switch (spec.Kind)
        {
            case ParamKind.Choice:
            {
                var combo = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
                var current = action.Params.GetValueOrDefault(spec.Key);
                var selectedIndex = 0;
                var options = spec.Options ?? [];
                for (var i = 0; i < options.Count; i++)
                {
                    combo.Items.Add(new ComboBoxItem { Content = Loc.Get(options[i].LabelKey), Tag = options[i].Value });
                    if (current != null && !current.IsVariable && current.Text == options[i].Value)
                        selectedIndex = i;
                }

                combo.SelectedIndex = selectedIndex;
                combo.SelectionChanged += (_, _) =>
                {
                    if (combo.SelectedItem is ComboBoxItem { Tag: string option })
                        Commit(ShortcutValue.Literal(option), close: true);
                };
                panel.Children.Add(combo);
                initialFocus = combo;
                break;
            }

            case ParamKind.Number:
            {
                var current = action.Params.GetValueOrDefault(spec.Key);
                var box = new NumericUpDown
                {
                    Minimum = (decimal)spec.Min,
                    Maximum = (decimal)spec.Max,
                    Increment = 1,
                    FormatString = "0.###",
                    Value = current is { IsVariable: false } && double.TryParse(current.Text, NumberStyles.Float,
                        CultureInfo.InvariantCulture, out var parsed) ? (decimal)parsed : (decimal)((spec.Min + spec.Max) / 2),
                };

                // Commit once on close instead of per tick: rebuilding rows mid-edit would
                // detach the flyout's anchor under the user's cursor.
                var userTouched = false;
                var variableChosen = false;
                box.ValueChanged += (_, _) => userTouched = true;
                flyout.Closed += (_, _) =>
                {
                    if (!userTouched || variableChosen) return;
                    if (box.Value is { } value)
                        Commit(ShortcutValue.Literal(value.ToString(CultureInfo.InvariantCulture)), close: false);
                };
                panel.Children.Add(box);
                panel.Children.Add(BuildVariableRow(action, spec, (newValue, close) =>
                {
                    variableChosen = true;
                    Commit(newValue, close);
                }));
                initialFocus = box;
                break;
            }

            case ParamKind.Hotkey:
            {
                var current = action.Params.GetValueOrDefault(spec.Key);
                var original = current is { IsVariable: false } ? current.Text?.Trim() ?? string.Empty : string.Empty;
                var capture = new HotkeyCaptureBox
                {
                    Value = original,
                    RequireModifier = false,
                    IsReadOnly = false,
                    ShowStatus = true,
                };

                // Do not rebuild rows while the flyout is open: committing there would destroy
                // the anchor and close the capture box mid-recording. Keep the last valid value
                // and write it when the flyout closes.
                var pending = original;
                var changed = false;
                capture.ValueChanged += (_, e) =>
                {
                    pending = e.Value ?? string.Empty;
                    changed = true;
                };
                panel.Children.Add(capture);

                flyout.Closed += (_, _) =>
                {
                    if (!changed) return;
                    if (pending.Length == 0) action.Params.Remove(spec.Key);
                    else action.Params[spec.Key] = ShortcutValue.Literal(pending);
                    CommitChange();
                };

                initialFocus = capture;
                break;
            }

            default:
            {
                var current = action.Params.GetValueOrDefault(spec.Key);
                var box = new TextBox { Text = current is { IsVariable: false } ? current.Text ?? string.Empty : string.Empty };

                // A variable picked from the menu closes the flyout while the text box still
                // holds the old literal; its lose-focus commit must not overwrite the choice.
                var variableChosen = false;
                // While the native picker runs, the window deactivation may steal focus from the
                // box; that lose-focus commit must not fire either (the picked value is committed
                // explicitly when the dialog returns).
                var browsing = false;
                void CommitText(bool close)
                {
                    if (variableChosen || browsing) return;

                    var text = box.Text ?? string.Empty;

                    // Nothing meaningful to write: an unset parameter being dismissed, or a
                    // variable parameter whose box was never typed into (it shows empty).
                    if (text.Length == 0 && (current is null || current.IsVariable)) return;

                    Commit(ShortcutValue.Literal(text), close);
                }

                box.LostFocus += (_, _) => CommitText(false);
                box.KeyDown += (_, e) =>
                {
                    if (e.Key != Key.Enter) return;
                    CommitText(true);
                    e.Handled = true;
                };

                var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
                box.Width = 190;
                row.Children.Add(box);

                if (spec.Kind is ParamKind.FilePath or ParamKind.FolderPath)
                    row.Children.Add(BuildBrowseButton(spec, box, Commit, value => browsing = value));

                row.Children.Add(BuildVariableButton(action, (newValue, close) =>
                {
                    variableChosen = true;
                    Commit(newValue, close);
                }));
                panel.Children.Add(row);
                initialFocus = box;
                break;
            }
        }

        // Focus once the popup is actually open: a synchronous Focus() right after ShowAt is
        // dropped because the flyout's content is not attached to a visual tree yet; the posted
        // retry covers hosts where even the Opened callback runs before the popup can take
        // focus. With focus inside the flyout's own tree, the editor window's Delete / Ctrl+D
        // shortcuts no longer fire while the user is editing a parameter value.
        flyout.Opened += (_, _) =>
        {
            FocusEditor(initialFocus);
            Dispatcher.UIThread.Post(() => FocusEditor(initialFocus), DispatcherPriority.Input);
        };
        flyout.ShowAt(anchor);
    }

    /// <summary>The hotkey capture needs focus inside its text box, not on the wrapper control.</summary>
    private static void FocusEditor(Control? editor)
    {
        if (editor is HotkeyCaptureBox capture) capture.FocusInput();
        else editor?.Focus();
    }

    private Button BuildBrowseButton(ParamSpec spec, TextBox box, Action<ShortcutValue, bool> commit,
        Action<bool>? onBrowsingChanged = null)
    {
        // Focusable=false is essential: pressing a focusable button would steal focus from the
        // text box, whose lose-focus commit rebuilds the action rows — that destroys the
        // flyout's anchor chip and closes the flyout mid-press, so the button's Click never
        // fired and the picker silently never opened.
        var button = new Button
        {
            Content = Loc.Get("Shortcuts_Param_Browse"),
            FontSize = 12,
            Focusable = false,
        };
        button.Click += (_, _) =>
        {
            // Native IFileOpenDialog (same approach as the Folders widget): the managed storage
            // provider never surfaced a dialog inside this app's custom-chrome windows.
            onBrowsingChanged?.Invoke(true);
            try
            {
                var ownerHandle = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
                var titleKey = spec.Kind switch
                {
                    ParamKind.FolderPath => "Shortcuts_Param_PickFolder",
                    ParamKind.FilePath => "Shortcuts_Param_PickFile",
                    _ => "Shortcuts_Param_PickApp",
                };
                var title = Loc.Get(titleKey);
                var picked = spec.Kind == ParamKind.FolderPath
                    ? FileDialogs.PickFolder(ownerHandle, title)
                    : FileDialogs.PickFile(ownerHandle, title);

                if (!string.IsNullOrEmpty(picked))
                {
                    box.Text = picked;
                    commit(ShortcutValue.Literal(picked), false);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Shortcuts] Browse failed: {ex.Message}");
            }
            finally
            {
                onBrowsingChanged?.Invoke(false);
            }
        };
        return button;
    }

    private StackPanel BuildVariableRow(ShortcutAction action, ParamSpec spec, Action<ShortcutValue, bool> commit)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        row.Children.Add(BuildVariableButton(action, commit));
        return row;
    }

    private Button BuildVariableButton(ShortcutAction action, Action<ShortcutValue, bool> commit)
    {
        // Non-focusable for the same reason as the browse button: the focus change would
        // trigger the text box's lose-focus commit, whose row rebuild closes the flyout.
        var button = new Button
        {
            Content = Loc.Get("Shortcuts_Param_UseVariable"),
            FontSize = 12,
            Focusable = false,
        };
        button.Click += (_, _) =>
        {
            var menu = new MenuFlyout();

            var last = new MenuItem { Header = Loc.Get("Shortcuts_Param_LastResult") };
            last.Click += (_, _) => commit(ShortcutValue.Variable(ValueKinds.LastResult), true);
            menu.Items.Add(last);
            menu.Items.Add(new Separator());

            var any = false;
            foreach (var candidate in VariableCandidates(action))
            {
                any = true;
                var definition = ActionCatalog.Get(candidate.Type);
                var title = definition != null ? Loc.Get(definition.TitleKey) : candidate.Type;
                var item = new MenuItem { Header = title };
                var candidateId = candidate.Id;
                item.Click += (_, _) => commit(ShortcutValue.Variable(candidateId), true);
                menu.Items.Add(item);
            }

            if (!any)
            {
                menu.Items.Add(new MenuItem { Header = Loc.Get("Shortcuts_Param_NoCandidates"), IsEnabled = false });
            }

            menu.ShowAt(button);
        };
        return button;
    }

    private IEnumerable<ShortcutAction> VariableCandidates(ShortcutAction current)
    {
        var flat = ShortcutTree.Flatten(shortcut.Actions).ToList();
        var selfIndex = flat.FindIndex(a => a.Id == current.Id);

        for (var i = 0; i < flat.Count; i++)
        {
            if (i == selfIndex) break;
            var candidate = flat[i];
            if (ShortcutTree.Contains(current, candidate.Id)) continue;
            yield return candidate;
        }
    }
}
