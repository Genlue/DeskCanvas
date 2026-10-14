using Avalonia.Media;
using Avalonia.Media.Immutable;
using Timetable.Models;

namespace Timetable.Services;

/// <summary>
/// Default course colours and helpers. Blocks use the course colour as a solid fill
/// and pick a readable foreground from its luminance, so no per-theme tuning is needed.
/// </summary>
public static class CoursePalette
{
    /// <summary>Harmonious defaults; a course without a colour keeps the same slot while the name is unchanged.</summary>
    public static readonly string[] Defaults =
    [
        "#5B8FF9", "#61DDAA", "#F6BD16", "#7262FD", "#78D3F8",
        "#9661BC", "#F6903D", "#00949F", "#F08BB4", "#E8684A"
    ];

    private static readonly IBrush DarkForeground = new SolidColorBrush(Color.FromRgb(0x21, 0x23, 0x27));
    private static readonly IBrush LightForeground = Brushes.White;

    /// <summary>
    /// Shared immutable fill brushes, one per distinct course colour. Blocks used to
    /// allocate a fresh mutable SolidColorBrush per block per rebuild (and a rebuild
    /// happens on every resize step / week switch / settings change); the palette is
    /// tiny, so one bounded cache serves every surface (widget, panel, review preview)
    /// allocation-free. Immutable brushes also skip the mutable-object change tracking.
    /// Bounded by the distinct course colours (≤ MaxCourses user colours + 10 defaults).
    /// </summary>
    private static readonly Dictionary<Color, IBrush> BrushCache = [];

    public static IBrush BrushFor(Color color)
    {
        if (BrushCache.TryGetValue(color, out var cached)) return cached;
        var brush = new ImmutableSolidColorBrush(color);
        BrushCache.Add(color, brush);
        return brush;
    }

    /// <summary>Stable colour for a name (FNV-1a), so the same course always gets the same default colour.</summary>
    public static string DefaultColorFor(string name)
    {
        unchecked
        {
            var hash = 2166136261u;
            foreach (var c in name)
            {
                hash ^= c;
                hash *= 16777619;
            }
            return Defaults[(int)(hash % (uint)Defaults.Length)];
        }
    }

    public static Color ResolveColor(TimetableCourse course)
    {
        if (course.Color is { } hex && TryParseHex(hex, out var parsed)) return parsed;
        return TryParseHex(DefaultColorFor(course.Name ?? ""), out var fallback) ? fallback : Colors.SlateBlue;
    }

    public static bool TryParseHex(string? value, out Color color)
    {
        color = default;
        if (string.IsNullOrWhiteSpace(value)) return false;

        var text = value.Trim();
        if (text.StartsWith('#')) text = text[1..];
        if (text.Length == 3)
        {
            // #RGB → #RRGGBB
            text = $"{text[0]}{text[0]}{text[1]}{text[1]}{text[2]}{text[2]}";
        }
        if (text.Length == 8)
        {
            // #AARRGGBB → drop alpha (unused: blocks are flat)
            text = text[2..];
        }
        if (text.Length != 6) return false;

        if (!byte.TryParse(text[..2], System.Globalization.NumberStyles.HexNumber, null, out var r) ||
            !byte.TryParse(text[2..4], System.Globalization.NumberStyles.HexNumber, null, out var g) ||
            !byte.TryParse(text[4..6], System.Globalization.NumberStyles.HexNumber, null, out var b))
            return false;

        color = Color.FromRgb(r, g, b);
        return true;
    }

    /// <summary>Readable text brush for a block filled with <paramref name="background"/>.</summary>
    public static IBrush ForegroundFor(Color background)
    {
        var luminance = (0.299 * background.R + 0.587 * background.G + 0.114 * background.B) / 255.0;
        return luminance > 0.62 ? DarkForeground : LightForeground;
    }
}
