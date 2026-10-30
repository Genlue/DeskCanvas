using Avalonia.Controls;
using Avalonia.Interactivity;
using Weather.ViewModels;

namespace Weather.Views.Controls;

public partial class ForecastSmall : UserControl
{
    private readonly Forecast? owner;

    public ForecastSmall(ForecastViewModel viewModel, Forecast? owner = null)
    {
        this.owner = owner;
        DataContext = viewModel;
        InitializeComponent();
    }

    public void OpenPopup(object? sender, RoutedEventArgs e)
    {
        owner?.OpenPopupWindow();
    }
}