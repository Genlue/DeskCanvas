using System.Text.Json.Serialization;
using Folders.Services;

namespace Folders.Models;

/// <summary>
/// Model for a SingleFile widget, pinned to a single file, folder, or shortcut.
/// </summary>
/// <param name="Path">The target path of the file, folder, or shortcut.</param>
/// <param name="IconPercent">The size of the icon as a percentage of the available dimension (e.g. 70 = 70%).</param>
/// <param name="HoverZoom">
/// Pointer-hover zoom of the icon, in percent (100 = none, 150 = max). The drawn size is capped so
/// the zoomed icon still fits inside the card.
/// </param>
public record SingleFileModel(
    string? Path = null,
    double IconPercent = 70.0,
    double HoverZoom = HoverZoomMath.DefaultSinglePercent)
{
    /// <summary>Hover zoom multiplier, clamped to the supported range.</summary>
    [JsonIgnore]
    public double EffectiveHoverScale => HoverZoomMath.ScaleOf(HoverZoom);
}
