using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using DeskCanvas.Core.Interfaces;
using DeskCanvas.Core.Models;
using Storage.Locales;
using Storage.Models;
using Storage.Services;

namespace Storage.Views.Settings;

public partial class StorageSettings : UserControl
{
    private readonly IWidgetLayoutProvider? widgetLayoutProvider;
    private StorageModel model;
    private bool isInitializing = true;

    private record OptionItem<T>(string Label, T Value)
    {
        public override string ToString() => Label;
    }

    private readonly OptionItem<StorageDisplayMode>[] displayModeOptions;
    private readonly OptionItem<StorageTextFormat>[] textFormatOptions;

    public StorageSettings() : this(null) { }

    public StorageSettings(IWidgetLayoutProvider? widgetLayoutProvider)
    {
        this.widgetLayoutProvider = widgetLayoutProvider;
        model = widgetLayoutProvider != null
            ? (ReadModel(widgetLayoutProvider.Get()) ?? new StorageModel())
            : new StorageModel();

        displayModeOptions =
        [
            new(Locale.Storage_Mode_ProgressBarList, StorageDisplayMode.ProgressBarList),
            new(Locale.Storage_Mode_RingDashboard, StorageDisplayMode.RingDashboard)
        ];

        textFormatOptions =
        [
            new(Locale.Storage_Format_FreeSpace, StorageTextFormat.FreeSpace),
            new(Locale.Storage_Format_UsedAndTotal, StorageTextFormat.UsedAndTotal),
            new(Locale.Storage_Format_Percentage, StorageTextFormat.Percentage)
        ];

        InitializeComponent();

        DisplayModeCombo.ItemsSource = displayModeOptions;
        TextFormatCombo.ItemsSource = textFormatOptions;

        LoadFromModel();
        RefreshDrivesList();
        isInitializing = false;
    }

    private void LoadFromModel()
    {
        DisplayModeCombo.SelectedItem = Array.Find(displayModeOptions, x => x.Value == model.DisplayMode) ?? displayModeOptions[0];
        TextFormatCombo.SelectedItem = Array.Find(textFormatOptions, x => x.Value == model.TextFormat) ?? textFormatOptions[0];
        ShowHeaderSwitch.IsChecked = model.ShowHeader;
        UseWarningColorsSwitch.IsChecked = model.UseWarningColors;
        DoubleClickToOpenSwitch.IsChecked = model.DoubleClickToOpen;
        ShowRemovableSwitch.IsChecked = model.ShowRemovableDrives;
        ShowNetworkSwitch.IsChecked = model.ShowNetworkDrives;
    }

