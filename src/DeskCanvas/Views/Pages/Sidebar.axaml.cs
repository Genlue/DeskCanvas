using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using DeskCanvas.Core.Interfaces;
using DeskCanvas.Core.Models;
using DeskCanvas.Core.Models.Settings;
using DeskCanvas.Locales;
using DeskCanvas.Services;

namespace DeskCanvas.Views.Pages;

/// <summary>
/// The dedicated 侧栏 settings page: global hotkey, per-screen width, fullscreen / process
/// blocking, and the quick actions (show, clear, open the gallery).
/// <para>
/// Everything is read from and written to <see cref="AppSettings.Sidebar"/> so it travels with the
/// profile / backup; the sidebar and hotkey services are resolved from the app container.
/// </para>
/// </summary>
public partial class Sidebar : UserControl
{
    private readonly IAppSettingsProvider? settingsProvider;

    private bool recording;
    private bool loading;

    /// <summary>True while the metric boxes are being filled from settings, so the change handlers stay quiet.</summary>
    private bool updatingMetrics;

    /// <summary>
    /// Parameterless on purpose: the XAML loader (and the settings page factory) build pages with
    /// no arguments, and every dependency is available from the app container by the time a page is
    /// shown.
    /// </summary>
    public Sidebar()
    {
        settingsProvider = App.Services?.GetService(typeof(IAppSettingsProvider)) as IAppSettingsProvider;
        InitializeComponent();

        Loaded += (_, _) => Reload();

        RecordButton.Click += (_, _) => BeginRecording();
        HotKeyBox.KeyDown += OnRecordKeyDown;
        HotKeyBox.GotFocus += (_, _) => { if (recording) HotKeyBox.Text = Locale.Sidebar_HotKey_Recording; };
        HotKeyBox.LostFocus += (_, _) => { if (recording) EndRecording(); };

        DefaultWidthBox.ValueChanged += (_, _) => Save(sidebar => sidebar with { DefaultWidthDip = Value(DefaultWidthBox, sidebar.DefaultWidthDip) });
        CurrentWidthBox.ValueChanged += (_, _) => SaveCurrentWidth();
        ColumnsBox.ValueChanged += (_, _) => SaveColumns();
        BlockFullscreenCheck.IsCheckedChanged += (_, _) => Save(sidebar => sidebar with { BlockOnFullscreen = BlockFullscreenCheck.IsChecked == true });

        AddProcessButton.Click += (_, _) => AddSelectedProcess();
        RemoveProcessButton.Click += (_, _) => RemoveSelectedProcess();
        ShowButton.Click += (_, _) => ShowSidebar();
        ClearButton.Click += (_, _) => ClearSidebar();
        GalleryButton.Click += (_, _) => OpenGallery();

        ShadowEnabledCheck.IsCheckedChanged += (_, _) => SaveShadow(shadow => shadow with { Enabled = ShadowEnabledCheck.IsChecked == true });
        ShadowOpacitySlider.PropertyChanged += (_, e) =>
        {
            if (e.Property != Slider.ValueProperty) return;
            UpdateShadowSummary();
            SaveShadow(shadow => shadow with { Opacity = ShadowOpacitySlider.Value });
        };
        ShadowExtendBox.ValueChanged += (_, _) => SaveShadow(shadow => shadow with { ExtendDip = ShadowValue(ShadowExtendBox, shadow.ExtendDip) });
        ShadowFeatherBox.ValueChanged += (_, _) => SaveShadow(shadow => shadow with { FeatherDip = ShadowValue(ShadowFeatherBox, shadow.FeatherDip) });
        ShadowFadeStartBox.ValueChanged += (_, _) => SaveShadow(shadow => shadow with { FadeStartOffsetDip = ShadowValue(ShadowFadeStartBox, shadow.FadeStartOffsetDip) });
        ShadowColorBox.LostFocus += (_, _) => SaveShadow(shadow => shadow with { Color = ShadowColorBox.Text ?? shadow.Color });

        WidgetMarginBox.ValueChanged += (_, _) => SaveWidgetMetrics(sidebar =>
            sidebar with { WidgetMarginDip = MetricInt(WidgetMarginBox, sidebar.EffectiveWidgetMarginDip) });
        WidgetRadiusBox.ValueChanged += (_, _) => SaveWidgetMetrics(sidebar =>
            sidebar with { WidgetRadiusDip = MetricInt(WidgetRadiusBox, (int)Math.Round(DesktopRadiusDip())) });

        // Clearing the override is a save like any other: SaveWidgetMetrics refills both boxes from
        // the store afterwards, so the boxes show the effective values again (with the change
        // handlers muted, otherwise that refill would immediately look like a fresh edit).
        WidgetMarginResetButton.Click += (_, _) =>
            SaveWidgetMetrics(sidebar => sidebar with { WidgetMarginDip = null });
        WidgetRadiusResetButton.Click += (_, _) =>
            SaveWidgetMetrics(sidebar => sidebar with { WidgetRadiusDip = null });
    }

