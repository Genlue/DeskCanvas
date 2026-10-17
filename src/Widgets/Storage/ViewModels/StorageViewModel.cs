using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using DeskCanvas.Contracts;
using DeskCanvas.Core.Interfaces;
using DeskCanvas.Core.Models.Settings;
using DeskCanvas.Core.Services;
using Storage.Locales;
using Storage.Models;
using Storage.Services;

namespace Storage.ViewModels;

public class DriveItemViewModel : INotifyPropertyChanged
{
    private readonly IAppSettingsProvider? appSettingsProvider;
    private bool hasDrive;
    private string rootPath = string.Empty;
    private string driveLetter = string.Empty;
    private string volumeLabel = string.Empty;
    private string displayTitle = string.Empty;
    private DriveType driveType = DriveType.Fixed;
    private string driveFormat = "NTFS";
    private bool isSystemDrive;
    private long totalBytes;
    private long freeBytes;
    private long usedBytes;
    private double usageFraction;
    private int usagePercent;
    private StorageTextFormat textFormat = StorageTextFormat.FreeSpace;
    private bool useWarningColors = true;

    /// <summary>
    /// Variant source override for the readout colours. The host view wires this to the
    /// window's ActualThemeVariant so a drive ring honours the variant of the screen it sits
    /// on (the per-screen policy writes the variant per window). Null keeps the historic
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

    public DriveItemViewModel(IAppSettingsProvider? appSettingsProvider = null)
    {
        this.appSettingsProvider = appSettingsProvider;
    }

    public void UpdateFrom(StorageService.DriveStorageInfo? info, StorageModel model)
    {
        textFormat = model.TextFormat;
        useWarningColors = model.UseWarningColors;

        if (info == null)
        {
            hasDrive = false;
            rootPath = string.Empty;
            driveLetter = string.Empty;
            volumeLabel = string.Empty;
            displayTitle = string.Empty;
            totalBytes = 0;
            freeBytes = 0;
            usedBytes = 0;
            usageFraction = 0;
            usagePercent = 0;
            NotifyAll();
            return;
        }

        hasDrive = true;
        rootPath = info.RootPath;
        driveLetter = info.DriveLetter;
        volumeLabel = info.VolumeLabel;
        displayTitle = info.DisplayTitle;
        driveType = info.DriveType;
        driveFormat = info.DriveFormat;
        isSystemDrive = info.IsSystemDrive;
        totalBytes = info.TotalBytes;
        freeBytes = info.FreeBytes;
        usedBytes = info.UsedBytes;
        usageFraction = Math.Clamp(info.UsageFraction, 0.0, 1.0);
        usagePercent = Math.Clamp(info.UsagePercent, 0, 100);
        NotifyAll();
    }

    public bool HasDrive => hasDrive;
    public string RootPath => rootPath;
    public string DriveLetter => driveLetter;
    public string VolumeLabel => volumeLabel;
    public string DisplayTitle => displayTitle;
    public DriveType DriveType => driveType;
    public string DriveFormat => driveFormat;
    public bool IsSystemDrive => isSystemDrive;
    public long TotalBytes => totalBytes;
    public long FreeBytes => freeBytes;
    public long UsedBytes => usedBytes;
    public double UsageFraction => hasDrive ? usageFraction : 0.0;
    public int UsagePercent => hasDrive ? usagePercent : 0;
    public bool IsCritical => hasDrive && (usagePercent >= 90 || (totalBytes >= 20L * 1024 * 1024 * 1024 && freeBytes < 8L * 1024 * 1024 * 1024));
    public bool IsWarning => hasDrive && (usagePercent >= 75 || IsCritical);

    private static bool IsZh => System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.Equals("zh", StringComparison.OrdinalIgnoreCase);

    public string RightStatusText => !hasDrive
        ? string.Empty
        : textFormat switch
        {
            StorageTextFormat.UsedAndTotal => $"{StorageService.FormatBytes(usedBytes, compact: true)} / {StorageService.FormatBytes(totalBytes, compact: true)}",
            StorageTextFormat.Percentage => $"{usagePercent}%",
            _ => IsZh ? $"{StorageService.FormatBytes(freeBytes, compact: true)} 可用" : $"{StorageService.FormatBytes(freeBytes, compact: true)} free"
        };

