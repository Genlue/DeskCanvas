using System;
using System.Globalization;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Batteries.Locales;
using Batteries.Models;
using Batteries.Services;
using DeskCanvas.Core.Interfaces;
using DeskCanvas.Core.Models;

namespace Batteries.Views.Settings;

public partial class BatteriesSettings : UserControl
{
    private readonly IWidgetLayoutProvider widgetLayoutProvider;
    private BatteriesModel model;
    private bool isInitializing = true;

    private static bool IsZh =>
        CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.Equals("zh", StringComparison.OrdinalIgnoreCase);

    public BatteriesSettings() : this(null!) { }

    public BatteriesSettings(IWidgetLayoutProvider? widgetLayoutProvider)
    {
        this.widgetLayoutProvider = widgetLayoutProvider!;
        model = widgetLayoutProvider != null ? (ReadModel(widgetLayoutProvider.Get()) ?? new BatteriesModel()) : new BatteriesModel();

        InitializeComponent();
        LoadFromModel();
        RefreshDevices();
        isInitializing = false;
    }

    private void LoadFromModel()
    {
        ShowPeripheralsSwitch.IsChecked = model.ShowPeripherals;
        ShowPercentageSwitch.IsChecked = model.ShowPercentage;
        ShowRemainingTimeSwitch.IsChecked = model.ShowRemainingTime;
    }

