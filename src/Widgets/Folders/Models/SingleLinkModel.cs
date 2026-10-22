namespace Folders.Models;

/// <summary>
/// Model for a SingleLink widget, pinned to a web URL.
/// </summary>
/// <param name="Url">The target web URL.</param>
/// <param name="Title">The optional display title / tooltip.</param>
/// <param name="BrowserPath">The executable path of the browser to open with, or null for system default.</param>
/// <param name="CustomIconPath">The local cached path to the custom icon file.</param>
/// <param name="IconPercent">The size of the icon as a percentage of the available dimension (e.g. 70 = 70%).</param>
public record SingleLinkModel(
    string? Url = null,
    string? Title = null,
    string? BrowserPath = null,
    string? CustomIconPath = null,
    double IconPercent = 70.0);
