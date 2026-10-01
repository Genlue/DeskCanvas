using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using DeskCanvas.Locales;
using DeskCanvas.Services;

namespace DeskCanvas.Views;

public partial class UpdatePopup : Window, INotifyPropertyChanged
{
    private readonly ReleaseInfo releaseInfo;
    private readonly UpdateService updateService;
    private CancellationTokenSource? downloadCts;
    private string? downloadedFilePath;

    public new event PropertyChangedEventHandler? PropertyChanged;

    public UpdatePopup(ReleaseInfo releaseInfo, UpdateService updateService)
    {
        this.releaseInfo = releaseInfo;
        this.updateService = updateService;
        DataContext = this;
        InitializeComponent();
    }

    public UpdatePopup(Version version, UpdateService updateService)
        : this(new ReleaseInfo(version, $"v{version}", $"DeskCanvas {version}", "", "https://github.com/Genlue/DeskCanvas/releases/latest", null, null, 0), updateService)
    {
    }

    public string UpdateText => string.Format(Locale.Update_UpdateFormat, releaseInfo.Version);
    public string Changelog => releaseInfo.Changelog?.Trim() ?? "";
    public bool HasChangelog => !string.IsNullOrWhiteSpace(Changelog);

    public string DownloadButtonText => string.IsNullOrWhiteSpace(releaseInfo.DownloadUrl)
        ? Locale.Update_Download
        : Locale.Update_Download;

    private bool isDownloading;
    public bool IsDownloading
    {
        get => isDownloading;
        set { isDownloading = value; OnPropertyChanged(); UpdateUIStates(); }
    }

    private bool isCompleted;
    public bool IsCompleted
    {
        get => isCompleted;
        set { isCompleted = value; OnPropertyChanged(); UpdateUIStates(); }
    }

    private bool hasError;
    public bool HasError
    {
        get => hasError;
        set { hasError = value; OnPropertyChanged(); UpdateUIStates(); }
    }

    private double downloadPercentage;
    public double DownloadPercentage
    {
        get => downloadPercentage;
        set { downloadPercentage = value; OnPropertyChanged(); }
    }

    private bool isProgressIndeterminate = true;
    public bool IsProgressIndeterminate
    {
        get => isProgressIndeterminate;
        set { isProgressIndeterminate = value; OnPropertyChanged(); }
    }

    private string statusText = "";
    public string StatusText
    {
        get => statusText;
        set { statusText = value; OnPropertyChanged(); }
    }

    public bool ShowNormalButtons => !IsDownloading && !IsCompleted && !HasError;
    public bool ShowProgressArea => IsDownloading || IsCompleted || HasError;

    private void UpdateUIStates()
    {
        OnPropertyChanged(nameof(ShowNormalButtons));
        OnPropertyChanged(nameof(ShowProgressArea));
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    private async void DownloadUpdate(object? sender, RoutedEventArgs e)
    {
        // If no direct installer asset is attached to GitHub release, open release page
        if (string.IsNullOrWhiteSpace(releaseInfo.DownloadUrl))
        {
            Process.Start(new ProcessStartInfo(releaseInfo.ReleasePageUrl) { UseShellExecute = true });
            Close();
            return;
        }

        HasError = false;
        IsCompleted = false;
        IsDownloading = true;
        DownloadPercentage = 0;
        IsProgressIndeterminate = true;
        StatusText = "正在准备下载安装包...";

        downloadCts = new CancellationTokenSource();

        var progress = new Progress<(long downloaded, long total, double percentage)>(p =>
        {
            if (p.percentage >= 0)
            {
                DownloadPercentage = p.percentage;
                IsProgressIndeterminate = false;
                StatusText = string.Format(Locale.Update_DownloadingFormat,
                    p.downloaded / 1048576.0,
                    p.total / 1048576.0,
                    p.percentage);
            }
            else
            {
                IsProgressIndeterminate = true;
                StatusText = $"正在下载... {p.downloaded / 1048576.0:0.0} MB";
            }
        });

        try
        {
            downloadedFilePath = await updateService.DownloadInstallerAsync(releaseInfo, progress, downloadCts.Token);
            IsDownloading = false;
            IsCompleted = true;
            StatusText = Locale.Update_Installing;

            // Wait briefly then launch installer and exit app
            await Task.Delay(500);
            updateService.LaunchInstallerAndExit(downloadedFilePath);
        }
        catch (OperationCanceledException)
        {
            IsDownloading = false;
            StatusText = "";
        }
        catch (Exception ex)
        {
            IsDownloading = false;
            HasError = true;
            StatusText = string.Format(Locale.Update_DownloadFailed, ex.Message);
        }
    }

    private void InstallNow(object? sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(downloadedFilePath) && File.Exists(downloadedFilePath))
        {
            updateService.LaunchInstallerAndExit(downloadedFilePath);
        }
    }

    private void CancelDownload(object? sender, RoutedEventArgs e)
    {
        downloadCts?.Cancel();
        IsDownloading = false;
        StatusText = "";
    }

    private void OpenInBrowser(object? sender, RoutedEventArgs e)
    {
        Process.Start(new ProcessStartInfo(releaseInfo.ReleasePageUrl) { UseShellExecute = true });
        Close();
    }

    private void Later(object? sender, RoutedEventArgs e)
    {
        downloadCts?.Cancel();
        Close();
    }

    private void SkipThisVersion(object? sender, RoutedEventArgs e)
    {
        downloadCts?.Cancel();
        updateService.SkipVersion(releaseInfo.Version);
        Close();
    }
}