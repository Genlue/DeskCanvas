using System;
using Avalonia;
using Avalonia.Controls;
using DeskCanvas.Core.Interfaces;

namespace DeskCanvas.ViewModels;

public record WidgetPreviewViewModel(
    UserControl Control,
    string Type,
    string Subtype,
    string? Title,
    string? Subtitle,
    int DefaultColumns = 2,
    int DefaultRows = 2)
{
    public bool IsDoubleWidth => DefaultColumns >= 3;

    public double CardWidth => IsDoubleWidth ? 340 : 160;

    public double PreviewWidth => IsDoubleWidth ? 340 : 160;

    public double PreviewHeight => (DefaultColumns >= 4 && DefaultRows >= 4) ? 340 : 160;

    public bool IsFlushOrFrameless =>
        Control.Classes.Contains("Flush") ||
        Control.Classes.Contains("Frameless") ||
        Control is IFramelessWidget;

    public Thickness ContentMargin => IsFlushOrFrameless ? new Thickness(0) : new Thickness(12);

    public string SizeBadge => (DefaultColumns, DefaultRows) switch
    {
        (1, 1) => "1×1 · 微型",
        (>= 4, >= 4) => $"{DefaultColumns}×{DefaultRows} · 大号",
        (>= 3, 1) => $"{DefaultColumns}×1 · 横条",
        (>= 3, _) => $"{DefaultColumns}×{DefaultRows} · 中号",
        _ => $"{DefaultColumns}×{DefaultRows} · 标准",
    };
}