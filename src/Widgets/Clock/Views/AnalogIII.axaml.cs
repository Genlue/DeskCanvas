using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Clock.Models;
using Clock.ViewModels;
using DeskCanvas.Contracts;
using DeskCanvas.Runtime;
using DeskCanvas.Core.Interfaces;

namespace Clock.Views;

/// <summary>
/// 表盘-2（城市/时区表盘）：通过 <see cref="IWidgetSurfaceHints"/> 声明 Colorful 模式下的专属卡片画刷
/// （ClockAnalogIIICardBackground —— 亮色白卡、暗色黑卡，表盘全域即卡片，避免亮色模式下因卡片恒黑导致黑字黑针"一片黑"）。
/// </summary>
public partial class AnalogIII : UserControl, IFixedSizeWidget, IWidgetSurfaceHints, IWidgetActivityConsumer
{
    /// <inheritdoc />
    public string? ColorfulBackgroundResourceKey => "ClockAnalogIIICardBackground";

    private readonly AnalogClockViewModel viewModel;

    public IReadOnlyList<(int Columns, int Rows)> AllowedBaseSpans => AnalogI.AllowedBaseSpansOf;
    public IReadOnlyList<(int Columns, int Rows)> PresetSpans => AnalogI.PresetSpansOf;

    public bool IsAllowedSpan(int columns, int rows) => AnalogI.IsAllowedSpanOf(columns, rows);
    public (int Columns, int Rows) SnapSpan(int columns, int rows) => AnalogI.SnapSpanOf(columns, rows);

    public AnalogIII() : this(new ClockModel()) {}

    /// <summary>
    /// <paramref name="session"/> is injected by WidgetFactory when the host runs the session
    /// pipeline (a stable instance identity): the view model then gates its timer subscription
    /// on the session's activity. Null keeps the legacy always-on behaviour (preview, legacy
    /// layout entries) — those hosts never push activity either.
    /// </summary>
    public AnalogIII(ClockModel clockModel, IWidgetSession? session = null)
    {
        viewModel = new AnalogClockViewModel(clockModel, session);
        DataContext = viewModel;
        Loaded += (_, _) => viewModel.Start();
        Unloaded += (_, _) => viewModel.Stop();
        SizeChanged += OnSizeChanged;
        InitializeComponent();
    }

    /// <inheritdoc />
    public void OnSessionActivity(ActivitySnapshot activity) => viewModel.OnSessionActivity(activity);

    // S/M tiers: the tick ring is the core of this style and survives shrinking,
    // but the four numerals fall below readable size — drop them there.
    private void OnSizeChanged(object? sender, SizeChangedEventArgs e) =>
        Numbers.IsVisible = System.Math.Min(e.NewSize.Width, e.NewSize.Height) > 90;
}