    public string UsedOverTotalText => !hasDrive
        ? string.Empty
        : $"{StorageService.FormatBytes(usedBytes, compact: true)} / {StorageService.FormatBytes(totalBytes, compact: true)}";

    public string FreeSpaceText => !hasDrive
        ? string.Empty
        : (IsZh ? $"剩余 {StorageService.FormatBytes(freeBytes, compact: true)}" : $"{StorageService.FormatBytes(freeBytes, compact: true)} free");

    public string PercentText => hasDrive ? $"{usagePercent}%" : string.Empty;

    public string RingBottomText => !hasDrive
        ? string.Empty
        : textFormat switch
        {
            StorageTextFormat.FreeSpace => $"{driveLetter} {StorageService.FormatBytes(freeBytes, compact: true)}",
            StorageTextFormat.UsedAndTotal => $"{driveLetter} {StorageService.FormatBytes(usedBytes, compact: true)}",
            _ => $"{driveLetter} {usagePercent}%"
        };

    public string MiniBottomText => !hasDrive
        ? string.Empty
        : $"{driveLetter} {StorageService.FormatBytes(freeBytes, compact: true)}";

    public string TooltipText => !hasDrive
        ? string.Empty
        : (IsZh
            ? $"{displayTitle} ({driveFormat})\n已用: {StorageService.FormatBytes(usedBytes)} ({usagePercent}%)\n可用: {StorageService.FormatBytes(freeBytes)}\n总计: {StorageService.FormatBytes(totalBytes)}"
            : $"{displayTitle} ({driveFormat})\nUsed: {StorageService.FormatBytes(usedBytes)} ({usagePercent}%)\nFree: {StorageService.FormatBytes(freeBytes)}\nTotal: {StorageService.FormatBytes(totalBytes)}");

    private const double FullCircleDash = 39.27;
    public double StrokeDashOffset => FullCircleDash * (1.0 - UsageFraction);

    public string IconData
    {
        get
        {
            if (isSystemDrive)
            {
                // System / Windows drive icon
                return "M3 5.5L10.5 4.5V11.5H3V5.5ZM11.5 4.35L21 3V11.5H11.5V4.35ZM3 12.5H10.5V19.5L3 18.5V12.5ZM11.5 12.5H21V21L11.5 19.65V12.5Z";
            }

            return driveType switch
            {
                DriveType.Removable => "M7 2v11m0 0a2 2 0 1 0 4 0m-4 0a2 2 0 1 1 4 0m4-9v7a4 4 0 0 1-4 4H9v4h2v2H7v-2h2v-4H7a4 4 0 0 1-4-4V6h2v4a2 2 0 0 0 2 2h2V4H7l3-3 3 3h-2v8h2a2 2 0 0 0 2-2V6h2z",
                DriveType.Network => "M19.35 10.04C18.67 6.59 15.64 4 12 4 9.11 4 6.6 5.64 5.35 8.04 2.34 8.36 0 10.91 0 14c0 3.31 2.69 6 6 6h13c2.76 0 5-2.24 5-5 0-2.64-2.05-4.78-4.65-4.96z",
                _ => "M4 4h16a2 2 0 0 1 2 2v12a2 2 0 0 1-2 2H4a2 2 0 0 1-2-2V6a2 2 0 0 1 2-2zm0 2v8h16V6H4zm0 10v2h16v-2H4zm13 0.5a1 1 0 1 1 0 2 1 1 0 0 1 0-2zm-3 0a1 1 0 1 1 0 2 1 1 0 0 1 0-2z"
            };
        }
    }

