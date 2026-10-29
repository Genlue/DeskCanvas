using DeskCanvas.Services;
using System;
using System.Globalization;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace DeskCanvas.Views;

/// <summary>
/// Free-form content scale input for a single widget: type any ratio
/// (clamped to 0.1× – 5×); "恢复默认" clears the widget's own value so it
/// follows the screen default (1.0× when the screen has none either).
/// <para>
/// The window chrome (transparent frame, card, draggable header, Escape) comes from
/// <see cref="HostDialogWindow"/> + the shared <see cref="Controls.DialogShell"/>; this class keeps
/// only the two actions and the Enter shortcut. The window used to declare its own acrylic card and
/// its own drag handler on the content panel — the acrylic was the ring around the dialog, and the
/// press never reached the handler because the extended caption area owned it.
/// </para>
/// </summary>
public partial class ScaleDialog : HostDialogWindow
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
        // Enter applies. Escape is the base class's contract (one rule for every dialog).
        KeyDown += (_, e) =>
        {
            if (e.Key != Key.Return || e.Handled) return;
            e.Handled = true;
            Apply(this, new RoutedEventArgs());
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
}
