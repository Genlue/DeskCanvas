using Avalonia.Controls;
using Clock.ViewModels;
using DeskCanvas.Core.Interfaces;

namespace Clock.Views.Settings;

public partial class FramelessClockSettings : UserControl
{
    public FramelessClockSettings(IWidgetLayoutProvider widgetLayoutProvider)
    {
        DataContext = new FramelessClockSettingsViewModel(widgetLayoutProvider);
        InitializeComponent();
    }
}