    public IBrush StatusBrush
    {
        get
        {
            if (!hasDrive) return Brushes.Transparent;
            Theme? theme = null;
            try { theme = appSettingsProvider?.Get()?.Theme; } catch { }
            var isDark = EffectiveVariant == ThemeVariant.Dark;

            if (theme != null && !theme.IsColorful && theme.Monochrome)
            {
                if (theme.EffectiveMonochromeVariant == MonochromeStyle.BlackWhite)
                    return isDark ? Brushes.White : Brushes.Black;

                if (theme.EffectiveMonochromeVariant == MonochromeStyle.BackgroundColor)
                {
                    if (Application.Current != null &&
                        Application.Current.TryGetResource("SystemControlForegroundAccentBrush", EffectiveVariant, out var themed) &&
                        themed is IBrush themedBrush)
                    {
                        return themedBrush;
                    }
                    var hex = isDark ? theme.EffectiveSolidBackgroundLight : theme.EffectiveSolidBackgroundDark;
                    return new SolidColorBrush(Color.TryParse(hex, out var background)
                        ? background
                        : (isDark ? Colors.White : Colors.Black));
                }

                if (!string.IsNullOrWhiteSpace(theme.AccentColor) && Color.TryParse(theme.AccentColor, out var parsedAccent))
                    return new SolidColorBrush(parsedAccent);
                if (Application.Current != null &&
                    Application.Current.TryGetResource("SystemControlForegroundAccentBrush", EffectiveVariant, out var res) &&
                    res is IBrush ab)
                {
                    return ab;
                }
                return new SolidColorBrush(Color.Parse(isDark ? "#70A5FF" : "#0078D4"));
            }

            if (useWarningColors)
            {
                if (IsCritical)
                    return new SolidColorBrush(Color.Parse(isDark ? "#FF453A" : "#FF3B30"));
                if (usagePercent >= 75)
                    return new SolidColorBrush(Color.Parse(isDark ? "#FF9F0A" : "#FF9500"));
                return new SolidColorBrush(Color.Parse(isDark ? "#30D158" : "#34C759"));
            }

            if (driveType == DriveType.Removable)
                return new SolidColorBrush(Color.Parse(isDark ? "#30D158" : "#34C759"));

            if (driveType == DriveType.Network)
                return new SolidColorBrush(Color.Parse(isDark ? "#64D2FF" : "#30B0C7"));

            if (isSystemDrive)
                return new SolidColorBrush(Color.Parse(isDark ? "#0A84FF" : "#007AFF"));

            // macOS Storage segmented color assignment for secondary partitions (D:, E:, F:...):
            char dl = string.IsNullOrEmpty(driveLetter) ? 'D' : char.ToUpperInvariant(driveLetter[0]);
            return (dl % 3) switch
            {
                0 => new SolidColorBrush(Color.Parse(isDark ? "#BF5AF2" : "#AF52DE")), // macOS Purple
                1 => new SolidColorBrush(Color.Parse(isDark ? "#64D2FF" : "#30B0C7")), // macOS Teal/Cyan
                _ => new SolidColorBrush(Color.Parse(isDark ? "#FF9F0A" : "#FF9500"))  // macOS Amber
            };
        }
    }

    /// <summary>
    /// Track tint source for the dashboard-style ring/bar: the status colour while a drive is
    /// present; a neutral foreground tint for an empty slot, whose StatusBrush is Transparent —
    /// without this the unfilled ring would vanish entirely instead of staying as a faint circle.
    /// </summary>
    public IBrush TrackBrush
    {
        get
        {
            if (hasDrive) return StatusBrush;
            if (Application.Current != null &&
                Application.Current.TryGetResource("SystemControlForegroundBaseHighBrush", EffectiveVariant, out var neutral) &&
                neutral is IBrush neutralBrush)
                return neutralBrush;
            return Brushes.Gray;
        }
    }

    public void NotifyBrushChanged()
    {
        OnPropertyChanged(nameof(StatusBrush));
        OnPropertyChanged(nameof(TrackBrush));
    }

    private void NotifyAll()
    {
        OnPropertyChanged(nameof(HasDrive));
        OnPropertyChanged(nameof(RootPath));
        OnPropertyChanged(nameof(DriveLetter));
        OnPropertyChanged(nameof(VolumeLabel));
        OnPropertyChanged(nameof(DisplayTitle));
        OnPropertyChanged(nameof(DriveType));
        OnPropertyChanged(nameof(DriveFormat));
        OnPropertyChanged(nameof(IsSystemDrive));
        OnPropertyChanged(nameof(TotalBytes));
        OnPropertyChanged(nameof(FreeBytes));
        OnPropertyChanged(nameof(UsedBytes));
        OnPropertyChanged(nameof(UsageFraction));
        OnPropertyChanged(nameof(UsagePercent));
        OnPropertyChanged(nameof(IsCritical));
        OnPropertyChanged(nameof(IsWarning));
        OnPropertyChanged(nameof(RightStatusText));
        OnPropertyChanged(nameof(UsedOverTotalText));
        OnPropertyChanged(nameof(FreeSpaceText));
        OnPropertyChanged(nameof(PercentText));
        OnPropertyChanged(nameof(RingBottomText));
        OnPropertyChanged(nameof(MiniBottomText));
        OnPropertyChanged(nameof(TooltipText));
        OnPropertyChanged(nameof(StrokeDashOffset));
        OnPropertyChanged(nameof(IconData));
        OnPropertyChanged(nameof(StatusBrush));
        OnPropertyChanged(nameof(TrackBrush));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? propName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propName));
}

