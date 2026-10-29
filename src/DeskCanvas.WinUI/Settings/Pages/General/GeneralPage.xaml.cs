using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using DeskCanvas.Core.Interfaces;
using DeskCanvas.Core.Models.Settings;
using DeskCanvas.Core.Services;
using DeskCanvas.Locales;
using DeskCanvas.Runtime;
using DeskCanvas.Services;
using DeskCanvas.ViewModels;
using DeskCanvas.WinUI.Host;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DeskCanvas.WinUI.Settings.Pages;

/// <summary>
/// 常规页（报告 §3 第二步系统页表第一行）。
/// <para>
/// 对照 <c>src/DeskCanvas/Views/Pages/General.axaml(.cs)</c> 与 <c>ViewModels/GeneralViewModel.cs</c>
/// 逐项迁移：语言、默认地区（搜索/选择/清除）、开机自启、更新周期/代理/连通性/当前状态/手动检查。
/// </para>
/// <para>
/// 生命周期按 <see cref="ISettingsPage"/> 的七条契约：构造只建控件；Attach 里解析依赖、订阅
/// <see cref="IAppSettingsProvider.DataChanged"/>、启动异步；Detach 幂等退订并取消搜索/连通性请求。
/// 设置写入仍使用原 Provider（报告 §4.2），没有新建第二套持久化。
/// </para>
/// </summary>
public sealed partial class GeneralPage : UserControl, ISettingsPage
{
    private const string DefaultRegionOwner = "settings-default-region";
    private const string LatestReleasePageUrl = "https://github.com/Genlue/DeskCanvas/releases/latest";

    private readonly SettingsPageLifetime lifetime = new();
    private readonly ObservableCollection<RegionSearchResult> regionResults = [];
    private readonly ObservableCollection<CultureInfo> languages = [];
    private readonly ObservableCollection<UpdateIntervalOption> updateIntervals = [];

    private IAppSettingsProvider? settingsProvider;
    private ILocaleService? localeService;
    private UpdateService? updateService;

    private CancellationTokenSource? regionSearchCts;
    private CancellationTokenSource? connectivityCts;

    /// <summary>初始化/外部刷新写控件时置位，避免把展示更新当成用户提交（报告 §4.2）。</summary>
    private bool suppressControlEvents;

    private bool searchingRegion;
    private bool testingConnectivity;
    private bool checkingUpdates;

    public GeneralPage()
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
        SmokeLog.Stage("general-attach");

        // 先解析必需依赖：解析失败时页面直接抛给外壳，不会留下半订阅状态。

        settingsProvider = context.GetRequiredService<IAppSettingsProvider>();
        localeService = context.GetRequiredService<ILocaleService>();
        updateService = context.GetRequiredService<UpdateService>();

        ApplyLocalizedText();
        PopulateLanguages();
        PopulateUpdateIntervals();
        ResetTransientState();
        RefreshFromSettings(settingsProvider.Get());

