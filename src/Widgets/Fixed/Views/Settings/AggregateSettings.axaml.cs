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
using DeskCanvas.Views.Controls;

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
        bool isZh = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.Equals("zh", StringComparison.OrdinalIgnoreCase);
        SearchBox.Text = !isZh && model.City == "北京" ? "Beijing" : model.City;
        CityStatusText.Text = string.Format(Locale.Setting_CoordFormat, model.Latitude, model.Longitude);

        UnitCombo.SelectedIndex = string.Equals(model.TemperatureUnit, "fahrenheit", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        Use24HoursSwitch.IsChecked = model.Is24Hour;
        ShowSecondsSwitch.IsChecked = model.ShowSeconds;

        FirstDayCombo.SelectedIndex = model.FirstDayOfWeek == DayOfWeek.Sunday ? 1 : 0;
        PaddingUpDown.Value = (decimal)(model.Padding > 0 ? model.Padding : 14.0);

        HollowTodaySwitch.IsChecked = model.HollowTodayNumber;
        TodayColorModeCombo.SelectedIndex = model.TodayColorMode == "Custom" ? 1 : 0;
        UpdateTodayColorVisibility();

        // The shared ColorInput holds the persisted "#RRGGBB" string; an unset model value
        // falls back to the accent so both rows start on something sensible.
        TodayColorLightInput.Value = NormalizeHex(model.TodayColorLight) ?? ToHex(ResolveAccentColor());
        TodayColorDarkInput.Value = NormalizeHex(model.TodayColorDark) ?? ToHex(ResolveAccentColor());
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
        CityStatusText.Text = string.Format(Locale.Setting_CityMatchedFormat, city.SearchName);
    }

    private async Task ResolveCityAsync(string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return;

        var current = weatherService;
        if (current == null) return;

        CityStatusText.Text = Locale.Setting_CitySearching;

        var results = await current.SearchCitiesAsync(query);
        if (results != null && results.Count > 0)
        {
            var matched = results[0];
            ApplyCity(matched);
        }
        else
        {
            CityStatusText.Text = Locale.Setting_CityNotFound;
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
            TodayColorLight = isCustom ? (model.TodayColorLight ?? TodayColorLightInput.Value) : model.TodayColorLight,
            TodayColorDark = isCustom ? (model.TodayColorDark ?? TodayColorDarkInput.Value) : model.TodayColorDark
        };
        UpdateTodayColorVisibility();
        Save();
    }

    /// <summary>
    /// Fires once per accepted user edit of either colour row (W9). Reading both rows keeps the
    /// old picker behaviour — the model always stores the pair — while the shared control
    /// guarantees the value is a valid, normalised "#RRGGBB" by the time it gets here.
    /// </summary>
    private void OnColorCommitted(object? sender, ColorInputValueCommittedEventArgs e)
    {
        if (isInitializing) return;
        model = model with
        {
            TodayColorLight = TodayColorLightInput.Value,
            TodayColorDark = TodayColorDarkInput.Value
        };
        Save();
    }

    private static string? NormalizeHex(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (!Color.TryParse(value, out var color)) return null;
        return ToHex(color);
    }

    private static string ToHex(Color color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";

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
