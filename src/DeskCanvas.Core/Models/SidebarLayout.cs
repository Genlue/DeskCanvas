using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

namespace DeskCanvas.Core.Models;

/// <summary>
/// The右侧小组件侧栏 configuration of a single screen, stored inside
/// <see cref="ScreenLayout.Sidebar"/>. The sidebar is a separate widget surface:
/// its widget set, order, per-widget configuration, width and column count are completely
/// independent from the desktop widgets of the same screen.
/// <para>
/// The sidebar is a <b>square-cell grid</b>: the width is divided into <see cref="Columns"/>
/// equal columns, and every widget occupies a whole number of cells. A 360 DIP sidebar with 4
/// columns therefore has 90×90 DIP cells, and each row fits four 1×1 widgets.
/// </para>
/// </summary>
/// <param name="WidthDip">Sidebar width in DIPs. Clamped to
/// <see cref="Settings.SidebarSettings.MinWidthDip"/>–<see cref="Settings.SidebarSettings.MaxWidthDip"/>
/// (and the screen width) at runtime.</param>
/// <param name="Widgets">The sidebar widget entries in display order; <c>null</c>/empty = empty sidebar.</param>
/// <param name="Columns">How many grid columns the width is divided into.
/// <c>null</c> in old configurations → <see cref="DefaultColumns"/>.</param>
public record SidebarLayout(
    double WidthDip = 360,
    List<SidebarWidgetEntry>? Widgets = null,
    int Columns = SidebarLayout.DefaultColumns)
{
    /// <summary>Columns a sidebar uses when nothing else was configured (360 / 4 = 90 DIP cells).</summary>
    public const int DefaultColumns = 4;

    /// <summary>Fewest columns a sidebar may be configured with.</summary>
    public const int MinColumns = 1;

    /// <summary>Most columns a sidebar may be configured with (a 280 DIP sidebar still gets 35 DIP cells).</summary>
    public const int MaxColumns = 8;

    /// <summary>
    /// The entries with a stable, non-null widget list (old configurations deserialize
    /// without the field).
    /// </summary>
    [JsonIgnore]
    public List<SidebarWidgetEntry> Items => Widgets ?? [];

    /// <summary>Entries sorted by <see cref="SidebarWidgetEntry.Order"/> (ties keep list order).</summary>
    [JsonIgnore]
    public List<SidebarWidgetEntry> Ordered => [.. Items.OrderBy(entry => entry.Order)];

    /// <summary>The column count actually used, clamped to the supported range.</summary>
    [JsonIgnore]
    public int EffectiveColumns => Math.Clamp(Columns, MinColumns, MaxColumns);
}

/// <summary>
/// One widget placed in the sidebar. Unlike desktop widgets — which are identified by
/// their <see cref="WidgetLayout"/> value — sidebar widgets carry a stable
/// <see cref="InstanceId"/>, so several instances of the same type can coexist with
/// their own configuration and survive re-ordering.
/// </summary>
/// <param name="InstanceId">Stable, unique instance id (GUID). Never derived from Type/SubType/X/Y.</param>
/// <param name="Layout">The widget's layout (content scale, settings JSON, and the pixel size the
/// <i>desktop</i> would use). The sidebar does not lay out from these pixels — its geometry comes
/// from <see cref="Columns"/>/<see cref="Rows"/> — but they are kept coherent with the span so any
/// consumer that derives a span from pixels still agrees (see <c>SidebarService.AddWidget</c>).</param>
/// <param name="Order">Ascending display order inside the sidebar.</param>
/// <param name="Columns">Grid columns this widget occupies. <c>0</c> = not recorded (pre-grid
/// configuration) — the widget's own idiom decides, see <c>SidebarWidgetHost.Span</c>.</param>
/// <param name="Rows">Grid rows this widget occupies. <c>0</c> = not recorded.</param>
public record SidebarWidgetEntry(
    string InstanceId,
    WidgetLayout Layout,
    int Order = 0,
    int Columns = 0,
    int Rows = 0)
{
    /// <summary>
    /// Whether this entry carries an explicit grid span. Sidebar entries written before the
    /// sidebar became a grid deserialize with <c>0</c>/<c>0</c> and resolve their span from the
    /// widget instead of being read as a zero-sized card.
    /// </summary>
    [JsonIgnore]
    public bool HasSpan => Columns > 0 && Rows > 0;
}
