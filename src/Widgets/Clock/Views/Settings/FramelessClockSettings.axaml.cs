using Avalonia.Controls;
using Clock.ViewModels;
using DeskCanvas.Core.Interfaces;

namespace Clock.Views.Settings;

public partial class FramelessClockSettings : UserControl
{
    /// <summary>
    /// The clock is locked to the 毛玻璃 material, so the settings carry no theme- or
    /// optics-dependent rows and the view model never reads global settings — the edit window
    /// only needs the widget's own layout.
    /// </summary>
    public FramelessClockSettings(IWidgetLayoutProvider widgetLayoutProvider)
    {
        DataContext = new FramelessClockSettingsViewModel(widgetLayoutProvider);
        InitializeComponent();
    }
}
