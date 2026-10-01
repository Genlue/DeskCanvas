using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using FixedWidgets.Locales;
using FixedWidgets.Models;
using FixedWidgets.Services;
using DeskCanvas.Core.Interfaces;
using DeskCanvas.Core.Models;

namespace FixedWidgets.Views.Settings;

public partial class AggregateSettings : UserControl
{
    private readonly IWidgetLayoutProvider widgetLayoutProvider;
    private OpenMeteoWeatherService? weatherService = new();
    private AggregateModel model;
    private bool isInitializing = true;

    public AggregateSettings() : this(null!)
    {
    }

    public AggregateSettings(IWidgetLayoutProvider? widgetLayoutProvider)
    {
        this.widgetLayoutProvider = widgetLayoutProvider!;
        model = widgetLayoutProvider != null ? (ReadModel(widgetLayoutProvider.Get()) ?? new AggregateModel()) : new AggregateModel();

        InitializeComponent();
        SearchBox.AsyncPopulator = SearchCity;
        InitOptions();
        LoadFromModel();
        isInitializing = false;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    /// <summary>
    /// The settings window caches its pages, so this page is unloaded when it is navigated away
    /// from and added back later: the geocoding service released by <see cref="OnUnloaded"/> is
    /// rebuilt here so the city search keeps working.
    /// </summary>
    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        weatherService ??= new OpenMeteoWeatherService();
    }

    /// <summary>
    /// Release the service's HTTP client — one per page instance would otherwise stay alive for
    /// the lifetime of the process. A disposed service is never used again; the next load builds
    /// a fresh one. Idempotent.
    /// </summary>
    private void OnUnloaded(object? sender, RoutedEventArgs e)
    {
        weatherService?.Dispose();
        weatherService = null;
    }

    private void InitOptions()
    {
        UnitCombo.ItemsSource = new List<string>
        {
            Locale.Setting_Unit_Celsius,
            Locale.Setting_Unit_Fahrenheit
        };

        FirstDayCombo.ItemsSource = new List<string>
        {
            Locale.Setting_FirstDay_Monday,
            Locale.Setting_FirstDay_Sunday
        };
    }

    private void LoadFromModel()
    {
        SearchBox.Text = model.City;
        CityStatusText.Text = $"当前坐标: {model.Latitude:F2}°, {model.Longitude:F2}°";

        UnitCombo.SelectedIndex = string.Equals(model.TemperatureUnit, "fahrenheit", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        Use24HoursSwitch.IsChecked = model.Is24Hour;
        ShowSecondsSwitch.IsChecked = model.ShowSeconds;

        FirstDayCombo.SelectedIndex = model.FirstDayOfWeek == DayOfWeek.Sunday ? 1 : 0;
        PaddingUpDown.Value = (decimal)(model.Padding > 0 ? model.Padding : 14.0);

        HollowTodaySwitch.IsChecked = model.HollowTodayNumber;
        TodayColorModeCombo.SelectedIndex = model.TodayColorMode == "Custom" ? 1 : 0;
        UpdateTodayColorVisibility();

        if (!string.IsNullOrWhiteSpace(model.TodayColorLight) && Color.TryParse(model.TodayColorLight, out var lightColor))
            TodayColorLightPicker.Color = lightColor;
        else
            TodayColorLightPicker.Color = ResolveAccentColor();

        if (!string.IsNullOrWhiteSpace(model.TodayColorDark) && Color.TryParse(model.TodayColorDark, out var darkColor))
            TodayColorDarkPicker.Color = darkColor;
        else
            TodayColorDarkPicker.Color = ResolveAccentColor();
    }

    private async Task<IEnumerable<object>> SearchCity(string? query, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(query)) return [];
        var current = weatherService;
        if (current == null) return [];

        var list = await current.SearchCitiesAsync(query, token);
        return list ?? [];
    }

    private void SearchBox_OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (isInitializing || SearchBox.SelectedItem is not City city) return;

