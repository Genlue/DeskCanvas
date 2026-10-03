using System.Collections.Generic;

namespace StackWidgets.Models;

/// <summary>
/// Description of a single stacked child widget.
/// </summary>
public record StackedWidgetEntry(
    string AssemblyName,
    string ViewTypeName,
    string DisplayTitle,
    string? SettingsJson = null,
    double? ContentScale = null
);

public class StackedWidgetSettingItem
{
    public StackedWidgetEntry Entry { get; set; } = null!;
    public int Index { get; set; }
    public string DisplayTitle => Entry.DisplayTitle;
    public bool HasSettings { get; set; }
}

/// <summary>
/// Stored configuration for the Widget Stack component.
/// </summary>
public record WidgetStackModel(
    List<StackedWidgetEntry>? Entries = null,
    int SelectedIndex = 0,
    bool AllowWheelSwitch = true,
    bool WheelSwitchOnlyOnDots = true,
    bool AutoHideDots = false
)
{
    public List<StackedWidgetEntry> Entries { get; init; } = Entries ?? [];

    public WidgetStackModel() : this([], 0, true, true, false) {}

    public static List<StackedWidgetEntry> GetDefaultEntries() => [];
}
