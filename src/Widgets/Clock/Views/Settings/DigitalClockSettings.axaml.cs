using Avalonia.Controls;
using Clock.ViewModels;
using DeskCanvas.Core.Interfaces;

namespace Clock.Views.Settings;

public partial class DigitalClockSettings : UserControl
{
    public DigitalClockSettings(IWidgetLayoutProvider widgetLayoutProvider)
    {
        DataContext = new AnalogClockSettingsViewModel(widgetLayoutProvider);
        InitializeComponent();
    }
}