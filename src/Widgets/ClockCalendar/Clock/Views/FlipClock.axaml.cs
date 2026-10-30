using System;
using System.Collections.Generic;
using Avalonia.Controls;
using Clock.Models;
using Clock.ViewModels;
using DeskCanvas.Contracts;
using DeskCanvas.Runtime;
using DeskCanvas.Core.Interfaces;
using DeskCanvas.Core.Models;

namespace Clock.Views;

/// <summary>
/// Mechanical split-flap clock widget locked to the 2:1 proportion lattice (<c>4×2</c>, <c>6×3</c>, <c>8×4</c>).
/// Declares <see cref="IWidgetSurfaceHints"/> for Colorful theme card background (<c>FlipClockCardBackground</c>).
/// </summary>
public partial class FlipClock : UserControl, IFixedSizeWidget, IWidgetSelfRefreshing, IWidgetSurfaceHints, IWidgetActivityConsumer
{
    /// <inheritdoc />
    public string? ColorfulBackgroundResourceKey => "FlipClockCardBackground";

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

    /// <summary>
    /// <paramref name="session"/> is injected by WidgetFactory when the host runs the session
    /// pipeline (a stable instance identity): the view model then gates its timer subscription
    /// on the session's activity. Null keeps the legacy always-on behaviour (preview, legacy
    /// layout entries) — those hosts never push activity either.
    /// </summary>
    public FlipClock(FlipClockModel flipClockModel, IWidgetSession? session = null)
    {
        model = flipClockModel;
        viewModel = new FlipClockViewModel(model, session);
        DataContext = viewModel;
        Loaded += (_, _) => viewModel.Start();
        Unloaded += (_, _) => viewModel.Stop();
        InitializeComponent();
    }

    /// <inheritdoc />
    public void OnSessionActivity(ActivitySnapshot activity) => viewModel.OnSessionActivity(activity);

    public void Refresh(WidgetLayout layout)
    {
        var newModel = layout.GetModel<FlipClockModel>() ?? new FlipClockModel();
        model = newModel;
        viewModel.UpdateModel(newModel);
    }
}
