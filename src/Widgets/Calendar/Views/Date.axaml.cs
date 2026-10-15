using Avalonia.Controls;
using Calendar.ViewModels;
using DeskCanvas.Core.Interfaces;

namespace Calendar.Views;

/// <summary>
/// 1x1 日期小组件：通过 IWidgetSurfaceHints 声明 Colorful 模式下的专属卡片画刷。
/// </summary>
public partial class Date : UserControl, IWidgetSurfaceHints
{
    /// <inheritdoc />
    public string? ColorfulBackgroundResourceKey => "CalendarCardBackground";

    private readonly DateCalendarViewModel viewModel;

    public Date()
    {
        viewModel = new DateCalendarViewModel();
        DataContext = viewModel;
        Loaded += (_, _) => viewModel.Start();
        Unloaded += (_, _) => viewModel.Stop();
        InitializeComponent();
    }
}