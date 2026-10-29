using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using DeskCanvas.Core.Interfaces;
using DeskCanvas.Core.Models.Settings;
using DeskCanvas.Locales;
using DeskCanvas.Services;
using DeskCanvas.ViewModels;
using DeskCanvas.WinUI.Controls;
using DeskCanvas.WinUI.Host;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace DeskCanvas.WinUI.Settings.Pages.Appearance;

/// <summary>
/// 系统设置 → 外观（旧 <c>Views/Pages/Appearance.axaml</c> + <c>ViewModels/AppearanceViewModel</c> 的
/// WinUI 版，报告 §3 第二步系统设置表第二行）。
/// <para>
/// 状态与提交语义完全复用旧 <see cref="AppearanceViewModel"/>：材质/明暗/强调色/单色/背景色由它写
/// <see cref="IAppSettingsProvider"/>；不透明度、玻璃参数、高光、阴影强度、强调色偏移仍走
/// <c>AppearanceSettingsSession</c> 的 draft（拖动只 Stage）→ <c>ValueCommitted</c> 才 Commit
/// （报告 §4.2）。本页只负责把 WinUI 控件接上这些成员，不另建一套保存逻辑。
/// </para>
/// <para>
/// 与旧页面的差异（已在回报中登记）：
/// <list type="bullet">
/// <item><description>材质样片换成 <see cref="ThemeButton"/>（旧实现会解码壁纸、画液态玻璃预览）；
/// 真实组件预览要等 S5 的 <c>AvaloniaSwapChainPanel</c>，本轮不接。</description></item>
/// <item><description>壁纸对齐窗口属弹窗批次（S4.3），按钮给出明确的未实现提示而不是假装打开。</description></item>
/// <item><description>进程白名单的挑选对话框由本页临时实现（复用 <see cref="FocusWatchService.EnumerateWindowedProcesses"/>），
/// S4.3 的统一 ProcessPicker 落地后应替换。</description></item>
/// <item><description>WinUI 设置窗口目前只有原生标题栏，红绿灯样式只保存设置，由后续窗口外观批次呈现。</description></item>
/// </list>
/// </para>
/// </summary>
public sealed partial class AppearancePage : UserControl, ISettingsPage, IDisposable
{
    /// <summary>Attach/Detach 对称状态机（重复 Attach 只跑一次、Detach 幂等）。</summary>
    private readonly SettingsPageLifetime lifetime = new();

    /// <summary>材质样片（页面自己持有；控件不订阅设置）。</summary>
    private readonly List<ThemeButton> themeCards = [];

    private IAppSettingsProvider? provider;
    private FocusWatchService? focusWatch;
    private AppearanceViewModel? viewModel;

    // 下拉框的稳定项列表（VM 的 DarkModes / TitleBarStyles 每次都新建数组，先取一份保证 SelectedItem 命中同一个实例）
    private DarkModeViewModel[] darkModes = [];
    private FocusSurfaceViewModel[] focusSurfaceOptions = [];
    private AccentColorViewModel[] accentItems = [];
    private MonochromeVariantViewModel[] monochromeVariants = [];
    private TitleBarStyleViewModel[] titleBarStyles = [];
    private TitleBarSizeViewModel[] titleBarSizes = [];

    /// <summary>刷新期间为 true：程序化写控件不能被当成用户提交（否则打开页面就会写盘）。</summary>
    private bool syncing;

    private bool refreshQueued;
    private bool attached;

    public AppearancePage()
    {
        InitializeComponent();
        BuildThemeCards();
        ApplyLocalizedTexts();
    }

    /// <inheritdoc />
    public UIElement View => this;

    /// <inheritdoc />
    public void Attach(SettingsPageContext context) => lifetime.Attach(context, OnAttachPage);

    /// <inheritdoc />
    public void Detach() => lifetime.Detach(OnDetachPage);

    /// <summary>外壳在 Detach 之后还会调用 Dispose（如果页面实现了）；重复 Detach 是安全的。</summary>
    public void Dispose() => Detach();

    // ---------------------------------------------------------------------------------------
    // 生命周期
    // ---------------------------------------------------------------------------------------

    private void OnAttachPage(SettingsPageContext context)
    {
        provider = context.GetRequiredService<IAppSettingsProvider>();
        focusWatch = context.GetService<FocusWatchService>();

        // 旧 VM 的构造会读出全部外观状态并在 Provider 变化时广播属性通知；它构造时挂上的两个订阅
        // 由 Dispose 解除（见下面的 Detach），页面重建不会叠加处理器。

        viewModel = new AppearanceViewModel(provider, focusWatch, createThemeSamples: false);
        viewModel.PropertyChanged += OnViewModelPropertyChanged;
        provider.DataChanged += OnProviderDataChanged;

        attached = true;
        DataContext = viewModel;

        CaptureOptionLists();
        ApplyOptionItemsSources();
        RefreshFontItems();
        RefreshView();
        UpdateSaveError();

        SmokeLog.Stage("appearance-attach");
    }

