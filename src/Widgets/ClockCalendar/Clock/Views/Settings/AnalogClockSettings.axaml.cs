using Avalonia.Controls;
using Avalonia.Media;
using Clock.ViewModels;
using DeskCanvas.Core.Interfaces;

namespace Clock.Views.Settings;

public partial class AnalogClockSettings : UserControl
{
    public AnalogClockSettings(IWidgetLayoutProvider widgetLayoutProvider)
    {
        var viewModel = new AnalogClockSettingsViewModel(widgetLayoutProvider);
        DataContext = viewModel;
        InitializeComponent();

        // The widget model stores the two dial colors as "#RRGGBB" strings while the view model
        // exposes them as Color; the shared ColorInput edits strings, so the pair is mirrored
        // here instead of widening the view model's public surface.
        LoadDialColors(viewModel);
        DialColorDarkInput.ValueCommitted += (_, e) =>
            ApplyDialColor(viewModel, dark: true, e.Value);
        DialColorLightInput.ValueCommitted += (_, e) =>
            ApplyDialColor(viewModel, dark: false, e.Value);
    }

    private void LoadDialColors(AnalogClockSettingsViewModel viewModel)
    {
        DialColorDarkInput.Value = ToHex(viewModel.DialColorDark);
        DialColorLightInput.Value = ToHex(viewModel.DialColorLight);
    }

    private static void ApplyDialColor(AnalogClockSettingsViewModel viewModel, bool dark, string? hex)
    {
        if (hex is null || !Color.TryParse(hex, out var color)) return;

        if (dark) viewModel.DialColorDark = color;
        else viewModel.DialColorLight = color;
    }

    private static string ToHex(Color color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";
}
