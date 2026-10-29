using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using DeskCanvas.Core;
using DeskCanvas.Core.Services;
using DeskCanvas.Locales;
using DeskCanvas.Services;
using DeskCanvas.WinUI.Host;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DeskCanvas.WinUI.Settings.Pages;

/// <summary>
/// 关于页（报告 §3 第二步系统页表最后一行）。
/// <para>
/// 对照 <c>src/DeskCanvas/Views/Pages/About.axaml(.cs)</c>：版本号、更新入口、我的/原作者仓库、
/// 原作者问题反馈、卸载入口，以及旧页的「快捷操作与技巧」。
/// </para>
/// <para>
/// 更新只通过原 <see cref="UpdateService"/> 取数据；旧实现直接 <c>new UpdatePopup(...)</c>，
/// 迁移期改成页内 InfoBar + 发布页链接（统一更新通知窗口属于后续 S4.3，本轮不伪造）。
/// 卸载沿用旧入口：启动安装器自带的 Inno Setup <c>unins000.exe</c>，失焦回退到系统
/// 「应用和功能」；不自行删除注册表/文件。
/// </para>
/// </summary>
public sealed partial class AboutPage : UserControl, ISettingsPage
{
    private const string ProductMetadata = "DeskCanvas · fork of creewick/uWidgets";
    private const string LatestReleasePageUrl = "https://github.com/Genlue/DeskCanvas/releases/latest";
    private const string MyRepositoryUrl = "https://github.com/Genlue/DeskCanvas";
    private const string OriginalRepositoryUrl = "https://www.github.com/creewick/uWidgets";
    private const string OriginalIssuesUrl = "https://github.com/creewick/uWidgets/issues";

    private readonly SettingsPageLifetime lifetime = new();

    private UpdateService? updateService;

    /// <summary>每次 Attach/Detach 递增；晚到的更新检查结果据此丢弃，不再触碰已卸下的页面。</summary>
    private int updateCheckGeneration;

    public AboutPage()
    {
        InitializeComponent();
    }

    /// <inheritdoc />
    public UIElement View => this;

    /// <inheritdoc />
    public void Attach(SettingsPageContext context) => lifetime.Attach(context, OnAttachPage);

    /// <inheritdoc />
    public void Detach() => lifetime.Detach(OnDetachPage);

    // ---- 生命周期 ---------------------------------------------------------------------

    private void OnAttachPage(SettingsPageContext context)
    {
        SmokeLog.Stage("about-attach");

        updateService = context.GetRequiredService<UpdateService>();

        ApplyLocalizedText();

        VersionText.Text = ProductVersionText;
        StatusNotice.IsOpen = false;
        StatusNoticeAction.Tag = null;
        StatusNoticeAction.Visibility = Visibility.Collapsed;
        UpdateRow.Visibility = Visibility.Collapsed;
        UpdateDivider.Visibility = Visibility.Collapsed;
        UpdateButton.IsEnabled = true;

        // 旧 About 构造时也会顺手查一次“有没有新版本”，只在命中时显示更新行。

        // Attach 是唯一允许发起异步的位置（ISettingsPage 契约 2）。

        var generation = ++updateCheckGeneration;
        _ = CheckForAvailableUpdateAsync(generation);
    }

    private void OnDetachPage()
    {
        SmokeLog.Stage("about-detach");

        // 关闭/离页后晚到的检查结果不能再改 UI；GetUpdateVersionAsync 没有取消参数，
        // 只能取消“采纳结果”这一步（该方法内部的设置状态写入与旧页面行为一致）。

        updateCheckGeneration++;
        updateService = null;

        StatusNotice.IsOpen = false;
        StatusNoticeAction.Tag = null;
        UpdateButton.IsEnabled = true;
    }

    // ---- 文案 -------------------------------------------------------------------------

