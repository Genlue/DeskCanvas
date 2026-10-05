using Avalonia.Controls;
using Clock.ViewModels;
using DeskCanvas.Core.Interfaces;
using DeskCanvas.Services;

namespace Clock.Views.Settings;

public partial class FramelessClockSettings : UserControl
{
    /// <summary>
    /// No default value on <paramref name="appSettingsProvider"/> on purpose: with one,
    /// ActivatorUtilities used the default (null) instead of resolving the registered service,
    /// so the edit window's view model always ran provider-less and its theme-dependent rows
    /// (<see cref="FramelessClockSettingsViewModel.ShowLiquidGlassOpacity"/>) never followed the
    /// global theme.
    /// </summary>
    public FramelessClockSettings(IWidgetLayoutProvider widgetLayoutProvider, IAppSettingsProvider? appSettingsProvider)
    {
        DataContext = new FramelessClockSettingsViewModel(widgetLayoutProvider, appSettingsProvider);
        InitializeComponent();
    }
}
