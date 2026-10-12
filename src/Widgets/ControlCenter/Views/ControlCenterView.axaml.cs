using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ControlCenter.Models;
using ControlCenter.Services;
using ControlCenter.Views.Controls;
using DeskCanvas.Core.Interfaces;
using DeskCanvas.Core.Models;

namespace ControlCenter.Views;

public partial class ControlCenterView : UserControl, IWidgetSelfRefreshing, IWidgetSuspendable, IDisposable
{
    private ControlCenterModel model;
    private readonly DispatcherTimer pollTimer;
    private readonly List<ControlCapsuleView> activeCapsules = [];
    private int currentGridCols = -1;
    private int currentGridRows = -1;
    private string currentLayoutSignature = string.Empty;
    private bool isSuspended;

    public ControlCenterView() : this(new ControlCenterModel()) { }

    public ControlCenterView(ControlCenterModel model)
    {
        this.model = model ?? new ControlCenterModel();
        InitializeComponent();

        pollTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(1500)
        };
        pollTimer.Tick += (_, _) => RefreshStates(false);

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        SizeChanged += OnSizeChanged;
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        RebuildGridIfNeeded(force: true);
        RefreshStates(forceSlowQueries: true);
        if (!isSuspended)
            pollTimer.Start();
    }

    private void OnUnloaded(object? sender, RoutedEventArgs e)
    {
        pollTimer.Stop();
    }

    private void OnSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        RebuildGridIfNeeded(force: false);
    }

    private (int Cols, int Rows) ResolveTargetGridDimensions()
    {
        for (var node = this.GetVisualParent(); node != null; node = node.GetVisualParent())
        {
            if (node is DeskCanvas.Views.Widget widget)
            {
                var span = widget.CurrentSpan;
                if (span.Columns <= 2 && span.Rows <= 2) return (2, 2);
                if (span.Columns >= 3 && span.Rows >= 3) return (4, 4);
                return (4, 2);
            }
        }

        // Gallery preview or size context menu heuristic (columns * 100 DIP)
        var size = Bounds.Size;
        if (size.Width > 0 && size.Height > 0)
        {
            double aspect = size.Width / size.Height;
            if (aspect >= 1.45)
                return (4, 2);
            if (size.Width >= 320 && size.Height >= 320)
                return (4, 4);
            if (size.Width <= 240 && size.Height <= 240)
                return (2, 2);
        }

        return (4, 2);
    }

    private void RebuildGridIfNeeded(bool force)
    {
        var (cols, rows) = ResolveTargetGridDimensions();
        var placements = model.GetLayoutForGrid(cols, rows);
        var signature = $"{cols}x{rows}:{model.WheelStep}:" +
                        string.Join(";", placements.Select(p => $"{(int)p.Type}@{p.Column},{p.Row},{p.ColumnSpan}x{p.RowSpan}"));

        if (!force && cols == currentGridCols && rows == currentGridRows && signature == currentLayoutSignature)
            return;

        currentGridCols = cols;
        currentGridRows = rows;
        currentLayoutSignature = signature;

        BoardGrid.Children.Clear();
        activeCapsules.Clear();

        BoardGrid.ColumnDefinitions = new ColumnDefinitions(string.Join(",", Enumerable.Repeat("*", cols)));
        BoardGrid.RowDefinitions = new RowDefinitions(string.Join(",", Enumerable.Repeat("*", rows)));

        var snapshot = SystemControlService.Shared.GetSnapshot();

        foreach (var item in placements)
        {
            var capsule = new ControlCapsuleView(item.Type, item.ColumnSpan, item.RowSpan, model.WheelStep)
            {
                Margin = new Thickness(3.5)
            };
            capsule.StateMutated += OnCapsuleStateMutated;
            capsule.RenderSnapshot(snapshot);

            Grid.SetColumn(capsule, item.Column);
            Grid.SetRow(capsule, item.Row);
            Grid.SetColumnSpan(capsule, item.ColumnSpan);
            Grid.SetRowSpan(capsule, item.RowSpan);

            BoardGrid.Children.Add(capsule);
            activeCapsules.Add(capsule);
        }
    }

    private async void OnCapsuleStateMutated()
    {
        RefreshStates(forceSlowQueries: true);
        await Task.Delay(250);
        if (IsLoaded && !isSuspended)
            RefreshStates(forceSlowQueries: true);
    }

    private void RefreshStates(bool forceSlowQueries)
    {
        if (activeCapsules.Count == 0) return;
        var snapshot = SystemControlService.Shared.GetSnapshot(forceSlowQueries);
        foreach (var capsule in activeCapsules)
        {
            capsule.RenderSnapshot(snapshot);
        }
    }

    public void Refresh(WidgetLayout layout)
    {
        if (layout.Settings is { ValueKind: JsonValueKind.Object } settings)
        {
            try
            {
                var updated = settings.Deserialize<ControlCenterModel>();
                if (updated != null)
                {
                    model = updated;
                }
            }
            catch { }
        }

        RebuildGridIfNeeded(force: true);
        RefreshStates(forceSlowQueries: false);
    }

    public void Suspend()
    {
        isSuspended = true;
        pollTimer.Stop();
    }

    public void Resume()
    {
        isSuspended = false;
        RefreshStates(forceSlowQueries: true);
        if (IsLoaded)
            pollTimer.Start();
    }

    public void Dispose()
    {
        pollTimer.Stop();
    }
}
