using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Interactivity;
using Avalonia.Media;
using FixedWidgets.Models;
using FixedWidgets.ViewModels;
using DeskCanvas.Core.Interfaces;
using DeskCanvas.Core.Models;

namespace FixedWidgets.Views;

public partial class AggregateView : UserControl, IFixedSizeWidget, IWidgetSelfRefreshing
{
    private AggregateModel model;
    private AggregateViewModel? viewModel;

    // IFixedSizeWidget implementation: the card is a fixed-aspect vector design (312×136 inside a
    // uniform Viewbox), so it may only be resized to spans that keep its 2:1 shape. Every whole
    // step counts — 4×2, 6×3, 8×4, 10×5 … — because the spans themselves stay integers, which is
    // what the grid needs; the content then scales uniformly to whatever the cell provides.
    public IReadOnlyList<(int Columns, int Rows)> AllowedBaseSpans { get; } = [(4, 2)];

    /// <summary>The sizes offered as presets, on the shape lattice (static so checks can read it).</summary>
    public static IReadOnlyList<(int Columns, int Rows)> PresetSpansOf { get; } = [(4, 2), (6, 3), (8, 4)];

    public IReadOnlyList<(int Columns, int Rows)> PresetSpans => PresetSpansOf;

    public bool IsAllowedSpan(int columns, int rows) => IsAllowedSpanOf(columns, rows);

    public (int Columns, int Rows) SnapSpan(int columns, int rows) => SnapSpanOf(columns, rows);

    /// <summary>
    /// Is this span on the card's shape lattice (columns = 2 × rows, at least 4×2)?
    /// Static so the rule can be verified without building a widget (see <c>tests/FixedSpanChecks</c>).
    /// </summary>
    public static bool IsAllowedSpanOf(int columns, int rows) =>
        columns >= 4 && rows >= 2 && rows * 2 == columns;

    /// <summary>
    /// Nearest allowed span. Both steppers land on the same lattice: the average of the
    /// column-pair count and the row count, rounded away from zero so a half step (e.g. 6×2 on
    /// the way up from 4×2) grows the card instead of silently snapping back to the current size.
    /// </summary>
    public static (int Columns, int Rows) SnapSpanOf(int columns, int rows)
    {
        var steps = (columns / 2.0 + rows) / 2.0;
        int k = Math.Max(2, (int)Math.Round(steps, MidpointRounding.AwayFromZero));
        return (k * 2, k);
    }

    public AggregateView() : this(new AggregateModel())
    {
    }

    public AggregateView(AggregateModel model)
    {
        this.model = model;
        viewModel = new AggregateViewModel(model);
        DataContext = viewModel;
        InitializeComponent();
        Classes.Add("Flush");

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    /// <summary>
    /// The view can be detached and re-added later (the settings window caches pages and the
    /// Gallery keeps a live preview control), so the timer subscriptions and the weather client
    /// released by <see cref="OnUnloaded"/> are restarted here — on a *new* view model, because
    /// the released one was disposed.
    /// </summary>
    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        if (viewModel != null) return;
        viewModel = new AggregateViewModel(model);
        DataContext = viewModel;
    }

    /// <summary>
    /// Release the view model (its second/hour timer subscriptions and its HTTP client) without
    /// detaching this handler: the control stays usable and is unloaded again on every later
    /// removal from the visual tree.
    /// </summary>
    private void OnUnloaded(object? sender, RoutedEventArgs e)
    {
        viewModel?.Dispose();
        viewModel = null;
    }

    public void Refresh(WidgetLayout layout)
    {
        var newModel = layout.GetModel<AggregateModel>() ?? new AggregateModel();
        // Keep the model for a view model that is rebuilt on the next load.
        model = newModel;
        viewModel?.UpdateModel(newModel);
    }
}
