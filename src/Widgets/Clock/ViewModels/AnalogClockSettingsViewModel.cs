using System.Linq;
using System.Text.Json;
using Avalonia.Media;
using Clock.Models;
using Clock.Locales;
using ReactiveUI;
using DeskCanvas.Core.Interfaces;

namespace Clock.ViewModels;

/// <summary>One option of the 表盘颜色 mode combo (跟随背景 / 黑白 / 自定义).</summary>
public record DialColorModeOption(ClockDialColorMode Mode, string Name);

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

    /// <summary>
    /// 表盘颜色 exists for 表盘-3 (AnalogIV) only — its opaque dial surface is what the
    /// setting recolors. This panel is shared by the three analog dials, so the row is
    /// gated by the widget sub-type instead of a separate settings view.
    /// </summary>
    public bool ShowDialColorSetting => widgetLayoutProvider.Get().SubType == "AnalogIV";

    /// <summary>The 表盘颜色 mode options, in display order.</summary>
    public DialColorModeOption[] DialColorModes { get; } =
    [
        new(ClockDialColorMode.BackgroundColor, Locale.Clock_DialColor_Background),
        new(ClockDialColorMode.BlackWhite, Locale.Clock_DialColor_BlackWhite),
        new(ClockDialColorMode.Custom, Locale.Clock_DialColor_Custom)
    ];

    public DialColorModeOption DialColorMode
    {
        get => DialColorModes.FirstOrDefault(option => option.Mode == clockModel.DialColorMode)
               ?? DialColorModes[0];
        set
        {
            if (value == null || value.Mode == clockModel.DialColorMode) return;
            UpdateClockModel(clockModel with { DialColorMode = value.Mode });
            this.RaisePropertyChanged(nameof(DialColorMode));
            this.RaisePropertyChanged(nameof(ShowDialColorPickers));
        }
    }

    /// <summary>True while the light/dark color pickers are shown (自定义 mode).</summary>
    public bool ShowDialColorPickers => clockModel.DialColorMode == ClockDialColorMode.Custom;

    /// <summary>自定义 dial color in light mode (hex stored in the widget model).</summary>
    public Color DialColorLight
    {
        get => ParseColor(clockModel.EffectiveDialColorLight, ClockModel.DefaultDialColorLight);
        set
        {
            var hex = ToHex(value);
            if (hex == clockModel.DialColorLight) return;
            UpdateClockModel(clockModel with { DialColorLight = hex });
            this.RaisePropertyChanged(nameof(DialColorLight));
        }
    }

    /// <summary>自定义 dial color in dark mode (hex stored in the widget model).</summary>
    public Color DialColorDark
    {
        get => ParseColor(clockModel.EffectiveDialColorDark, ClockModel.DefaultDialColorDark);
        set
        {
            var hex = ToHex(value);
            if (hex == clockModel.DialColorDark) return;
            UpdateClockModel(clockModel with { DialColorDark = hex });
            this.RaisePropertyChanged(nameof(DialColorDark));
        }
    }

    private static Color ParseColor(string hex, string fallbackHex) =>
        Color.TryParse(hex, out var color) ? color : Color.Parse(fallbackHex);

    private static string ToHex(Color color) =>
        $"#{color.R:X2}{color.G:X2}{color.B:X2}";

    private void UpdateClockModel(ClockModel newClockModel)
    {
        clockModel = newClockModel;
        var widgetSettings = widgetLayoutProvider.Get();
        var newSettings = widgetSettings with { Settings = JsonSerializer.SerializeToElement(clockModel) };
        widgetLayoutProvider.Save(newSettings);
    }
}
