using Avalonia.Controls;
using Avalonia.Interactivity;
using Clock.Models;
using Clock.ViewModels;
using DeskCanvas.Contracts;
using DeskCanvas.Core.Interfaces;
using DeskCanvas.Runtime;
using DeskCanvas.UI;

namespace Clock.Views;

/// <summary>
/// 数字时钟小组件：通过 IWidgetSurfaceHints 声明 Colorful 模式下的专属卡片画刷。
/// </summary>
public partial class Digital : UserControl, IWidgetSurfaceHints, IWidgetActivityConsumer
{
    private readonly ClockStatusPresenter statusPresenter = new();
    /// <inheritdoc />
    public string? ColorfulBackgroundResourceKey => "ClockDigitalCardBackground";

    private readonly DigitalClockViewModel viewModel;

    public Digital() : this(new ClockModel()) {}

    /// <summary>
    /// <paramref name="session"/> is injected by WidgetFactory when the host runs the session
    /// pipeline (a stable instance identity): the view model then gates its timer subscription
    /// on the session's activity. Null keeps the legacy always-on behaviour (preview, legacy
    /// layout entries) — those hosts never push activity either.
    /// </summary>
    public Digital(ClockModel clockModel, IWidgetSession? session = null)
    {
        viewModel = new DigitalClockViewModel(clockModel, session);
        DataContext = viewModel;
        Loaded += (_, _) => { statusPresenter.ShowTime("clock"); viewModel.Start(); };
        Unloaded += (_, _) => viewModel.Stop();
        SizeChanged += OnSizeChanged;
        InitializeComponent();
    }

    /// <inheritdoc />
    public void OnSessionActivity(ActivitySnapshot activity) => viewModel.OnSessionActivity(activity);

    // S/M tiers: at a single-cell height the date line (natural 8/52) collapses to
    // a few pixels — show the time only and let the Viewbox use all the height.
    private void OnSizeChanged(object? sender, SizeChangedEventArgs e) =>
        Date.IsVisible = viewModel.ShowDate
                         && e.NewSize.Height > 60
                         && e.NewSize.Width > 90;
}
