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

    public string RightStatusText => !hasDrive
        ? string.Empty
        : textFormat switch
        {
            StorageTextFormat.UsedAndTotal => $"{StorageService.FormatBytes(usedBytes, compact: true)} / {StorageService.FormatBytes(totalBytes, compact: true)}",
            StorageTextFormat.Percentage => $"{usagePercent}%",
            _ => $"{StorageService.FormatBytes(freeBytes, compact: true)} 可用"
        };

    public string UsedOverTotalText => !hasDrive
        ? string.Empty
        : $"{StorageService.FormatBytes(usedBytes, compact: true)} / {StorageService.FormatBytes(totalBytes, compact: true)}";

    public string FreeSpaceText => !hasDrive
        ? string.Empty
        : $"剩余 {StorageService.FormatBytes(freeBytes, compact: true)}";

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
        : $"{displayTitle} ({driveFormat})\n已用: {StorageService.FormatBytes(usedBytes)} ({usagePercent}%)\n可用: {StorageService.FormatBytes(freeBytes)}\n总计: {StorageService.FormatBytes(totalBytes)}";

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
            var theme = appSettingsProvider?.Get()?.Theme;
            var isDark = Application.Current?.ActualThemeVariant == ThemeVariant.Dark;

            if (theme != null && !theme.IsColorful && theme.Monochrome)
            {
                if (theme.EffectiveMonochromeVariant == MonochromeStyle.BlackWhite)
                    return isDark ? Brushes.White : Brushes.Black;

                if (theme.EffectiveMonochromeVariant == MonochromeStyle.BackgroundColor)
                {
                    if (Application.Current != null &&
                        Application.Current.TryGetResource("SystemControlForegroundAccentBrush", Application.Current.ActualThemeVariant, out var themed) &&
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
                    Application.Current.TryGetResource("SystemControlForegroundAccentBrush", Application.Current.ActualThemeVariant, out var res) &&
                    res is IBrush ab)
                {
                    return ab;
                }
                return new SolidColorBrush(Color.Parse(isDark ? "#70A5FF" : "#0078D4"));
            }

            if (useWarningColors)
            {
                bool isCritical = usagePercent >= 90 || (totalBytes >= 20L * 1024 * 1024 * 1024 && freeBytes < 8L * 1024 * 1024 * 1024);
                if (isCritical)
                    return new SolidColorBrush(Color.Parse(isDark ? "#FF453A" : "#FF3B30"));
                if (usagePercent >= 75)
                    return new SolidColorBrush(Color.Parse(isDark ? "#FF9F0A" : "#FF9500"));
            }

            if (driveType == DriveType.Removable)
                return new SolidColorBrush(Color.Parse(isDark ? "#30D158" : "#34C759"));

            return new SolidColorBrush(Color.Parse(isDark ? "#0A84FF" : "#007AFF"));
        }
    }

    public void NotifyBrushChanged()
    {
        OnPropertyChanged(nameof(StatusBrush));
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
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? propName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propName));
}

public class StorageViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly IAppSettingsProvider appSettingsProvider;
    private StorageModel model;
    private readonly DispatcherTimer timer;
    private bool disposed;

    private long totalCapacityBytes;
    private long totalFreeBytes;
    private long totalUsedBytes;
    private double totalUsageFraction;
    private int totalUsagePercent;

    public List<DriveItemViewModel> Drives { get; private set; } = [];
    public ObservableCollection<DriveItemViewModel> RingSlots { get; }

    public event Action? DrivesUpdated;

    public StorageViewModel(StorageModel? initialModel = null, IAppSettingsProvider? settingsProvider = null)
    {
        model = initialModel ?? new StorageModel();
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
        timer.Start();

        appSettingsProvider.DataChanged += OnSettingsChanged;

        PollDrives();
    }

    public StorageModel Model => model;

    public string HeaderTitle => Locale.Storage_HeaderTitle;

    public string HeaderSummaryText => totalCapacityBytes <= 0
        ? string.Empty
        : model.TextFormat switch
        {
            StorageTextFormat.UsedAndTotal => $"{StorageService.FormatBytes(totalUsedBytes, compact: true)} / {StorageService.FormatBytes(totalCapacityBytes, compact: true)}",
            StorageTextFormat.Percentage => $"总计 {totalUsagePercent}%",
            _ => $"可用 {StorageService.FormatBytes(totalFreeBytes, compact: true)}"
        };

    public string WideSummaryText => totalCapacityBytes <= 0
        ? string.Empty
        : $"已用 {StorageService.FormatBytes(totalUsedBytes)} / {StorageService.FormatBytes(totalCapacityBytes)} ({totalUsagePercent}%) · 可用 {StorageService.FormatBytes(totalFreeBytes)}";

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

    public void Suspend()
    {
        if (disposed) return;
        timer.Stop();
    }

    public void Resume()
    {
        if (disposed) return;
        PollDrives();
        timer.Start();
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        timer.Stop();
        appSettingsProvider.DataChanged -= OnSettingsChanged;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? propName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propName));
}
