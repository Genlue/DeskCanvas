using Avalonia.Controls;
using Calendar.ViewModels;
using DeskCanvas.Core.Interfaces;

namespace Calendar.Views.Settings;

public partial class DateSettings : UserControl
{
    public DateSettings(IWidgetLayoutProvider widgetLayoutProvider)
    {
        DataContext = new DateSettingsViewModel(widgetLayoutProvider);
        InitializeComponent();
    }
}