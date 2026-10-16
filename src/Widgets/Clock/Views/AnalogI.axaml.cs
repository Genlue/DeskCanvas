using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Styling;
using Clock.Models;
using Clock.ViewModels;
using DeskCanvas.Core.Interfaces;

namespace Clock.Views;

/// <summary>
/// 表盘-1：通过 <see cref="IWidgetSurfaceHints"/> 向宿主声明多彩模式下的专属卡片画刷
/// （ClockCardBackground —— 恒黑卡片，复刻 Apple 时钟小组件亮暗皆黑的卡片外观；
/// 资源键按变体解析，宿主不按视图类型名判断外观）。其余主题不消费该键。
/// 支持“表盘不透明”切换与表盘颜色定制（跟随背景、黑白、自定义）。
/// </summary>
public partial class AnalogI : UserControl, IFixedSizeWidget, IWidgetSurfaceHints
{
    /// <inheritdoc />
    public string? ColorfulBackgroundResourceKey => "ClockCardBackground";

    private const string DialBackgroundBrushKey = "ClockDialBackgroundBrush";

    private readonly AnalogClockViewModel viewModel;
    private readonly ClockModel clockModel;

    public static readonly IReadOnlyList<(int Columns, int Rows)> AllowedBaseSpansOf = [(1, 1)];
    public static readonly IReadOnlyList<(int Columns, int Rows)> PresetSpansOf = [(1, 1), (2, 2), (3, 3), (4, 4)];

    public IReadOnlyList<(int Columns, int Rows)> AllowedBaseSpans => AllowedBaseSpansOf;
    public IReadOnlyList<(int Columns, int Rows)> PresetSpans => PresetSpansOf;

    public bool IsAllowedSpan(int columns, int rows) => IsAllowedSpanOf(columns, rows);
    public (int Columns, int Rows) SnapSpan(int columns, int rows) => SnapSpanOf(columns, rows);

    public static bool IsAllowedSpanOf(int columns, int rows) =>
        columns >= 1 && rows >= 1 && columns == rows;

    public static (int Columns, int Rows) SnapSpanOf(int columns, int rows)
    {
        int k = Math.Max(1, (int)Math.Round((columns + rows) / 2.0, MidpointRounding.AwayFromZero));
        return (k, k);
    }

    public AnalogI() : this(new ClockModel()) {}
    
    public AnalogI(ClockModel clockModel)
    {
        this.clockModel = clockModel;
        viewModel = new AnalogClockViewModel(clockModel);
        DataContext = viewModel;
        Loaded += (_, _) => viewModel.Start();
        Unloaded += (_, _) => viewModel.Stop();
        SizeChanged += OnSizeChanged;
        InitializeComponent();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        ActualThemeVariantChanged -= OnActualThemeVariantChanged;
        ActualThemeVariantChanged += OnActualThemeVariantChanged;
        ApplyDialColor();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        ActualThemeVariantChanged -= OnActualThemeVariantChanged;
        base.OnDetachedFromVisualTree(e);
    }

    private void OnActualThemeVariantChanged(object? sender, EventArgs e) => ApplyDialColor();

    /// <summary>
    /// 表盘颜色: 当 IsDialOpaque 为 true 时，根据 DialColorMode 应用不透明背景（跟随背景使用
    /// ClockOpaqueDialBackgroundBrush，黑白和自定义使用指定颜色）；当 IsDialOpaque 为 false 时，
    /// 移除本地画刷覆盖，回退至主题字典的半透明 ClockDialBackgroundBrush。
    /// </summary>
    private void ApplyDialColor()
    {
        if (!clockModel.IsDialOpaque)
        {
            Resources.Remove(DialBackgroundBrushKey);
            return;
        }

        if (ResolveDialColor(clockModel, ActualThemeVariant == ThemeVariant.Dark) is { } color)
        {
            Resources[DialBackgroundBrushKey] = new SolidColorBrush(color);
        }
        else
        {
            // 跟随背景 (opaque): 使用主题的不透明背景画刷 ClockOpaqueDialBackgroundBrush
            if (this.TryFindResource("ClockOpaqueDialBackgroundBrush", out var opaqueBrush) && opaqueBrush is IBrush brush)
                Resources[DialBackgroundBrushKey] = brush;
            else
                Resources.Remove(DialBackgroundBrushKey);
        }
    }

    /// <summary>
    /// The dial surface color for the given mode and light/dark variant; <c>null</c> means
    /// 跟随背景 (the theme dictionaries' brush is used as-is). Pure function.
    /// </summary>
    public static Color? ResolveDialColor(ClockModel model, bool isDarkVariant) => model.DialColorMode switch
    {
        ClockDialColorMode.BlackWhite => Color.Parse(isDarkVariant
            ? ClockModel.DefaultDialColorDark
            : ClockModel.DefaultDialColorLight),
        ClockDialColorMode.Custom => Color.TryParse(
            isDarkVariant ? model.EffectiveDialColorDark : model.EffectiveDialColorLight, out var custom)
            ? custom
            : null,
        _ => null
    };

    // S/M tiers: the dial shrinks to a single-cell diameter and the 12 numbers
    // become illegible — keep just the ring, strokes and hands.
    private void OnSizeChanged(object? sender, SizeChangedEventArgs e) =>
        Numbers.IsVisible = System.Math.Min(e.NewSize.Width, e.NewSize.Height) > 90;
}
