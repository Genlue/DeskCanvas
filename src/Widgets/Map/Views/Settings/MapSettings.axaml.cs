using System;
using Avalonia.Controls;
using Map.ViewModels.Settings;
using DeskCanvas.Core.Interfaces;

namespace Map.Views.Settings;

public partial class MapSettings : UserControl
{
    public MapSettings() : this(null!) { }

    // 文案全部来自 Map.Locales 资源（XAML 内 x:Static 直接绑定），不再按语言在代码里二次赋值。
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
