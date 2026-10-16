using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Batteries.Locales;
using Batteries.Models;
using Batteries.Services;
using DeskCanvas.Contracts;
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
                OnPropertyChanged(nameof(TrackBrush));
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
                OnPropertyChanged(nameof(TrackBrush));
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
                OnPropertyChanged(nameof(TrackBrush));
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

    /// <summary>
    /// Variant source override for the readout colours. The host view wires this to the
    /// window's ActualThemeVariant so a widget honours the variant of the screen it sits on
    /// (the per-screen policy writes the variant per window). Null keeps the historic
    /// behaviour: the uniform Application variant.
    /// </summary>
    public Func<ThemeVariant?>? VariantProvider { get; set; }

    /// <summary>The variant the brushes resolve against: the host window's variant when the
    /// view wired <see cref="VariantProvider"/>, otherwise the uniform Application variant.</summary>
    private ThemeVariant EffectiveVariant =>
        ResolveVariant(VariantProvider?.Invoke(), Application.Current?.ActualThemeVariant);

    /// <summary>
    /// Pure seam: the effective variant for a readout. The host window's variant wins
    /// (per-screen policy); a null window variant (control not attached / uniform host)
    /// falls back to the uniform Application variant, then to the platform default.
    /// </summary>
    public static ThemeVariant ResolveVariant(ThemeVariant? hostWindowVariant, ThemeVariant? uniformVariant) =>
        hostWindowVariant ?? uniformVariant ?? ThemeVariant.Default;

    /// <summary>
    /// Pure seam: the status ring colour for a present device — the pinned hex trio
    /// (green / orange / red, each with a dark- and a light-mode pair). Carried over
    /// byte-identical from the pre-refactor literals so the brush refactor cannot
    /// silently drift the palette.
    /// </summary>
    public static Color StatusColor(bool isDark, bool isCharging, int percentage)
    {
        if (isCharging || percentage > 20)
            return Color.Parse(isDark ? "#30D158" : "#34C759");
        if (percentage > 10)
            return Color.Parse(isDark ? "#FF9F0A" : "#FF9500");
        return Color.Parse(isDark ? "#FF453A" : "#FF3B30");
    }

    public IBrush StatusBrush
    {
        get
        {
            if (!hasDevice) return Brushes.Transparent;
            Theme? theme = null;
            try { theme = appSettingsProvider?.Get()?.Theme; } catch { }
            if (theme != null && !theme.IsColorful && theme.Monochrome)
            {
                var isDark = EffectiveVariant == ThemeVariant.Dark;
                // 黑白: the ring/badge fill is the accent itself — pure black (light) / white (dark).
                if (theme.EffectiveMonochromeVariant == MonochromeStyle.BlackWhite)
                    return isDark ? Brushes.White : Brushes.Black;
                // 背景色: the accent locks to the inverted background; the hand-picked accent
                // must not leak, so resolve the themed brush the dictionaries use.
                if (theme.EffectiveMonochromeVariant == MonochromeStyle.BackgroundColor)
                {
                    if (Application.Current != null && Application.Current.TryGetResource("SystemControlForegroundAccentBrush", EffectiveVariant, out var themed) && themed is IBrush themedBrush)
                        return themedBrush;
                    var hex = isDark ? theme.EffectiveSolidBackgroundLight : theme.EffectiveSolidBackgroundDark;
                    return new SolidColorBrush(Color.TryParse(hex, out var background)
                        ? background
                        : (isDark ? Colors.White : Colors.Black));
                }
                // 强调色: the hand-picked accent is honored.
                if (!string.IsNullOrWhiteSpace(theme.AccentColor) && Color.TryParse(theme.AccentColor, out var parsedAccent))
                    return new SolidColorBrush(parsedAccent);
                if (Application.Current != null && Application.Current.TryGetResource("SystemControlForegroundAccentBrush", EffectiveVariant, out var res) && res is IBrush ab)
                    return ab;
                return isDark ? new SolidColorBrush(Color.Parse("#70A5FF")) : new SolidColorBrush(Color.Parse("#0078D4"));
            }
            return new SolidColorBrush(StatusColor(EffectiveVariant == ThemeVariant.Dark, IsCharging, Percentage));
        }
    }

    /// <summary>
    /// Track tint source for the dashboard-style ring: the status colour while a device is
    /// present; a neutral foreground tint for an empty slot, whose StatusBrush is Transparent —
    /// without this the unfilled ring would vanish entirely instead of staying as a faint circle.
    /// </summary>
    public IBrush TrackBrush
    {
        get
        {
            if (hasDevice) return StatusBrush;
            if (Application.Current != null &&
                Application.Current.TryGetResource("SystemControlForegroundBaseHighBrush", EffectiveVariant, out var neutral) &&
                neutral is IBrush neutralBrush)
                return neutralBrush;
            return Brushes.Gray;
        }
    }

    public IBrush ChargingBadgeForeground
    {
        get
        {
            var theme = appSettingsProvider?.Get()?.Theme;
            if (theme != null && !theme.IsColorful && theme.Monochrome)
            {
                var isDark = EffectiveVariant == ThemeVariant.Dark;
                return theme.EffectiveMonochromeVariant switch
                {
                    // 黑白: the badge fill is the black (light) / white (dark) accent — invert it.
                    MonochromeStyle.BlackWhite => isDark ? Brushes.Black : Brushes.White,
                    // 背景色: the badge fill is the dark (light mode) / light (dark mode)
                    // background color — the same polarity AccentContrastForegroundBrush uses.
                    MonochromeStyle.BackgroundColor => isDark ? Brushes.Black : Brushes.White,
                    _ => Brushes.White,
                };
            }
            return Brushes.White;
        }
    }

    public void NotifyStatusBrushChanged()
    {
        OnPropertyChanged(nameof(StatusBrush));
        OnPropertyChanged(nameof(ChargingBadgeForeground));
    }

    private static bool IsZh =>
        CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.Equals("zh", StringComparison.OrdinalIgnoreCase);

    public string StatusText => !hasDevice
        ? string.Empty
        : (IsCharging ? (IsZh ? "⚡ 充电中" : "⚡ Charging") : $"{Percentage}%");

    public string TooltipText => !hasDevice
        ? string.Empty
        : $"{Name}: {(IsCharging ? (IsZh ? "充电中 " : "Charging ") : "")}{Percentage}%";

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
    private readonly IAppSettingsProvider? appSettingsProvider;
    private BatteriesModel model;
    private readonly IWidgetSession? session;
    private string summaryText = string.Empty;

    public ObservableCollection<BatteryDeviceItem> Items { get; }

    public ObservableCollection<BatteryDeviceItem> Devices => Items;
    public BatteryDeviceItem MainDevice => Items[0];

    public BatteriesViewModel(BatteriesModel? initialModel = null, IAppSettingsProvider? settingsProvider = null, IWidgetSession? session = null)
    {
        model = initialModel ?? new BatteriesModel();
        // Two-tier resolution: an explicitly injected provider wins, then the app-level
        // service. The historic third tier (a standalone provider instance built inside the
        // component) is gone — with both tiers empty the widget runs on safe defaults (null
        // here means "no theme info", which the readout items already treat as the colourful
        // branch); no provider instance is ever created or cached inside the component.
        appSettingsProvider = settingsProvider
            ?? (DeskCanvas.App.Services?.GetService(typeof(IAppSettingsProvider)) as IAppSettingsProvider);

        this.session = session;
        // Session-less widgets (gallery preview, legacy layout entries) keep the historic
        // always-on behaviour; with a session the poll is gated by RenderActive.
        renderActive = session?.Activity.RenderActive ?? true;

        Items = [
            new BatteryDeviceItem(Locale.Batteries_MainDevice, DeviceKind.Computer, 100, true, true, appSettingsProvider),
            new BatteryDeviceItem(string.Empty, DeviceKind.Mouse, 0, false, false, appSettingsProvider),
            new BatteryDeviceItem(string.Empty, DeviceKind.Keyboard, 0, false, false, appSettingsProvider),
            new BatteryDeviceItem(string.Empty, DeviceKind.Headphones, 0, false, false, appSettingsProvider)
        ];

        if (appSettingsProvider != null)
        {
            appSettingsProvider.DataChanged += OnSettingsChanged;
        }

        UpdatePollSubscription();

        PollPowerStatus();
    }

    // ------------------------------------------------------------------
    //  Shared-timer polling (TimerService) with a 3-tick divider
    // ------------------------------------------------------------------

    /// <summary>
    /// Pure seam: whether the poll runs on a shared 1 s tick. <paramref name="tickIndex"/> is
    /// the 0-based tick count since the subscription went live; index 0 — the FIRST tick after
    /// (re)subscribing — polls immediately ("subscribe means refresh"), then every third tick
    /// (0, 3, 6, …), the same effective 3 s cadence the retired private UI-thread timer
    /// delivered. This boundary is pinned by tests/BatteriesChecks; do not shift it.
    /// </summary>
    public static bool ShouldPollOnTick(int tickIndex) => tickIndex % 3 == 0;

    // 0-based count of shared 1 s ticks since the subscription went live (see ShouldPollOnTick).
    private int tickCounter;
    // Reconciled poll state: subscribed to the shared timer exactly while both gates are open.
    private bool pollSubscribed;
    // Gate 1: the session's latest activity push (session-less widgets are always true).
    private bool renderActive;
    // Gate 2: the host's explicit fullscreen suspension (IWidgetSuspendable).
    private bool suspended;

    private void OnSharedTick()
    {
        var poll = ShouldPollOnTick(tickCounter);
        tickCounter++;
        if (poll) PollPowerStatus();
    }

    /// <summary>
    /// Single subscription state machine over BOTH gates (reconciliation, not toggling): the
    /// poll subscribes to the shared timer exactly while the session renders AND the host has
    /// not suspended the widget. Ticks stop automatically during fullscreen because
    /// TimerService.PauseAll pauses every shared timer; the explicit Suspend below is the
    /// second, widget-owned gate. Either gate closed stops the poll; resuming requires BOTH
    /// open again. Overlapping transitions cannot double-(un)subscribe.
    /// </summary>
    private void UpdatePollSubscription()
    {
        var wanted = renderActive && !suspended;
        if (wanted && !pollSubscribed)
        {
            pollSubscribed = true;
            tickCounter = 0; // the first tick after (re)subscribing polls immediately
            DeskCanvas.Services.TimerService.Timer1Second.Subscribe(OnSharedTick);
        }
        else if (!wanted && pollSubscribed)
        {
            pollSubscribed = false;
            DeskCanvas.Services.TimerService.Timer1Second.Unsubscribe(OnSharedTick);
        }
    }

    /// <summary>
    /// Host activity push (the <see cref="DeskCanvas.Runtime.IWidgetActivityConsumer"/> path,
    /// invoked on the UI thread): the poll follows the snapshot's RenderActive. Session-less
    /// view models ignore the push entirely (the host never sends one to them).
    /// </summary>
    public void OnSessionActivity(ActivitySnapshot activity)
    {
        if (session == null) return;
        renderActive = activity.RenderActive;
        UpdatePollSubscription();
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
        PollCountForChecks++;
        var info = PowerService.GetCurrentPowerInfo();

        Items[0].Percentage = info.Percentage;
        Items[0].IsCharging = info.IsCharging;
        Items[0].HasDevice = true;

        bool isZh = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.Equals("zh", StringComparison.OrdinalIgnoreCase);

        if (!info.HasBattery)
        {
            Items[0].Name = Locale.Batteries_AcPower;
            SummaryText = isZh ? "已连接交流电源" : "Connected to AC power";
        }
        else if (info.IsCharging)
        {
            Items[0].Name = Locale.Batteries_MainDevice;
            SummaryText = $"{Locale.Batteries_Charging} ({info.Percentage}%)";
        }
        else
        {
            Items[0].Name = Locale.Batteries_MainDevice;
            int hours = info.RemainingMinutes / 60;
            int mins = info.RemainingMinutes % 60;
            SummaryText = info.RemainingMinutes > 0
                ? (isZh ? $"预计剩余 {hours}小时{mins}分" : $"~{hours}h {mins}m remaining")
                : (isZh ? $"剩余电量 {info.Percentage}%" : $"{info.Percentage}% remaining");
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
        if (pollSubscribed)
        {
            pollSubscribed = false;
            DeskCanvas.Services.TimerService.Timer1Second.Unsubscribe(OnSharedTick);
        }
        if (appSettingsProvider != null)
        {
            appSettingsProvider.DataChanged -= OnSettingsChanged;
        }
    }

    // ------------------------------------------------------------------
    //  Fullscreen suspension (host: IWidgetSuspendable)
    // ------------------------------------------------------------------

    /// <summary>
    /// Called by the host when every attached screen is covered (see <c>IWidgetSuspendable</c>).
    /// The 3 s poll is the most expensive recurring work this widget owns — with ShowPeripherals
    /// on it walks the whole device tree through SetupAPI every tick — and the result cannot be
    /// seen while covered. Second gate beside the session activity; idempotent.
    /// </summary>
    public void Suspend()
    {
        if (suspended) return;
        suspended = true;
        UpdatePollSubscription();
    }

    /// <summary>Undoes <see cref="Suspend"/>; polls once immediately so the readout is fresh
    /// even if the suspension lasted longer than several poll intervals. The re-subscription
    /// also resets the divider, so the first shared tick after resuming polls again — one
    /// extra read within a second of resuming, harmless (the poll is idempotent).</summary>
    public void Resume()
    {
        if (!suspended) return;
        suspended = false;
        PollPowerStatus();
        UpdatePollSubscription();
    }

    // ------------------------------------------------------------------
    //  Check seams (tests/BatteriesChecks)
    // ------------------------------------------------------------------

    /// <summary>Check seam: whether the poll currently holds a shared-timer subscription.</summary>
    public bool IsPollingForChecks => pollSubscribed;

    /// <summary>Check seam: the latest session gate value.</summary>
    public bool RenderActiveForChecks => renderActive;

    /// <summary>Check seam: the latest explicit suspension gate value.</summary>
    public bool SuspendedForChecks => suspended;

    /// <summary>Check seam: the 0-based shared-tick counter driving the divider.</summary>
    public int TickCounterForChecks => tickCounter;

    /// <summary>Check seam: how many times the power status was actually polled.</summary>
    public int PollCountForChecks { get; private set; }

    /// <summary>Check seam: the injected session (null on the legacy path).</summary>
    public IWidgetSession? SessionForChecks => session;

    /// <summary>Check seam: the resolved settings provider (null on the safe-default path).</summary>
    public IAppSettingsProvider? ProviderForChecks => appSettingsProvider;

    /// <summary>Check seam: drives one shared 1 s tick by hand (the headless rig has no live
    /// dispatcher ticks), exercising the divider exactly as <see cref="OnSharedTick"/> does.</summary>
    public void SimulateTickForChecks() => OnSharedTick();

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? propName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propName));
}