        ApplyCity(city);
    }

    private void SearchBox_OnKeyUp(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            if (SearchBox.SelectedItem is City city)
            {
                ApplyCity(city);
            }
            else if (!string.IsNullOrWhiteSpace(SearchBox.Text))
            {
                _ = ResolveCityAsync(SearchBox.Text.Trim());
            }
        }
    }

    private void SearchBox_OnLostFocus(object? sender, RoutedEventArgs e)
    {
        if (SearchBox.SelectedItem is City city)
        {
            ApplyCity(city);
        }
        else if (!string.IsNullOrWhiteSpace(SearchBox.Text) &&
                 !string.Equals(SearchBox.Text.Trim(), model.City, StringComparison.OrdinalIgnoreCase))
        {
            _ = ResolveCityAsync(SearchBox.Text.Trim());
        }
    }

    private void ApplyCity(City city)
    {
        isInitializing = true;
        SearchBox.Text = city.Name;
        isInitializing = false;

        model = model with
        {
            City = city.Name,
            Latitude = city.Latitude,
            Longitude = city.Longitude
        };
        Save();
        CityStatusText.Text = $"已识别: {city.SearchName}";
    }

    private async Task ResolveCityAsync(string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return;

        var current = weatherService;
        if (current == null) return;

        CityStatusText.Text = "正在从天气源识别...";

        var results = await current.SearchCitiesAsync(query);
        if (results != null && results.Count > 0)
        {
            var matched = results[0];
            ApplyCity(matched);
        }
        else
        {
            CityStatusText.Text = "未在天气源检索到该城市";
        }
    }

    private void OnUnitChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (isInitializing || UnitCombo.SelectedIndex < 0) return;
        model = model with
        {
            TemperatureUnit = UnitCombo.SelectedIndex == 1 ? "fahrenheit" : "celsius"
        };
        Save();
    }

    private void OnToggleChanged(object? sender, RoutedEventArgs e)
    {
        if (isInitializing) return;
        model = model with
        {
            Is24Hour = Use24HoursSwitch.IsChecked ?? true,
            ShowSeconds = ShowSecondsSwitch.IsChecked ?? false,
            HollowTodayNumber = HollowTodaySwitch.IsChecked ?? true
        };
        Save();
    }

    private void OnTodayColorModeChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (isInitializing || TodayColorModeCombo.SelectedIndex < 0) return;
        var isCustom = TodayColorModeCombo.SelectedIndex == 1;
        model = model with
        {
            TodayColorMode = isCustom ? "Custom" : "Accent",
            TodayColorLight = isCustom ? (model.TodayColorLight ?? TodayColorLightPicker.Color.ToString()) : model.TodayColorLight,
            TodayColorDark = isCustom ? (model.TodayColorDark ?? TodayColorDarkPicker.Color.ToString()) : model.TodayColorDark
        };
        UpdateTodayColorVisibility();
        Save();
    }

    private void OnColorPickerChanged(object? sender, ColorChangedEventArgs e)
    {
        if (isInitializing) return;
        model = model with
        {
            TodayColorLight = TodayColorLightPicker.Color.ToString(),
            TodayColorDark = TodayColorDarkPicker.Color.ToString()
        };
        Save();
    }

    private void UpdateTodayColorVisibility()
    {
        var isCustom = TodayColorModeCombo.SelectedIndex == 1;
        TodayColorLightSetting.IsVisible = isCustom;
        TodayColorDarkSetting.IsVisible = isCustom;
    }

    private static Color ResolveAccentColor()
    {
        if (Application.Current != null &&
            Application.Current.TryFindResource("SystemAccentColor", out var value) &&
            value is Color accent)
        {
            return accent;
        }
        return Color.Parse("#FF7043");
    }

    private void OnFirstDayChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (isInitializing || FirstDayCombo.SelectedIndex < 0) return;
        model = model with
        {
            FirstDayOfWeek = FirstDayCombo.SelectedIndex == 1 ? DayOfWeek.Sunday : DayOfWeek.Monday
        };
        Save();
    }

    private void OnPaddingChanged(object? sender, NumericUpDownValueChangedEventArgs e)
    {
        if (isInitializing || !e.NewValue.HasValue) return;
        model = model with
        {
            Padding = (double)e.NewValue.Value
        };
        Save();
    }

    private static AggregateModel? ReadModel(WidgetLayout layout)
    {
        try { return layout.GetModel<AggregateModel>(); }
        catch { return null; }
    }

    private void Save()
    {
        if (widgetLayoutProvider == null) return;
        var layout = widgetLayoutProvider.Get();
        widgetLayoutProvider.Save(layout with { Settings = JsonSerializer.SerializeToElement(model) });
    }
}
