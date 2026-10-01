using Avalonia.Controls;
using Monitor.ViewModels;
using DeskCanvas.Core.Interfaces;

namespace Monitor.Views.Settings;

public partial class MultiDashboardSettings : UserControl
{
    public MultiDashboardSettings(IWidgetLayoutProvider widgetLayoutProvider)
    {
        DataContext = new MultiDashboardSettingsViewModel(widgetLayoutProvider);
        InitializeComponent();
    }
}