public class StorageViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly IAppSettingsProvider appSettingsProvider;
    private readonly IWidgetSession? session;
    private StorageModel model;
    private readonly DispatcherTimer timer;
    private bool disposed;
    private bool renderActive = true;
    private bool suspended;

    // Gates reconciled on every change: the ten-second poll runs exactly while the session
    // renders, the host has not suspended the widget and the view model is not disposed.
    private int pollStartsForChecks;

    private long totalCapacityBytes;
    private long totalFreeBytes;
    private long totalUsedBytes;
    private double totalUsageFraction;
    private int totalUsagePercent;

    public List<DriveItemViewModel> Drives { get; private set; } = [];
    public ObservableCollection<DriveItemViewModel> RingSlots { get; }

    public event Action? DrivesUpdated;

    /// <summary>
    /// <paramref name="session"/> is injected by WidgetFactory when the host runs the session
    /// pipeline: the ten-second poll is then gated on the session's activity (besides the
    /// host's explicit <see cref="Suspend"/>). Null keeps the legacy always-on behaviour
    /// (preview, legacy layout entries) — those hosts never push activity either.
    /// </summary>
    public StorageViewModel(StorageModel? initialModel = null, IAppSettingsProvider? settingsProvider = null, IWidgetSession? session = null)
    {
        model = initialModel ?? new StorageModel();
        this.session = session;
        renderActive = session?.Activity.RenderActive ?? true;
        appSettingsProvider = settingsProvider
            ?? (DeskCanvas.App.Services?.GetService(typeof(IAppSettingsProvider)) as IAppSettingsProvider)
            ?? new AppSettingsProvider();

        RingSlots =
        [
            new DriveItemViewModel(appSettingsProvider),
            new DriveItemViewModel(appSettingsProvider),
            new DriveItemViewModel(appSettingsProvider),
            new DriveItemViewModel(appSettingsProvider)
        ];

        timer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(10)
        };
        timer.Tick += (_, _) => PollDrives();
        UpdateTimerSubscription();

        appSettingsProvider.DataChanged += OnSettingsChanged;

        PollDrives();
    }

    public StorageModel Model => model;

    private static bool IsZh => System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.Equals("zh", StringComparison.OrdinalIgnoreCase);

    public string HeaderTitle => Locale.Storage_HeaderTitle;

    public string HeaderSummaryText => totalCapacityBytes <= 0
        ? string.Empty
        : model.TextFormat switch
        {
            StorageTextFormat.UsedAndTotal => $"{StorageService.FormatBytes(totalUsedBytes, compact: true)} / {StorageService.FormatBytes(totalCapacityBytes, compact: true)}",
            StorageTextFormat.Percentage => IsZh ? $"总计 {totalUsagePercent}%" : $"Total {totalUsagePercent}%",
            _ => IsZh ? $"可用 {StorageService.FormatBytes(totalFreeBytes, compact: true)}" : $"{StorageService.FormatBytes(totalFreeBytes, compact: true)} free"
        };

    public string WideSummaryText => totalCapacityBytes <= 0
        ? string.Empty
        : (IsZh
            ? $"已用 {StorageService.FormatBytes(totalUsedBytes)} / {StorageService.FormatBytes(totalCapacityBytes)} ({totalUsagePercent}%) · 可用 {StorageService.FormatBytes(totalFreeBytes)}"
            : $"Used {StorageService.FormatBytes(totalUsedBytes)} / {StorageService.FormatBytes(totalCapacityBytes)} ({totalUsagePercent}%) · {StorageService.FormatBytes(totalFreeBytes)} free");

    public double TotalUsageFraction => totalUsageFraction;
    public int TotalUsagePercent => totalUsagePercent;

    private void OnSettingsChanged(object sender, AppSettings? oldData, AppSettings newData)
    {
        Dispatcher.UIThread.Post(RefreshBrushes);
    }

    public void RefreshBrushes()
    {
        foreach (var d in Drives)
            d.NotifyBrushChanged();
        foreach (var slot in RingSlots)
            slot.NotifyBrushChanged();
        DrivesUpdated?.Invoke();
    }

    public void UpdateModel(StorageModel newModel)
    {
        model = newModel;
        PollDrives();
    }

    public void PollDrives()
    {
        if (disposed) return;

        var infos = StorageService.GetDrives(
            includeRemovable: model.ShowRemovableDrives,
            includeNetwork: model.ShowNetworkDrives,
            hiddenDrives: model.HiddenDrives);

        // Ensure at least system drive fallback if user hid everything
        if (infos.Count == 0)
        {
            var all = StorageService.GetAllCandidateDrives(includeRemovable: true, includeNetwork: true);
            if (all.Count > 0)
                infos.Add(all[0]);
        }

        totalCapacityBytes = infos.Sum(x => x.TotalBytes);
        totalFreeBytes = infos.Sum(x => x.FreeBytes);
        totalUsedBytes = infos.Sum(x => x.UsedBytes);
        totalUsageFraction = totalCapacityBytes > 0
            ? Math.Clamp((double)totalUsedBytes / totalCapacityBytes, 0.0, 1.0)
            : 0.0;
        totalUsagePercent = (int)Math.Round(totalUsageFraction * 100.0);

        while (Drives.Count < infos.Count)
            Drives.Add(new DriveItemViewModel(appSettingsProvider));
        while (Drives.Count > infos.Count)
            Drives.RemoveAt(Drives.Count - 1);

        for (int i = 0; i < infos.Count; i++)
        {
            Drives[i].UpdateFrom(infos[i], model);
        }

        for (int i = 0; i < RingSlots.Count; i++)
        {
            if (i < infos.Count)
                RingSlots[i].UpdateFrom(infos[i], model);
            else
                RingSlots[i].UpdateFrom(null, model);
        }

        OnPropertyChanged(nameof(HeaderTitle));
        OnPropertyChanged(nameof(HeaderSummaryText));
        OnPropertyChanged(nameof(WideSummaryText));
        OnPropertyChanged(nameof(TotalUsageFraction));
        OnPropertyChanged(nameof(TotalUsagePercent));
        DrivesUpdated?.Invoke();
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
        UpdateTimerSubscription();
    }

    /// <summary>
    /// Single subscription state machine over all three gates (reconciliation, not toggling):
    /// the ten-second DispatcherTimer runs exactly while the session renders AND the host has
    /// not suspended the widget AND the view model is not disposed. Either gate closed stops
    /// the poll; resuming requires all open again. Overlapping transitions cannot leave the
    /// timer in a state the gates do not describe.
    /// </summary>
    private void UpdateTimerSubscription()
    {
        var wanted = renderActive && !suspended && !disposed;
        if (wanted && !timer.IsEnabled)
        {
            timer.Start();
            pollStartsForChecks++;
        }
        else if (!wanted && timer.IsEnabled)
        {
            timer.Stop();
        }
    }

    public void Suspend()
    {
        if (suspended || disposed) return;
        suspended = true;
        UpdateTimerSubscription();
    }

    public void Resume()
    {
        if (!suspended || disposed) return;
        suspended = false;
        PollDrives();
        UpdateTimerSubscription();
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        UpdateTimerSubscription();
        appSettingsProvider.DataChanged -= OnSettingsChanged;
    }

    // ------------------------------------------------------------------
    //  Check seams (tests/DataWidgetSessionChecks)
    // ------------------------------------------------------------------

    /// <summary>Check seam: whether the ten-second poll timer is currently running.</summary>
    public bool IsPollingForChecks => timer.IsEnabled;

    /// <summary>Check seam: the latest session gate value.</summary>
    public bool RenderActiveForChecks => renderActive;

    /// <summary>Check seam: the latest explicit suspension gate value.</summary>
    public bool SuspendedForChecks => suspended;

    /// <summary>Check seam: the injected session (null on the legacy path).</summary>
    public IWidgetSession? SessionForChecks => session;

    /// <summary>Check seam: how many times the poll actually (re)started — a covered session
    /// must not start it, and overlapping transitions must not double-start it.</summary>
    public int PollStartsForChecks => pollStartsForChecks;

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? propName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propName));
}
