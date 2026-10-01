using System.Text.Json;
using Clock.Models;
using ReactiveUI;
using DeskCanvas.Core.Interfaces;

namespace Clock.ViewModels;

public class AnalogClockSettingsViewModel(IWidgetLayoutProvider widgetLayoutProvider) : ReactiveObject
{
    private ClockModel clockModel = widgetLayoutProvider.Get().GetModel<ClockModel>() ?? new ClockModel();

    public bool ShowSeconds
    {
        get => clockModel.ShowSeconds;
        set => UpdateClockModel(clockModel with { ShowSeconds = value });
    }

    public bool ShowDate
    {
        get => clockModel.ShowDate;
        set => UpdateClockModel(clockModel with { ShowDate = value });
    }

    public bool Use24Hours
    {
        get => clockModel.Use24Hours;
        set => UpdateClockModel(clockModel with { Use24Hours = value });
    }

    public bool ShowTimeZones => !UseLocalTimeZone;

    public bool UseLocalTimeZone
    {
        get => clockModel.TimeZoneId == null;
        set
        {
            UpdateClockModel(clockModel with { TimeZoneId = value ? null : TimeZoneInfo.Local.Id });
            this.RaisePropertyChanged(nameof(ShowTimeZones));
        }
    }

    public TimeZoneInfo TimeZone
    {
        get => clockModel.TimeZoneId != null
                ? TimeZoneInfo.FindSystemTimeZoneById(clockModel.TimeZoneId)
                : TimeZoneInfo.Local;
        set => UpdateClockModel(clockModel with { TimeZoneId = value.Id });
    }

    public TimeZoneInfo[] TimeZones => TimeZoneInfo.GetSystemTimeZones().Append(TimeZone).ToArray();

    private void UpdateClockModel(ClockModel newClockModel)
    {
        clockModel = newClockModel;
        var widgetSettings = widgetLayoutProvider.Get();
        var newSettings = widgetSettings with { Settings = JsonSerializer.SerializeToElement(clockModel) };
        widgetLayoutProvider.Save(newSettings);
    }
}