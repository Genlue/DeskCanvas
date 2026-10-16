using Avalonia.Controls;
using Calendar.ViewModels;
using DeskCanvas.Contracts;
using DeskCanvas.Core.Interfaces;
using DeskCanvas.Runtime;

namespace Calendar.Views;

/// <summary>
/// 1x1 日期小组件：通过 IWidgetSurfaceHints 声明 Colorful 模式下的专属卡片画刷。
/// </summary>
public partial class Date : UserControl, IWidgetSurfaceHints, IWidgetActivityConsumer
{
    /// <inheritdoc />
    public string? ColorfulBackgroundResourceKey => "CalendarCardBackground";

    private readonly DateCalendarViewModel viewModel;

    /// <summary>
    /// <paramref name="session"/> is injected by WidgetFactory when the host runs the session
    /// pipeline (a stable instance identity): the view model then gates its timer subscription
    /// on the session's activity. Null keeps the legacy always-on behaviour (preview, legacy
    /// layout entries) — those hosts never push activity either.
    /// </summary>
    public Date(IWidgetSession? session = null)
    {
        viewModel = new DateCalendarViewModel(session);
        DataContext = viewModel;
        Loaded += (_, _) => viewModel.Start();
        Unloaded += (_, _) => viewModel.Stop();
        InitializeComponent();
    }

    /// <inheritdoc />
    public void OnSessionActivity(ActivitySnapshot activity) => viewModel.OnSessionActivity(activity);
}
