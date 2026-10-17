using System.Diagnostics;
using System.Reflection;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using DeskCanvas.Core.Interfaces;
using DeskCanvas.Locales;
using DeskCanvas.Services;

namespace DeskCanvas.Views.Pages;

public partial class About : UserControl
{
    private readonly IAppSettingsProvider appSettingsProvider;
    public string? Version => Assembly.GetExecutingAssembly().GetName().Version?.ToString();

    private static bool IsZh => System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.Equals(
        "zh", System.StringComparison.OrdinalIgnoreCase);

    public string TipsHeader => IsZh ? "快捷操作与技巧" : "Shortcuts & Tips";
    public string QuickMoveTitle => IsZh ? "快捷移动小组件" : "Quick Move Widget";
    public string QuickMoveSubtitle => IsZh
        ? "按住 Ctrl 键并使用鼠标左键拖动，可在任何组件（包括地图、便签、文件夹等可交互组件）上直接移动窗口"
        : "Hold Ctrl and left-drag to move any widget window directly (including interactive widgets like Map, Notes, and Folders)";
    public string QuickMoveShortcut => IsZh ? "Ctrl + 鼠标左键拖动" : "Ctrl + Left Drag";
    public string SwitchStackTitle => IsZh ? "切换重叠组件" : "Switch Stacked Widgets";
    public string SwitchStackSubtitle => IsZh
        ? "在重叠组件上按住鼠标中键并滚动滚轮，即可在多个子组件间循环切换，无需对准右侧小圆点"
        : "Hold the middle mouse button and scroll the wheel over a Widget Stack to cycle through stacked widgets without aiming at the side dots";
    public string SwitchStackShortcut => IsZh ? "鼠标中键 + 滚轮" : "Middle Click + Scroll";
    
    public About(IAppSettingsProvider appSettingsProvider)
    {
        this.appSettingsProvider = appSettingsProvider;
        DataContext = this;
        _ = CheckForUpdates();
        InitializeComponent();
    }
    
    private async Task CheckForUpdates()
    {
        var version = await new UpdateService(appSettingsProvider).GetUpdateVersionAsync();
        if (version == null) return;
        
        Update.IsVisible = true;
        Update.Subtitle = string.Format(Locale.Update_UpdateFormat, version);
    }
    
    private async void GoToUpdate(object? sender, RoutedEventArgs e)
    {
        var svc = new UpdateService(appSettingsProvider);
        var (info, result) = await svc.CheckForUpdatesDetailedAsync(isManual: true);
        if (result == UpdateCheckResult.UpdateAvailable && info != null)
        {
            new UpdatePopup(info, svc).Show();
        }
        else
        {
            Process.Start(
                new ProcessStartInfo("https://github.com/Genlue/DeskCanvas/releases/latest") { UseShellExecute = true });
        }
    }

    private void GoToMyRepository(object? sender, RoutedEventArgs e)
    {
        Process.Start(
            new ProcessStartInfo("https://github.com/Genlue/DeskCanvas") { UseShellExecute = true });
    }

    private void GoToRepository(object? sender, RoutedEventArgs e)
    {
        Process.Start(
            new ProcessStartInfo("https://www.github.com/creewick/uWidgets") { UseShellExecute = true });
    }

    private void GoToIssues(object? sender, RoutedEventArgs e)
    {
        Process.Start(
            new ProcessStartInfo("https://github.com/creewick/uWidgets/issues") { UseShellExecute = true });
    }

    private void OnUninstallClicked(object? sender, RoutedEventArgs e)
    {
        // 1. Check for standard Inno Setup uninstaller (unins000.exe)
        var uninsPath = System.IO.Path.Combine(DeskCanvas.Core.Const.CurrentFolder, "unins000.exe");
        if (System.IO.File.Exists(uninsPath))
        {
            try
            {
                Process.Start(new ProcessStartInfo(uninsPath) { UseShellExecute = true });
                return;
            }
            catch { }
        }

        // 2. Check for standalone uninstaller fallback if present
        var customUninstaller = System.IO.Path.Combine(DeskCanvas.Core.Const.CurrentFolder, "DeskCanvasUninstaller.exe");
        if (System.IO.File.Exists(customUninstaller))
        {
            try
            {
                Process.Start(new ProcessStartInfo(customUninstaller) { UseShellExecute = true });
                return;
            }
            catch { }
        }

        // 3. Fallback: open Windows Apps & Features
        try
        {
            Process.Start(new ProcessStartInfo("ms-settings:appsfeatures") { UseShellExecute = true });
        }
        catch { }
    }
}