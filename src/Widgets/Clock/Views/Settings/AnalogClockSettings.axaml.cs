using Avalonia.Controls;
using Clock.ViewModels;
using DeskCanvas.Core.Interfaces;

namespace Clock.Views.Settings;

public partial class AnalogClockSettings : UserControl
{
    public AnalogClockSettings(IWidgetLayoutProvider widgetLayoutProvider)
    {
        DataContext = new AnalogClockSettingsViewModel(widgetLayoutProvider);
        InitializeComponent();
    }
}