using Avalonia.Controls;
using Map.ViewModels.Settings;
using DeskCanvas.Core.Interfaces;

namespace Map.Views.Settings;

public partial class MapSettings : UserControl
{
    public MapSettings() : this(null!) { }

    public MapSettings(IWidgetLayoutProvider widgetLayoutProvider)
    {
        DataContext = new MapSettingsViewModel(widgetLayoutProvider);
        InitializeComponent();
    }

    private async void OnLocateNowClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is MapSettingsViewModel vm)
        {
            await vm.LocateNowAsync();
        }
    }
}
