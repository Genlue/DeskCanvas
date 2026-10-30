using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Search.Services;
using Search.ViewModels;

namespace Search.Views.Controls;

public partial class Search1x1 : UserControl
{
    private SearchViewModel? viewModel;

    public Search1x1()
    {
        InitializeComponent();
    }

    public Search1x1(SearchViewModel vm) : this()
    {
        viewModel = vm;
        DataContext = vm;
    }

    private void OnCardClicked(object? sender, RoutedEventArgs e)
    {
        // Button click automatically opens the attached Flyout.
        // Focus the text box once the flyout is opened.
        if (FlyoutTextBox != null)
        {
            FlyoutTextBox.Focus();
        }
    }

    private void OnEngineIconClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is Button btn && viewModel != null)
        {
            SearchMenuHelper.ShowEngineMenu(btn, viewModel);
        }
    }

    private void OnFlyoutKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && viewModel != null)
        {
            viewModel.ExecuteSearch();
            MainCardButton.Flyout?.Hide();
        }
    }

    private void OnFlyoutSearchClicked(object? sender, RoutedEventArgs e)
    {
        if (viewModel != null)
        {
            viewModel.ExecuteSearch();
            MainCardButton.Flyout?.Hide();
        }
    }
}
