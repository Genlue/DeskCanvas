using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Calendar.ViewModels;

namespace Calendar.Views.Controls;

public partial class ScheduleLarge : UserControl
{
    private readonly Schedule parentWidget;
    private readonly ScheduleViewModel viewModel;

    public ScheduleLarge(Schedule parentWidget, ScheduleViewModel viewModel)
    {
        this.parentWidget = parentWidget;
        this.viewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
    }

    private void OnOpenPanelClicked(object? sender, RoutedEventArgs e)
    {
        parentWidget.OpenPanel();
    }

    private void OnDayItemClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: DateTime date })
        {
            viewModel.SelectedDate = date;
        }
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
