using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Search.Models;
using Search.Services;
using Search.ViewModels;

namespace Search.Views.Controls;

public partial class Search2x2 : UserControl
{
    private SearchViewModel? viewModel;

    public Search2x2()
    {
        InitializeComponent();
        SearchScrollHelper.Attach(DockScroller);
    }

    public Search2x2(SearchViewModel vm) : this()
    {
        viewModel = vm;
        DataContext = vm;
        UpdateQuickDock();
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SearchViewModel.AllEngines))
            {
                UpdateQuickDock();
            }
        };
    }

    private void UpdateQuickDock()
    {
        if (viewModel == null) return;
        // Load all enabled engines so user can scroll horizontally through them
        QuickDockItems.ItemsSource = viewModel.AllEngines.Where(e => e.IsEnabled).ToList();
    }

    private void OnEngineSelectorClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is Button btn && viewModel != null)
        {
            SearchMenuHelper.ShowEngineMenu(btn, viewModel);
        }
    }

    private void OnOpenHomepageClicked(object? sender, RoutedEventArgs e)
    {
        if (viewModel?.CurrentEngine != null)
        {
            SearchLauncher.Launch(viewModel.CurrentEngine, "");
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

    private void OnQuickEngineClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: SearchEngine engine } && viewModel != null)
        {
            if (viewModel.HasQuery)
            {
                // If user already typed query, directly search with clicked engine!
                viewModel.ExecuteSearch(overrideEngine: engine);
            }
            else
            {
                // Otherwise switch active engine and focus input box
                viewModel.SelectEngine(engine);
                SearchInputBox.Focus();
            }
        }
    }
}
