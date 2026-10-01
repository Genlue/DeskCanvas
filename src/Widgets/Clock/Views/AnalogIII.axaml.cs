using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Clock.Models;
using Clock.ViewModels;
using DeskCanvas.Core.Interfaces;

namespace Clock.Views;

public partial class AnalogIII : UserControl, IFixedSizeWidget
{
    private readonly AnalogClockViewModel viewModel;

    public IReadOnlyList<(int Columns, int Rows)> AllowedBaseSpans => AnalogI.AllowedBaseSpansOf;
    public IReadOnlyList<(int Columns, int Rows)> PresetSpans => AnalogI.PresetSpansOf;

    public bool IsAllowedSpan(int columns, int rows) => AnalogI.IsAllowedSpanOf(columns, rows);
    public (int Columns, int Rows) SnapSpan(int columns, int rows) => AnalogI.SnapSpanOf(columns, rows);

    public AnalogIII() : this(new ClockModel()) {}
    
    public AnalogIII(ClockModel clockModel)
    {
        viewModel = new AnalogClockViewModel(clockModel);
        DataContext = viewModel;
        Loaded += (_, _) => viewModel.Start();
        Unloaded += (_, _) => viewModel.Stop();
        SizeChanged += OnSizeChanged;
        InitializeComponent();
    }

    // S/M tiers: the tick ring is the core of this style and survives shrinking,
    // but the four numerals fall below readable size — drop them there.
    private void OnSizeChanged(object? sender, SizeChangedEventArgs e) =>
        Numbers.IsVisible = System.Math.Min(e.NewSize.Width, e.NewSize.Height) > 90;
}
