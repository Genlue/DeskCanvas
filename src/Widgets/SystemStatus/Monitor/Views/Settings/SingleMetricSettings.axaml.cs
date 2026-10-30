using Avalonia.Controls;
using Monitor.ViewModels;
using DeskCanvas.Core.Interfaces;

namespace Monitor.Views.Settings;

public partial class SingleMetricSettings : UserControl
{
    public SingleMetricSettings(IWidgetLayoutProvider widgetLayoutProvider)
    {
        DataContext = new SingleMetricSettingsViewModel(widgetLayoutProvider);
        InitializeComponent();
    }
}