    private void OnToggleChanged(object? sender, RoutedEventArgs e)
    {
        if (isInitializing) return;

        model = model with
        {
            ShowPeripherals = ShowPeripheralsSwitch.IsChecked ?? true,
            ShowPercentage = ShowPercentageSwitch.IsChecked ?? true,
            ShowRemainingTime = ShowRemainingTimeSwitch.IsChecked ?? true
        };

        Save();
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

    private static BatteriesModel? ReadModel(WidgetLayout? layout)
    {
        if (layout?.Settings is not { } settings) return null;
        if (settings.ValueKind != JsonValueKind.Object) return null;
        try
        {
            return settings.Deserialize<BatteriesModel>();
        }
        catch
        {
            return null;
        }
    }

    private void OnRefreshDevicesClicked(object? sender, RoutedEventArgs e)
    {
        RefreshDevices();
    }

    private void RefreshDevices()
    {
        var power = PowerService.GetCurrentPowerInfo();

        if (!power.HasBattery)
        {
            MainDeviceTitle.Text = Locale.Batteries_AcPower;
            MainDeviceSubtitle.Text = power.IsAcConnected
                ? (IsZh ? "已连接交流电源 (无内置电池)" : "Connected to AC power (no internal battery)")
                : (IsZh ? "未检测到系统电池" : "No system battery detected");
            MainDevicePercent.Text = "100%";
            MainDeviceBadge.Background = new SolidColorBrush(Color.FromArgb(32, 0, 122, 255));
            MainDevicePercent.Foreground = new SolidColorBrush(Color.Parse("#007AFF"));
        }
        else
        {
            MainDeviceTitle.Text = IsZh ? "电脑内置电池" : Locale.Batteries_MainDevice;
            var status = power.IsCharging
                ? (IsZh ? "正在充电 ⚡" : "Charging ⚡")
                : (power.IsAcConnected
                    ? (IsZh ? "已连接电源 (未充电)" : "Plugged in (not charging)")
                    : (IsZh ? "使用电池" : "On battery"));
            var time = power.RemainingMinutes > 0
                ? (IsZh
                    ? $" · 预计可用 {power.RemainingMinutes / 60}小时{power.RemainingMinutes % 60}分"
                    : $" · ~{power.RemainingMinutes / 60}h {power.RemainingMinutes % 60}m remaining")
                : "";
            MainDeviceSubtitle.Text = $"{status}{time}";
            MainDevicePercent.Text = $"{power.Percentage}%";

            var hex = power.Percentage > 20 ? "#34C759" : (power.Percentage > 10 ? "#FF9500" : "#FF3B30");
            var col = Color.Parse(hex);
            MainDeviceBadge.Background = new SolidColorBrush(Color.FromArgb(32, col.R, col.G, col.B));
            MainDevicePercent.Foreground = new SolidColorBrush(col);
        }

        PeripheralsPanel.Children.Clear();
        var peripherals = PowerService.GetConnectedPeripherals(model.DeviceTypeOverrides);
        if (peripherals.Count == 0)
        {
            NoPeripheralsBorder.IsVisible = true;
        }
        else
        {
            NoPeripheralsBorder.IsVisible = false;
            foreach (var p in peripherals)
            {
                PeripheralsPanel.Children.Add(CreatePeripheralCard(p));
            }
        }

        PairedDevicesPanel.Children.Clear();
        var pairedDevices = PowerService.GetDiscoveredBluetoothDevices(model.DeviceTypeOverrides);
        if (pairedDevices.Count == 0)
        {
            var emptyLabel = new TextBlock
            {
                Text = IsZh ? "未扫描到其他已配对的蓝牙设备" : "No other paired Bluetooth devices found",
                FontSize = 12,
                Margin = new Thickness(4, 6, 0, 6)
            };
            emptyLabel.Bind(TextBlock.ForegroundProperty, new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("SecondaryTextBrush"));
            PairedDevicesPanel.Children.Add(emptyLabel);
        }
        else
        {
            foreach (var d in pairedDevices)
            {
                PairedDevicesPanel.Children.Add(CreatePairedDeviceCard(d));
            }
        }
    }

    private static string GetDeviceIconData(DeviceKind kind) => kind switch
    {
        DeviceKind.Computer => "M20 18c1.1 0 1.99-.9 1.99-2L22 6c0-1.1-.9-2-2-2H4c-1.1 0-2 .9-2 2v10c0 1.1.9 2 2 2H0v2h24v-2h-4zM4 6h16v10H4V6z",
        DeviceKind.Mouse => "M13 1.07V9h7c0-4.08-3.05-7.44-7-7.93zM4 15c0 4.42 3.58 8 8 8s8-3.58 8-8v-4H4v4zm7-13.93C7.05 1.56 4 4.92 4 9h7V1.07z",
        DeviceKind.Keyboard => "M20 5H4c-1.1 0-1.99.9-1.99 2L2 17c0 1.1.9 2 2 2h16c1.1 0 2-.9 2-2V7c0-1.1-.9-2-2-2zm-9 3h2v2h-2V8zm0 3h2v2h-2v-2zM8 8h2v2H8V8zm0 3h2v2H8v-2zm-1 2H5v-2h2v2zm0-3H5V8h2v2zm9 7H8v-2h8v2zm0-4h-2v-2h2v2zm0-3h-2V8h2v2zm3 3h-2v-2h2v2zm0-3h-2V8h2v2z",
        DeviceKind.Headphones => "M12 3a9 9 0 0 0-9 9v7c0 1.66 1.34 3 3 3h1a2 2 0 0 0 2-2v-4a2 2 0 0 0-2-2H5v-2a7 7 0 0 1 14 0v2h-2a2 2 0 0 0-2 2v4a2 2 0 0 0 2 2h1c1.66 0 3-1.34 3-3v-7a9 9 0 0 0-9-9z",
        DeviceKind.Phone => "M17 1.01L7 1c-1.1 0-2 .9-2 2v18c0 1.1.9 2 2 2h10c1.1 0 2-.9 2-2V3c0-1.1-.9-1.99-2-1.99zM17 19H7V5h10v14z",
        DeviceKind.Gamepad => "M21 6H3c-1.1 0-2 .9-2 2v8c0 1.1.9 2 2 2h18c1.1 0 2-.9 2-2V8c0-1.1-.9-2-2-2zm-10 7H8v3H6v-3H3v-2h3V8h2v3h3v2zm4.5 2c-.83 0-1.5-.67-1.5-1.5s.67-1.5 1.5-1.5 1.5.67 1.5 1.5-.67 1.5-1.5 1.5zm4-3c-.83 0-1.5-.67-1.5-1.5S18.67 9 19.5 9s1.5.67 1.5 1.5-.67 1.5-1.5 1.5z",
        _ => "M15.67 4H14V2h-4v2H8.33C7.6 4 7 4.6 7 5.33v15.33C7 21.4 7.6 22 8.33 22h7.33c.74 0 1.34-.6 1.34-1.33V5.33C17 4.6 16.4 4 15.67 4z"
    };

    private static string GetDeviceKindName(DeviceKind kind) => kind switch
    {
        DeviceKind.Mouse => IsZh ? "鼠标" : "Mouse",
        DeviceKind.Gamepad => IsZh ? "游戏手柄" : "Controller",
        DeviceKind.Headphones => IsZh ? "耳机" : "Headphones",
        DeviceKind.Keyboard => IsZh ? "键盘" : "Keyboard",
        DeviceKind.Phone => IsZh ? "手机" : "Phone",
        DeviceKind.Computer => IsZh ? "电脑" : "PC",
        _ => IsZh ? "其他外设" : "Other"
    };

    private record DeviceKindOption(string Display, DeviceKind Kind)
    {
        public override string ToString() => Display;
    }

    private static DeviceKindOption[] GetKindOptions() =>
    [
        new(IsZh ? "🖱️ 鼠标" : "🖱️ Mouse", DeviceKind.Mouse),
        new(IsZh ? "🎮 游戏手柄" : "🎮 Controller", DeviceKind.Gamepad),
        new(IsZh ? "🎧 蓝牙耳机" : "🎧 Headphones", DeviceKind.Headphones),
        new(IsZh ? "⌨️ 蓝牙键盘" : "⌨️ Keyboard", DeviceKind.Keyboard),
        new(IsZh ? "📱 手机" : "📱 Phone", DeviceKind.Phone),
        new(IsZh ? "💻 电脑" : "💻 PC", DeviceKind.Computer),
        new(IsZh ? "🔌 其他外设" : "🔌 Other", DeviceKind.Other)
    ];

    private void SetDeviceTypeOverride(string name, DeviceKind kind)
    {
        var dict = model.DeviceTypeOverrides != null
            ? new System.Collections.Generic.Dictionary<string, DeviceKind>(model.DeviceTypeOverrides, StringComparer.OrdinalIgnoreCase)
            : new System.Collections.Generic.Dictionary<string, DeviceKind>(StringComparer.OrdinalIgnoreCase);

        dict[name] = kind;
        model = model with { DeviceTypeOverrides = dict };
        Save();
        RefreshDevices();
    }

    private void ResetDeviceTypeOverride(string name)
    {
        if (model.DeviceTypeOverrides == null) return;
        var dict = new System.Collections.Generic.Dictionary<string, DeviceKind>(model.DeviceTypeOverrides, StringComparer.OrdinalIgnoreCase);
        if (dict.Remove(name))
        {
            model = model with { DeviceTypeOverrides = dict };
            Save();
            RefreshDevices();
        }
    }

    private Control CreatePeripheralCard(PowerService.PeripheralInfo p)
    {
        var border = new Border
        {
            CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(12, 10)
        };
        border.Bind(Border.BorderBrushProperty, new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("ToolTipBorderBrush"));
        border.Bind(Border.BackgroundProperty, new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("SystemControlBackgroundAltHighBrush"));

        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto")
        };

        var icon = new PathIcon
        {
            Data = StreamGeometry.Parse(GetDeviceIconData(p.Kind)),
            Height = 24,
            Width = 24,
            Margin = new Thickness(0, 0, 12, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        icon.Bind(PathIcon.ForegroundProperty, new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("SystemControlForegroundBaseHighBrush"));
        Grid.SetColumn(icon, 0);

        var stack = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0)
        };

        var titleBlock = new TextBlock
        {
            Text = p.Name,
            FontSize = 13,
            FontWeight = FontWeight.Medium,
            // No fixed height here: a long device name wraps instead of being ellipsed away.
            TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.None
        };
        titleBlock.Bind(TextBlock.ForegroundProperty, new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("SystemControlForegroundBaseHighBrush"));

        bool isOverridden = model.DeviceTypeOverrides != null && model.DeviceTypeOverrides.ContainsKey(p.Name);
        var connectedText = IsZh ? "已连接" : "Connected";
        var customText = IsZh ? " (已自定义)" : " (Custom)";
        var subText = isOverridden ? $"{connectedText} · {GetDeviceKindName(p.Kind)}{customText}" : $"{connectedText} · {GetDeviceKindName(p.Kind)}";
        var subBlock = new TextBlock
        {
            Text = subText,
            FontSize = 11,
            Opacity = 0.7,
            Margin = new Thickness(0, 2, 0, 0)
        };
        subBlock.Bind(TextBlock.ForegroundProperty, new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("SystemControlForegroundBaseHighBrush"));

        stack.Children.Add(titleBlock);
        stack.Children.Add(subBlock);
        Grid.SetColumn(stack, 1);

        // Type ComboBox selector
        var kindOptions = GetKindOptions();
        var combo = new ComboBox
        {
            ItemsSource = kindOptions,
            SelectedItem = System.Array.Find(kindOptions, o => o.Kind == p.Kind) ?? kindOptions[0],
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0),
            FontSize = 11,
            Height = 28
        };
        combo.SelectionChanged += (_, _) =>
        {
            if (isInitializing) return;
            if (combo.SelectedItem is DeviceKindOption opt && opt.Kind != p.Kind)
            {
                SetDeviceTypeOverride(p.Name, opt.Kind);
            }
        };
        Grid.SetColumn(combo, 2);

