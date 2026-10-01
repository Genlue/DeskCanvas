using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Batteries.Locales;
using Batteries.Models;
using Batteries.Services;
using DeskCanvas.Core.Interfaces;
using DeskCanvas.Core.Models.Settings;
using DeskCanvas.Core.Services;

namespace Batteries.ViewModels;

public class BatteryDeviceItem : INotifyPropertyChanged
{
    private readonly IAppSettingsProvider? appSettingsProvider;
    private string name = string.Empty;
    private int percentage;
    private bool isCharging;
    private bool hasDevice;
    private DeviceKind kind;

    public BatteryDeviceItem(string name, DeviceKind kind, int percentage, bool isCharging, bool hasDevice = true, IAppSettingsProvider? appSettingsProvider = null)
    {
        this.name = name;
        this.kind = kind;
        this.percentage = Math.Clamp(percentage, 0, 100);
        this.isCharging = isCharging;
        this.hasDevice = hasDevice;
        this.appSettingsProvider = appSettingsProvider;
    }

    public bool HasDevice
    {
        get => hasDevice;
        set
        {
            if (hasDevice != value)
            {
                hasDevice = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsEmpty));
                OnPropertyChanged(nameof(PercentText));
                OnPropertyChanged(nameof(ProgressFraction));
                OnPropertyChanged(nameof(StrokeDashOffset));
                OnPropertyChanged(nameof(StatusBrush));
                OnPropertyChanged(nameof(StatusText));
                OnPropertyChanged(nameof(TooltipText));
            }
        }
    }

    public bool IsEmpty => !hasDevice;

    public string Name
    {
        get => name;
        set { if (name != value) { name = value; OnPropertyChanged(); OnPropertyChanged(nameof(TooltipText)); } }
    }

    public DeviceKind Kind
    {
        get => kind;
        set { if (kind != value) { kind = value; OnPropertyChanged(); OnPropertyChanged(nameof(IconData)); } }
    }

    public int Percentage
    {
        get => percentage;
        set
        {
            var clamped = Math.Clamp(value, 0, 100);
            if (percentage != clamped)
            {
                percentage = clamped;
                OnPropertyChanged();
                OnPropertyChanged(nameof(PercentText));
                OnPropertyChanged(nameof(ProgressFraction));
                OnPropertyChanged(nameof(StrokeDashOffset));
                OnPropertyChanged(nameof(StatusBrush));
                OnPropertyChanged(nameof(StatusText));
                OnPropertyChanged(nameof(TooltipText));
            }
        }
    }

    public bool IsCharging
    {
        get => isCharging;
        set
        {
            if (isCharging != value)
            {
                isCharging = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(StatusBrush));
                OnPropertyChanged(nameof(StatusText));
                OnPropertyChanged(nameof(TooltipText));
            }
        }
    }

    public string PercentText => hasDevice ? $"{Percentage}%" : string.Empty;

    public double ProgressFraction => hasDevice ? (Percentage / 100.0) : 0.0;

    // 半径 50、StrokeThickness 8 → 周长 2π×50 ≈ 314.159；StrokeDashArray 的单位是描边宽度，
    // 故整圈 ≈ 314.159/8 ≈ 39.27 单位（旧值 31.416 是按描边 10 标定的，改粗度后立刻失配）。
    private const double FullCircleDash = 39.27;

    public double StrokeDashOffset => FullCircleDash * (1.0 - ProgressFraction);

    public IBrush StatusBrush
    {
        get
        {
            if (!hasDevice) return Brushes.Transparent;
            var theme = appSettingsProvider?.Get()?.Theme;
            if (theme != null && !theme.IsColorful && theme.Monochrome)
            {
                if (theme.EffectiveMonochromeVariant == MonochromeStyle.BlackWhite)
                {
                    var isDark = Application.Current?.ActualThemeVariant == ThemeVariant.Dark;
                    return isDark ? Brushes.White : Brushes.Black;
                }
                else // Accent
                {
                    if (!string.IsNullOrWhiteSpace(theme.AccentColor) && Color.TryParse(theme.AccentColor, out var parsedAccent))
                        return new SolidColorBrush(parsedAccent);
                    if (Application.Current != null && Application.Current.TryGetResource("SystemControlForegroundAccentBrush", Application.Current.ActualThemeVariant, out var res) && res is IBrush ab)
                        return ab;
                    var isDark = Application.Current?.ActualThemeVariant == ThemeVariant.Dark;
                    return isDark ? new SolidColorBrush(Color.Parse("#70A5FF")) : new SolidColorBrush(Color.Parse("#0078D4"));
                }
            }
            if (IsCharging) return new SolidColorBrush(Color.Parse("#34C759"));
            if (Percentage > 20) return new SolidColorBrush(Color.Parse("#34C759"));
            if (Percentage > 10) return new SolidColorBrush(Color.Parse("#FF9500"));
            return new SolidColorBrush(Color.Parse("#FF3B30"));
        }
    }

    public IBrush ChargingBadgeForeground
    {
        get
        {
            var theme = appSettingsProvider?.Get()?.Theme;
            if (theme != null && !theme.IsColorful && theme.Monochrome && theme.EffectiveMonochromeVariant == MonochromeStyle.BlackWhite)
            {
                var isDark = Application.Current?.ActualThemeVariant == ThemeVariant.Dark;
                return isDark ? Brushes.Black : Brushes.White;
            }
            return Brushes.White;
        }
    }

    public void NotifyStatusBrushChanged()
    {
        OnPropertyChanged(nameof(StatusBrush));
        OnPropertyChanged(nameof(ChargingBadgeForeground));
    }

    public string StatusText => !hasDevice ? string.Empty : (IsCharging ? "⚡ 充电中" : $"{Percentage}%");

    public string TooltipText => !hasDevice ? string.Empty : $"{Name}: {(IsCharging ? "充电中 " : "")}{Percentage}%";

    public string IconData => Kind switch
    {
        DeviceKind.Computer => "M20 18c1.1 0 1.99-.9 1.99-2L22 6c0-1.1-.9-2-2-2H4c-1.1 0-2 .9-2 2v10c0 1.1.9 2 2 2H0v2h24v-2h-4zM4 6h16v10H4V6z",
        DeviceKind.Mouse => "M13 1.07V9h7c0-4.08-3.05-7.44-7-7.93zM4 15c0 4.42 3.58 8 8 8s8-3.58 8-8v-4H4v4zm7-13.93C7.05 1.56 4 4.92 4 9h7V1.07z",
        DeviceKind.Keyboard => "M20 5H4c-1.1 0-1.99.9-1.99 2L2 17c0 1.1.9 2 2 2h16c1.1 0 2-.9 2-2V7c0-1.1-.9-2-2-2zm-9 3h2v2h-2V8zm0 3h2v2h-2v-2zM8 8h2v2H8V8zm0 3h2v2H8v-2zm-1 2H5v-2h2v2zm0-3H5V8h2v2zm9 7H8v-2h8v2zm0-4h-2v-2h2v2zm0-3h-2V8h2v2zm3 3h-2v-2h2v2zm0-3h-2V8h2v2z",
        DeviceKind.Headphones => "M12 3a9 9 0 0 0-9 9v7c0 1.66 1.34 3 3 3h1a2 2 0 0 0 2-2v-4a2 2 0 0 0-2-2H5v-2a7 7 0 0 1 14 0v2h-2a2 2 0 0 0-2 2v4a2 2 0 0 0 2 2h1c1.66 0 3-1.34 3-3v-7a9 9 0 0 0-9-9z",
        DeviceKind.Phone => "M17 1.01L7 1c-1.1 0-2 .9-2 2v18c0 1.1.9 2 2 2h10c1.1 0 2-.9 2-2V3c0-1.1-.9-1.99-2-1.99zM17 19H7V5h10v14z",
        DeviceKind.Gamepad => "M21 6H3c-1.1 0-2 .9-2 2v8c0 1.1.9 2 2 2h18c1.1 0 2-.9 2-2V8c0-1.1-.9-2-2-2zm-10 7H8v3H6v-3H3v-2h3V8h2v3h3v2zm4.5 2c-.83 0-1.5-.67-1.5-1.5s.67-1.5 1.5-1.5 1.5.67 1.5 1.5-.67 1.5-1.5 1.5zm4-3c-.83 0-1.5-.67-1.5-1.5S18.67 9 19.5 9s1.5.67 1.5 1.5-.67 1.5-1.5 1.5z",
        DeviceKind.Other => "M15.67 4H14V2h-4v2H8.33C7.6 4 7 4.6 7 5.33v15.33C7 21.4 7.6 22 8.33 22h7.33c.74 0 1.34-.6 1.34-1.33V5.33C17 4.6 16.4 4 15.67 4z",
        _ => "M15.67 4H14V2h-4v2H8.33C7.6 4 7 4.6 7 5.33v15.33C7 21.4 7.6 22 8.33 22h7.33c.74 0 1.34-.6 1.34-1.33V5.33C17 4.6 16.4 4 15.67 4z"
    };

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? propName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propName));
}

