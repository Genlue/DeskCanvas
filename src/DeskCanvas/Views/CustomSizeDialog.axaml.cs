using System;
using System.Globalization;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace DeskCanvas.Views;

/// <summary>
/// The two-number dialog used for a widget's size. The desktop free mode uses it for a pixel size;
/// the sidebar grid uses it for a cell span ("列 × 行"), which is why the wording, the ranges and
/// the reset pair are all parameters rather than literals.
/// </summary>
public partial class CustomSizeDialog : Window
{
    private readonly Action<int, int> onApply;
    private readonly int minFirst;
    private readonly int maxFirst;
    private readonly int minSecond;
    private readonly int maxSecond;
    private readonly int resetFirst;
    private readonly int resetSecond;

    public CustomSizeDialog(int currentWidth, int currentHeight, Action<int, int> onApply,
        string? title = null,
        string? firstLabel = null,
        string? secondLabel = null,
        int minFirst = 48,
        int maxFirst = 3840,
        int minSecond = 48,
        int maxSecond = 2160,
        int resetFirst = 160,
        int resetSecond = 160)
    {
        this.onApply = onApply;
        this.minFirst = minFirst;
        this.maxFirst = maxFirst;
        this.minSecond = minSecond;
        this.maxSecond = maxSecond;
        this.resetFirst = resetFirst;
        this.resetSecond = resetSecond;

        InitializeComponent();

        if (title != null) TitleText.Text = title;
        if (firstLabel != null) FirstLabel.Text = firstLabel;
        if (secondLabel != null) SecondLabel.Text = secondLabel;
        ResetButton.Content = $"默认 ({resetFirst}×{resetSecond})";

        WidthInput.Text = currentWidth.ToString(CultureInfo.InvariantCulture);
        HeightInput.Text = currentHeight.ToString(CultureInfo.InvariantCulture);
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
        if (int.TryParse(WidthInput.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var first)
            && int.TryParse(HeightInput.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var second))
        {
            onApply(Math.Clamp(first, minFirst, maxFirst), Math.Clamp(second, minSecond, maxSecond));
        }
        Close();
    }

    private void Reset(object? sender, RoutedEventArgs e)
    {
        onApply(resetFirst, resetSecond);
        Close();
    }

    private void Drag(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source is Button or TextBox) return;
        BeginMoveDrag(e);
    }
}
