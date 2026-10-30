using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Search.Models;
using Search.Services;
using Search.ViewModels;

namespace Search.Views.Controls;

public partial class Search4x4 : UserControl
{
    private SearchViewModel? viewModel;

    public Search4x4()
    {
        InitializeComponent();
    }

    public Search4x4(SearchViewModel vm) : this()
    {
        viewModel = vm;
        DataContext = vm;
        UpdateItems();

        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SearchViewModel.DisplayEngines) ||
                e.PropertyName == nameof(SearchViewModel.RecentSearches))
            {
                UpdateItems();
            }
        };
    }

    private void UpdateItems()
    {
        if (viewModel == null) return;
        // Symmetrically display top 8 engines (4 columns x 2 rows)
        GridMatrixItems.ItemsSource = viewModel.DisplayEngines.Take(8).ToList();
        // Display top 3 history search chips so they never truncate
        HistoryChipsItems.ItemsSource = viewModel.RecentSearches.Take(3).ToList();
    }

    private void OnEngineSelectorClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is Button btn && viewModel != null)
        {
            SearchMenuHelper.ShowEngineMenu(btn, viewModel);
        }
    }

    private void OnCategoryClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string cat } && viewModel != null)
        {
            viewModel.SelectedCategory = cat;
            UpdateItems();
        }
    }

    private void OnInputKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && viewModel != null)
        {
            viewModel.ExecuteSearch();
            UpdateItems();
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
        UpdateItems();
    }

    private void OnGridEngineClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: SearchEngine engine } && viewModel != null)
        {
            if (viewModel.HasQuery)
            {
                viewModel.ExecuteSearch(overrideEngine: engine);
            }
            else
            {
                viewModel.SelectEngine(engine);
                SearchInputBox.Focus();
            }
            UpdateItems();
        }
    }

    private void OnHistoryChipClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string query } && viewModel != null)
        {
            viewModel.QueryText = query;
            viewModel.ExecuteSearch(query);
            UpdateItems();
        }
    }

    private void OnClearAllHistoryClicked(object? sender, RoutedEventArgs e)
    {
        viewModel?.ClearAllHistory();
        UpdateItems();
    }
}
