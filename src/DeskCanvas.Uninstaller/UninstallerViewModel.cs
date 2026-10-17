using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia.Threading;
using DeskCanvas.Core.Services;

namespace DeskCanvas.Uninstaller;

public sealed class UninstallerViewModel : INotifyPropertyChanged
{
    private readonly UninstallerOptions initialOptions;
    private readonly string? shadowDir;

    private string title = "DeskCanvas 卸载向导";
    private string subtitle = "准备从这台电脑中彻底卸载 DeskCanvas。";
    private string installDirectoryLabel = "程序安装位置：";
    private string installDirectory;
    private string dataDirectoryLabel = "用户数据位置：";
    private string dataDirectory;
    private bool removeData = false; // Default unchecked per user directive!
    private string removeDataCheckboxText = "同时删除用户数据与配置（包含所有 Profile 与小组件数据）";
    private string removeDataNote = "默认保留您的配置。若需全新重新安装，请勾选此项。";
    private string statusText = "准备就绪。点击下方按钮开始卸载。";
    private int progressValue = 0;
    private bool isInProgress = false;
    private bool isCompleted = false;
    private string actionButtonText = "开始卸载";
    private string cancelButtonText = "取消";

    public event PropertyChangedEventHandler? PropertyChanged;
    public event Action? RequestClose;

    public UninstallerViewModel(UninstallerOptions? options = null, string? shadowDir = null)
    {
        this.shadowDir = shadowDir;
        var resolvedInstall = options?.InstallDirectory ?? UninstallerEngine.ResolveInstallDirectory();
        var resolvedData = options?.DataDirectory ?? UninstallerEngine.ResolveDataDirectory(resolvedInstall);

        this.initialOptions = options ?? new UninstallerOptions(
            InstallDirectory: resolvedInstall,
            DataDirectory: resolvedData,
            RemoveData: false);

        this.installDirectory = initialOptions.InstallDirectory;
        this.dataDirectory = initialOptions.DataDirectory;
        this.removeData = initialOptions.RemoveData;
    }

    public string Title
    {
        get => title;
        set => SetField(ref title, value);
    }

    public string Subtitle
    {
        get => subtitle;
        set => SetField(ref subtitle, value);
    }

    public string InstallDirectoryLabel
    {
        get => installDirectoryLabel;
        set => SetField(ref installDirectoryLabel, value);
    }

    public string InstallDirectory
    {
        get => installDirectory;
        set => SetField(ref installDirectory, value);
    }

    public string DataDirectoryLabel
    {
        get => dataDirectoryLabel;
        set => SetField(ref dataDirectoryLabel, value);
    }

    public string DataDirectory
    {
        get => dataDirectory;
        set => SetField(ref dataDirectory, value);
    }

    /// <summary>
    /// Whether to delete the user data directory during uninstallation. Default is false.
    /// </summary>
    public bool RemoveData
    {
        get => removeData;
        set => SetField(ref removeData, value);
    }

    public string RemoveDataCheckboxText
    {
        get => removeDataCheckboxText;
        set => SetField(ref removeDataCheckboxText, value);
    }

    public string RemoveDataNote
    {
        get => removeDataNote;
        set => SetField(ref removeDataNote, value);
    }

    public string StatusText
    {
        get => statusText;
        set => SetField(ref statusText, value);
    }

    public int ProgressValue
    {
        get => progressValue;
        set => SetField(ref progressValue, value);
    }

    public bool IsInProgress
    {
        get => isInProgress;
        private set
        {
            if (SetField(ref isInProgress, value))
            {
                OnPropertyChanged(nameof(CanChangeOptions));
                OnPropertyChanged(nameof(CanCancel));
            }
        }
    }

    public bool IsCompleted
    {
        get => isCompleted;
        private set
        {
            if (SetField(ref isCompleted, value))
            {
                OnPropertyChanged(nameof(CanChangeOptions));
                OnPropertyChanged(nameof(CanCancel));
            }
        }
    }

    public bool CanChangeOptions => !IsInProgress && !IsCompleted;

    public bool CanCancel => !IsInProgress && !IsCompleted;

    public string ActionButtonText
    {
        get => actionButtonText;
        set => SetField(ref actionButtonText, value);
    }

    public string CancelButtonText
    {
        get => cancelButtonText;
        set => SetField(ref cancelButtonText, value);
    }

    public void OnActionClicked()
    {
        if (IsCompleted)
        {
            RequestClose?.Invoke();
            return;
        }

        if (IsInProgress) return;

        IsInProgress = true;
        StatusText = "正在准备卸载...";
        ProgressValue = 5;

        var options = new UninstallerOptions(
            InstallDirectory: InstallDirectory,
            DataDirectory: DataDirectory,
            RemoveData: RemoveData);

        var progressReporter = new Progress<UninstallerProgress>(p =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                StatusText = p.Status;
                ProgressValue = p.Percentage;
            });
        });

        Task.Run(() =>
        {
            var result = UninstallerEngine.Execute(options, progressReporter);

            Dispatcher.UIThread.Post(() =>
            {
                IsInProgress = false;
                IsCompleted = true;
                ActionButtonText = "完成并关闭";

                if (result.Success)
                {
                    StatusText = RemoveData
                        ? "DeskCanvas 及全部用户数据已成功卸载。"
                        : "DeskCanvas 已成功卸载（用户数据与配置已保留）。";
                    ProgressValue = 100;
                }
                else
                {
                    StatusText = $"卸载遇到问题: {string.Join("; ", result.Errors)}";
                }
            });
        });
    }

    public void OnCancelClicked()
    {
        RequestClose?.Invoke();
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }
}
