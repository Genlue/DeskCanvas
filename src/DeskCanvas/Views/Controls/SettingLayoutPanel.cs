using Avalonia;
using Avalonia.Controls;
using SettingLayoutSlot = DeskCanvas.UI.Controls.SettingLayoutSlot;

namespace DeskCanvas.Views.Controls;

/// <summary>
/// Compatibility facade for existing settings pages and widget binaries.
/// The measuring/arranging engine belongs to DeskCanvas.UI; the facade keeps the host type
/// reachable and re-exposes the <c>Slot</c> attached property so old AXAML written as
/// <c>controls:SettingLayoutPanel.Slot="Icon"</c> keeps resolving through this type.
/// </summary>
public class SettingLayoutPanel : UI.Controls.SettingLayoutPanel
{
    // Old AXAML resolves attached properties through this type; keep one property identity.
    public new static readonly AttachedProperty<SettingLayoutSlot> SlotProperty =
        UI.Controls.SettingLayoutPanel.SlotProperty;

    public new static SettingLayoutSlot GetSlot(Control element) => UI.Controls.SettingLayoutPanel.GetSlot(element);
    public new static void SetSlot(Control element, SettingLayoutSlot value) => UI.Controls.SettingLayoutPanel.SetSlot(element, value);
}
