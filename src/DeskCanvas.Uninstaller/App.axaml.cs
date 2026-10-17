using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace DeskCanvas.Uninstaller;

public partial class App : Application
{
    private readonly UninstallerViewModel? viewModel;

    public App() { }

    public App(UninstallerViewModel viewModel)
    {
        this.viewModel = viewModel;
    }

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var vm = viewModel ?? new UninstallerViewModel();
            desktop.MainWindow = new MainWindow(vm);
        }

        base.OnFrameworkInitializationCompleted();
    }
}
