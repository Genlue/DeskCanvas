using System.Text.Json.Serialization;
using Folders.Services;

namespace Folders.Models;

/// <summary>
/// Model of the Folders widget, stored in <c>layout.json</c>.
/// </summary>
/// <param name="Items">Absolute paths of pinned items (files, folders, shortcuts).</param>
/// <param name="ShowNames">Whether item names are displayed under the icons.</param>
/// <param name="CustomNames">Optional custom display names per item path.</param>
/// <param name="Columns">Number of items per row.</param>
/// <param name="RowSpacing">Vertical spacing between rows.</param>
/// <param name="ShowScrollbar">Whether the scrollbar is shown in the widget.</param>
/// <param name="ShowTitle">Whether a title is shown at the top-left.</param>
/// <param name="Title">Title text shown at the top-left.</param>
/// <param name="TitleOffsetX">Title offset as a percentage of the widget width: 0 = left-aligned, 100 = shifted fully out to the right.</param>
/// <param name="BoldTitle">Whether the title uses a bold font.</param>
/// <param name="BoldNames">Whether item names use a bold font.</param>
/// <param name="MaxNameLines">Maximum number of lines a name may wrap to (1-4, 2 by default).</param>
/// <param name="MaxNameChars">Maximum characters per name line (0 = no limit, natural wrapping).</param>
/// <param name="CamelCaseWrap">Break names at capital letters for better wrapping.</param>
/// <param name="LayoutMode">Item layout: "Grid" (uniform grid) or "List" (vertical rows).</param>
/// <param name="RowLayout">Vertical layout in grid mode: "Auto" (rows stretch to divide the
/// available height evenly) or "Fixed" (constant row pitch — an icon's vertical position
/// depends only on its row index, never on how many rows there are in total).</param>
/// <param name="WatchFolder">When set, the widget lists the contents of this folder (live) instead of <see cref="Items"/>.</param>
/// <param name="ShowHiddenFiles">Whether hidden/system files are shown in watched-folder mode (default: filtered out).</param>
/// <param name="WatchSortBy">Sort field for watched-folder contents: "Name", "Created" or "Modified".</param>
/// <param name="DirectoriesFirst">Folders before files (true) or files before folders (false).</param>
/// <param name="SortDescending">Reverse the sort order within each type group.</param>
/// <param name="HideExtensions">Hide file extensions for watched-folder contents.</param>
/// <param name="HideSubfolders">Hide subfolders in watched-folder mode (only show direct children).</param>
/// <param name="Padding">Inner padding of the widget, in pixels.</param>
/// <param name="HoverZoom">
/// Pointer-hover zoom of an icon, in percent (100 = none, 150 = max). The scroll viewport reserves
/// the growth at the top and bottom: a scrolling list is the one place where the viewport, not the
/// card, is the hard edge, so the gutter is what keeps a zoomed icon whole.
/// </param>
public record FolderModel(
    List<string> Items,
    bool ShowNames = true,
    Dictionary<string, string>? CustomNames = null,
    int Columns = 4,
    int RowSpacing = 4,
    bool ShowScrollbar = false,
    bool ShowTitle = false,
    string Title = "文件夹",
    double IconSize = 40,
    double FontSize = 12,
    double TitleOffsetX = 0,
    bool BoldTitle = true,
    bool BoldNames = false,
    int MaxNameLines = 2,
    int MaxNameChars = 0,
    bool CamelCaseWrap = false,
    string LayoutMode = "Grid",
    string RowLayout = "Auto",
    string? WatchFolder = null,
    bool ShowHiddenFiles = false,
    string WatchSortBy = "Name",
    bool DirectoriesFirst = true,
    bool SortDescending = false,
    bool HideExtensions = false,
    bool HideSubfolders = false,
    int Padding = 8,
    double HoverZoom = HoverZoomMath.DefaultGridPercent)
{
    [JsonIgnore]
    public bool IsEmpty => Items is not { Count: > 0 };

    /// <summary>Hover zoom multiplier, clamped to the supported range.</summary>
    [JsonIgnore]
    public double EffectiveHoverScale => HoverZoomMath.ScaleOf(HoverZoom);

    /// <summary>
    /// Resolve the display name of a path (custom name or file name).
    /// </summary>
    public string GetDisplayName(string path)
    {
        if (CustomNames is { } names && names.TryGetValue(path, out var custom)
            && !string.IsNullOrWhiteSpace(custom))
            return custom;

        var name = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        return string.IsNullOrEmpty(name) ? path : name;
    }
}