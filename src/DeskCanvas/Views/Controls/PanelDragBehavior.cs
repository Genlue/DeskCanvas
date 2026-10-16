using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;

namespace DeskCanvas.Views.Controls;

/// <summary>
/// Host facade of the shared drag-handle behavior (the real implementation lives in
/// <c>DeskCanvas.UI.Controls.PanelDragBehavior</c>): keeps the old namespace and member shape
/// for the host AXAML (EditWidget title area, ShortcutsEditorWindow) and the existing checks.
/// The attached-property identity is aliased — the facade fields ARE the shared property
/// instances — so host AXAML, the shared shell and the checks all read and write the same
/// storage and the same class handlers.
/// </summary>
public static class PanelDragBehavior
{
    /// <summary>Marks the surface whose left press moves the hosting window (shared property identity).</summary>
    public static readonly AttachedProperty<bool> IsDragHandleProperty =
        global::DeskCanvas.UI.Controls.PanelDragBehavior.IsDragHandleProperty;

    /// <summary>Marks a subtree as interactive (shared property identity).</summary>
    public static readonly AttachedProperty<bool> IsInteractiveProperty =
        global::DeskCanvas.UI.Controls.PanelDragBehavior.IsInteractiveProperty;

    public static bool GetIsDragHandle(Control element) =>
        global::DeskCanvas.UI.Controls.PanelDragBehavior.GetIsDragHandle(element);

    public static void SetIsDragHandle(Control element, bool value) =>
        global::DeskCanvas.UI.Controls.PanelDragBehavior.SetIsDragHandle(element, value);

    public static bool GetIsInteractive(Control element) =>
        global::DeskCanvas.UI.Controls.PanelDragBehavior.GetIsInteractive(element);

    public static void SetIsInteractive(Control element, bool value) =>
        global::DeskCanvas.UI.Controls.PanelDragBehavior.SetIsInteractive(element, value);

    /// <summary>True when the press source must not start a drag (shared logic).</summary>
    public static bool IsDragExcluded(Visual? source, Control handle) =>
        global::DeskCanvas.UI.Controls.PanelDragBehavior.IsDragExcluded(source, handle);
}
