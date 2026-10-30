using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Search.Services;
using Search.ViewModels;

namespace Search.Views.Controls;

public partial class Search4x1 : UserControl
{
    private SearchViewModel? viewModel;

    public Search4x1()
    {
        InitializeComponent();
    }

    public Search4x1(SearchViewModel vm) : this()
    {
        viewModel = vm;
        DataContext = vm;
    }

    private void OnEngineSelectorClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is Button btn && viewModel != null)
        {
            SearchMenuHelper.ShowEngineMenu(btn, viewModel);
        }
    }

    private void OnInputKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && viewModel != null)
        {
            viewModel.ExecuteSearch();
        }
    }

    private void OnClearClicked(object? sender, RoutedEventArgs e)
    {
        viewModel?.ClearQuery();
        SearchInputBox.Focus();
    }

    private void OnSearchActionClicked(object? sender, RoutedEventArgs e)
    {
        viewModel?.ExecuteSearch();
    }
}
