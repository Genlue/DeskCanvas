using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Calendar.ViewModels;

namespace Calendar.Views.Controls;

public partial class ScheduleSmall : UserControl
{
    private readonly Schedule parentWidget;
    private readonly ScheduleViewModel viewModel;

    public ScheduleSmall(Schedule parentWidget, ScheduleViewModel viewModel)
    {
        this.parentWidget = parentWidget;
        this.viewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
    }

    private void OnHeaderClicked(object? sender, RoutedEventArgs e)
    {
        parentWidget.OpenPanel();
    }

    private void OnOpenPanelClicked(object? sender, RoutedEventArgs e)
    {
        parentWidget.OpenPanel();
    }

    private void OnEventClicked(object? sender, RoutedEventArgs e)
    {
        parentWidget.OpenPanel(targetEventId: viewModel.NextEvent?.Id);
    }

    private void OnEventItemClicked(object? sender, RoutedEventArgs e)
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
