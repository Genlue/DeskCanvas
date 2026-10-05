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
    IReadOnlyList<(int Columns, int Rows)>? PresetSpans = null)
{
    public IReadOnlyList<(int Columns, int Rows)> PresetSpans { get; } = PresetSpans ?? [];

    public bool IsDoubleWidth => DefaultColumns >= 3;

    public double CardWidth => IsDoubleWidth ? 340 : 160;

    public double PreviewWidth => IsDoubleWidth ? 340 : 160;

    public double PreviewHeight => (DefaultColumns >= 4 && DefaultRows >= 4) ? 340 : 160;

    public string SizeBadge => BadgeFor(DefaultColumns, DefaultRows);

    /// <summary>Only widgets with more than one preset span get the right-click size panel.</summary>
    public bool HasSizePresets => PresetSpans.Count > 1;

    public string ToolTipText => HasSizePresets
        ? "左键添加到桌面 · 右键选择尺寸"
        : "点击添加到桌面";

    public static string BadgeFor(int columns, int rows) => (columns, rows) switch
    {
        (1, 1) => "1×1 · 微型",
        (>= 4, >= 4) => $"{columns}×{rows} · 大号",
        (>= 3, 1) => $"{columns}×1 · 横条",
        (>= 3, _) => $"{columns}×{rows} · 中号",
        _ => $"{columns}×{rows} · 标准",
    };
}
