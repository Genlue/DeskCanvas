using Avalonia.Media;
using Avalonia.Media.Immutable;
using Timetable.Models;

namespace Timetable.Services;

/// <summary>Default course colours; block foreground follows luminance.</summary>
public static class CoursePalette
{
    /// <summary>Default Apple-inspired course colours.</summary>
    public static readonly string[] Defaults =
    [
        "#508AEB", // 蔚蓝 (Apple Blue)
        "#43C690", // 薄荷绿 (Apple Mint)
        "#F3A83B", // 活力橙 (Apple Orange)
        "#7B6BE6", // 梦幻紫 (Apple Purple)
        "#4EB5E6", // 海天蓝 (Apple Cyan)
        "#E86D83", // 珊瑚粉 (Apple Coral)
        "#8E6FD8", // 紫罗兰 (Apple Violet)
        "#2BB7B3", // 冰川青 (Apple Teal)
        "#E08658", // 柔桃色 (Warm Peach)
        "#D45D79"  // 浆果红 (Apple Rose)
    ];

    private static readonly IBrush DarkForeground = new SolidColorBrush(Color.FromRgb(0x21, 0x23, 0x27));
    private static readonly IBrush LightForeground = Brushes.White;

    /// <summary>Monochrome mode and the accent/text pair its course blocks use.</summary>
    public readonly record struct BlockPaint(bool Monochrome, Color Accent, IBrush Foreground);

    public static (IBrush Background, IBrush Foreground) Paint(TimetableCourse course, BlockPaint paint)
    {
        if (!paint.Monochrome)
        {
            var color = ResolveColor(course);
            return (BrushFor(color), ForegroundFor(color));
        }

        var fill = Color.FromArgb(0x26, paint.Accent.R, paint.Accent.G, paint.Accent.B);
        return (BrushFor(fill), paint.Foreground);
    }

    /// <summary>Immutable fill brushes, one per distinct course colour.</summary>
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
