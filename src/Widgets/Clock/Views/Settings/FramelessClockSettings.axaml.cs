using Avalonia.Controls;
using Clock.ViewModels;
using DeskCanvas.Core.Interfaces;
using DeskCanvas.Services;

namespace Clock.Views.Settings;

public partial class FramelessClockSettings : UserControl
{
    public FramelessClockSettings(IWidgetLayoutProvider widgetLayoutProvider, IAppSettingsProvider? appSettingsProvider = null)
    {
        DataContext = new FramelessClockSettingsViewModel(widgetLayoutProvider, appSettingsProvider);
        InitializeComponent();
    }
}