    // ---------- Widget inset / corner radius ----------

    /// <summary>
    /// The desktop's own radius for the screen under the cursor: the screen's override when it has
    /// one, otherwise the global <see cref="Dimensions.Radius"/>. This is what the sidebar falls
    /// back to while it has no radius of its own — and it is the same chain the cards resolved
    /// before the sidebar had a radius setting at all.
    /// </summary>
    private double DesktopRadiusDip()
    {
        var settings = settingsProvider?.Get();
        var screenId = SidebarService?.CursorScreenId();
        var layout = App.Services?.GetService(typeof(ILayoutProvider)) as ILayoutProvider;
        var perScreen = screenId != null ? layout?.Get().FindById(screenId)?.Radius : null;
        return perScreen ?? settings?.Dimensions.Radius ?? 0;
    }

    /// <summary>Show the effective inset / radius in the boxes, without treating it as an edit.</summary>
    private void ShowMetricsInBoxes(int marginDip, double radiusDip)
    {
        updatingMetrics = true;
        try
        {
            WidgetMarginBox.Value = marginDip;
            WidgetRadiusBox.Value = (decimal)Math.Round(radiusDip);
        }
        finally
        {
            updatingMetrics = false;
        }
    }

    private void SaveWidgetMetrics(Func<SidebarSettings, SidebarSettings> edit)
    {
        if (loading || updatingMetrics || settingsProvider == null) return;
        var settings = settingsProvider.Get();
        var sidebar = settings.EffectiveSidebar;
        settingsProvider.Save(settings with { Sidebar = edit(sidebar) });
        ReloadWidgetMetrics();
    }

    private void ReloadWidgetMetrics()
    {
        if (settingsProvider == null) return;

        var sidebar = settingsProvider.Get().EffectiveSidebar;
        var desktopRadius = DesktopRadiusDip();
        ShowMetricsInBoxes(sidebar.EffectiveWidgetMarginDip, sidebar.EffectiveWidgetRadiusDip((int)Math.Round(desktopRadius)));

        var margin = sidebar.EffectiveWidgetMarginDip;
        var radius = sidebar.EffectiveWidgetRadiusDip((int)Math.Round(desktopRadius));
        var radiusSource = sidebar.WidgetRadiusDip != null
            ? "专属"
            : $"跟随桌面（{desktopRadius:0}）";
        var marginSource = sidebar.WidgetMarginDip != null ? "专属" : "默认";

        WidgetMetricsSummary.Text =
            $"内边距 {margin} DIP（{marginSource}）→ 相邻组件间距 {margin * 2} DIP，卡片相对网格单元四周各留 {margin}。 " +
            $"圆角 {radius} DIP（{radiusSource}）。";
    }

    private static int MetricInt(NumericUpDown box, int fallback) =>
        box.Value is { } value ? (int)Math.Round((double)value) : fallback;

    private SidebarService? SidebarService =>
        App.Services?.GetService(typeof(SidebarService)) as SidebarService;

