using Avalonia.Controls;
using Avalonia.Interactivity;
using Habits.ViewModels;

namespace Habits.Views.Controls;

public partial class HabitsListCard : UserControl
{
    private readonly HabitsView parentView;
    private readonly HabitsViewModel viewModel;

    public HabitsListCard(HabitsView parentView, HabitsViewModel viewModel)
    {
        this.parentView = parentView;
        this.viewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
    }

    private void OnCheckInClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: HabitDisplayItem item })
        {
            if (item.IsNumeric && !item.IsCompleted)
            {
                viewModel.StepNumeric(item.Id, increment: true);
            }
            else
            {
                viewModel.ToggleCheckIn(item.Id);
            }
        }
    }

    private void OnStepMinusClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: HabitDisplayItem item })
        {
            viewModel.StepNumeric(item.Id, increment: false);
        }
    }

    private void OnStepPlusClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: HabitDisplayItem item })
        {
            viewModel.StepNumeric(item.Id, increment: true);
        }
    }

    private void OnOpenPopupClicked(object? sender, RoutedEventArgs e)
    {
        parentView.OpenPopupWindow();
    }
}
