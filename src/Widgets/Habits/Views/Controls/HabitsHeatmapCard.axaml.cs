using Avalonia.Controls;
using Avalonia.Interactivity;
using Habits.ViewModels;

namespace Habits.Views.Controls;

public partial class HabitsHeatmapCard : UserControl
{
    private readonly HabitsView parentView;
    private readonly HabitsViewModel viewModel;

    public HabitsHeatmapCard(HabitsView parentView, HabitsViewModel viewModel)
    {
        this.parentView = parentView;
        this.viewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
    }

    private void OnOpenPopupClicked(object? sender, RoutedEventArgs e)
    {
        parentView.OpenPopupWindow();
    }
}
