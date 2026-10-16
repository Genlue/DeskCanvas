using System;
using System.Collections.Concurrent;
using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace Habits.Services;

/// <summary>
/// Pre-allocated, immutable brushes and thread-safe brush cache for the Habits widget.
/// Eliminates GC pressure from repeatedly allocating SolidColorBrush instances on every render/refresh.
/// </summary>
public static class HabitBrushes
{
    public static readonly IBrush Transparent = Brushes.Transparent;
    public static readonly IBrush White = Brushes.White;
    public static readonly IBrush DodgerBlue = Brushes.DodgerBlue;
    public static readonly IBrush Gray = Brushes.Gray;
    public static readonly IBrush ForestGreen = Brushes.ForestGreen;

    public static readonly IBrush CardBackground = new ImmutableSolidColorBrush(Color.FromArgb(18, 128, 128, 128));
    public static readonly IBrush IconBackground = new ImmutableSolidColorBrush(Color.FromArgb(24, 128, 128, 128));
    public static readonly IBrush ButtonBackground = new ImmutableSolidColorBrush(Color.FromArgb(30, 128, 128, 128));
    public static readonly IBrush StatsCardBackground = new ImmutableSolidColorBrush(Color.FromArgb(12, 128, 128, 128));
    public static readonly IBrush CalendarSelected = new ImmutableSolidColorBrush(Color.FromArgb(40, 0, 122, 255));
    public static readonly IBrush CalendarDone = new ImmutableSolidColorBrush(Color.FromArgb(25, 52, 199, 89));

    // Heatmap levels (0 to 4)
    public static readonly IBrush HeatmapLevel0 = new ImmutableSolidColorBrush(Color.FromArgb(18, 128, 128, 128));
    public static readonly IBrush HeatmapLevel1 = new ImmutableSolidColorBrush(Color.FromArgb(64, 52, 199, 89));
    public static readonly IBrush HeatmapLevel2 = new ImmutableSolidColorBrush(Color.FromArgb(112, 52, 199, 89));
    public static readonly IBrush HeatmapLevel3 = new ImmutableSolidColorBrush(Color.FromArgb(165, 52, 199, 89));
    public static readonly IBrush HeatmapLevel4 = new ImmutableSolidColorBrush(Color.FromArgb(255, 52, 199, 89));

    private static readonly ConcurrentDictionary<string, IBrush> ColorCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Gets a cached immutable heatmap brush for level 0..4.
    /// </summary>
    public static IBrush GetHeatmapBrush(int level) => level switch
    {
        4 => HeatmapLevel4,
        3 => HeatmapLevel3,
        2 => HeatmapLevel2,
        1 => HeatmapLevel1,
        _ => HeatmapLevel0
    };

    /// <summary>
    /// Gets or creates a cached immutable SolidColorBrush from a hex color string.
    /// </summary>
    public static IBrush GetBrush(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) return DodgerBlue;
        return ColorCache.GetOrAdd(hex, static h =>
        {
            if (Color.TryParse(h, out var col))
            {
                return new ImmutableSolidColorBrush(col);
            }
            return DodgerBlue;
        });
    }
}
