using System;
using System.Globalization;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Calendar.ViewModels;
using DeskCanvas.Core.Interfaces;

namespace Calendar.Views.Settings;

public partial class MonthCalendarSettings : UserControl
{
    public MonthCalendarSettings(IWidgetLayoutProvider widgetLayoutProvider)
    {
        DataContext = new MonthCalendarSettingsViewModel(widgetLayoutProvider);
        InitializeComponent();
    }
}

/// <summary>
/// Adapts the view model's <see cref="Color"/> today colours to the persisted "#RRGGBB" string
/// the shared <c>ColorInput</c> edits (W9). Keeping the conversion here means the compound
/// control stays unaware of the calendar model and the model keeps storing exactly the format
/// it always stored.
/// </summary>
public sealed class ColorHexConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is Color color ? ToHex(color) : null;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string text || string.IsNullOrWhiteSpace(text)) return null;
        try { return Color.Parse(text); }
        catch (FormatException) { return null; }
    }

    private static string ToHex(Color color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";
}
