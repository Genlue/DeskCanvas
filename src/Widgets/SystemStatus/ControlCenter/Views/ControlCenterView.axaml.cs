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
using DeskCanvas.Contracts;
using DeskCanvas.Core.Interfaces;
using DeskCanvas.Core.Models;
using DeskCanvas.Runtime;

namespace ControlCenter.Views;

public partial class ControlCenterView : UserControl, IWidgetSelfRefreshing, IWidgetSuspendable, IDisposable, IWidgetSurfaceHints, IWidgetActivityConsumer
{
    public string? ColorfulBackgroundResourceKey => "ControlCenterCardBackground";

    private ControlCenterModel model;
    private readonly IWidgetSession? session;
    private bool renderActive;
    private readonly DispatcherTimer pollTimer;
    private readonly List<ControlCapsuleView> activeCapsules = [];
    private int currentGridCols = -1;
    private int currentGridRows = -1;
    private string currentLayoutSignature = string.Empty;
    private bool isSuspended;

    public ControlCenterView() : this(new ControlCenterModel()) { }

    /// <summary>
    /// <paramref name="session"/> is injected by WidgetFactory when the host runs the session
    /// pipeline: the 1.5 s system-state poll then follows the session's
    /// <see cref="ActivitySnapshot.RenderActive"/> — a covered card stops polling (and
    /// refreshes once on re-emergence). Null keeps the legacy always-on behaviour (preview,
    /// legacy layout entries) — those hosts never push activity either.
    /// </summary>
    public ControlCenterView(ControlCenterModel model, IWidgetSession? session = null)
    {
        this.model = model ?? new ControlCenterModel();
        this.session = session;
        renderActive = session?.Activity.RenderActive ?? true;
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
        UpdatePollTimer();
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

    /// <summary>
    /// Host activity push (the <see cref="IWidgetActivityConsumer"/> path, invoked on the UI
    /// thread): the 1.5 s poll follows the snapshot's <see cref="ActivitySnapshot.RenderActive"/>.
    /// On re-emergence the capsules refresh once so the first visible frame is not stale.
    /// Session-less views ignore the push entirely (the host never sends one to them).
    /// </summary>
    public void OnSessionActivity(ActivitySnapshot activity)
    {
        if (session == null) return;
        var wasActive = renderActive;
        renderActive = activity.RenderActive;
        UpdatePollTimer();
        if (!wasActive && renderActive)
        {
            RefreshStates(forceSlowQueries: false);
        }
    }

    /// <summary>Check seam: the injected session (null on the legacy path).</summary>
    public IWidgetSession? SessionForChecks => session;

    /// <summary>Check seam: the last activity gate value the view reconciled against.</summary>
    public bool RenderActiveForChecks => renderActive;

    /// <summary>Check seam: whether the 1.5 s system-state poll is currently running.</summary>
    public bool IsPollingForChecks => pollTimer.IsEnabled;

    /// <summary>
    /// Single run-state machine for the shared poll timer: wanted = loaded AND not fullscreen-
    /// suspended AND the session renders. Overlapping transitions cannot double-start or
    /// double-stop — the state is reconciled from all three inputs on every change instead of
    /// toggled from each.
    /// </summary>
    private void UpdatePollTimer()
    {
        var wanted = IsLoaded && !isSuspended && renderActive;
        if (wanted && !pollTimer.IsEnabled)
        {
            pollTimer.Start();
        }
        else if (!wanted && pollTimer.IsEnabled)
        {
            pollTimer.Stop();
        }
    }

    public void Suspend()
    {
        isSuspended = true;
        UpdatePollTimer();
    }

    public void Resume()
    {
        isSuspended = false;
        RefreshStates(forceSlowQueries: true);
        UpdatePollTimer();
    }

    public void Dispose()
    {
        pollTimer.Stop();
    }
}