        // Percentage Badge (the ring / text colours are battery-level data colours)
        var badge = new Border
        {
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(8, 3),
            VerticalAlignment = VerticalAlignment.Center
        };
        var hex = p.Percentage > 20 ? "#34C759" : (p.Percentage > 10 ? "#FF9500" : "#FF3B30");
        var col = Color.Parse(hex);
        badge.Background = new SolidColorBrush(Color.FromArgb(32, col.R, col.G, col.B));

        var percentBlock = new TextBlock
        {
            Text = $"{p.Percentage}%",
            FontSize = 12,
            FontWeight = FontWeight.SemiBold,
            Foreground = new SolidColorBrush(col)
        };
        badge.Child = percentBlock;
        Grid.SetColumn(badge, 3);

        grid.Children.Add(icon);
        grid.Children.Add(stack);
        grid.Children.Add(combo);
        grid.Children.Add(badge);
        border.Child = grid;

        return border;
    }

    private Control CreatePairedDeviceCard(PowerService.BluetoothDeviceInfo d)
    {
        var border = new Border
        {
            CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(12, 8)
        };
        border.Bind(Border.BorderBrushProperty, new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("ToolTipBorderBrush"));
        border.Bind(Border.BackgroundProperty, new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("SystemControlBackgroundAltHighBrush"));

        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto")
        };

        var icon = new PathIcon
        {
            Data = StreamGeometry.Parse(GetDeviceIconData(d.Kind)),
            Height = 20,
            Width = 20,
            Margin = new Thickness(0, 0, 12, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Opacity = d.IsConnected ? 1.0 : 0.6
        };
        icon.Bind(PathIcon.ForegroundProperty, new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("SystemControlForegroundBaseHighBrush"));
        Grid.SetColumn(icon, 0);

        var stack = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0)
        };

        var titleBlock = new TextBlock
        {
            Text = d.CleanName,
            FontSize = 12,
            FontWeight = FontWeight.Medium,
            TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.None
        };
        titleBlock.Bind(TextBlock.ForegroundProperty, new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("SystemControlForegroundBaseHighBrush"));

        bool isOverridden = model.DeviceTypeOverrides != null &&
            (model.DeviceTypeOverrides.ContainsKey(d.CleanName) || model.DeviceTypeOverrides.ContainsKey(d.Name));

        var statusText = d.IsConnected
            ? (IsZh ? "● 已连接" : "● Connected")
            : (IsZh ? "○ 未连接" : "○ Disconnected");
        if (d.BatteryPercentage != null) statusText += $" · {d.BatteryPercentage}%";
        if (isOverridden) statusText += IsZh ? " (已自定义)" : " (Custom)";

        var subBlock = new TextBlock
        {
            Text = statusText,
            FontSize = 11,
            Opacity = d.IsConnected ? 0.8 : 0.45,
            Margin = new Thickness(0, 2, 0, 0)
        };
        subBlock.Bind(TextBlock.ForegroundProperty, new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("SystemControlForegroundBaseHighBrush"));

        stack.Children.Add(titleBlock);
        stack.Children.Add(subBlock);
        Grid.SetColumn(stack, 1);

        // Type ComboBox selector
        var kindOptions = GetKindOptions();
        var combo = new ComboBox
        {
            ItemsSource = kindOptions,
            SelectedItem = System.Array.Find(kindOptions, o => o.Kind == d.Kind) ?? kindOptions[0],
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 11,
            Height = 26,
            Margin = new Thickness(0, 0, 6, 0)
        };
        combo.SelectionChanged += (_, _) =>
        {
            if (isInitializing) return;
            if (combo.SelectedItem is DeviceKindOption opt && opt.Kind != d.Kind)
            {
                SetDeviceTypeOverride(d.CleanName, opt.Kind);
            }
        };
        Grid.SetColumn(combo, 2);

        // Reset button (if custom override exists)
        if (isOverridden)
        {
            var resetBtn = new Button
            {
                Content = IsZh ? "重置" : "Reset",
                FontSize = 10,
                Padding = new Thickness(6, 2),
                VerticalAlignment = VerticalAlignment.Center
            };
            resetBtn.Click += (_, _) => ResetDeviceTypeOverride(d.CleanName);
            Grid.SetColumn(resetBtn, 3);
            grid.Children.Add(resetBtn);
        }

        grid.Children.Add(icon);
        grid.Children.Add(stack);
        grid.Children.Add(combo);
        border.Child = grid;

        return border;
    }
}
