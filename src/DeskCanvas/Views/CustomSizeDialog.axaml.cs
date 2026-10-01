using System;
using System.Globalization;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace DeskCanvas.Views;

public partial class CustomSizeDialog : Window
{
    private readonly Action<int, int> onApply;

    public CustomSizeDialog(int currentWidth, int currentHeight, Action<int, int> onApply)
    {
        this.onApply = onApply;
        InitializeComponent();
        WidthInput.Text = currentWidth.ToString();
        HeightInput.Text = currentHeight.ToString();
        Opened += (_, _) =>
        {
            WidthInput.Focus();
            WidthInput.SelectAll();
        };
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) Close();
            if (e.Key == Key.Return) Apply(this, new RoutedEventArgs());
        };
    }

    private void Apply(object? sender, RoutedEventArgs e)
    {
        if (int.TryParse(WidthInput.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var w)
            && int.TryParse(HeightInput.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var h))
        {
            onApply(Math.Clamp(w, 48, 3840), Math.Clamp(h, 48, 2160));
        }
        Close();
    }

    private void Reset(object? sender, RoutedEventArgs e)
    {
        onApply(160, 160);
        Close();
    }

    private void Drag(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source is Button or TextBox) return;
        BeginMoveDrag(e);
    }
}
