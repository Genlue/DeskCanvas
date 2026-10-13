using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.VisualTree;

namespace DeskCanvas.Views.Controls;

/// <summary>
/// Shared drag-handle behavior for chrome-less panels. First consumer: the EditWidget title
/// area; the secondary-panel shell reuses the same contract. Attach
/// <see cref="IsDragHandleProperty"/> to the handle surface (which must have a non-null
/// Background to be hit-testable) and a left press anywhere on it starts the window move —
/// unless the press originates from an interactive control or a subtree marked
/// <see cref="IsInteractiveProperty"/>, so clicking Save / typing in the title / dragging a
/// slider inside the handle never moves the window.
/// </summary>
public static class PanelDragBehavior
{
    /// <summary>Marks the surface whose left press moves the hosting window.</summary>
    public static readonly AttachedProperty<bool> IsDragHandleProperty =
        AvaloniaProperty.RegisterAttached<Control, bool>("IsDragHandle", typeof(PanelDragBehavior));

    /// <summary>Marks a subtree as interactive (business controls the type list can not know).</summary>
    public static readonly AttachedProperty<bool> IsInteractiveProperty =
        AvaloniaProperty.RegisterAttached<Control, bool>("IsInteractive", typeof(PanelDragBehavior));

    public static bool GetIsDragHandle(Control element) => element.GetValue(IsDragHandleProperty);
    public static void SetIsDragHandle(Control element, bool value) => element.SetValue(IsDragHandleProperty, value);

    public static bool GetIsInteractive(Control element) => element.GetValue(IsInteractiveProperty);
    public static void SetIsInteractive(Control element, bool value) => element.SetValue(IsInteractiveProperty, value);

    static PanelDragBehavior()
    {
        IsDragHandleProperty.Changed.AddClassHandler<Control>(OnDragHandleChanged);
    }

    private static void OnDragHandleChanged(Control control, AvaloniaPropertyChangedEventArgs args)
    {
        if (args.NewValue is true)
        {
            control.AddHandler(InputElement.PointerPressedEvent, OnPointerPressed);
        }
        else
        {
            control.RemoveHandler(InputElement.PointerPressedEvent, OnPointerPressed);
        }
    }

    private static void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control handle) return;
        if (e.Handled) return;
        if (!e.GetCurrentPoint(handle).Properties.IsLeftButtonPressed) return;
        if (IsDragExcluded(e.Source as Visual, handle)) return;

        if (TopLevel.GetTopLevel(handle) is Window window)
        {
            window.BeginMoveDrag(e);
            e.Handled = true;
        }
    }

    /// <summary>
    /// True when a press from <paramref name="source"/> inside <paramref name="handle"/> must
    /// not start a drag: any interactive control between the source and the handle (inclusive)
    /// blocks it — a Button and everything inside it, text inputs, selectors, sliders, numeric
    /// boxes, menus, date pickers, scroll bars — as does an explicit
    /// <see cref="IsInteractiveProperty"/> mark. Exposed for the foundation checks.
    /// </summary>
    public static bool IsDragExcluded(Visual? source, Control handle)
    {
        for (var visual = source; visual is not null; visual = visual.GetVisualParent())
        {
            if (visual is Control control && (GetIsInteractive(control) || IsInteractiveControl(control)))
                return true;

            if (ReferenceEquals(visual, handle))
                break;
        }

        return false;
    }

    private static bool IsInteractiveControl(Control control) => control switch
    {
        Button => true,                 // includes ToggleButton / CheckBox / RadioButton
        TextBox => true,
        SelectingItemsControl => true,  // ComboBox / ListBox / MenuItem (MenuItemBase derives here)
        Slider => true,
        NumericUpDown => true,
        CalendarDatePicker => true,     // DatePicker derives from it
        ScrollBar => true,
        _ => false,
    };
}
