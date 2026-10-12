using System;
using System.Collections.Generic;
using Avalonia.Controls;
using Clock.Models;
using Clock.ViewModels;
using DeskCanvas.Core.Interfaces;
using DeskCanvas.Core.Models;

namespace Clock.Views;

/// <summary>
/// Mechanical split-flap clock widget locked to the 2:1 proportion lattice (<c>4×2</c>, <c>6×3</c>, <c>8×4</c>).
/// </summary>
public partial class FlipClock : UserControl, IFixedSizeWidget, IWidgetSelfRefreshing
{
    private FlipClockModel model;
    private readonly FlipClockViewModel viewModel;

    public static readonly IReadOnlyList<(int Columns, int Rows)> AllowedBaseSpansOf = [(4, 2)];
    public static readonly IReadOnlyList<(int Columns, int Rows)> PresetSpansOf = [(4, 2), (6, 3), (8, 4)];

    public IReadOnlyList<(int Columns, int Rows)> AllowedBaseSpans => AllowedBaseSpansOf;
    public IReadOnlyList<(int Columns, int Rows)> PresetSpans => PresetSpansOf;

    public bool IsAllowedSpan(int columns, int rows) => IsAllowedSpanOf(columns, rows);
    public (int Columns, int Rows) SnapSpan(int columns, int rows) => SnapSpanOf(columns, rows);

    public static bool IsAllowedSpanOf(int columns, int rows) =>
        columns >= 4 && rows >= 2 && rows * 2 == columns;

    public static (int Columns, int Rows) SnapSpanOf(int columns, int rows)
    {
        var steps = (columns / 2.0 + rows) / 2.0;
        int k = Math.Max(2, (int)Math.Round(steps, MidpointRounding.AwayFromZero));
        return (k * 2, k);
    }

    public FlipClock() : this(new FlipClockModel())
    {
    }

    public FlipClock(FlipClockModel flipClockModel)
    {
        model = flipClockModel;
        viewModel = new FlipClockViewModel(model);
        DataContext = viewModel;
        Loaded += (_, _) => viewModel.Start();
        Unloaded += (_, _) => viewModel.Stop();
        InitializeComponent();
    }

    public void Refresh(WidgetLayout layout)
    {
        var newModel = layout.GetModel<FlipClockModel>() ?? new FlipClockModel();
        model = newModel;
        viewModel.UpdateModel(newModel);
    }
}