    private IGlobalHotKeyService? HotKeys =>
        App.Services?.GetService(typeof(IGlobalHotKeyService)) as IGlobalHotKeyService;

    private void Reload()
    {
        if (settingsProvider == null) return;

        loading = true;
        try
        {
            var sidebar = settingsProvider.Get().EffectiveSidebar;
            HotKeyBox.Text = HotKeys?.HotKey ?? sidebar.HotKey;
            HotKeyHint.Text = Locale.Sidebar_HotKey_Hint + "  " + RegistrationStatus();
            DefaultWidthBox.Value = (decimal)sidebar.DefaultWidthDip;
            CurrentWidthBox.Value = (decimal)CurrentScreenWidth(sidebar.DefaultWidthDip);
            ColumnsBox.Value = CurrentScreenColumns();
            UpdateGridSummary();
            BlockFullscreenCheck.IsChecked = sidebar.BlockOnFullscreen;

            BlockedList.ItemsSource = (sidebar.BlockedProcessNames ?? []).ToList();
            RunningProcesses.ItemsSource = RunningProcessNames();
            ReloadShadow();
            ReloadWidgetMetrics();
        }
        finally
        {
            loading = false;
        }
    }

    // ---------- Diffuse shadow ----------

    /// <summary>
    /// Load the shadow controls from the stored settings. The sliders/boxes are written through the
    /// same <see cref="Save"/> path as everything else, so the shadow travels with the profile.
    /// </summary>
    private void ReloadShadow()
    {
        var shadow = settingsProvider?.Get().EffectiveSidebar.EffectiveShadow ?? new SidebarShadowSettings();
        ShadowEnabledCheck.IsChecked = shadow.Enabled;
        ShadowOpacitySlider.Value = shadow.EffectiveOpacity;
        ShadowExtendBox.Value = (decimal)shadow.EffectiveExtendDip;
        ShadowFeatherBox.Value = (decimal)shadow.EffectiveFeatherDip;
        ShadowFadeStartBox.Value = (decimal)shadow.EffectiveFadeStartOffsetDip;
        ShadowColorBox.Text = shadow.Color;

        // 毛玻璃 limits the OS frost to the cards (the window region is the only way to say where
        // the acrylic backdrop may appear), and the same region is what would have to carry the
        // shadow — so the wash cannot be composited in that material. Say so instead of leaving the
        // user to wonder why the settings do nothing.
        ShadowMaterialNote.Text = settingsProvider?.Get().Theme.UsesNativeBlur == true
            ? "当前材质为毛玻璃：系统模糊只能限制在组件矩形内，因此阴影不绘制（切到液态玻璃 / 柔光玻璃即可看到）。"
            : string.Empty;

        UpdateShadowSummary();
    }

    private void SaveShadow(Func<SidebarShadowSettings, SidebarShadowSettings> edit)
    {
        if (loading || settingsProvider == null) return;
        var settings = settingsProvider.Get();
        var sidebar = settings.EffectiveSidebar;
        var updated = edit(sidebar.EffectiveShadow);
        settingsProvider.Save(settings with { Sidebar = sidebar with { Shadow = updated } });
        UpdateShadowSummary();
    }

    /// <summary>Spell the gradient out as the fractions the user will actually see.</summary>
    private void UpdateShadowSummary()
    {
        var shadow = settingsProvider?.Get().EffectiveSidebar.EffectiveShadow ?? new SidebarShadowSettings();
        ShadowOpacityText.Text = $"{ShadowOpacitySlider.Value:0.00}";
        var feather = (double)(ShadowFeatherBox.Value ?? (decimal)shadow.EffectiveFeatherDip);
        ShadowSummary.Text =
            $"{shadow.EffectiveColor} · 峰值 {ShadowOpacitySlider.Value:0.00} · " +
            $"从最左侧组件起向左 {feather:0} DIP 内淡到 0，淡出段整体落在组件左侧 {shadow.EffectiveExtendDip:0} DIP 的窗口范围内。";
    }

