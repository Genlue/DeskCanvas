using System;
using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Clock.Models;
using Clock.ViewModels;
using DeskCanvas.Core.Interfaces;

namespace Clock.Views;

public partial class AnalogI : UserControl, IFixedSizeWidget
{
    private readonly AnalogClockViewModel viewModel;

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
        viewModel = new AnalogClockViewModel(clockModel);
        DataContext = viewModel;
        Loaded += (_, _) => viewModel.Start();
        Unloaded += (_, _) => viewModel.Stop();
        SizeChanged += OnSizeChanged;
        InitializeComponent();
    }

    // S/M tiers: the dial shrinks to a single-cell diameter and the 12 numbers
    // become illegible — keep just the ring, strokes and hands.
    private void OnSizeChanged(object? sender, SizeChangedEventArgs e) =>
        Numbers.IsVisible = System.Math.Min(e.NewSize.Width, e.NewSize.Height) > 90;
}