    private void OnDetachPage()
    {
        attached = false;
        SmokeLog.Stage("appearance-detach");

        if (viewModel is not null) viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        if (provider is not null) provider.DataChanged -= OnProviderDataChanged;

        // 先断 DataContext 再 Dispose：绑定不再持有 VM，VM 再解除对 Provider/失焦监视器的订阅。

        DataContext = null;
        viewModel?.Dispose();
        viewModel = null;
        provider = null;
        focusWatch = null;

        // 只属于可见期的动态内容（白名单条目、字体项）在这里放掉；页面实例本身由外壳丢弃。

        WhitelistChipsPanel.Children.Clear();
        FontCombo.ItemsSource = null;
        UpdateSaveError();
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AppearanceViewModel.SettingsError) or nameof(AppearanceViewModel.HasSettingsError))
        {
            UpdateSaveError();
        }
    }

    /// <summary>
    /// Provider 变化（本页保存、别的页面/组件保存、方案切换）后的统一刷新。

    /// 合并同一轮里的多次保存，避免一次提交刷新多次；离页后不再执行。

    /// </summary>
    private void OnProviderDataChanged(object? sender, AppSettings? oldData, AppSettings newData)
    {
        if (!attached) return;
        if (refreshQueued) return;

        refreshQueued = true;
        var queue = DispatcherQueue;
        if (queue is null)
        {
            refreshQueued = false;
            RefreshView();
            return;
        }

        var queued = queue.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            refreshQueued = false;
            if (!attached) return;
            RefreshView();
        });

        // 队列不可用（窗口正在销毁）时同步刷新一次，别把「已排队」标志永久留下。

        if (!queued)
        {
            refreshQueued = false;
            if (attached) RefreshView();
        }
    }

    // ---------------------------------------------------------------------------------------
    // 视图刷新（唯一把设置写回控件的地方）
    // ---------------------------------------------------------------------------------------

    private void RefreshView()
    {
        var vm = viewModel;
        var settingsProvider = provider;
        if (vm is null || settingsProvider is null) return;

        var settings = settingsProvider.Get();
        var theme = settings.Theme;

        syncing = true;
        try
        {
            // ---- 失焦自动切换 ----
            var focusEnabled = vm.FocusLinkEnabled;
            SetVisible(FocusFocusedRow, focusEnabled);
            SetVisible(FocusUnfocusedRow, focusEnabled);
            SetVisible(FocusColorPolicyHintText, focusEnabled);
            SetVisible(FocusDelayRow, focusEnabled);
            SetVisible(FocusWhitelistRow, focusEnabled);

            FocusLinkEnabledToggle.IsOn = focusEnabled;
            FocusDelayBox.Value = vm.FocusSwitchDelayMs;

            SelectItem(FocusModeFocusedCombo, vm.FocusModeFocused);
            SelectItem(FocusSurfaceFocusedCombo, vm.FocusSurfaceFocused);
            SelectItem(FocusModeUnfocusedCombo, vm.FocusModeUnfocused);
            SelectItem(FocusSurfaceUnfocusedCombo, vm.FocusSurfaceUnfocused);

            RebuildWhitelistChips(vm);

            // ---- 材质样片与当前材质 ----
            foreach (var card in themeCards)
            {
                card.IsSelected = card.Surface == theme.EffectiveSurface;
                card.IsDarkPreview = theme.DarkMode ?? (ActualTheme == ElementTheme.Dark);
            }

            CurrentMaterialText.Text = string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                AppearancePageStrings.CurrentMaterialFormat,
                SurfaceNames.Of(theme.EffectiveSurface));

            // ---- 高光 / 玻璃光学 ----
            SetVisible(HighlightHeaderText, vm.ShowHighlightSettings);
            SetVisible(HighlightRow, vm.ShowHighlightSettings);
            SetVisible(HighlightAngleRow, vm.ShowHighlightSettings);
            SetVisible(GlassOpticsSection, vm.ShowGlassOpticsSettings);

            LiveSamplingToggle.IsOn = vm.LiveSampling;
            LiveSamplingIntervalBox.Value = vm.LiveSamplingInterval;
            SetVisible(LiveSamplingIntervalRow, vm.LiveSampling);

            // ---- 颜色 ----
            SetVisible(DarkModeRow, vm.ShowDarkModeRow);
            SetVisible(OpacityRow, vm.ShowOpacitySetting);
            SetVisible(AccentColorRow, vm.ShowAccentSetting);
            SetVisible(AccentShiftRow, vm.ShowAccentSetting);
            SetVisible(AccentColorInput, vm.ShowColorPalette);
            SetVisible(MonochromeRow, vm.ShowMonochromeSetting);
            SetVisible(MonochromeVariantCombo, vm.ShowMonochromeVariant);
            SetVisible(SolidBackgroundRow, vm.ShowSolidBackgroundSetting);

            SelectItem(DarkModeCombo, vm.DarkMode);
            SelectItem(AccentModeCombo, vm.AccentMode);
            SelectItem(MonochromeVariantCombo, vm.MonochromeVariant);
            TextShadowToggle.IsOn = vm.TextShadowEnabled;
            MonochromeToggle.IsOn = vm.Monochrome;
            FollowAccentToggle.IsOn = vm.FollowAccentBackground;
            SetVisible(FollowAccentHintText, vm.FollowAccentBackground);

            // 跟随强调色开启时两个取色器仍显示（编辑的是「存起来的」那一对），但不可编辑。

            // WinUI 的 StackPanel 不是 Control、没有 IsEnabled，所以逐项处理可交互控件 + 淡化标签。

            var solidEditable = vm.SolidBackgroundEditable;
            SolidDarkInput.IsEnabled = solidEditable;
            ApplySolidToLightButton.IsEnabled = solidEditable;
            SolidDarkInput.Opacity = solidEditable ? 1.0 : 0.5;
            SolidDarkLabelText.Opacity = solidEditable ? 1.0 : 0.5;
            SolidLightInput.IsEnabled = solidEditable;
            ApplySolidToDarkButton.IsEnabled = solidEditable;
            SolidLightInput.Opacity = solidEditable ? 1.0 : 0.5;
            SolidLightLabelText.Opacity = solidEditable ? 1.0 : 0.5;

            // ---- 高级外观 ----
            SelectItem(FontCombo, vm.Font);
            FontNoResultsText.Visibility = vm.NoFontResults ? Visibility.Visible : Visibility.Collapsed;
            SelectItem(TitleBarStyleCombo, vm.TitleBarStyle);
            SelectItem(TitleBarSizeCombo, vm.TitleBarSize);
            SetVisible(TitleBarSizeRow, vm.ShowTitleBarSize);
        }
        finally
        {
            syncing = false;
        }
    }

    private static void SetVisible(UIElement element, bool visible)
        => element.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>
    /// 选中一个与 <paramref name="wanted"/> 值相等（record 值语义）的现有项。

    /// 直接赋 VM 返回的新实例在 WinUI 里可能匹配不到 ItemsSource，故显式按 Equals 找一遍。

    /// </summary>
    private static void SelectItem(ComboBox combo, object? wanted)
    {
        if (wanted is null || combo.ItemsSource is not IEnumerable items)
        {
            combo.SelectedItem = null;
            return;
        }

        foreach (var item in items)
        {
            if (Equals(item, wanted))
            {
                combo.SelectedItem = item;
                return;
            }
        }

        combo.SelectedItem = null;
    }

    /// <summary>重试保存按钮对应的错误提示条（与 VM 的 SettingsError 同步）。</summary>
    private void UpdateSaveError()
    {
        var message = viewModel?.SettingsError;
        var hasError = !string.IsNullOrEmpty(message);

        SaveErrorBar.Message = message ?? string.Empty;
        SaveErrorBar.IsOpen = hasError;
        SaveErrorActions.Visibility = hasError ? Visibility.Visible : Visibility.Collapsed;
    }

    // ---------------------------------------------------------------------------------------
    // 列表项与动态条目
    // ---------------------------------------------------------------------------------------

    private void CaptureOptionLists()
    {
        var vm = viewModel;
        if (vm is null) return;

        darkModes = vm.DarkModes;
        focusSurfaceOptions = vm.FocusSurfaceOptions;
        accentItems = vm.AccentComboboxItems;
        monochromeVariants = vm.MonochromeVariants;
        titleBarStyles = vm.TitleBarStyles;
        titleBarSizes = vm.TitleBarSizes;
    }

    /// <summary>选项来源只设一次：每次刷新都重设会让打开中的下拉框丢选中项、并触发多余事件。</summary>
    private void ApplyOptionItemsSources()
    {
        FocusModeFocusedCombo.ItemsSource = darkModes;
        FocusModeUnfocusedCombo.ItemsSource = darkModes;
        FocusSurfaceFocusedCombo.ItemsSource = focusSurfaceOptions;
        FocusSurfaceUnfocusedCombo.ItemsSource = focusSurfaceOptions;
        DarkModeCombo.ItemsSource = darkModes;
        AccentModeCombo.ItemsSource = accentItems;
        MonochromeVariantCombo.ItemsSource = monochromeVariants;
        TitleBarStyleCombo.ItemsSource = titleBarStyles;
        TitleBarSizeCombo.ItemsSource = titleBarSizes;
    }

    private void RefreshFontItems()
    {
        var vm = viewModel;
        if (vm is null) return;

        var previous = syncing;
        syncing = true;
        try
        {
            FontCombo.ItemsSource = vm.FilteredFonts;
            SelectItem(FontCombo, vm.Font);
            FontNoResultsText.Visibility = vm.NoFontResults ? Visibility.Visible : Visibility.Collapsed;
        }
        finally
        {
            syncing = previous;
        }
    }

    /// <summary>白名单条目（旧页的 WrapPanel chip：名字 + 移除按钮）。</summary>
    private void RebuildWhitelistChips(AppearanceViewModel vm)
    {
        WhitelistChipsPanel.Children.Clear();

        foreach (var item in vm.FocusWhitelist)
        {
            var name = new TextBlock
            {
                Text = item.Name,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = 220,
            };

            var remove = new Button
            {
                Content = Locale.Settings_Appearance_FocusLink_Whitelist_Remove,
                Tag = item.Name,
                MinWidth = 0,
                MinHeight = 0,
                Padding = new Thickness(6, 2, 6, 2),
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
            };
            remove.Click += OnRemoveFocusProcessClick;

            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            row.Children.Add(name);
            row.Children.Add(remove);

            var chip = new Border
            {
                Background = ResolveBrush("HostSurfaceSubtleBrush"),
                CornerRadius = ResolveCornerRadius("HostControlCornerRadius"),
                Padding = new Thickness(8, 2, 8, 2),
                VerticalAlignment = VerticalAlignment.Center,
                Child = row,
            };

            WhitelistChipsPanel.Children.Add(chip);
        }

        // 没有条目时把 chip 条一起收起，避免白名单行上方留一块空白高度。

        SetVisible(WhitelistChipsScroll, vm.FocusWhitelist.Count > 0);
        SetVisible(WhitelistEmptyText, vm.ShowFocusWhitelistEmpty);
    }

    private static Brush? ResolveBrush(string key)
        => Application.Current?.Resources is { } resources
           && resources.TryGetValue(key, out var value) && value is Brush brush
            ? brush
            : null;

    private static CornerRadius ResolveCornerRadius(string key)
        => Application.Current?.Resources is { } resources
           && resources.TryGetValue(key, out var value) && value is CornerRadius radius
            ? radius
            : new CornerRadius(6);

    // ---------------------------------------------------------------------------------------
    // 材质样片
    // ---------------------------------------------------------------------------------------

    private void BuildThemeCards()
    {
        foreach (var template in AppearanceViewModel.SurfaceTemplates)
        {
            var card = new ThemeButton
            {
                Surface = template.EffectiveSurface,
                Label = SurfaceNames.Of(template.EffectiveSurface),
                Tag = template,
                IsDarkPreview = false,
            };
            card.Click += OnThemeCardClick;

            themeCards.Add(card);
            ThemeCardsPanel.Children.Add(card);
        }
    }

    private void OnThemeCardClick(object sender, RoutedEventArgs e)
    {
        if (sender is not ThemeButton card || card.Tag is not Theme template) return;
        ApplySurfacePreset(template);
    }

    /// <summary>
    /// 切材质（与旧 <c>Views/Controls/ThemeButton.Apply</c> 同一套步骤）：先把当前材质的自定义写回它
    /// 自己的 SurfaceThemes 槽位，再取目标材质已保存的槽位（没有则用内置预设），保留字体/原生边框/
    /// 明暗这些全局偏好，最后整体保存。未选择当前材质时不重复写盘。

    /// </summary>
    private void ApplySurfacePreset(Theme template)
    {
        var settingsProvider = provider;
        if (settingsProvider is null) return;

        try
        {
            var settings = settingsProvider.Get();
            if (settings.Theme.EffectiveSurface == template.EffectiveSurface) return;

            var currentSurface = settings.Theme.EffectiveSurface;

            // 1. 当前材质的已改参数落到它自己的配置方案槽位。

            var updated = settings.WithThemeForSurface(currentSurface, settings.Theme);

            // 2. 目标材质：优先用户上次在该材质下调好的槽位，否则内置预设。

            var target = updated.GetThemeForSurface(template.EffectiveSurface);

            // 3. 字体 / 原生边框 / 明暗属于全局外观，切材质不丢。

            target = target with
            {
                Surface = template.EffectiveSurface,
                FontFamily = settings.Theme.FontFamily,
                UseNativeFrame = settings.Theme.UseNativeFrame,
                DarkMode = settings.Theme.DarkMode,
                AutoTheme = settings.Theme.AutoTheme,
            };

            // 4. 保存：写入目标材质的槽位并把它设为当前主题。

            settingsProvider.Save(updated.WithThemeForSurface(template.EffectiveSurface, target) with { Theme = target });
        }
        catch (Exception ex)
        {
            // 旧控件没有错误路径；这里至少把失败说出来，不静默失败。这条消息不说「草稿已保留」：
            // 材质切换是直接写盘，没有可重试的草稿（VM 的保存失败文案只用于 draft 会话）。

            Debug.WriteLine($"[Appearance] 切换材质失败：{ex}");
            SaveErrorBar.Message = AppearancePageStrings.SettingsSaveFailed;
            SaveErrorBar.IsOpen = true;
        }
    }

    // ---------------------------------------------------------------------------------------
    // 用户提交（开关 / 下拉框 / 数字框 / 滑块 / 按钮）
    // ---------------------------------------------------------------------------------------

    private void OnFocusLinkEnabledToggled(object sender, RoutedEventArgs e)
    {
        if (syncing || viewModel is null) return;
        RunDirectSave(() => viewModel.FocusLinkEnabled = FocusLinkEnabledToggle.IsOn);
    }

    private void OnFocusModeFocusedChanged(object sender, SelectionChangedEventArgs e)
    {
        if (syncing || viewModel is null) return;
        if (FocusModeFocusedCombo.SelectedItem is DarkModeViewModel mode) RunDirectSave(() => viewModel.FocusModeFocused = mode);
    }

    private void OnFocusModeUnfocusedChanged(object sender, SelectionChangedEventArgs e)
    {
        if (syncing || viewModel is null) return;
        if (FocusModeUnfocusedCombo.SelectedItem is DarkModeViewModel mode) RunDirectSave(() => viewModel.FocusModeUnfocused = mode);
    }

    private void OnFocusSurfaceFocusedChanged(object sender, SelectionChangedEventArgs e)
    {
        if (syncing || viewModel is null) return;
        if (FocusSurfaceFocusedCombo.SelectedItem is FocusSurfaceViewModel option) RunDirectSave(() => viewModel.FocusSurfaceFocused = option);
    }

    private void OnFocusSurfaceUnfocusedChanged(object sender, SelectionChangedEventArgs e)
    {
        if (syncing || viewModel is null) return;
        if (FocusSurfaceUnfocusedCombo.SelectedItem is FocusSurfaceViewModel option) RunDirectSave(() => viewModel.FocusSurfaceUnfocused = option);
    }

    private void OnFocusDelayChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (syncing || viewModel is null) return;
        if (!double.IsFinite(args.NewValue)) return;
        RunDirectSave(() => viewModel.FocusSwitchDelayMs = (int)Math.Round(args.NewValue));
    }

    private void OnLiveSamplingToggled(object sender, RoutedEventArgs e)
    {
        if (syncing || viewModel is null) return;
        viewModel.LiveSampling = LiveSamplingToggle.IsOn;
    }

    private void OnLiveSamplingIntervalChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (syncing || viewModel is null) return;
        if (!double.IsFinite(args.NewValue)) return;
        viewModel.LiveSamplingInterval = (int)Math.Round(args.NewValue);
    }

    private void OnDarkModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (syncing || viewModel is null) return;
        if (DarkModeCombo.SelectedItem is DarkModeViewModel mode) viewModel.DarkMode = mode;
    }

    private void OnAccentModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (syncing || viewModel is null) return;
        if (AccentModeCombo.SelectedItem is AccentColorViewModel accent) viewModel.AccentMode = accent;
    }

    private void OnMonochromeToggled(object sender, RoutedEventArgs e)
    {
        if (syncing || viewModel is null) return;
        viewModel.Monochrome = MonochromeToggle.IsOn;
    }

    private void OnMonochromeVariantChanged(object sender, SelectionChangedEventArgs e)
    {
        if (syncing || viewModel is null) return;
        if (MonochromeVariantCombo.SelectedItem is MonochromeVariantViewModel variant) viewModel.MonochromeVariant = variant;
    }

    private void OnTextShadowToggled(object sender, RoutedEventArgs e)
    {
        if (syncing || viewModel is null) return;
        viewModel.TextShadowEnabled = TextShadowToggle.IsOn;
    }

    private void OnFollowAccentToggled(object sender, RoutedEventArgs e)
    {
        if (syncing || viewModel is null) return;
        viewModel.FollowAccentBackground = FollowAccentToggle.IsOn;
    }

    private void OnTitleBarStyleChanged(object sender, SelectionChangedEventArgs e)
    {
        if (syncing || viewModel is null) return;
        if (TitleBarStyleCombo.SelectedItem is TitleBarStyleViewModel style) RunDirectSave(() => viewModel.TitleBarStyle = style);
    }

    private void OnTitleBarSizeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (syncing || viewModel is null) return;
        if (TitleBarSizeCombo.SelectedItem is TitleBarSizeViewModel size) RunDirectSave(() => viewModel.TitleBarSize = size);
    }

    private void OnFontSearchChanged(object sender, TextChangedEventArgs e)
    {
        if (syncing || viewModel is null) return;
        viewModel.FontSearchText = FontSearchBox.Text ?? string.Empty;
        RefreshFontItems();
    }

    private void OnFontChanged(object sender, SelectionChangedEventArgs e)
    {
        if (syncing || viewModel is null) return;
        // 过滤列表会临时清空选中项，这不是用户选择（与旧页 VM 的 strlen 守卫一致）。

        if (FontCombo.SelectedItem is not string font || string.IsNullOrWhiteSpace(font)) return;
        viewModel.Font = font;
    }

    /// <summary>所有滑块（玻璃参数 / 高光 / 阴影强度 / 强调色偏移）只在这里提交一次。</summary>
    private void OnSliderValueCommitted(object sender, LabeledSliderValueCommittedEventArgs e)
    {
        if (syncing) return;
        var committed = viewModel?.CommitThemeSliders() ?? true;
        if (!committed)
        {
            Debug.WriteLine("[Appearance] 外观滑块保存失败，错误已由 VM 的 SettingsError 呈现。");
        }
    }

    /// <summary>不透明度滑块：旧页走 CommitOpacityLevel（与主题滑块同一个 session）。</summary>
    private void OnOpacityValueCommitted(object sender, LabeledSliderValueCommittedEventArgs e)
    {
        if (syncing) return;
        viewModel?.CommitOpacityLevel();
    }

    private void OnRetrySaveClick(object sender, RoutedEventArgs e)
    {
        viewModel?.RetrySettingsSave();
        UpdateSaveError();
    }

    private void OnReloadSettingsClick(object sender, RoutedEventArgs e)
    {
        viewModel?.ReloadSettings();
        RefreshView();
        UpdateSaveError();
    }

    private void OnResetGlassClick(object sender, RoutedEventArgs e) => viewModel?.ResetLiquidGlass();

    private void OnRefreshWallpaperClick(object sender, RoutedEventArgs e) => viewModel?.RefreshLiquidGlassWallpaper();

    private void OnApplySolidToLightClick(object sender, RoutedEventArgs e) => viewModel?.ApplySolidToLight();

    private void OnApplySolidToDarkClick(object sender, RoutedEventArgs e) => viewModel?.ApplySolidToDark();

    private void OnOpenWallpaperAlignClick(object sender, RoutedEventArgs e)
    {
        // 壁纸对齐窗口（旧 WallpaperAlignDialog）由弹窗批次（S4.3）实现；在它存在之前保持
        // 按钮可用但明确说明未实现，避免出现“点了没反应”的假入口。

        AlignUnavailableBar.Message = AppearancePageStrings.WallpaperAlignUnavailable;
        AlignUnavailableBar.IsOpen = true;
    }

    private void OnRemoveFocusProcessClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string name }) RunDirectSave(() => viewModel?.RemoveFocusProcess(name));
    }

    /// <summary>
    /// 旧 VM 里这几项直接调 <c>IAppSettingsProvider.Save</c>（不经过 draft 会话），写失败时 VM 没有
    /// 错误状态、异常会从 WinUI 事件处理器里抛出去。页面在这里兜底：把失败说出来，并把控件退回
    /// 已保存的值，不让界面停在一个没落盘的状态（报告 §4.2）。

    /// </summary>
    private void RunDirectSave(Action save)
    {
        try
        {
            save();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Appearance] 直接保存外观设置失败：{ex}");
            SaveErrorBar.Message = AppearancePageStrings.SettingsSaveFailed;
            SaveErrorBar.IsOpen = true;
            RefreshView();
        }
    }

    /// <summary>挑选进程加入白名单（临时页内实现，复用失焦监视器的进程枚举）。</summary>
    private async void OnAddProcessClick(object sender, RoutedEventArgs e)
    {
        var vm = viewModel;
        if (vm is null) return;

        try
        {
            var content = new ProcessPickerContent();
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = Locale.ProcessPicker_Title,
                PrimaryButtonText = Locale.ProcessPicker_Add,
                CloseButtonText = Locale.ProcessPicker_Cancel,
                DefaultButton = ContentDialogButton.Primary,
                Content = content,
            };

            var result = await dialog.ShowAsync();
            if (result != ContentDialogResult.Primary) return;

            var selected = content.SelectedProcessNames;
            if (selected.Count > 0) RunDirectSave(() => vm.AddFocusProcesses(selected));
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Appearance] 打开进程选择框失败：{ex}");
        }
    }

    // ---------------------------------------------------------------------------------------
    // 静态文案（WinUI XAML 没有 x:Static；守卫规则 7 也不允许在 .xaml 里写中文字面量）
    // ---------------------------------------------------------------------------------------

    private void ApplyLocalizedTexts()
    {
        // 顶部保存错误
        RetrySaveButton.Content = Locale.Settings_Appearance_SaveRetry;
        ReloadSettingsButton.Content = Locale.Settings_Appearance_SaveReload;

        // 失焦自动切换
        FocusLinkHeaderText.Text = Locale.Settings_Appearance_FocusLink_Title;
        SetRowText(FocusLinkEnabledRow,
            Locale.Settings_Appearance_FocusLink_Enable_Title,
            Locale.Settings_Appearance_FocusLink_Enable_Subtitle);
        SetRowText(FocusFocusedRow,
            Locale.Settings_Appearance_FocusLink_FocusedState_Title,
            Locale.Settings_Appearance_FocusLink_State_Subtitle);
        SetRowText(FocusUnfocusedRow,
            Locale.Settings_Appearance_FocusLink_UnfocusedState_Title,
            Locale.Settings_Appearance_FocusLink_State_Subtitle);
        FocusColorPolicyHintText.Text = Locale.Settings_Appearance_FocusLink_ColorPolicy_Hint;
        SetRowText(FocusDelayRow,
            Locale.Settings_Appearance_FocusLink_Delay_Title,
            Locale.Settings_Appearance_FocusLink_Delay_Subtitle);
        SetRowText(FocusWhitelistRow,
            Locale.Settings_Appearance_FocusLink_Whitelist_Title,
            Locale.Settings_Appearance_FocusLink_Whitelist_Subtitle);
        WhitelistEmptyText.Text = Locale.Settings_Appearance_FocusLink_Whitelist_Empty;
        AddProcessButton.Content = Locale.Settings_Appearance_FocusLink_Whitelist_Add;
        SetRowText(FocusStatusRow, Locale.Settings_Appearance_FocusLink_Status_Title, string.Empty);

        // 应用主题
        ApplyThemeHeaderText.Text = Locale.Settings_Appearance_ApplyTheme;

        // 高光
        HighlightHeaderText.Text = Locale.Settings_Appearance_Highlight;
        SetRowText(HighlightRow,
            Locale.Settings_Appearance_Glass_Highlight,
            Locale.Settings_Appearance_Glass_HighlightHint);
        SetRowText(HighlightAngleRow,
            Locale.Settings_Appearance_Glass_HighlightAngle,
            Locale.Settings_Appearance_Glass_HighlightAngleHint);

        // 玻璃光学（折叠区）
        GlassOpticsSection.Header = Locale.Settings_Appearance_Surface_LiquidGlass;
        SetRowText(LiveSamplingRow,
            Locale.Settings_Appearance_Glass_LiveSampling,
            Locale.Settings_Appearance_Glass_LiveSamplingHint);
        SetRowText(LiveSamplingIntervalRow,
            Locale.Settings_Appearance_Glass_LiveSamplingInterval,
            Locale.Settings_Appearance_Glass_LiveSamplingIntervalHint);
        SetRowText(GlassBlurRow,
            Locale.Settings_Appearance_Glass_Blur,
            Locale.Settings_Appearance_Glass_BlurHint);
        SetRowText(GlassRefractionRow,
            Locale.Settings_Appearance_Glass_Refraction,
            Locale.Settings_Appearance_Glass_RefractionHint);
        SetRowText(GlassDispersionRow,
            Locale.Settings_Appearance_Glass_Dispersion,
            Locale.Settings_Appearance_Glass_DispersionHint);
        SetRowText(GlassVibrancyRow,
            Locale.Settings_Appearance_Glass_Vibrancy,
            Locale.Settings_Appearance_Glass_VibrancyHint);
        SetRowText(GlassBackdropClarityRow,
            Locale.Settings_Appearance_Glass_BackdropClarity,
            Locale.Settings_Appearance_Glass_BackdropClarityHint);

        AlignButton.Content = Locale.Settings_Appearance_Glass_Align;
        ResetGlassButton.Content = Locale.Settings_Appearance_Glass_Reset;
        RefreshWallpaperButton.Content = Locale.Settings_Appearance_Glass_Refresh;
        AlignUnavailableBar.Title = AppearancePageStrings.WallpaperAlignUnavailableTitle;

        // 颜色
        ColorsHeaderText.Text = Locale.Settings_Appearance_Colors;
        SetRowText(DarkModeRow,
            Locale.Settings_Appearance_DarkMode_Title,
            Locale.Settings_Appearance_DarkMode_Subtitle);
        SetRowText(OpacityRow,
            Locale.Settings_Appearance_OpacityLevel_Title,
            Locale.Settings_Appearance_OpacityLevel_Subtitle);
        SetRowText(TextShadowRow,
            Locale.Settings_Appearance_TextShadow_Title,
            Locale.Settings_Appearance_TextShadow_Subtitle);
        SetRowText(AccentColorRow, Locale.Settings_Appearance_AccentColor_Title, string.Empty);
        SetRowText(AccentShiftRow,
            Locale.Settings_Appearance_AccentShift_Title,
            Locale.Settings_Appearance_AccentShift_Subtitle);
        SetRowText(MonochromeRow,
            Locale.Settings_Appearance_Monochrome_Title,
            Locale.Settings_Appearance_Monochrome_Subtitle);
        SetRowText(SolidBackgroundRow,
            Locale.Settings_Appearance_SolidBackground_Title,
            Locale.Settings_Appearance_SolidBackground_Subtitle);
        FollowAccentLabelText.Text = Locale.Settings_Appearance_Background_FollowAccent_Title;
        FollowAccentHintText.Text = Locale.Settings_Appearance_Background_FollowAccent_Subtitle;
        SolidDarkLabelText.Text = Locale.Settings_Appearance_SolidBackground_Dark;
        SolidLightLabelText.Text = Locale.Settings_Appearance_SolidBackground_Light;
        ApplySolidToLightButton.Content = Locale.Settings_Appearance_SolidBackground_ApplyToLight;
        ApplySolidToDarkButton.Content = Locale.Settings_Appearance_SolidBackground_ApplyToDark;

        // 高级外观
        AdvancedHeaderText.Text = Locale.Settings_Advanced_Appearance;
        SetRowText(FontRow,
            Locale.Settings_Advanced_FontFamily_Title,
            Locale.Settings_Advanced_FontFamily_Subtitle);
        FontSearchBox.PlaceholderText = Locale.Settings_Appearance_FontSearchWatermark;
        FontNoResultsText.Text = Locale.Settings_Appearance_FontNoResults;
        SetRowText(TitleBarStyleRow,
            Locale.Settings_Advanced_TitleBarStyle_Title,
            Locale.Settings_Advanced_TitleBarStyle_Subtitle);
        TitleBarStyleNoteText.Text = AppearancePageStrings.TitleBarStyleWinUiNote;
        SetRowText(TitleBarSizeRow,
            Locale.Settings_Advanced_TitleBarSize_Title,
            Locale.Settings_Advanced_TitleBarSize_Subtitle);

        // 行级无障碍名称传给真正可交互的部件（旧页用 AutomationProperties.Name 做同一件事）
        SetAutomationName(FocusLinkEnabledToggle, Locale.Settings_Appearance_FocusLink_Enable_Title);
        SetAutomationName(FocusDelayBox, Locale.Settings_Appearance_FocusLink_Delay_Title);
        SetAutomationName(HighlightSlider, Locale.Settings_Appearance_Glass_Highlight);
        SetAutomationName(HighlightAngleSlider, Locale.Settings_Appearance_Glass_HighlightAngle);
        SetAutomationName(LiveSamplingIntervalBox, Locale.Settings_Appearance_Glass_LiveSamplingInterval);
        SetAutomationName(GlassBlurSlider, Locale.Settings_Appearance_Glass_Blur);
        SetAutomationName(GlassRefractionSlider, Locale.Settings_Appearance_Glass_Refraction);
        SetAutomationName(GlassDispersionSlider, Locale.Settings_Appearance_Glass_Dispersion);
        SetAutomationName(GlassVibrancySlider, Locale.Settings_Appearance_Glass_Vibrancy);
        SetAutomationName(GlassBackdropClaritySlider, Locale.Settings_Appearance_Glass_BackdropClarity);
        SetAutomationName(OpacitySlider, Locale.Settings_Appearance_OpacityLevel_Title);
        SetAutomationName(TextShadowStrengthSlider, Locale.Settings_Appearance_TextShadow_Strength);
        SetAutomationName(AccentShiftSlider, Locale.Settings_Appearance_AccentShift_Title);
        SetAutomationName(AccentModeCombo, Locale.Settings_Appearance_AccentColor_Title);
        SetAutomationName(DarkModeCombo, Locale.Settings_Appearance_DarkMode_Title);
        SetAutomationName(MonochromeVariantCombo, Locale.Settings_Appearance_Monochrome_Title);
        SetAutomationName(FontCombo, Locale.Settings_Advanced_FontFamily_Title);
        SetAutomationName(TitleBarStyleCombo, Locale.Settings_Advanced_TitleBarStyle_Title);
        SetAutomationName(TitleBarSizeCombo, Locale.Settings_Advanced_TitleBarSize_Title);
        SetAutomationName(FocusSurfaceFocusedCombo, Locale.Settings_Appearance_FocusLink_FocusedState_Title);
        SetAutomationName(FocusSurfaceUnfocusedCombo, Locale.Settings_Appearance_FocusLink_UnfocusedState_Title);
        SetAutomationName(FocusStatusText, Locale.Settings_Appearance_FocusLink_Status_Title);
    }

    private static void SetRowText(SettingRow row, string title, string subtitle)
    {
        row.Title = title ?? string.Empty;
        row.Subtitle = subtitle ?? string.Empty;
    }

    private static void SetAutomationName(DependencyObject element, string name)
    {
        if (string.IsNullOrEmpty(name)) return;
        AutomationProperties.SetName(element, name);
    }
}

/// <summary>
/// 页面自注册：不修改共享的 <see cref="SettingsPageFactory"/>/<see cref="SettingsPageRegistry"/>，
/// 本程序集一加载就认领 <see cref="SettingsPageKeys.Appearance"/>（报告 §3 第二步的页面键约定）。
/// </summary>
internal static class AppearancePageRegistration
{
    [ModuleInitializer]
    internal static void Register()
        => SettingsPageRegistry.Register(SettingsPageKeys.Appearance, static _ => new AppearancePage());
}
