using Avalonia.Controls;
using Avalonia.Interactivity;

namespace DeskCanvas.Uninstaller;

public partial class MainWindow : Window
{
    private readonly UninstallerViewModel viewModel;

    public MainWindow() : this(new UninstallerViewModel())
    {
    }

    public MainWindow(UninstallerViewModel viewModel)
    {
        InitializeComponent();
        this.viewModel = viewModel;
        DataContext = viewModel;

        viewModel.RequestClose += Close;
    }

    private void OnActionClick(object? sender, RoutedEventArgs e)
    {
        viewModel.OnActionClicked();
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        viewModel.OnCancelClicked();
    }
}
