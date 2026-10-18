using System.Collections.Generic;
using Avalonia.Controls;

namespace DeskCanvas.ViewModels;

public record WidgetPreviewViewModel(
    UserControl Control,
    string Type,
    string Subtype,
    System.Type ViewType,
    string? Title,
    int DefaultColumns = 2,
    int DefaultRows = 2,
    IReadOnlyList<(int Columns, int Rows)>? PresetSpans = null,
    string? Category = null)
{
    public IReadOnlyList<(int Columns, int Rows)> PresetSpans { get; } = PresetSpans ?? [];

    public bool IsDoubleWidth => DefaultColumns >= 3;

    public double CardWidth => IsDoubleWidth ? 340 : 160;

    public double PreviewWidth => IsDoubleWidth ? 340 : 160;

    public double PreviewHeight => (DefaultColumns >= 4 && DefaultRows >= 4) ? 340 : 160;

    public string SizeBadge => BadgeFor(DefaultColumns, DefaultRows);

    /// <summary>Only widgets with more than one preset span get the right-click size panel.</summary>
    public bool HasSizePresets => PresetSpans.Count > 1;

    private static bool IsZh => System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.Equals(
        "zh", System.StringComparison.OrdinalIgnoreCase);

    public string ToolTipText => HasSizePresets
        ? (IsZh ? "左键添加到桌面 · 右键选择尺寸" : "Left-click to add · Right-click for sizes")
        : (IsZh ? "点击添加到桌面" : "Click to add to desktop");

    public static string BadgeFor(int columns, int rows) => (columns, rows) switch
    {
        (1, 1) => IsZh ? "1×1 · 微型" : "1×1 · Mini",
        (>= 4, >= 4) => IsZh ? $"{columns}×{rows} · 大号" : $"{columns}×{rows} · Large",
        (>= 3, 1) => IsZh ? $"{columns}×1 · 横条" : $"{columns}×1 · Wide",
        (>= 3, _) => IsZh ? $"{columns}×{rows} · 中号" : $"{columns}×{rows} · Medium",
        _ => IsZh ? $"{columns}×{rows} · 标准" : $"{columns}×{rows} · Standard",
    };
}