    private void ApplyLocalizedText()
    {
        VersionRow.Title = Locale.Settings_About_Version ?? string.Empty;
        VersionRow.Subtitle = ProductMetadata;

        UpdateRow.Title = Locale.Update_SoftwareUpdate ?? string.Empty;
        UpdateButton.Content = Locale.Settings_About_Repository_Action ?? string.Empty;

        TipsHeader.Text = AboutStrings.TipsHeader;
        QuickMoveRow.Title = AboutStrings.QuickMoveTitle;
        QuickMoveRow.Subtitle = AboutStrings.QuickMoveSubtitle;
        QuickMoveShortcutText.Text = AboutStrings.QuickMoveShortcut;
        SwitchStackRow.Title = AboutStrings.SwitchStackTitle;
        SwitchStackRow.Subtitle = AboutStrings.SwitchStackSubtitle;
        SwitchStackShortcutText.Text = AboutStrings.SwitchStackShortcut;

        ActionsHeader.Text = Locale.Settings_About_Actions ?? string.Empty;

        MyRepositoryRow.Title = Locale.Settings_About_MyRepository ?? string.Empty;
        MyRepositoryRow.Subtitle = "Genlue/DeskCanvas";
        MyRepositoryButton.Content = Locale.Settings_About_Repository_Action ?? string.Empty;

        OriginalRepositoryRow.Title = Locale.Settings_About_OriginalRepository ?? string.Empty;
        OriginalRepositoryRow.Subtitle = "creewick/uWidgets";
        OriginalRepositoryButton.Content = Locale.Settings_About_Repository_Action ?? string.Empty;

        ReportBugRow.Title = Locale.Settings_About_OriginalReportBug ?? string.Empty;
        ReportBugRow.Subtitle = "creewick/uWidgets/issues";
        ReportBugButton.Content = Locale.Settings_About_Repository_Action ?? string.Empty;

        UninstallRow.Title = Locale.Settings_About_Uninstall_Title ?? string.Empty;
        UninstallRow.Subtitle = Locale.Settings_About_Uninstall_Subtitle ?? string.Empty;
        UninstallButton.Content = Locale.Settings_About_Uninstall_Action ?? string.Empty;

        StatusNoticeAction.Content = Locale.Settings_About_Repository_Action ?? string.Empty;
    }

    // ---- 更新 -------------------------------------------------------------------------

    private async Task CheckForAvailableUpdateAsync(int generation)
    {
        var service = updateService;
        if (service is null) return;

        try
        {
            var version = await service.GetUpdateVersionAsync().ConfigureAwait(true);
            if (version is null || generation != updateCheckGeneration || !lifetime.IsAttached) return;

            UpdateRow.Subtitle = string.Format(Locale.Update_UpdateFormat ?? "{0}", version);
            UpdateDivider.Visibility = Visibility.Visible;
            UpdateRow.Visibility = Visibility.Visible;
        }
        catch (Exception ex)
        {
            // 自动探测失败不打扰用户：手动更新入口仍在，旧页面同样静默。

            Debug.WriteLine($"[About] startup update probe failed: {ex.Message}");
        }
    }

