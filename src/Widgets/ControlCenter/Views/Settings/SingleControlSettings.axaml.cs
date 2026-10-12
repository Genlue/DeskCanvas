using System;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using ControlCenter.Models;
using DeskCanvas.Core.Interfaces;
using DeskCanvas.Core.Models;

namespace ControlCenter.Views.Settings;

public partial class SingleControlSettings : UserControl
{
    private readonly IWidgetLayoutProvider? widgetLayoutProvider;
    private SingleControlModel model;
    private bool isInitializing = true;

    public SingleControlSettings() : this(null) { }

    public SingleControlSettings(IWidgetLayoutProvider? widgetLayoutProvider)
    {
        this.widgetLayoutProvider = widgetLayoutProvider;
        model = ReadModel(widgetLayoutProvider?.Get()) ?? new SingleControlModel();

        InitializeComponent();

        WheelStepCombo.SelectedIndex = model.WheelStep switch
        {
            1 => 0,
            2 => 1,
            5 => 2,
            10 => 3,
            _ => 1
        };

        isInitializing = false;
        RebuildItemCards();
    }

    private void RebuildItemCards()
    {
        ItemsGrid.Children.Clear();
        var accentBrush = GetBrush("SystemControlForegroundAccentBrush", Color.Parse("#007AFF"));
        var highFgBrush = GetBrush("SystemControlForegroundBaseHighBrush", Colors.White);

        foreach (var type in Enum.GetValues<ControlItemType>())
        {
            bool selected = model.ItemType == type;
            var card = new Border
            {
                CornerRadius = new CornerRadius(9),
                Background = new SolidColorBrush(selected ? Color.Parse("#2A007AFF") : Color.Parse("#14808080")),
                BorderBrush = selected ? accentBrush : new SolidColorBrush(Color.Parse("#28808080")),
                BorderThickness = new Thickness(selected ? 1.8 : 1),
                Padding = new Thickness(10, 9),
                Margin = new Thickness(4),
                Cursor = new Cursor(StandardCursorType.Hand)
            };

            var row = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 9
            };

            row.Children.Add(new PathIcon
            {
                Data = SafeParseGeometry(ControlCenterModel.GetItemIconPath(type, true)),
                Width = 18,
                Height = 18,
                Foreground = selected ? accentBrush : highFgBrush,
                VerticalAlignment = VerticalAlignment.Center
            });

            row.Children.Add(new TextBlock
            {
                Text = ControlCenterModel.GetItemDisplayName(type),
                FontSize = 12.5,
                FontWeight = selected ? FontWeight.SemiBold : FontWeight.Normal,
                Foreground = highFgBrush,
                VerticalAlignment = VerticalAlignment.Center
            });

            card.Child = row;
            card.PointerPressed += (_, e) =>
            {
                if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
                e.Handled = true;
                model.ItemType = type;
                RebuildItemCards();
                Save();
            };

            ItemsGrid.Children.Add(card);
        }
    }

    private void OnWheelStepChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (isInitializing) return;
        if (WheelStepCombo.SelectedItem is ComboBoxItem { Tag: string tag } && int.TryParse(tag, out int step))
        {
            model.WheelStep = step;
            Save();
        }
    }

    private void Save()
    {
        if (widgetLayoutProvider == null) return;
        var current = widgetLayoutProvider.Get();
        var element = JsonSerializer.SerializeToElement(model);
        widgetLayoutProvider.Save(current with { Settings = element });
    }

    private static SingleControlModel? ReadModel(WidgetLayout? layout)
    {
        if (layout?.Settings is not { ValueKind: JsonValueKind.Object } settings)
            return null;
        try
        {
            return settings.Deserialize<SingleControlModel>();
        }
        catch
        {
            return null;
        }
    }

    private IBrush GetBrush(string key, Color fallback)
    {
        if (this.TryFindResource(key, ActualThemeVariant, out var res) && res is IBrush b)
            return b;
        if (Application.Current?.TryFindResource(key, ActualThemeVariant, out var appRes) == true && appRes is IBrush ab)
            return ab;
        return new SolidColorBrush(fallback);
    }

    private static StreamGeometry? SafeParseGeometry(string path)
    {
        try { return StreamGeometry.Parse(path); }
        catch { return null; }
    }
}
