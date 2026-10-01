using System.Text.Json;
using Avalonia.Media;
using Avalonia.Styling;
using Calendar.Models;
using ReactiveUI;
using DeskCanvas.Core.Interfaces;

namespace Calendar.ViewModels;

public class MonthCalendarSettingsViewModel(IWidgetLayoutProvider widgetLayoutProvider) : ReactiveObject
{
    public MonthCalendarModel Model =>
        widgetLayoutProvider.Get().GetModel<MonthCalendarModel>() 
        ?? new MonthCalendarModel(DayOfWeek.Monday);

    public DayOfWeek[] Days => Enum.GetValues<DayOfWeek>();
    
    public DayOfWeek FirstDayOfWeek
    {
        get => Model.FirstDayOfWeek;
        set
        {
            var newModel = Model with { FirstDayOfWeek = value };
            Save(newModel);
            this.RaisePropertyChanged();
        }
    }

    public string[] TodayColorModes { get; } = ["Accent", "Custom"];

    public bool IsCustomTodayColor => Model.TodayColorMode == "Custom";

    /// <summary>
    /// Punch today's number out of the marker disc (镂空). On by default; turning it off paints
    /// the number on top of the disc in white, which is the historic look.
    /// </summary>
    public bool HollowTodayNumber
    {
        get => Model.HollowTodayNumber;
        set
        {
            if (value == Model.HollowTodayNumber) return;
            Save(Model with { HollowTodayNumber = value });
            this.RaisePropertyChanged();
        }
    }

    /// <summary>0 = follow accent, 1 = custom. Drives the mode ComboBox.</summary>
    public int TodayColorModeIndex
    {
        get => Model.TodayColorMode == "Custom" ? 1 : 0;
        set
        {
            var mode = value == 1 ? "Custom" : "Accent";
            if (mode == Model.TodayColorMode) return;

            // Switching to custom seeds BOTH theme colors with the current accent, so the two
            // pickers are immediately usable and light/dark can be tuned independently without
            // one of them silently falling back to the accent.
            var updated = mode == "Custom"
                ? Model with
                {
                    TodayColorMode = mode,
                    TodayColorLight = Model.TodayColorLight ?? ToHex(AccentColor()),
                    TodayColorDark = Model.TodayColorDark ?? ToHex(AccentColor(dark: true))
                }
                : Model with { TodayColorMode = mode };

            Save(updated);
            this.RaisePropertyChanged();
            this.RaisePropertyChanged(nameof(IsCustomTodayColor));
            this.RaisePropertyChanged(nameof(TodayColorLight));
            this.RaisePropertyChanged(nameof(TodayColorDark));
        }
    }

    /// <summary>
    /// Custom today color for the light theme (persisted as #RRGGBB). Falls back to
    /// the current accent when unset so the picker starts on something sensible.
    /// </summary>
    public Color TodayColorLight
    {
        get => ParseColor(Model.TodayColorLight) ?? AccentColor();
        set
        {
            var hex = ToHex(value);
            if (hex == Model.TodayColorLight) return;
            Save(Model with { TodayColorLight = hex });
            this.RaisePropertyChanged();
        }
    }

    /// <summary>Custom today color for the dark theme (persisted as #RRGGBB).</summary>
    public Color TodayColorDark
    {
        get => ParseColor(Model.TodayColorDark) ?? AccentColor(dark: true);
        set
        {
            var hex = ToHex(value);
            if (hex == Model.TodayColorDark) return;
            Save(Model with { TodayColorDark = hex });
            this.RaisePropertyChanged();
        }
    }

    private static Color? ParseColor(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) return null;
        try { return Color.Parse(hex); }
        catch (FormatException) { return null; }
    }

    private static string ToHex(Color color) =>
        $"#{color.R:X2}{color.G:X2}{color.B:X2}";

    private static Color AccentColor(bool dark = false)
    {
        if (Avalonia.Application.Current is { } app &&
            ((Avalonia.Controls.IResourceHost)app).TryGetResource(
                "SystemAccentColor",
                dark ? ThemeVariant.Dark : ThemeVariant.Light,
                out var value) &&
            value is Color color)
            return color;

        return Color.Parse("#0078D7");
    }

    private void Save(MonthCalendarModel newModel)
    {
        var newSettings = widgetLayoutProvider.Get() with
        {
            Settings = JsonSerializer.SerializeToElement(newModel)
        };
        widgetLayoutProvider.Save(newSettings);
    }
}
