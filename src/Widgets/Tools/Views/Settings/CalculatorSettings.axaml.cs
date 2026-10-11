using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using DeskCanvas.Core.Interfaces;
using DeskCanvas.Core.Models;
using Tools.Models;
using Tools.Services;

namespace Tools.Views.Settings;

public partial class CalculatorSettings : UserControl
{
    private record IntOption(string Label, int Value)
    {
        public override string ToString() => Label;
    }

    private record StrOption(string Label, string Code)
    {
        public override string ToString() => Label;
    }

    private static readonly List<IntOption> DecimalOptions =
    [
        new("2 位小数", 2),
        new("4 位小数", 4),
        new("6 位小数 (默认)", 6),
        new("8 位小数", 8),
        new("10 位小数", 10),
    ];

    private static readonly List<StrOption> AngleOptions =
    [
        new("角度制 (DEG)", "DEG"),
        new("弧度制 (RAD)", "RAD"),
    ];

    private static readonly List<StrOption> TabOptions =
    [
        new("科学计算 (Scientific)", "Scientific"),
        new("程序员 (Programmer)", "Programmer"),
        new("单位换算 (Converter)", "Converter"),
    ];

    private readonly IWidgetLayoutProvider? widgetLayoutProvider;
    private CalculatorModel model;
    private bool isInitializing = true;

    public CalculatorSettings() : this(null) { }

    public CalculatorSettings(IWidgetLayoutProvider? widgetLayoutProvider)
    {
        this.widgetLayoutProvider = widgetLayoutProvider;
        model = widgetLayoutProvider != null ? (ReadModel(widgetLayoutProvider.Get()) ?? new CalculatorModel()) : new CalculatorModel();

        InitializeComponent();

        DecimalPlacesCombo.ItemsSource = DecimalOptions;
        DecimalPlacesCombo.SelectedItem = DecimalOptions.FirstOrDefault(o => o.Value == model.DecimalPlaces) ?? DecimalOptions[2];

        AngleModeCombo.ItemsSource = AngleOptions;
        AngleModeCombo.SelectedItem = AngleOptions.FirstOrDefault(o => o.Code.Equals(model.DefaultAngleMode, StringComparison.OrdinalIgnoreCase)) ?? AngleOptions[0];

        DefaultTabCombo.ItemsSource = TabOptions;
        DefaultTabCombo.SelectedItem = TabOptions.FirstOrDefault(o => o.Code.Equals(model.DefaultPanelTab, StringComparison.OrdinalIgnoreCase)) ?? TabOptions[0];

        ThousandsSepToggle.IsChecked = model.UseThousandsSeparator;
        ClickToCopyToggle.IsChecked = model.ClickHistoryToCopy;

        MaxHistorySlider.Value = Math.Clamp(model.MaxHistoryItems, 10, 100);
        MaxHistoryValueText.Text = $"{model.MaxHistoryItems}";

        isInitializing = false;
    }

    private void OnDecimalPlacesChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (isInitializing) return;
        if (DecimalPlacesCombo.SelectedItem is IntOption opt)
        {
            model = model with { DecimalPlaces = opt.Value };
            SaveAndApply();
        }
    }

    private void OnAngleModeChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (isInitializing) return;
        if (AngleModeCombo.SelectedItem is StrOption opt)
        {
            model = model with { DefaultAngleMode = opt.Code };
            SaveAndApply();
        }
    }

    private void OnDefaultTabChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (isInitializing) return;
        if (DefaultTabCombo.SelectedItem is StrOption opt)
        {
            model = model with { DefaultPanelTab = opt.Code };
            SaveAndApply();
        }
    }

    private void OnMaxHistoryChanged(object? sender, RangeBaseValueChangedEventArgs e)
    {
        if (isInitializing) return;
        int val = (int)Math.Round(e.NewValue);
        MaxHistoryValueText.Text = $"{val}";
        model = model with { MaxHistoryItems = val };
        SaveAndApply();
    }

    private void OnToggleChanged(object? sender, RoutedEventArgs e)
    {
        if (isInitializing) return;
        model = model with
        {
            UseThousandsSeparator = ThousandsSepToggle.IsChecked ?? true,
            ClickHistoryToCopy = ClickToCopyToggle.IsChecked ?? false,
        };
        SaveAndApply();
    }

    private void OnClearAllClicked(object? sender, RoutedEventArgs e)
    {
        CalculatorStore.Instance.ClearHistory();
        CalculatorStore.Instance.MemoryClear();
    }

    private static CalculatorModel? ReadModel(WidgetLayout layout)
    {
        if (layout.Settings is not { ValueKind: JsonValueKind.Object } settings) return null;
        try
        {
            return settings.Deserialize<CalculatorModel>();
        }
        catch
        {
            return null;
        }
    }

    private void SaveAndApply()
    {
        CalculatorStore.Instance.Configure(model);
        if (widgetLayoutProvider == null) return;
        widgetLayoutProvider.Save(widgetLayoutProvider.Get() with
        {
            Settings = JsonSerializer.SerializeToElement(model)
        });
    }
}
