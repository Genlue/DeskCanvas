using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;
using Clock.Models;
using Clock.ViewModels;
using DeskCanvas.Core.Interfaces;

namespace Clock.Views;

/// <summary>
/// 表盘-3：通过 <see cref="IWidgetSurfaceHints"/> 向宿主声明多彩模式下的专属卡片画刷
/// （ClockCardBackground —— 恒黑卡片，与表盘-1 一致，复刻 Apple 时钟小组件亮暗皆黑的
/// 卡片外观；资源键按变体解析，宿主不按视图类型名判断外观）。其余主题不消费该键。
/// </summary>
public partial class AnalogIV : UserControl, IFixedSizeWidget, IWidgetSurfaceHints
{
    /// <inheritdoc />
    public string? ColorfulBackgroundResourceKey => "ClockCardBackground";

    /// <summary>
    /// 表盘颜色 override key: the dial surface brush every theme dictionary publishes
    /// under this key (Colorful's fixed white/charcoal pair, the monochrome variants'
    /// 背景色 pair). The view only ever adds/removes a per-instance override — removing
    /// it hands the lookup back to the theme dictionaries.
    /// </summary>
    private const string DialBackgroundBrushKey = "ClockOpaqueDialBackgroundBrush";

    private readonly AnalogClockViewModel viewModel;
    private readonly ClockModel clockModel;

    public IReadOnlyList<(int Columns, int Rows)> AllowedBaseSpans => AnalogI.AllowedBaseSpansOf;
    public IReadOnlyList<(int Columns, int Rows)> PresetSpans => AnalogI.PresetSpansOf;

    public bool IsAllowedSpan(int columns, int rows) => AnalogI.IsAllowedSpanOf(columns, rows);
    public (int Columns, int Rows) SnapSpan(int columns, int rows) => AnalogI.SnapSpanOf(columns, rows);

    public AnalogIV() : this(new ClockModel()) {}
    
    public AnalogIV(ClockModel clockModel)
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
    /// 表盘颜色 (the shared analog settings panel, gated to 表盘-3): 黑白 and 自定义 resolve
    /// to a per-instance override of <see cref="DialBackgroundBrushKey"/>, which the dial
    /// ellipse picks up through its existing {DynamicResource} lookup. 跟随背景 removes the
    /// override, handing the lookup back to the theme dictionaries — so the dial keeps
    /// live-following both the light/dark variant and the 背景色 settings.
    /// </summary>
    private void ApplyDialColor()
    {
        if (ResolveDialColor(clockModel, ActualThemeVariant == ThemeVariant.Dark) is { } color)
            Resources[DialBackgroundBrushKey] = new SolidColorBrush(color);
        else
            Resources.Remove(DialBackgroundBrushKey);
    }

    /// <summary>
    /// The dial surface color for the given mode and light/dark variant; <c>null</c> means
    /// 跟随背景 (the theme dictionaries' brush is used as-is). Pure function, pinned by
    /// tests/ClockThemeChecks.
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