    private void OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        ApplyAndSaveOptions();
    }

    private void OnOptionChanged(object? sender, RoutedEventArgs e)
    {
        ApplyAndSaveOptions();
    }

    private void ApplyAndSaveOptions()
    {
        if (isInitializing) return;

        var selectedMode = (DisplayModeCombo.SelectedItem as OptionItem<StorageDisplayMode>)?.Value ?? StorageDisplayMode.ProgressBarList;
        var selectedFormat = (TextFormatCombo.SelectedItem as OptionItem<StorageTextFormat>)?.Value ?? StorageTextFormat.FreeSpace;

        model = model with
        {
            DisplayMode = selectedMode,
            TextFormat = selectedFormat,
            ShowHeader = ShowHeaderSwitch.IsChecked ?? true,
            UseWarningColors = UseWarningColorsSwitch.IsChecked ?? true,
            DoubleClickToOpen = DoubleClickToOpenSwitch.IsChecked ?? true,
            ShowRemovableDrives = ShowRemovableSwitch.IsChecked ?? true,
            ShowNetworkDrives = ShowNetworkSwitch.IsChecked ?? false
        };

        Save();
        RefreshDrivesList();
    }

    private void OnRefreshDrivesClicked(object? sender, RoutedEventArgs e)
    {
        RefreshDrivesList();
    }

    private void RefreshDrivesList()
    {
        DrivesPanel.Children.Clear();
        var allDrives = StorageService.GetAllCandidateDrives(includeRemovable: true, includeNetwork: true);

        if (allDrives.Count == 0)
        {
            var emptyText = new TextBlock
            {
                Text = Locale.Storage_NoDrives,
                FontSize = 12,
                Opacity = 0.6,
                Margin = new Thickness(4, 8)
            };
            emptyText.Bind(TextBlock.ForegroundProperty, new DynamicResourceExtension("SystemControlForegroundBaseHighBrush"));
            DrivesPanel.Children.Add(emptyText);
            return;
        }

        var hiddenSet = model.HiddenDrives != null
            ? new HashSet<string>(model.HiddenDrives.Select(StorageService.NormalizeDriveKey), StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var d in allDrives)
        {
            DrivesPanel.Children.Add(CreateDriveCard(d, hiddenSet));
        }
    }

    private Control CreateDriveCard(StorageService.DriveStorageInfo d, HashSet<string> hiddenSet)
    {
        var normKey = StorageService.NormalizeDriveKey(d.DriveLetter);
        bool isTypeEnabled = d.DriveType switch
        {
            DriveType.Removable => model.ShowRemovableDrives,
            DriveType.Network => model.ShowNetworkDrives,
            _ => true
        };
        bool isVisibleInWidget = isTypeEnabled && !hiddenSet.Contains(normKey);

        var border = new Border
        {
            CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(12, 10),
            Opacity = isVisibleInWidget ? 1.0 : 0.6
        };
        border.Bind(Border.BorderBrushProperty, new DynamicResourceExtension("ToolTipBorderBrush"));
        border.Bind(Border.BackgroundProperty, new DynamicResourceExtension("SystemControlBackgroundAltHighBrush"));

        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto,Auto")
        };

        // 0. CheckBox for toggling visibility
        var checkBox = new CheckBox
        {
            IsChecked = isVisibleInWidget,
            IsEnabled = isTypeEnabled,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0),
            MinWidth = 24
        };
        ToolTip.SetTip(checkBox, isTypeEnabled
            ? Locale.Storage_ShowDriveTooltip
            : Locale.Storage_EnableTypeFirstTooltip);
        checkBox.IsCheckedChanged += (_, _) =>
        {
            if (isInitializing) return;
            SetDriveHidden(normKey, !(checkBox.IsChecked ?? true));
        };
        Grid.SetColumn(checkBox, 0);

        // 1. Info Stack
        var stack = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0)
        };

        var typeTag = d.IsSystemDrive
            ? Locale.Storage_DriveTypeSystem
            : d.DriveType switch
            {
                DriveType.Removable => Locale.Storage_DriveTypeRemovable,
                DriveType.Network => Locale.Storage_DriveTypeNetwork,
                _ => Locale.Storage_DriveTypeLocal
            };

        var titleBlock = new TextBlock
        {
            Text = $"{d.DisplayTitle}  ({d.DriveFormat} · {typeTag})",
            FontSize = 13,
            FontWeight = FontWeight.Medium,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        titleBlock.Bind(TextBlock.ForegroundProperty, new DynamicResourceExtension("SystemControlForegroundBaseHighBrush"));

        var subBlock = new TextBlock
        {
            Text = string.Format(
                Locale.Storage_UsedFreeFormat,
                StorageService.FormatBytes(d.UsedBytes),
                StorageService.FormatBytes(d.TotalBytes),
                StorageService.FormatBytes(d.FreeBytes)),
            FontSize = 11,
            Opacity = 0.7,
            Margin = new Thickness(0, 3, 0, 0)
        };
        subBlock.Bind(TextBlock.ForegroundProperty, new DynamicResourceExtension("SystemControlForegroundBaseHighBrush"));

        stack.Children.Add(titleBlock);
        stack.Children.Add(subBlock);
        Grid.SetColumn(stack, 1);

        // 2. Open in Explorer button
        var openBtn = new Button
        {
            Content = Locale.Storage_Open,
            Classes = { "dc-toolbar-button" },
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0),
            Padding = new Thickness(8, 3),
            FontSize = 11
        };
        openBtn.Click += (_, _) => StorageService.OpenDriveInExplorer(d.RootPath);
        Grid.SetColumn(openBtn, 2);

        // 3. Usage Percentage Badge
        // 业务数据色：按使用率映射的告警/健康色（红/橙/绿/蓝），属于业务语义而非通用 UI 灰，
        // 是 §12.4 允许的例外。
        var hex = d.UsagePercent >= 90
            ? "#FF3B30"
            : d.UsagePercent >= 75
                ? "#FF9500"
                : d.DriveType == DriveType.Removable
                    ? "#34C759"
                    : "#007AFF";
        var col = Color.Parse(hex);

        var badge = new Border
        {
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(8, 3),
            VerticalAlignment = VerticalAlignment.Center,
            Background = new SolidColorBrush(Color.FromArgb(32, col.R, col.G, col.B)),
            Child = new TextBlock
            {
                Text = $"{d.UsagePercent}%",
                FontSize = 12,
                FontWeight = FontWeight.SemiBold,
                Foreground = new SolidColorBrush(col)
            }
        };
        Grid.SetColumn(badge, 3);

        grid.Children.Add(checkBox);
        grid.Children.Add(stack);
        grid.Children.Add(openBtn);
        grid.Children.Add(badge);
        border.Child = grid;
        return border;
    }

    private void SetDriveHidden(string normalizedDriveKey, bool hidden)
    {
        var set = model.HiddenDrives != null
            ? new HashSet<string>(model.HiddenDrives.Select(StorageService.NormalizeDriveKey), StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (hidden)
            set.Add(normalizedDriveKey);
        else
            set.Remove(normalizedDriveKey);

        model = model with
        {
            HiddenDrives = set.Count > 0 ? set.ToList() : null
        };

        Save();
        RefreshDrivesList();
    }

    private void Save()
    {
        if (widgetLayoutProvider == null) return;
        try
        {
            var layout = widgetLayoutProvider.Get();
            if (layout == null) return;
            widgetLayoutProvider.Save(layout with
            {
                Settings = JsonSerializer.SerializeToElement(model)
            });
        }
        catch { }
    }

    private static StorageModel? ReadModel(WidgetLayout? layout)
    {
        if (layout?.Settings is not { } settings) return null;
        if (settings.ValueKind != JsonValueKind.Object) return null;
        try
        {
            return settings.Deserialize<StorageModel>();
        }
        catch
        {
            return null;
        }
    }
}
