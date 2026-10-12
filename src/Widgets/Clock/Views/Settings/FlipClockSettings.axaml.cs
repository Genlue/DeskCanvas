using Avalonia.Controls;
using Clock.ViewModels;
using DeskCanvas.Core.Interfaces;

namespace Clock.Views.Settings;

public partial class FlipClockSettings : UserControl
{
    public FlipClockSettings(IWidgetLayoutProvider widgetLayoutProvider)
    {
        DataContext = new FlipClockSettingsViewModel(widgetLayoutProvider);
        InitializeComponent();
    }
}
