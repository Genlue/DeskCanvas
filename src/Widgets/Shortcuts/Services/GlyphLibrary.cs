using System.Collections.Concurrent;
using Avalonia.Media;

namespace Shortcuts.Services;

/// <summary>
/// Built-in stroke glyph set (24x24, drawn with a 1.8px round-cap stroke — the iOS
/// glyph look). Geometry parsing is cached and never throws: an unknown or malformed
/// id falls back to the bolt so a shortcut tile can always render.
/// </summary>
public static class GlyphLibrary
{
    private const string Fallback = "bolt";

    private static readonly Dictionary<string, string> PathData = new(StringComparer.Ordinal)
    {
        ["bolt"] = "M13 2 3 14h7l-1 8 11-13h-8z",
        ["timer"] = "M12 5.5a7.5 7.5 0 1 0 0 15a7.5 7.5 0 1 0 0-15zM12 9v4.5l3 2M9.5 2.5h5",
        ["play"] = "M8 5.5v13l11-6.5z",
        ["pause"] = "M9 5v14M15 5v14",
        ["next"] = "M6 5.5v13l9-6.5zM18.5 5v14",
        ["previous"] = "M18 5.5v13l-9-6.5zM5.5 5v14",
        ["music"] = "M9 18V5.5l10-2V15M9 18a2.5 2.5 0 1 1-5 0 2.5 2.5 0 0 1 5 0zM19 15a2.5 2.5 0 1 1-5 0 2.5 2.5 0 0 1 5 0z",
        ["volume"] = "M4 9.5h4l5-4v13l-5-4H4zM16 9.5a4 4 0 0 1 0 5M18.5 7a7.5 7.5 0 0 1 0 10",
        ["mute"] = "M4 9.5h4l5-4v13l-5-4H4zM16.5 9.5l5 5M21.5 9.5l-5 5",
        ["lock"] = "M6.5 10.5h11V20h-11zM9 10.5V8a3 3 0 0 1 6 0v2.5M12 14v3",
        ["moon"] = "M20 14.5A8.5 8.5 0 0 1 9.5 4 7 7 0 1 0 20 14.5z",
        ["sun"] = "M12 8.5a3.5 3.5 0 1 1 0 7 3.5 3.5 0 0 1 0-7zM12 2.5v2M12 19.5v2M4.6 4.6l1.4 1.4M18 18l1.4 1.4M2.5 12h2M19.5 12h2M4.6 19.4 6 18M18 6l1.4-1.4",
        ["camera"] = "M3.5 8h4l1.8-2.5h5.4L16.5 8h4V19h-17zM12 16.5a3.5 3.5 0 1 0 0-7 3.5 3.5 0 0 0 0 7z",
        ["trash"] = "M5 6.5h14M10 6.5v-2h4v2M7 6.5 8 20.5h8l1-14M11 10.5v6M13 10.5v6",
        ["clock"] = "M12 4.5a7.5 7.5 0 1 1 0 15 7.5 7.5 0 0 1 0-15zM12 8.5V12l2.5 1.8",
        ["bell"] = "M12 4a5.5 5.5 0 0 1 5.5 5.5V13l1.5 3.5H5L6.5 13V9.5A5.5 5.5 0 0 1 12 4zM10 19.5a2 2 0 0 0 4 0",
        ["clip"] = "M9 3.5h6V6H9zM7.5 5H6v15.5h12V5h-1.5M9.5 11h5M9.5 14.5h5",
        ["text"] = "M5 6h14M5 12h9M5 18h11",
        ["sparkle"] = "M12 3l1.8 4.7 4.7 1.8-4.7 1.8L12 16l-1.8-4.7L5.5 9.5l4.7-1.8zM18.5 15l.9 2.1 2.1.9-2.1.9-.9 2.1-.9-2.1-2.1-.9 2.1-.9z",
        ["branch"] = "M7 4v8.5a4 4 0 0 0 4 4h7M15.5 14l3 2.5-3 2.5",
        ["repeat"] = "M4 9.5A5.5 5.5 0 0 1 9.5 4H18M18 4l2.5 2.5L18 9M20 14.5A5.5 5.5 0 0 1 14.5 20H6M6 20l-2.5-2.5L6 15",
        ["comment"] = "M4 5.5h16v11H12l-4.5 4v-4H4z",
        ["sliders"] = "M4 7.5h9M17 7.5h3M4 16.5h3M11 16.5h9M14.5 5v5M8.5 14v5",
        ["list"] = "M5 6h.01M5 12h.01M5 18h.01M9.5 6h10M9.5 12h10M9.5 18h10",
        ["app"] = "M4.5 4.5h6v6h-6zM13.5 4.5h6v6h-6zM4.5 13.5h6v6h-6zM13.5 13.5h6v6h-6z",
        ["globe"] = "M12 3.5a8.5 8.5 0 1 1 0 17 8.5 8.5 0 0 1 0-17zM3.5 12h17M12 3.5c2.3 2.4 3.6 5.3 3.6 8.5s-1.3 6.1-3.6 8.5c-2.3-2.4-3.6-5.3-3.6-8.5S9.7 5.9 12 3.5z",
        ["search"] = "M11 4.5a6.5 6.5 0 1 1 0 13 6.5 6.5 0 0 1 0-13zM15.8 15.8l4.7 4.7",
        ["folder"] = "M3.5 6.5h6l2 2.5h9v10h-17z",
        ["eye"] = "M2.5 12S6 6.5 12 6.5 21.5 12 21.5 12 18 17.5 12 17.5 2.5 12 2.5 12zM12 9.5a2.5 2.5 0 1 1 0 5 2.5 2.5 0 0 1 0-5z",
        ["palette"] = "M12 3.5a8.5 8.5 0 0 0 0 17h1.6a1.9 1.9 0 0 0 0-3.8H12a1.6 1.6 0 0 1 0-3.2h3.2a4.8 4.8 0 0 0 4.8-4.8c0-2.9-3.6-5.2-8-5.2zM7.5 9.5a1 1 0 1 1 0-2 1 1 0 0 1 0 2z",
        ["plus"] = "M12 5.5v13M5.5 12h13",
        ["close"] = "M6.5 6.5l11 11M17.5 6.5l-11 11",
        ["check"] = "M5 12.5l4.5 4.5L19 7",
        ["chevron-down"] = "M6.5 9.5l5.5 5.5 5.5-5.5",
        ["chevron-right"] = "M9.5 6.5l5.5 5.5-5.5 5.5",
        ["grip"] = "M8.5 6h1.4M14.1 6h1.4M8.5 12h1.4M14.1 12h1.4M8.5 18h1.4M14.1 18h1.4",
        ["undo"] = "M4.5 9.5h9.5a5 5 0 0 1 0 10H9M8.5 5.5l-4 4 4 4",
        ["redo"] = "M19.5 9.5H10a5 5 0 0 0 0 10h5M15.5 5.5l4 4-4 4",
        ["stop"] = "M7 7h10v10H7z",
        ["pencil"] = "M4.5 19.5l1-4L16 5l3 3L8.5 18.5zM14 7l3 3",
        ["keyboard"] = "M3 7.5h18v9H3zM6 10.5h.01M9 10.5h.01M12 10.5h.01M15 10.5h.01M18 10.5h.01M7 13.5h10",
        ["desktop"] = "M3.5 5.5h17V16h-17zM8 19.5h8M12 16v3.5",
    };

    /// <summary>Glyphs offered by the icon picker (curated, recognizable set).</summary>
    public static readonly IReadOnlyList<string> PickerGlyphs =
    [
        "bolt", "timer", "play", "music", "lock", "moon", "sun", "camera",
        "trash", "clock", "bell", "clip", "text", "globe", "search", "folder",
        "app", "sparkle", "sliders", "list", "branch", "repeat", "comment", "eye",
        "keyboard", "desktop",
    ];

    private static readonly ConcurrentDictionary<string, Geometry?> GeometryCache = new(StringComparer.Ordinal);

    public static string GetPathData(string? id) =>
        id != null && PathData.TryGetValue(id, out var data) ? data : PathData[Fallback];

    /// <summary>Parsed geometry (cached); null is never returned — the bolt steps in.</summary>
    public static Geometry? GetGeometry(string? id)
    {
        var key = id != null && PathData.ContainsKey(id) ? id : Fallback;
        return GeometryCache.GetOrAdd(key, k =>
        {
            try
            {
                return StreamGeometry.Parse(PathData[k]);
            }
            catch
            {
                return null;
            }
        });
    }
}
