using Avalonia.Controls;
using Clock.ViewModels;
using DeskCanvas.Core.Interfaces;
using DeskCanvas.Services;

namespace Clock.Views.Settings;

public partial class FramelessClockSettings : UserControl
{
    public FramelessClockSettings(IWidgetLayoutProvider widgetLayoutProvider)
        : this(widgetLayoutProvider, null) { }

    /// <summary>
    /// No default value on <paramref name="appSettingsProvider"/> on purpose: with one,
    /// ActivatorUtilities uses the default (null) instead of resolving the registered service.
    /// </summary>
    public FramelessClockSettings(IWidgetLayoutProvider widgetLayoutProvider, IAppSettingsProvider? appSettingsProvider)
    {
        DataContext = new FramelessClockSettingsViewModel(widgetLayoutProvider, appSettingsProvider);
        InitializeComponent();
    }
}