    private async void OnUpdateButtonClick(object sender, RoutedEventArgs e)
    {
        var service = updateService;
        if (!lifetime.IsAttached || service is null) return;

        UpdateButton.IsEnabled = false;
        try
        {
            var (info, result) = await service.CheckForUpdatesDetailedAsync(isManual: true).ConfigureAwait(true);
            if (!lifetime.IsAttached) return;

            if (result == UpdateCheckResult.UpdateAvailable && info is not null)
            {
                var releaseUrl = string.IsNullOrWhiteSpace(info.ReleasePageUrl)
                    ? LatestReleasePageUrl
                    : info.ReleasePageUrl;

                UpdateRow.Subtitle = string.Format(Locale.Update_UpdateFormat ?? "{0}", info.Version);
                UpdateDivider.Visibility = Visibility.Visible;
                UpdateRow.Visibility = Visibility.Visible;
                ShowNotice(
                    InfoBarSeverity.Informational,
                    Locale.Update_SoftwareUpdate ?? string.Empty,
                    string.Format(Locale.Update_UpdateFormat ?? "{0}", info.Version),
                    releaseUrl);
            }
            else
            {
                // 与旧页一致：没有更新（或检查失败）时直接打开发布页。

                OpenUrl(LatestReleasePageUrl);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[About] update check failed: {ex.Message}");
            if (lifetime.IsAttached)
            {
                OpenUrl(LatestReleasePageUrl);
            }
        }
        finally
        {
            if (lifetime.IsAttached)
            {
                UpdateButton.IsEnabled = true;
            }
        }
    }

    // ---- 仓库 / 问题链接 ---------------------------------------------------------------

    private void OnMyRepositoryClick(object sender, RoutedEventArgs e) => OpenUrl(MyRepositoryUrl);

    private void OnOriginalRepositoryClick(object sender, RoutedEventArgs e) => OpenUrl(OriginalRepositoryUrl);

    private void OnReportBugClick(object sender, RoutedEventArgs e) => OpenUrl(OriginalIssuesUrl);

    // ---- 卸载 -------------------------------------------------------------------------

    /// <summary>
    /// 复用安装器的卸载流程：先找当前安装目录下的 Inno Setup uninstaller，找不到再打不开系统
    /// 「应用和功能」。目录解析走 <see cref="UninstallerEngine.ResolveInstallDirectory"/>，
    /// 但**不**调用 <see cref="UninstallerEngine.Execute"/>——那是独立卸载引擎，会在运行中删除
    /// 安装目录，也不包含安装器自己的「保留用户数据」确认，不是旧 About 的行为。

    /// </summary>
    private void OnUninstallClick(object sender, RoutedEventArgs e)
    {
        var installDirectory = UninstallerEngine.ResolveInstallDirectory(Const.CurrentFolder);
        var uninstaller = Path.Combine(installDirectory, "unins000.exe");

        if (File.Exists(uninstaller) && TryStart(uninstaller))
        {
            return;
        }

        const string windowsAppsFeatures = "ms-settings:appsfeatures";
        if (TryStart(windowsAppsFeatures))
        {
            return;
        }

        ShowNotice(
            InfoBarSeverity.Error,
            AboutStrings.OpenLinkFailedTitle,
            string.Format(AboutStrings.OpenLinkFailedMessage, uninstaller),
            null);
    }

    private static bool TryStart(string fileName)
    {
        try
        {
            Process.Start(new ProcessStartInfo(fileName) { UseShellExecute = true });
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[About] could not start '{fileName}': {ex.Message}");
            return false;
        }
    }

    // ---- 链接与提示 -------------------------------------------------------------------

    private void OpenUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;

        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[About] could not open '{url}': {ex.Message}");
            if (!lifetime.IsAttached) return;

            ShowNotice(
                InfoBarSeverity.Error,
                AboutStrings.OpenLinkFailedTitle,
                string.Format(AboutStrings.OpenLinkFailedMessage, url),
                null);
        }
    }

    private void ShowNotice(InfoBarSeverity severity, string title, string message, string? actionUrl)
    {
        StatusNotice.Severity = severity;
        StatusNotice.Title = title;
        StatusNotice.Message = message;
        StatusNoticeAction.Tag = actionUrl;
        StatusNoticeAction.Visibility = actionUrl is null ? Visibility.Collapsed : Visibility.Visible;
        StatusNotice.IsOpen = true;
    }

    private void OnStatusNoticeActionClick(object sender, RoutedEventArgs e)
    {
        if (StatusNoticeAction.Tag is string url)
        {
            OpenUrl(url);
        }
    }

    /// <summary>旧 About 显示的是宿主程序集版本（UpdateService 比较的也是它）。</summary>
    private static string ProductVersionText
        => typeof(UpdateService).Assembly.GetName().Version?.ToString() ?? "0.0.0";
}

/// <summary>自注册：页面键 → 页面工厂（不改共享设置工厂）。</summary>
internal static class AboutPageRegistration
{
    [ModuleInitializer]
    internal static void Register()
        => SettingsPageRegistry.Register(SettingsPageKeys.About, _ => new AboutPage());
}