        settingsProvider.DataChanged += OnSettingsChanged;
    }

    private void OnDetachPage()
    {
        SmokeLog.Stage("general-detach");

        if (settingsProvider is not null)
        {
            settingsProvider.DataChanged -= OnSettingsChanged;
        }

        // 取消仍可能在网络上的请求；页面实例随后由外壳丢弃，续体通过 lifetime.IsAttached 拦下。

        regionSearchCts?.Cancel();
        regionSearchCts = null;
        connectivityCts?.Cancel();
        connectivityCts = null;

        searchingRegion = false;
        testingConnectivity = false;
        checkingUpdates = false;

        RunSuppressed(() =>
        {
            RegionResultsCombo.SelectedItem = null;
            RegionResultsCombo.ItemsSource = null;
            regionResults.Clear();
            RegionQueryBox.Text = string.Empty;
            RegionResultRow.Subtitle = string.Empty;
            LanguageCombo.ItemsSource = null;
            UpdateIntervalCombo.ItemsSource = null;
            UpdateNotice.IsOpen = false;
            ErrorNotice.IsOpen = false;
        });

        languages.Clear();
        updateIntervals.Clear();

        settingsProvider = null;
        localeService = null;
        updateService = null;
    }

    // ---- 文案与初始内容 ---------------------------------------------------------------

    /// <summary>
    /// 与旧 <c>General.axaml</c> 一一对应的静态文案。旧的 <c>{x:Static Locale.*}</c> 在 WinUI
    /// 里没有 x:Static 等价物，统一在页面侧取一次并写入控件。

    /// </summary>
    private void ApplyLocalizedText()
    {
        LanguageHeader.Text = Locale.Settings_General_Language ?? string.Empty;
        LanguageRow.Title = Locale.Settings_General_Language_Title ?? string.Empty;
        LanguageRow.Subtitle = Locale.Settings_General_Language_Subtitle ?? string.Empty;

        DefaultRegionHeader.Text = Locale.Settings_General_DefaultRegion ?? string.Empty;
        RegionSearchRow.Title = Locale.Settings_General_DefaultRegion_Title ?? string.Empty;
        RegionQueryBox.PlaceholderText = Locale.Settings_General_DefaultRegion_Placeholder ?? string.Empty;
        RegionSearchButton.Content = Locale.Settings_General_DefaultRegion_Search ?? string.Empty;
        RegionClearButton.Content = Locale.Settings_General_DefaultRegion_Clear ?? string.Empty;
        RegionResultRow.Title = Locale.Settings_General_DefaultRegion_Results_Title ?? string.Empty;

        StartupHeader.Text = Locale.Settings_General_Startup ?? string.Empty;
        StartupRow.Title = Locale.Settings_General_Startup_Title ?? string.Empty;
        StartupRow.Subtitle = Locale.Settings_General_Startup_Subtitle ?? string.Empty;

        UpdateHeader.Text = Locale.Settings_General_Update_Title ?? string.Empty;
        UpdateIntervalRow.Title = Locale.Settings_General_Update_Interval ?? string.Empty;
        UpdateIntervalRow.Subtitle = Locale.Settings_General_Update_Interval_Subtitle ?? string.Empty;
        AutoProxyRow.Title = Locale.Settings_General_Update_AutoProxy ?? string.Empty;
        ConnectivityRow.Title = Locale.Settings_General_Update_Connectivity ?? string.Empty;
        ConnectivityRow.Subtitle = Locale.Settings_General_Update_Connectivity_Subtitle ?? string.Empty;
        ConnectivityButton.Content = Locale.Settings_General_Update_Connectivity_Action ?? string.Empty;
        CheckNowRow.Title = Locale.Settings_General_Update_CheckNow ?? string.Empty;
        CheckNowButton.Content = Locale.Settings_General_Update_CheckNow ?? string.Empty;

        UpdateNotice.Title = Locale.Update_SoftwareUpdate ?? string.Empty;
        UpdateNoticeAction.Content = Locale.Settings_About_Repository_Action ?? string.Empty;
    }

    private void PopulateLanguages()
    {
        languages.Clear();
        foreach (var culture in GeneralViewModel.GetAvailableLanguages())
        {
            languages.Add(culture);
        }

        RunSuppressed(() => LanguageCombo.ItemsSource = languages);
    }

    private void PopulateUpdateIntervals()
    {
        updateIntervals.Clear();
        updateIntervals.Add(new(UpdateCheckInterval.Disabled, Locale.Settings_General_Update_Interval_Disabled ?? string.Empty));
        updateIntervals.Add(new(UpdateCheckInterval.OnStartup, Locale.Settings_General_Update_Interval_OnStartup ?? string.Empty));
        updateIntervals.Add(new(UpdateCheckInterval.Every6Hours, Locale.Settings_General_Update_Interval_Every6Hours ?? string.Empty));
        updateIntervals.Add(new(UpdateCheckInterval.Every12Hours, Locale.Settings_General_Update_Interval_Every12Hours ?? string.Empty));
        updateIntervals.Add(new(UpdateCheckInterval.Daily, Locale.Settings_General_Update_Interval_Daily ?? string.Empty));
        updateIntervals.Add(new(UpdateCheckInterval.Weekly, Locale.Settings_General_Update_Interval_Weekly ?? string.Empty));

        RunSuppressed(() => UpdateIntervalCombo.ItemsSource = updateIntervals);
    }

    /// <summary>每次进入可见状态都从 Provider 重建一次瞬时 UI；页面实例不会复用上次草稿。</summary>
    private void ResetTransientState()
    {
        searchingRegion = false;
        testingConnectivity = false;
        checkingUpdates = false;

        RunSuppressed(() =>
        {
            RegionResultsCombo.ItemsSource = regionResults;
            RegionResultsCombo.SelectedItem = null;
            regionResults.Clear();
            RegionQueryBox.Text = string.Empty;
            RegionResultRow.Subtitle = string.Empty;
            UpdateNotice.IsOpen = false;
            ErrorNotice.IsOpen = false;
        });

        RegionSearchButton.IsEnabled = true;
        ConnectivityButton.IsEnabled = true;
        CheckNowButton.IsEnabled = true;
        ConnectivityRow.Subtitle = Locale.Settings_General_Update_Connectivity_Subtitle ?? string.Empty;
    }

    // ---- 设置读取（唯一事实源） -------------------------------------------------------

    private void OnSettingsChanged(object sender, AppSettings? oldData, AppSettings newData)
    {
        if (!lifetime.IsAttached) return;

        // 生产订阅者（例如 UpdateService 把检查结果写回，报告 §4.2）可能来自后台线程。

        var queue = DispatcherQueue;
        if (queue is null || queue.HasThreadAccess)
        {
            SafeRefresh(newData);
            return;
        }

        queue.TryEnqueue(() =>
        {
            if (lifetime.IsAttached)
            {
                SafeRefresh(newData);
            }
        });
    }

    private void SafeRefresh(AppSettings settings)
    {
        try
        {
            RefreshFromSettings(settings);
        }
        catch (Exception ex)
        {
            // 外部刷新失败不能把设置窗口带下水；保留当前显示即可。

            Debug.WriteLine($"[General] settings refresh failed: {ex}");
        }
    }

    private void RefreshFromSettings(AppSettings settings)
    {
        RunSuppressed(() =>
        {
            SelectLanguage(settings.Region.Language);
            SelectUpdateInterval(settings.UpdateInterval);

            AutoProxyToggle.IsOn = ProxySettings.GetMode(settings.HttpProxy) == ProxyModeKind.Auto;
            AutoProxyRow.Subtitle = FormatAutoProxySubtitle(settings.HttpProxy);

            // 正在检查时保留“Checking…”；检查返回后由点击处理器写入最终结果（与旧 VM 一致）。

            if (!checkingUpdates)
            {
                CheckNowRow.Subtitle = GeneralViewModel.FormatStoredUpdateStatusForDisplay(settings);
            }

            UpdateRegionSummary(settings);

            // 注册表是自启动开关的事实源（旧 VM 的 getter 同样直接读 StartupService）。

            StartupToggle.IsOn = new StartupService().IsEnabled();
        });
    }

    private void SelectLanguage(string? languageName)
    {
        if (string.IsNullOrWhiteSpace(languageName)) return;

        CultureInfo? culture = languages.FirstOrDefault(
            candidate => string.Equals(candidate.Name, languageName, StringComparison.OrdinalIgnoreCase));

        if (culture is null)
        {
            // 手改配置文件写入了未提供的 culture：仍然显示实际生效值，而不是悄悄换成第一项。

            try
            {
                culture = CultureInfo.GetCultureInfo(languageName);
                languages.Add(culture);
            }
            catch (CultureNotFoundException)
            {
                return;
            }
        }

        LanguageCombo.SelectedItem = culture;
    }

    private void SelectUpdateInterval(UpdateCheckInterval interval)
    {
        var option = updateIntervals.FirstOrDefault(candidate => candidate.Value == interval)
                     ?? updateIntervals.FirstOrDefault(candidate => candidate.Value == UpdateCheckInterval.Daily);
        if (option is not null)
        {
            UpdateIntervalCombo.SelectedItem = option;
        }
    }

    // ---- 语言 -------------------------------------------------------------------------

    private void OnLanguageSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (suppressControlEvents || !lifetime.IsAttached || settingsProvider is null) return;
        if (LanguageCombo.SelectedItem is not CultureInfo culture) return;

        var settings = settingsProvider.Get();
        if (string.Equals(settings.Region.Language, culture.Name, StringComparison.Ordinal)) return;

        if (!SaveSettings(settings with { Region = settings.Region with { Language = culture.Name } }))
        {
            RunSuppressed(() => SelectLanguage(settings.Region.Language));
            return;
        }

        // 与旧页一致：保存后由 LocaleService 切换文化（它的 DataChanged 订阅也会做一次，幂等）。

        // 该行带 RestartRequired 提示，已打开的 WinUI 控件不会就地重排。

        localeService?.SetCulture(culture.Name);
    }

    // ---- 默认地区 ---------------------------------------------------------------------

    private void OnSearchRegionClick(object sender, RoutedEventArgs e) => _ = SearchRegionAsync();

    private async Task SearchRegionAsync()
    {
        if (searchingRegion || !lifetime.IsAttached || settingsProvider is null) return;

        var query = (RegionQueryBox.Text ?? string.Empty).Trim();
        if (query.Length == 0) return;

        searchingRegion = true;
        RegionSearchButton.IsEnabled = false;
        RegionResultRow.Subtitle = Locale.Settings_General_DefaultRegion_Searching ?? string.Empty;

        using var cts = new CancellationTokenSource();
        regionSearchCts = cts;

        try
        {
            var language = Thread.CurrentThread.CurrentUICulture.TwoLetterISOLanguageName;
            var request = new OpenMeteoRequest(default, WeatherResource.Geocoding, query, language, 8);
            using var lease = await SharedDataServices.Weather
                .AcquireAsync(DefaultRegionOwner, request, cts.Token)
                .ConfigureAwait(true);

            if (!lifetime.IsAttached || cts.IsCancellationRequested) return;

            var hits = GeneralViewModel.ParseGeocodingResultsForDisplay(lease.Snapshot.Value);

            RunSuppressed(() =>
            {
                regionResults.Clear();
                foreach (var hit in hits)
                {
                    regionResults.Add(hit);
                }

                RegionResultsCombo.SelectedItem = null;
            });

            // 与旧 VM 相同：唯一命中直接采用，避免用户还要再点一次。

            var autoSelected = regionResults.Count == 1 && ApplyDefaultRegion(regionResults[0]);
            if (autoSelected)
            {
                RunSuppressed(() => RegionResultsCombo.SelectedItem = regionResults[0]);
            }

            RegionResultRow.Subtitle = regionResults.Count == 0
                ? Locale.Settings_General_DefaultRegion_NoResult ?? string.Empty
                : string.Format(Locale.Settings_General_DefaultRegion_Results ?? "{0}", regionResults.Count);
        }
        catch (OperationCanceledException)
        {
            // Detach 取消：页面已不可见，不再写任何 UI。

        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[General] default-region search failed: {ex.Message}");
            if (!lifetime.IsAttached || cts.IsCancellationRequested) return;

            RunSuppressed(() =>
            {
                regionResults.Clear();
                RegionResultsCombo.SelectedItem = null;
                RegionResultRow.Subtitle = Locale.Settings_General_DefaultRegion_Failed ?? string.Empty;
            });
        }
        finally
        {
            if (ReferenceEquals(regionSearchCts, cts))
            {
                regionSearchCts = null;
            }

            searchingRegion = false;
            if (lifetime.IsAttached)
            {
                RegionSearchButton.IsEnabled = true;
            }
        }
    }

    private void OnRegionResultSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (suppressControlEvents || !lifetime.IsAttached) return;
        if (RegionResultsCombo.SelectedItem is not RegionSearchResult result) return;

        if (ApplyDefaultRegion(result)) return;

        // 保存失败时把选择退回实际生效值，错误由 SaveSettings 已经显示。

        RunSuppressed(() =>
        {
            RegionResultsCombo.SelectedItem = null;
            if (settingsProvider is not null)
            {
                UpdateRegionSummary(settingsProvider.Get());
            }
        });
    }

    private void OnClearRegionClick(object sender, RoutedEventArgs e)
    {
        if (!lifetime.IsAttached || settingsProvider is null) return;

        var settings = settingsProvider.Get();
        if (settings.DefaultRegion is null) return;

        if (!SaveSettings(settings with { DefaultRegion = null })) return;

        RunSuppressed(() =>
        {
            RegionResultsCombo.SelectedItem = null;
            regionResults.Clear();
            RegionQueryBox.Text = string.Empty;
            RegionResultRow.Subtitle = string.Empty;
        });

        UpdateRegionSummary(settingsProvider.Get());
    }

    private bool ApplyDefaultRegion(RegionSearchResult result)
    {
        if (settingsProvider is null) return false;

        var region = new DefaultRegion(result.Name, result.Latitude, result.Longitude);
        if (!region.IsValid) return false;

        return SaveSettings(settingsProvider.Get() with { DefaultRegion = region.Normalized() });
    }

    private void UpdateRegionSummary(AppSettings settings)
    {
        var region = settings.EffectiveDefaultRegion;
        if (region is null)
        {
            RegionSearchRow.Subtitle = Locale.Settings_General_DefaultRegion_NotSet ?? string.Empty;
            RegionClearButton.Visibility = Visibility.Collapsed;
            return;
        }

        RegionSearchRow.Subtitle = string.Format(
            Locale.Settings_General_DefaultRegion_Current ?? "{0}",
            region.Name,
            region.Latitude,
            region.Longitude);
        RegionClearButton.Visibility = Visibility.Visible;
    }

    // ---- 开机自启动 -------------------------------------------------------------------

    private void OnStartupToggled(object sender, RoutedEventArgs e)
    {
        if (suppressControlEvents || !lifetime.IsAttached || settingsProvider is null) return;

        var desired = StartupToggle.IsOn;
        var startup = new StartupService();

        if (!startup.SetRunOnStartup(desired))
        {
            RunSuppressed(() => StartupToggle.IsOn = startup.IsEnabled());
            ShowError(
                GeneralStrings.StartupChangeFailedTitle,
                GeneralStrings.StartupChangeFailedMessage);
            return;
        }

        // 旧 VM 在注册表写入成功后保存镜像值；注册表仍是 getter 的事实源。

        SaveSettings(settingsProvider.Get() with { RunOnStartup = desired });
    }

    // ---- 更新周期 / 代理 ---------------------------------------------------------------

    private void OnUpdateIntervalSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (suppressControlEvents || !lifetime.IsAttached || settingsProvider is null) return;
        if (UpdateIntervalCombo.SelectedItem is not UpdateIntervalOption option) return;

        var settings = settingsProvider.Get();
        if (settings.UpdateInterval == option.Value) return;

        if (!SaveSettings(settings with { UpdateInterval = option.Value }))
        {
            RunSuppressed(() => SelectUpdateInterval(settings.UpdateInterval));
        }
    }

    private void OnAutoProxyToggled(object sender, RoutedEventArgs e)
    {
        if (suppressControlEvents || !lifetime.IsAttached || settingsProvider is null) return;

        var useAuto = AutoProxyToggle.IsOn;
        var settings = settingsProvider.Get();
        var currentIsAuto = ProxySettings.GetMode(settings.HttpProxy) == ProxyModeKind.Auto;
        if (currentIsAuto == useAuto) return;

        var nextProxy = useAuto ? "auto" : "direct";
        if (!SaveSettings(settings with { HttpProxy = nextProxy }))
        {
            RunSuppressed(() =>
            {
                AutoProxyToggle.IsOn = currentIsAuto;
                AutoProxyRow.Subtitle = FormatAutoProxySubtitle(settings.HttpProxy);
            });
            return;
        }

        RunSuppressed(() => AutoProxyRow.Subtitle = FormatAutoProxySubtitle(nextProxy));
    }

    private static string FormatAutoProxySubtitle(string? rawProxy)
        => ProxySettings.GetMode(rawProxy) switch
        {
            ProxyModeKind.Auto => Locale.Settings_General_Update_AutoProxy_Subtitle_On ?? string.Empty,
            ProxyModeKind.Direct => Locale.Settings_General_Update_AutoProxy_Subtitle_Direct ?? string.Empty,
            ProxyModeKind.System => Locale.Settings_General_Update_AutoProxy_Subtitle_System ?? string.Empty,
            ProxyModeKind.Custom => string.Format(
                Locale.Settings_General_Update_AutoProxy_Subtitle_Custom ?? "{0}", rawProxy),
            _ => Locale.Settings_General_Update_AutoProxy_Subtitle_On ?? string.Empty,
        };

    // ---- 连通性测试 -------------------------------------------------------------------

    private async void OnTestConnectivityClick(object sender, RoutedEventArgs e)
    {
        var service = updateService;
        if (testingConnectivity || !lifetime.IsAttached || service is null) return;

        testingConnectivity = true;
        ConnectivityButton.IsEnabled = false;
        ConnectivityRow.Subtitle = Locale.Settings_General_Update_Connectivity_Testing ?? string.Empty;

        using var cts = new CancellationTokenSource();
        connectivityCts = cts;

        try
        {
            var result = await service.TestGitHubConnectivityAsync(cts.Token).ConfigureAwait(true);
            if (!lifetime.IsAttached || cts.IsCancellationRequested) return;
            ConnectivityRow.Subtitle = result.Summary;
        }
        catch (OperationCanceledException)
        {
            // Detach 取消。

        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[General] connectivity test failed: {ex.Message}");
            if (!lifetime.IsAttached || cts.IsCancellationRequested) return;

            ConnectivityRow.Subtitle = string.Format(
                Locale.Settings_General_Update_Connectivity_Failed ?? "{0}",
                ex.Message,
                0,
                Locale.Settings_General_Update_Route_Direct ?? string.Empty);
        }
        finally
        {
            if (ReferenceEquals(connectivityCts, cts))
            {
                connectivityCts = null;
            }

            testingConnectivity = false;
            if (lifetime.IsAttached)
            {
                ConnectivityButton.IsEnabled = true;
            }
        }
    }

    // ---- 手动检查更新 -----------------------------------------------------------------

    private async void OnCheckForUpdatesClick(object sender, RoutedEventArgs e)
    {
        var service = updateService;
        if (checkingUpdates || !lifetime.IsAttached || service is null || settingsProvider is null) return;

        checkingUpdates = true;
        CheckNowButton.IsEnabled = false;
        CheckNowRow.Subtitle = Locale.Settings_General_Update_Checking ?? string.Empty;

        try
        {
            var (info, result) = await service.CheckForUpdatesDetailedAsync(isManual: true).ConfigureAwait(true);
            if (!lifetime.IsAttached) return;

            var storedStatus = GeneralViewModel.FormatStoredUpdateStatusForDisplay(settingsProvider.Get());

            if (result == UpdateCheckResult.UpdateAvailable && info is not null)
            {
                CheckNowRow.Subtitle = !string.IsNullOrWhiteSpace(storedStatus)
                    ? storedStatus
                    : string.Format(Locale.Settings_General_Update_FoundNew ?? "{0}", info.Version);
                ShowUpdateNotice(info);
            }
            else if (result == UpdateCheckResult.UpToDate)
            {
                UpdateNotice.IsOpen = false;
                CheckNowRow.Subtitle = !string.IsNullOrWhiteSpace(storedStatus)
                    ? storedStatus
                    : string.Format(Locale.Settings_General_Update_AlreadyLatest ?? "{0}", ProductVersionText);
            }
            else
            {
                UpdateNotice.IsOpen = false;
                CheckNowRow.Subtitle = !string.IsNullOrWhiteSpace(storedStatus)
                    ? storedStatus
                    : Locale.Settings_General_Update_CheckFailed ?? string.Empty;
            }
        }
        catch (Exception ex)
        {
            // CheckForUpdatesDetailedAsync 自己会吞掉网络异常；这里兜住意外（例如调度器已失效）。

            Debug.WriteLine($"[General] update check failed: {ex.Message}");
            if (lifetime.IsAttached)
            {
                CheckNowRow.Subtitle = Locale.Settings_General_Update_CheckFailed ?? string.Empty;
            }
        }
        finally
        {
            checkingUpdates = false;
            if (lifetime.IsAttached)
            {
                CheckNowButton.IsEnabled = true;
            }
        }
    }

    private void ShowUpdateNotice(ReleaseInfo info)
    {
        UpdateNotice.Message = string.Format(Locale.Update_UpdateFormat ?? "{0}", info.Version);
        UpdateNotice.Severity = InfoBarSeverity.Informational;
        UpdateNoticeAction.Visibility = Visibility.Visible;
        UpdateNoticeAction.Tag = string.IsNullOrWhiteSpace(info.ReleasePageUrl)
            ? LatestReleasePageUrl
            : info.ReleasePageUrl;
        UpdateNotice.IsOpen = true;
    }

    private void OnOpenUpdatePageClick(object sender, RoutedEventArgs e)
    {
        var target = UpdateNoticeAction.Tag as string ?? LatestReleasePageUrl;
        try
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[General] could not open update page: {ex.Message}");
            ShowError(
                GeneralStrings.OpenLinkFailedTitle,
                string.Format(GeneralStrings.OpenLinkFailedMessage, target));
        }
    }

    // ---- 保存与错误提示 ---------------------------------------------------------------

    /// <summary>
    /// 唯一的设置写入点：失败时保留页面并显示可关闭的错误条，不把“没保存”伪装成成功（报告 §4.2）。

    /// </summary>
    private bool SaveSettings(AppSettings next)
    {
        var provider = settingsProvider;
        if (provider is null) return false;

        try
        {
            provider.Save(next);
            ErrorNotice.IsOpen = false;
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[General] saving settings failed: {ex}");
            ShowError(
                GeneralStrings.SaveFailedTitle,
                string.Format(GeneralStrings.SaveFailedMessage, ex.Message));
            return false;
        }
    }

    private void ShowError(string title, string message)
    {
        ErrorNotice.Title = title;
        ErrorNotice.Message = message;
        ErrorNotice.Severity = InfoBarSeverity.Error;
        ErrorNotice.IsOpen = true;
    }

    private void RunSuppressed(Action action)
    {
        var previous = suppressControlEvents;
        suppressControlEvents = true;
        try
        {
            action();
        }
        finally
        {
            suppressControlEvents = previous;
        }
    }

    /// <summary>产品版本取旧宿主程序集（UpdateService 比较的也是同一个版本号）。</summary>
    private static string ProductVersionText
        => typeof(UpdateService).Assembly.GetName().Version?.ToString() ?? "0.0.0";
}

/// <summary>
/// 自注册：页面键 → 页面工厂。用 <see cref="ModuleInitializerAttribute"/> 在程序集加载时完成，
/// 不需要改共享的 <see cref="SettingsPageFactory"/>（报告 §3 第二步：页面执行者只加自己的文件）。
/// </summary>
internal static class GeneralPageRegistration
{
    [ModuleInitializer]
    internal static void Register()
        => SettingsPageRegistry.Register(SettingsPageKeys.General, _ => new GeneralPage());
}