    private static double ShadowValue(NumericUpDown box, double fallback) =>
        box.Value is { } value ? (double)value : fallback;

    private string RegistrationStatus() =>
        HotKeys?.IsRegistered == true ? Locale.Sidebar_HotKey_Registered : Locale.Sidebar_HotKey_Unregistered;

    /// <summary>The stored width of the screen the mouse is on (falls back to the default width).</summary>
    private double CurrentScreenWidth(double fallback)
    {
        var screenId = SidebarService?.CursorScreenId();
        if (screenId == null) return fallback;
        var layout = App.Services?.GetService(typeof(ILayoutProvider)) as ILayoutProvider;
        return layout?.Get().FindById(screenId)?.EffectiveSidebar.WidthDip ?? fallback;
    }

    /// <summary>The stored column count of the screen the mouse is on (falls back to the default).</summary>
    private int CurrentScreenColumns()
    {
        var screenId = SidebarService?.CursorScreenId();
        if (screenId == null) return SidebarLayout.DefaultColumns;
        var layout = App.Services?.GetService(typeof(ILayoutProvider)) as ILayoutProvider;
        return layout?.Get().FindById(screenId)?.EffectiveSidebar.EffectiveColumns ?? SidebarLayout.DefaultColumns;
    }

    /// <summary>
    /// Spell the grid arithmetic out ("360 ÷ 4 = 每格 90"), because that cell size is the number
    /// the user actually cares about: it is what one widget cell measures.
    /// </summary>
    private void UpdateGridSummary()
    {
        var width = Value(CurrentWidthBox, settingsProvider?.Get().EffectiveSidebar.DefaultWidthDip ?? 360);
        var columns = ColumnsBox.Value is { } value ? Math.Clamp((int)value, SidebarLayout.MinColumns, SidebarLayout.MaxColumns) : SidebarLayout.DefaultColumns;
        var cell = SidebarRules.CellSide(width, columns);
        GridSummary.Text = $"网格：{width:0} ÷ {columns} 列 = 每格 {cell:0.#}×{cell:0.#}，每行最多 {columns} 个 1×1 组件。";
    }

    private static List<string> RunningProcessNames()
    {
        try
        {
            return Process.GetProcesses()
                .Select(p => { try { return p.ProcessName; } catch { return null; } })
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToList()!;
        }
        catch
        {
            return [];
        }
    }

    // ---------- Hotkey recording ----------

    private void BeginRecording()
    {
        recording = true;
        StatusText.Text = Locale.Sidebar_HotKey_Recording;
        HotKeyBox.Focus();
    }

    private void EndRecording()
    {
        recording = false;
        HotKeyBox.Text = HotKeys?.HotKey ?? settingsProvider?.Get().EffectiveSidebar.HotKey ?? string.Empty;
    }

    private void OnRecordKeyDown(object? sender, KeyEventArgs e)
    {
        if (!recording) return;
        e.Handled = true;

        if (e.Key is Key.Escape)
        {
            EndRecording();
            StatusText.Text = string.Empty;
            return;
        }

        var text = Compose(e.KeyModifiers, e.Key);
        if (text == null)
        {
            StatusText.Text = Locale.Sidebar_HotKey_Hint;
            return;
        }

        if (HotKeys is { } hotKeys && !hotKeys.TrySetHotKey(text, out var error))
        {
            StatusText.Text = error ?? Locale.Sidebar_HotKey_Conflict;
            return;
        }

        recording = false;
        HotKeyBox.Text = HotKeys?.HotKey ?? text;
        HotKeyHint.Text = Locale.Sidebar_HotKey_Hint + "  " + RegistrationStatus();
        StatusText.Text = string.Empty;

        // Persist the normalized form so the hotkey is re-registered on the next launch.
        if (settingsProvider is not { } provider) return;
        var settings = provider.Get();
        provider.Save(settings with
        {
            Sidebar = settings.EffectiveSidebar with { HotKey = HotKeys?.HotKey ?? text }
        });
    }

