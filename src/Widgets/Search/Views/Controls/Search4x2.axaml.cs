using System.Linq;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Search.Models;
using Search.Services;
using Search.ViewModels;

namespace Search.Views.Controls;

public partial class Search4x2 : UserControl
{
    private SearchViewModel? viewModel;

    public Search4x2()
    {
        InitializeComponent();
        SearchScrollHelper.Attach(DockScroller);
    }

    public Search4x2(SearchViewModel vm) : this()
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

    private void OnCategoryClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string cat } && viewModel != null)
        {
            viewModel.SelectedCategory = cat;
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
                viewModel.ExecuteSearch(overrideEngine: engine);
            }
            else
            {
                viewModel.SelectEngine(engine);
                SearchInputBox.Focus();
            }
        }
    }

    private void OnHistoryClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || viewModel == null || !viewModel.HasHistory) return;

        var flyout = new MenuFlyout { Placement = PlacementMode.BottomEdgeAlignedRight };
        foreach (var query in viewModel.RecentSearches)
        {
            var text = query;
            var item = new MenuItem
            {
                Header = text,
                Icon = new PathIcon
                {
                    Data = Geometry.Parse(SearchIconProvider.HistoryIcon),
                    Width = 12,
                    Height = 12
                }
            };
            item.Click += (_, _) =>
            {
                viewModel.QueryText = text;
                viewModel.ExecuteSearch(text);
            };
            flyout.Items.Add(item);
        }

        flyout.Items.Add(new Separator());
        var clearItem = new MenuItem
        {
            Header = Locales.Locale.Search_Clear_History,
            Icon = new PathIcon
            {
                Data = Geometry.Parse(SearchIconProvider.TrashIcon),
                Width = 12,
                Height = 12,
                Foreground = Brush.Parse("#E05555")
            }
        };
        clearItem.Click += (_, _) => viewModel.ClearAllHistory();
        flyout.Items.Add(clearItem);

        flyout.ShowAt(btn);
    }
}
