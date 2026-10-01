using System.Text.Json;
using Clock.Locales;
using Clock.Models;
using ReactiveUI;
using DeskCanvas.Core.Interfaces;

namespace Clock.ViewModels;

public class WorldClockSettingsViewModel(IWidgetLayoutProvider widgetLayoutProvider) : ReactiveObject
{
    private WorldClockModel clockModel = GetInitialModel(widgetLayoutProvider);
    private static WorldClockModel GetInitialModel(IWidgetLayoutProvider widgetLayoutProvider)
    {
        var model = widgetLayoutProvider.Get().GetModel<WorldClockModel>();

        if (model?.TimeZoneIds == null || model.TimeZoneIds.Count < 4)
            return new WorldClockModel([null, null, null, null], [null, null, null, null]);

        if (model.CityNames is null || model.CityNames.Count < 4)
            return model with { CityNames = [null, null, null, null] };

        return model;
    }
    
    public TimeZoneInfo[] TimeZones => TimeZoneInfo.GetSystemTimeZones().Append(TimeZone1).Append(TimeZone2).Append(TimeZone3).Append(TimeZone4).ToArray();

    public string TimeZone1Title => $"{Locale.Clock_TimeZone} 1";
    public string TimeZone2Title => $"{Locale.Clock_TimeZone} 2";
    public string TimeZone3Title => $"{Locale.Clock_TimeZone} 3";
    public string TimeZone4Title => $"{Locale.Clock_TimeZone} 4";
    public string CityName1Title => $"{Locale.Clock_CityName} 1";
    public string CityName2Title => $"{Locale.Clock_CityName} 2";
    public string CityName3Title => $"{Locale.Clock_CityName} 3";
    public string CityName4Title => $"{Locale.Clock_CityName} 4";
    
    public TimeZoneInfo TimeZone1
    {
        get => Get(0);
        set => Set(0, value);
    }
    
    public TimeZoneInfo TimeZone2
    {
        get => Get(1);
        set => Set(1, value);
    }
    
    public TimeZoneInfo TimeZone3
    {
        get => Get(2);
        set => Set(2, value);
    }
    
    public TimeZoneInfo TimeZone4
    {
        get => Get(3);
        set => Set(3, value);
    }

    public string CityName1
    {
        get => GetCityName(0);
        set => SetCityName(0, value);
    }

    public string CityName2
    {
        get => GetCityName(1);
        set => SetCityName(1, value);
    }

    public string CityName3
    {
        get => GetCityName(2);
        set => SetCityName(2, value);
    }

    public string CityName4
    {
        get => GetCityName(3);
        set => SetCityName(3, value);
    }

    private TimeZoneInfo Get(int index)
    {
        var id = clockModel.TimeZoneIds[index];
        
        return id != null
            ? TimeZoneInfo.FindSystemTimeZoneById(id)
            : TimeZoneInfo.Local;
    }

    private void Set(int index, TimeZoneInfo timeZone)
    {
        clockModel.TimeZoneIds[index] = timeZone.Id;
        UpdateClockModel();
    }

    private string GetCityName(int index)
    {
        var name = clockModel.CityNames?.ElementAtOrDefault(index);
        return string.IsNullOrWhiteSpace(name) ? string.Empty : name!;
    }

    private void SetCityName(int index, string? value)
    {
        var names = new List<string?>(clockModel.CityNames ?? new List<string?>(new string?[4]));
        while (names.Count < 4) names.Add(null);

        var trimmed = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        if (names[index] == trimmed) return;
        names[index] = trimmed;

        clockModel = clockModel with { CityNames = names };
        UpdateClockModel();
    }
    
    private void UpdateClockModel()
    {
        var widgetSettings = widgetLayoutProvider.Get();
        var newSettings = widgetSettings with { Settings = JsonSerializer.SerializeToElement(clockModel) };
        widgetLayoutProvider.Save(newSettings);
    }
}