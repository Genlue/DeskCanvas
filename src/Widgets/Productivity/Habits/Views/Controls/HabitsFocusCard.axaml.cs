using Avalonia.Controls;
using Avalonia.Interactivity;
using Habits.ViewModels;

namespace Habits.Views.Controls;

public partial class HabitsFocusCard : UserControl
{
    private readonly HabitsView parentView;
    private readonly HabitsViewModel viewModel;

    public HabitsFocusCard(HabitsView parentView, HabitsViewModel viewModel)
    {
        this.parentView = parentView;
        this.viewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
    }

    private void OnPunchClicked(object? sender, RoutedEventArgs e)
    {
        if (viewModel.FocusedHabit != null)
        {
            viewModel.ToggleCheckIn(viewModel.FocusedHabit.Id);
        }
    }

    private void OnStepMinusClicked(object? sender, RoutedEventArgs e)
    {
        if (viewModel.FocusedHabit != null)
        {
            viewModel.StepNumeric(viewModel.FocusedHabit.Id, increment: false);
        }
    }

    private void OnStepPlusClicked(object? sender, RoutedEventArgs e)
    {
        if (viewModel.FocusedHabit != null)
        {
            viewModel.StepNumeric(viewModel.FocusedHabit.Id, increment: true);
        }
    }

    private void OnPrevHabitClicked(object? sender, RoutedEventArgs e)
    {
        viewModel.CycleFocusedHabit(next: false);
        parentView.SaveFocusedHabit(viewModel.FocusedHabit?.Id);
    }

    private void OnNextHabitClicked(object? sender, RoutedEventArgs e)
    {
        viewModel.CycleFocusedHabit(next: true);
        parentView.SaveFocusedHabit(viewModel.FocusedHabit?.Id);
    }

    private void OnOpenPopupClicked(object? sender, RoutedEventArgs e)
    {
        parentView.OpenPopupWindow();
    }
}
