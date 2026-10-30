using Avalonia.Controls;
using Avalonia.Interactivity;
using DeskCanvas.Core.Interfaces;
using Weather.Models.Geocoding;
using Weather.Services;
using Weather.ViewModels;

namespace Weather.Views.Settings;

public partial class ForecastSettings : UserControl
{
    private OpenMeteoWeatherProvider? provider;
    
    public ForecastSettings(IWidgetLayoutProvider widgetLayoutProvider)
    {
        provider = new OpenMeteoWeatherProvider();
        DataContext = new ForecastSettingsViewModel(widgetLayoutProvider);
        InitializeComponent();
        Search.AsyncPopulator = SearchCity;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    /// <summary>
    /// The settings window caches its pages, so this page is unloaded when it is navigated away
    /// from and added back later: the geocoding provider released by <see cref="OnUnloaded"/>
    /// is rebuilt here so the city search keeps working.
    /// </summary>
    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        provider ??= new OpenMeteoWeatherProvider();
    }

    /// <summary>
    /// Release this page's pending provider waits. A disposed provider is never used
    /// again; the next load builds a fresh owner. Idempotent.
    /// </summary>
    private void OnUnloaded(object? sender, RoutedEventArgs e)
    {
        provider?.Dispose();
        provider = null;
    }

    private async Task<IEnumerable<object>> SearchCity(string? query, CancellationToken token)
    {
        var current = provider;
        if (current == null) return [];

        return (await current.GetCitiesAsync(query ?? "", token) ?? []);
    }
    
    private void Search_OnLostFocus(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ForecastSettingsViewModel viewModel || Search.SelectedItem is not City city) return;
        viewModel.Location = city;
    }
}
