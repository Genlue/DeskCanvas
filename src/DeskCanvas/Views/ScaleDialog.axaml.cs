using System;
using System.Globalization;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace DeskCanvas.Views;

/// <summary>
/// Free-form content scale input for a single widget: type any ratio
/// (clamped to 0.1× – 5×); "恢复默认" clears the widget's own value so it
/// follows the screen default (1.0× when the screen has none either).
/// </summary>
public partial class ScaleDialog : Window
{
    private readonly Action<double?> onApply;

    public ScaleDialog(double current, Action<double?> onApply)
    {
        this.onApply = onApply;
        InitializeComponent();
        ScaleInput.Text = current.ToString("0.##", CultureInfo.InvariantCulture);
        Opened += (_, _) =>
        {
            ScaleInput.Focus();
            ScaleInput.SelectAll();
        };
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) Close();
            if (e.Key == Key.Return) Apply(this, new RoutedEventArgs());
        };
    }

    private void Apply(object? sender, RoutedEventArgs e)
    {
        if (double.TryParse(ScaleInput.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            onApply(Math.Clamp(value, 0.1, 5));
        Close();
    }

    private void Reset(object? sender, RoutedEventArgs e)
    {
        onApply(null);
        Close();
    }

    // Drag by the blank areas only; buttons and the text box keep their clicks.
    private void Drag(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source is Button or TextBox) return;
        BeginMoveDrag(e);
    }
}
