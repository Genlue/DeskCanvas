using Avalonia.Controls;
using Avalonia.Interactivity;
using Calendar.ViewModels;

namespace Calendar.Views.Controls;

public partial class ScheduleMedium : UserControl
{
    private readonly Schedule parentWidget;
    private readonly ScheduleViewModel viewModel;

    public ScheduleMedium(Schedule parentWidget, ScheduleViewModel viewModel)
    {
        this.parentWidget = parentWidget;
        this.viewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
    }

    private void OnDateBadgeClicked(object? sender, RoutedEventArgs e)
    {
        parentWidget.OpenPanel();
    }

    private void OnEventRowClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string eventId })
        {
            parentWidget.OpenPanel(targetEventId: eventId);
        }
        else
        {
            parentWidget.OpenPanel();
        }
    }
}