public class BatteriesViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly IAppSettingsProvider appSettingsProvider;
    private BatteriesModel model;
    private readonly DispatcherTimer timer;
    private string summaryText = string.Empty;

    public ObservableCollection<BatteryDeviceItem> Items { get; }

    public ObservableCollection<BatteryDeviceItem> Devices => Items;
    public BatteryDeviceItem MainDevice => Items[0];

    public BatteriesViewModel(BatteriesModel? initialModel = null, IAppSettingsProvider? settingsProvider = null)
    {
        model = initialModel ?? new BatteriesModel();
        appSettingsProvider = settingsProvider 
            ?? (DeskCanvas.App.Services?.GetService(typeof(IAppSettingsProvider)) as IAppSettingsProvider) 
            ?? new AppSettingsProvider();

        Items = [
            new BatteryDeviceItem(Locale.Batteries_MainDevice, DeviceKind.Computer, 100, true, true, appSettingsProvider),
            new BatteryDeviceItem(string.Empty, DeviceKind.Mouse, 0, false, false, appSettingsProvider),
            new BatteryDeviceItem(string.Empty, DeviceKind.Keyboard, 0, false, false, appSettingsProvider),
            new BatteryDeviceItem(string.Empty, DeviceKind.Headphones, 0, false, false, appSettingsProvider)
        ];

        timer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(3)
        };
        timer.Tick += (_, _) => PollPowerStatus();
        timer.Start();

        appSettingsProvider.DataChanged += OnSettingsChanged;

        PollPowerStatus();
    }

    private void OnSettingsChanged(object sender, AppSettings? oldData, AppSettings newData)
    {
        Dispatcher.UIThread.Post(RefreshStatusBrushes);
    }

    public void RefreshStatusBrushes()
    {
        foreach (var item in Items)
        {
            item.NotifyStatusBrushChanged();
        }
    }

    public BatteriesModel Model => model;

    public string SummaryText
    {
        get => summaryText;
        private set
        {
            if (summaryText != value)
            {
                summaryText = value;
                OnPropertyChanged();
            }
        }
    }

    public void UpdateModel(BatteriesModel newModel)
    {
        model = newModel;
        PollPowerStatus();
    }

    public void PollPowerStatus()
    {
        var info = PowerService.GetCurrentPowerInfo();

        Items[0].Percentage = info.Percentage;
        Items[0].IsCharging = info.IsCharging;
        Items[0].HasDevice = true;

        if (!info.HasBattery)
        {
            Items[0].Name = Locale.Batteries_AcPower;
            SummaryText = "已连接交流电源";
        }
        else if (info.IsCharging)
        {
            Items[0].Name = Locale.Batteries_MainDevice;
            SummaryText = $"{Locale.Batteries_Charging} ({info.Percentage}%)";
        }
        else
        {
            Items[0].Name = Locale.Batteries_MainDevice;
            SummaryText = info.RemainingMinutes > 0
                ? $"预计剩余 {(info.RemainingMinutes / 60)}小时{(info.RemainingMinutes % 60)}分"
                : $"剩余电量 {info.Percentage}%";
        }

        if (model.ShowPeripherals)
        {
            var peripherals = PowerService.GetConnectedPeripherals(model.DeviceTypeOverrides);
            for (int i = 0; i < 3; i++)
            {
                var slot = Items[i + 1];
                if (i < peripherals.Count)
                {
                    slot.Name = peripherals[i].Name;
                    slot.Kind = peripherals[i].Kind;
                    slot.Percentage = peripherals[i].Percentage;
                    slot.IsCharging = false;
                    slot.HasDevice = true;
                }
                else
                {
                    slot.Name = string.Empty;
                    slot.Percentage = 0;
                    slot.IsCharging = false;
                    slot.HasDevice = false;
                }
            }
        }
        else
        {
            for (int i = 1; i < 4; i++)
            {
                Items[i].Name = string.Empty;
                Items[i].Percentage = 0;
                Items[i].IsCharging = false;
                Items[i].HasDevice = false;
            }
        }
    }

    public void Dispose()
    {
        timer.Stop();
        appSettingsProvider.DataChanged -= OnSettingsChanged;
    }

    // ------------------------------------------------------------------
    //  Fullscreen suspension (host: IWidgetSuspendable)
    // ------------------------------------------------------------------

    private bool suspended;

    /// <summary>
    /// Called by the host when every attached screen is covered (see <c>IWidgetSuspendable</c>).
    /// The 3 s poll is the most expensive recurring work this widget owns — with ShowPeripherals
    /// on it walks the whole device tree through SetupAPI every tick — and the result cannot be
    /// seen while covered. Idempotent.
    /// </summary>
    public void Suspend()
    {
        if (suspended) return;
        suspended = true;
        timer.Stop();
    }

    /// <summary>Undoes <see cref="Suspend"/>; polls once immediately so the readout is fresh
    /// even if the suspension lasted longer than several poll intervals.</summary>
    public void Resume()
    {
        if (!suspended) return;
        suspended = false;
        PollPowerStatus();
        timer.Start();
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? propName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propName));
}
