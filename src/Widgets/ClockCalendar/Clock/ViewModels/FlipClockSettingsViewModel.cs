using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Clock.Locales;
using Clock.Models;
using DeskCanvas.Core.Interfaces;
using DeskCanvas.Runtime;
using ReactiveUI;

namespace Clock.ViewModels;

public record FlipSecondsStyleOption(FlipSecondsStyle Value, string DisplayName);

public record FlipDatePositionOption(FlipDatePosition Value, string DisplayName);

public class FlipClockSettingsViewModel : ReactiveObject
{
    private readonly IWidgetLayoutProvider widgetLayoutProvider;
    private FlipClockModel model;

    public IReadOnlyList<FlipSecondsStyleOption> SecondsStyleOptions { get; }
    public IReadOnlyList<FlipDatePositionOption> DatePositionOptions { get; }

    public FlipClockSettingsViewModel(IWidgetLayoutProvider widgetLayoutProvider)
    {
        this.widgetLayoutProvider = widgetLayoutProvider;
        model = widgetLayoutProvider.Get().GetModel<FlipClockModel>() ?? new FlipClockModel();

        SecondsStyleOptions =
        [
            new(FlipSecondsStyle.FlipCard, Locale.Clock_SecondsStyle_FlipCard),
            new(FlipSecondsStyle.Badge, Locale.Clock_SecondsStyle_Badge)
        ];

        DatePositionOptions =
        [
            new(FlipDatePosition.TopBar, Locale.Clock_DatePosition_TopBar),
            new(FlipDatePosition.BottomBar, Locale.Clock_DatePosition_BottomBar),
            new(FlipDatePosition.CardOverlay, Locale.Clock_DatePosition_CardOverlay)
        ];
    }

    public bool Use24Hours
    {
        get => model.Use24Hours;
        set
        {
            UpdateModel(model with { Use24Hours = value });
            this.RaisePropertyChanged(nameof(Use24Hours));
        }
    }

    public bool ShowSeconds
    {
        get => model.ShowSeconds;
        set
        {
            UpdateModel(model with { ShowSeconds = value });
            this.RaisePropertyChanged(nameof(ShowSeconds));
        }
    }

    public FlipSecondsStyleOption SelectedSecondsStyleOption
    {
        get => SecondsStyleOptions.FirstOrDefault(o => o.Value == model.SecondsStyle) ?? SecondsStyleOptions[0];
        set
        {
            if (value == null) return;
            UpdateModel(model with { SecondsStyle = value.Value });
            this.RaisePropertyChanged(nameof(SelectedSecondsStyleOption));
        }
    }

    public bool ShowDate
    {
        get => model.ShowDate;
        set
        {
            UpdateModel(model with { ShowDate = value });
            this.RaisePropertyChanged(nameof(ShowDate));
        }
    }

    public FlipDatePositionOption SelectedDatePositionOption
    {
        get => DatePositionOptions.FirstOrDefault(o => o.Value == model.DatePosition) ?? DatePositionOptions[0];
        set
        {
            if (value == null) return;
            UpdateModel(model with { DatePosition = value.Value });
            this.RaisePropertyChanged(nameof(SelectedDatePositionOption));
        }
    }

    public bool ShowTimeZones => !UseLocalTimeZone;

    public bool UseLocalTimeZone
    {
        get => model.TimeZoneId == null;
        set
        {
            UpdateModel(model with { TimeZoneId = value ? null : TimeZoneInfo.Local.Id });
            this.RaisePropertyChanged(nameof(UseLocalTimeZone));
            this.RaisePropertyChanged(nameof(ShowTimeZones));
        }
    }

    public TimeZoneInfo TimeZone
    {
        get => SharedTimeZone.Resolve(model.TimeZoneId);
        set
        {
            if (value == null) return;
            UpdateModel(model with { TimeZoneId = value.Id });
            this.RaisePropertyChanged(nameof(TimeZone));
        }
    }

    public TimeZoneInfo[] TimeZones => TimeZoneInfo.GetSystemTimeZones().Append(TimeZone).Distinct().ToArray();

    private void UpdateModel(FlipClockModel newModel)
    {
        model = newModel;
        var widgetSettings = widgetLayoutProvider.Get();
        var newSettings = widgetSettings with { Settings = JsonSerializer.SerializeToElement(model) };
        widgetLayoutProvider.Save(newSettings);
    }
}