    private static string? Compose(KeyModifiers modifiers, Key key)
    {
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
            or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin or Key.None)
            return null;

        var parts = new List<string>(4);
        if (modifiers.HasFlag(KeyModifiers.Control)) parts.Add("Ctrl");
        if (modifiers.HasFlag(KeyModifiers.Alt)) parts.Add("Alt");
        if (modifiers.HasFlag(KeyModifiers.Shift)) parts.Add("Shift");
        if (modifiers.HasFlag(KeyModifiers.Meta)) parts.Add("Win");
        parts.Add(key.ToString());
        return string.Join("+", parts);
    }

    // ---------- Width ----------

    private void SaveCurrentWidth()
    {
        if (loading || settingsProvider == null) return;
        var sidebar = SidebarService;
        var screenId = sidebar?.CursorScreenId();
        if (sidebar == null || screenId == null) return;
        sidebar.UpdateWidth(screenId, Value(CurrentWidthBox, sidebar.SidebarSettings.DefaultWidthDip));
        UpdateGridSummary();
    }

    /// <summary>
    /// Persist the grid column count of the screen under the cursor. Columns are per screen (like
    /// the width) because the cell size — and therefore how many widgets fit on a row — depends on
    /// both, and two monitors of different widths do not want the same count.
    /// </summary>
    private void SaveColumns()
    {
        if (loading) return;
        var sidebar = SidebarService;
        var screenId = sidebar?.CursorScreenId();
        UpdateGridSummary();
        if (sidebar == null || screenId == null) return;
        sidebar.UpdateColumns(screenId, (int)(ColumnsBox.Value ?? SidebarLayout.DefaultColumns));
    }

    private static double Value(NumericUpDown box, double fallback) =>
        box.Value is { } value ? (double)value : fallback;

    private void Save(Func<SidebarSettings, SidebarSettings> edit)
    {
        if (loading) return;
        if (settingsProvider is not { } provider) return;
        var settings = provider.Get();
        provider.Save(settings with { Sidebar = edit(settings.EffectiveSidebar) });
    }

    // ---------- Blocked processes ----------

    private void AddSelectedProcess()
    {
        if (RunningProcesses.SelectedItem is not string name) return;
        Save(sidebar =>
        {
            var names = (sidebar.BlockedProcessNames ?? []).ToList();
            if (!names.Contains(name, StringComparer.OrdinalIgnoreCase)) names.Add(name);
            return sidebar with { BlockedProcessNames = names };
        });
        ReloadBlocked();
    }

    private void RemoveSelectedProcess()
    {
        if (BlockedList.SelectedItem is not string name) return;
        Save(sidebar => sidebar with
        {
            BlockedProcessNames = (sidebar.BlockedProcessNames ?? [])
                .Where(item => !string.Equals(item, name, StringComparison.OrdinalIgnoreCase))
                .ToList()
        });
        ReloadBlocked();
    }

    private void ReloadBlocked() =>
        BlockedList.ItemsSource = (settingsProvider?.Get().EffectiveSidebar.BlockedProcessNames ?? []).ToList();

    // ---------- Actions ----------

    private void ShowSidebar()
    {
        var sidebar = SidebarService;
        if (sidebar == null)
        {
            StatusText.Text = "侧栏服务不可用。";
            return;
        }

        sidebar.ToggleForCursorScreen();
        StatusText.Text = sidebar.IsOpen ? "侧栏已打开。" : "侧栏已收回（或当前无可用显示器）。";
        CurrentWidthBox.Value = (decimal)CurrentScreenWidth(settingsProvider?.Get().EffectiveSidebar.DefaultWidthDip ?? 360);
    }

    private void ClearSidebar()
    {
        SidebarService?.ClearForCursorScreen();
        StatusText.Text = "已清空当前显示器的侧栏。";
    }

    private void OpenGallery() => GetSettingsWindow()?.SelectPage(typeof(Gallery));

    private Settings? GetSettingsWindow() => TopLevel.GetTopLevel(this) as Settings;
}
