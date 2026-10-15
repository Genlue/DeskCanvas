using System;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ControlCenter.Models;
using ControlCenter.Services;
using DeskCanvas.Core.Interfaces;
using DeskCanvas.Core.Models;

namespace ControlCenter.Views;

public partial class SingleControlView : UserControl, IWidgetSelfRefreshing, IWidgetSuspendable, IDisposable, IWidgetSurfaceHints
{
    public string? ColorfulBackgroundResourceKey => "ControlCenterCardBackground";

    private SingleControlModel model;
    private readonly DispatcherTimer pollTimer;
    private bool isSuspended;

    public SingleControlView() : this(new SingleControlModel()) { }

    public SingleControlView(SingleControlModel model)
    {
        this.model = model ?? new SingleControlModel();
        InitializeComponent();

        MainCapsule.IsStandaloneTile = true;
        MainCapsule.StateMutated += OnCapsuleStateMutated;

        pollTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(1500)
        };
        pollTimer.Tick += (_, _) => RefreshState(false);

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        SizeChanged += OnSizeChanged;
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        ApplySpanAndConfig();
        RefreshState(true);
        if (!isSuspended)
            pollTimer.Start();
    }

    private void OnUnloaded(object? sender, RoutedEventArgs e)
    {
        pollTimer.Stop();
    }

    private void OnSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        ApplySpanAndConfig();
    }

    private (int Cols, int Rows) ResolveSpan()
    {
        for (var node = this.GetVisualParent(); node != null; node = node.GetVisualParent())
        {
            if (node is DeskCanvas.Views.Widget widget)
            {
                var span = widget.CurrentSpan;
                int c = span.Columns >= 2 ? 2 : 1;
                int r = span.Rows >= 2 ? 2 : 1;
                return (c, r);
            }
        }

        var size = Bounds.Size;
        if (size.Width > 0 && size.Height > 0)
        {
            if (size.Width >= size.Height * 1.45) return (2, 1);
            if (size.Height >= size.Width * 1.45) return (1, 2);
            if (size.Width >= 165 && size.Height >= 165) return (2, 2);
        }

        return (1, 1);
    }

    private void ApplySpanAndConfig()
    {
        var (cols, rows) = ResolveSpan();
        MainCapsule.Configure(model.ItemType, cols, rows, model.WheelStep);
    }

    private async void OnCapsuleStateMutated()
    {
        RefreshState(true);
        await Task.Delay(250);
        if (IsLoaded && !isSuspended)
            RefreshState(true);
    }

    private void RefreshState(bool forceSlowQueries)
    {
        var snapshot = SystemControlService.Shared.GetSnapshot(forceSlowQueries);
        MainCapsule.RenderSnapshot(snapshot);
    }

    public void Refresh(WidgetLayout layout)
    {
        if (layout.Settings is { ValueKind: JsonValueKind.Object } settings)
        {
            try
            {
                var updated = settings.Deserialize<SingleControlModel>();
                if (updated != null)
                {
                    model = updated;
                }
            }
            catch { }
        }

        ApplySpanAndConfig();
        RefreshState(false);
    }

    public void Suspend()
    {
        isSuspended = true;
        pollTimer.Stop();
    }

    public void Resume()
    {
        isSuspended = false;
        RefreshState(true);
        if (IsLoaded)
            pollTimer.Start();
    }

    public void Dispose()
    {
        pollTimer.Stop();
    }
}
