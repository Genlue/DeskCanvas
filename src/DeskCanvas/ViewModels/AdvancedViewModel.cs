using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Threading;
using ReactiveUI;
using DeskCanvas.Core.Interfaces;
using DeskCanvas.Core.Models;
using DeskCanvas.Core.Models.Settings;
using DeskCanvas.Locales;
using DeskCanvas.Services;
using GridModeEnum = DeskCanvas.Core.Models.Settings.GridMode;

namespace DeskCanvas.ViewModels;

/// <summary>
/// A grid-mode option shown in the grid mode picker.
/// </summary>
public record GridModeOption(string Label, GridMode Value);

/// <summary>
/// An HTTP proxy option shown in the proxy picker.
/// </summary>
public record ProxyOption(string Label, string Value);

/// <summary>
/// A target screen option shown in the Advanced grid screen selector.
/// </summary>
public record ScreenGridTargetOption(string Label, string? ScreenConfigId, AttachedScreen? Attached);

/// <summary>
/// Advanced page view model. The manual-grid numeric fields target the selected screen
/// (or the global fallback grid).
/// </summary>
public class AdvancedViewModel : ReactiveObject, IDisposable
{
    private readonly IAppSettingsProvider appSettingsProvider;
    private readonly ILayoutProvider layoutProvider;
    private readonly DisplayMonitorService displayMonitor;

    private readonly ProfileService? profileService;
    private ScreenGridTargetOption? selectedScreenTarget;

    /// <summary>True while a coalesced <see cref="RaiseAllProperties"/> burst is queued.</summary>
    private bool raiseAllScheduled;

    /// <summary>True while a burst is being raised (guards against re-entrant raise).</summary>
    private bool raisingAllProperties;

    public AdvancedViewModel(IAppSettingsProvider appSettingsProvider, ILayoutProvider layoutProvider, DisplayMonitorService displayMonitor, ProfileService? profileService = null)
    {
        this.appSettingsProvider = appSettingsProvider;
        this.layoutProvider = layoutProvider;
        this.displayMonitor = displayMonitor;
        this.profileService = profileService;

        layoutProvider.DataChanged += OnLayoutDataChanged;
        appSettingsProvider.DataChanged += OnAppSettingsDataChanged;
        displayMonitor.ScreensChanged += OnScreensChanged;
        if (profileService != null)
        {
            profileService.ActiveProfileChanged += OnActiveProfileChanged;
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        layoutProvider.DataChanged -= OnLayoutDataChanged;
        appSettingsProvider.DataChanged -= OnAppSettingsDataChanged;
        displayMonitor.ScreensChanged -= OnScreensChanged;
        if (profileService != null)
        {
            profileService.ActiveProfileChanged -= OnActiveProfileChanged;
        }
    }

    private void OnScreensChanged(object? sender, EventArgs e) => RaiseAllProperties();
    private void OnActiveProfileChanged(object? sender, EventArgs e) => RaiseAllProperties();
    private void OnLayoutDataChanged(object? sender, ScreensLayout? oldData, ScreensLayout newData) => RaiseAllProperties();
    private void OnAppSettingsDataChanged(object? sender, AppSettings? oldData, AppSettings newData) => RaiseAllProperties();

    /// <summary>
    /// Coalesced, reentrancy-safe property-change burst.
    /// <para>
    /// Every notification source below (settings save, layout save, screen change, profile
    /// switch) can fire <i>inside</i> a two-way binding write: editing a grid field calls
    /// <see cref="SaveGrid"/>, which saves the settings, which makes every live widget
    /// re-save its layout, which raises the layout's <c>DataChanged</c> here — all before
    /// the binding that started it has finished. Raising <see cref="ScreenTargets"/> at
    /// that point replaces a <c>ComboBox</c>'s <c>ItemsSource</c> while Avalonia is still
    /// updating that control's selection, and <c>SelectionModel.SetSource</c> throws
    /// <c>InvalidOperationException: Cannot change source while update is in progress</c>.
    /// </para>
    /// <para>
    /// Deferring the burst to the dispatcher queue keeps the observable behaviour (the page
    /// still refreshes on every change) while guaranteeing it never runs in the middle of a
    /// binding/layout pass; the flag also collapses the N-per-edit bursts coming from the
    /// widgets into a single one.
    /// </para>
    /// </summary>
    private void RaiseAllProperties()
    {
        if (raiseAllScheduled) return;
        raiseAllScheduled = true;

        Dispatcher.UIThread.Post(() =>
        {
            raiseAllScheduled = false;
            RaiseAllPropertiesCore();
        }, DispatcherPriority.Background);
    }

    /// <summary>Guards against a synchronous re-entry coming from a binding write-back.</summary>
    private void RaiseAllPropertiesCore()
    {
        if (raisingAllProperties) return;
        raisingAllProperties = true;
        try
        {
            RaiseAllPropertiesUnsafe();
        }
        finally
        {
            raisingAllProperties = false;
        }
    }

    private void RaiseAllPropertiesUnsafe()
    {
        this.RaisePropertyChanged(nameof(GridMode));
        this.RaisePropertyChanged(nameof(IsManualGrid));
        this.RaisePropertyChanged(nameof(ScreenTargets));
        this.RaisePropertyChanged(nameof(SelectedScreenTarget));
        RaiseGridProperties();
        this.RaisePropertyChanged(nameof(SnapSize));
        this.RaisePropertyChanged(nameof(LockSize));
        this.RaisePropertyChanged(nameof(SnapPosition));
        this.RaisePropertyChanged(nameof(LockPosition));
        this.RaisePropertyChanged(nameof(ProxyMode));
        this.RaisePropertyChanged(nameof(ProxyCustomUrl));
        this.RaisePropertyChanged(nameof(UpdateUrl));
        this.RaisePropertyChanged(nameof(ShowTrayIcon));
    }

    private void RaiseGridProperties()
    {
        this.RaisePropertyChanged(nameof(GridColumns));
        this.RaisePropertyChanged(nameof(GridRows));
        this.RaisePropertyChanged(nameof(GridCellPercent));
        this.RaisePropertyChanged(nameof(GridXPercent));
        this.RaisePropertyChanged(nameof(GridYPercent));
        this.RaisePropertyChanged(nameof(CanResetScreenGrid));
    }

    public IReadOnlyList<ScreenGridTargetOption> ScreenTargets
    {
        get
        {
            var list = new List<ScreenGridTargetOption>
            {
                new(Locale.Settings_Advanced_GridTargetScreen_Global, null, null)
            };

            foreach (var attached in displayMonitor.Attached)
            {
                var config = attached.Config ?? displayMonitor.EnsureConfig(attached);
                var name = config.DisplayName 
                           ?? (attached.Identity.FriendlyName.Length > 0 ? attached.Identity.FriendlyName : "Screen");
                var suffix = attached.Screen.IsPrimary ? " [Primary]" : "";
                var label = $"{name} ({attached.Screen.Bounds.Width}×{attached.Screen.Bounds.Height}{suffix})";
                list.Add(new(label, config.Id, attached));
            }

            return list;
        }
    }

    public ScreenGridTargetOption SelectedScreenTarget
    {
        get
        {
            var targets = ScreenTargets;
            if (selectedScreenTarget != null)
            {
                var match = targets.FirstOrDefault(t =>
                    (t.ScreenConfigId != null && t.ScreenConfigId == selectedScreenTarget.ScreenConfigId)
                    || (t.Attached != null && selectedScreenTarget.Attached != null && t.Attached.Screen.Bounds == selectedScreenTarget.Attached.Screen.Bounds)
                    || (t.ScreenConfigId == null && selectedScreenTarget.ScreenConfigId == null && t.Attached == null && selectedScreenTarget.Attached == null));
                if (match != null) return match;
            }
            return targets[0];
        }
        set
        {
            selectedScreenTarget = value;
            this.RaisePropertyChanged(nameof(SelectedScreenTarget));
            RaiseGridProperties();
        }
    }

    public bool CanResetScreenGrid => SelectedScreenTarget.ScreenConfigId != null &&
        layoutProvider.Get().FindById(SelectedScreenTarget.ScreenConfigId)?.Grid != null;

    public void ResetScreenGrid()
    {
        var target = SelectedScreenTarget;
        if (target.ScreenConfigId == null) return;
        var screens = layoutProvider.Get();
        var screen = screens.FindById(target.ScreenConfigId);
        if (screen != null && screen.Grid != null)
        {
            layoutProvider.Save(screens.WithScreen(screen with { Grid = null }));
            displayMonitor.Refresh();
            RaiseGridProperties();
        }
    }

    /// <summary>
    /// The grid that actually applies to the currently selected screen target
    /// (per-screen entry → global <see cref="AppSettings.Grid"/> → default).
    /// </summary>
    private Grid EffectiveGrid
    {
        get
        {
            var target = SelectedScreenTarget;
            if (target.ScreenConfigId != null)
            {
                return layoutProvider.Get().FindById(target.ScreenConfigId)?.Grid
                       ?? appSettingsProvider.Get().Grid
                       ?? Grid.Default;
            }
            return appSettingsProvider.Get().Grid ?? Grid.Default;
        }
    }

    /// <summary>
    /// Persist a grid change to the selected screen's configuration (isolated),
    /// or to the global <see cref="AppSettings.Grid"/> when global default is selected.
    /// </summary>
    private void SaveGrid(Grid grid)
    {
        var target = SelectedScreenTarget;
        if (target.ScreenConfigId != null)
        {
            var screens = layoutProvider.Get();
            var screen = screens.FindById(target.ScreenConfigId);
            if (screen != null)
            {
                layoutProvider.Save(screens.WithScreen(screen with { Grid = grid }));
            }
            else if (target.Attached != null)
            {
                var config = displayMonitor.EnsureConfig(target.Attached);
                screens = layoutProvider.Get();
                layoutProvider.Save(screens.WithScreen(config with { Grid = grid }));
            }
            RaiseGridProperties();
            return;
        }

        appSettingsProvider.Save(appSettingsProvider.Get() with { Grid = grid });
        RaiseGridProperties();
    }

    // ---------- Grid mode ----------

    public GridModeOption[] GridModes =>
    [
        new(Locale.Settings_Advanced_GridMode_Manual, GridModeEnum.Manual),
        new(Locale.Settings_Advanced_GridMode_Free, GridModeEnum.Free)
    ];

    public GridModeOption GridMode
    {
        get => GridModes.FirstOrDefault(mode => mode.Value == appSettingsProvider.Get().Layout.GridMode)
               ?? GridModes[0];
        set
        {
            var settings = appSettingsProvider.Get();
            var newLayout = settings.Layout with { GridMode = value.Value };
            appSettingsProvider.Save(settings with { Layout = newLayout });
            this.RaisePropertyChanged(nameof(IsManualGrid));
        }
    }

    public bool IsManualGrid => appSettingsProvider.Get().Layout.GridMode == GridModeEnum.Manual;

    public int GridColumns
    {
        get => EffectiveGrid.Columns;
        set => SaveGrid(EffectiveGrid with { Columns = Math.Max(1, value) });
    }

    public int GridRows
    {
        get => EffectiveGrid.Rows;
        set => SaveGrid(EffectiveGrid with { Rows = Math.Max(1, value) });
    }

    public double GridCellPercent
    {
        get => EffectiveGrid.CellPercent;
        set => SaveGrid(EffectiveGrid with { CellPercent = Math.Max(0.01, value) });
    }

    public double GridXPercent
    {
        get => EffectiveGrid.XPercent;
        set => SaveGrid(EffectiveGrid with { XPercent = Math.Clamp(value, 0, 100) });
    }

    public double GridYPercent
    {
        get => EffectiveGrid.YPercent;
        set => SaveGrid(EffectiveGrid with { YPercent = Math.Clamp(value, 0, 100) });
    }

    // ---------- Sizing ----------

    public bool SnapPosition
    {
        get => appSettingsProvider.Get().Layout.SnapPosition;
        set
        {
            var settings = appSettingsProvider.Get();
            appSettingsProvider.Save(settings with { Layout = settings.Layout with { SnapPosition = value } });
        }
    }

    public bool SnapSize
    {
        get => appSettingsProvider.Get().Layout.SnapSize;
        set
        {
            var settings = appSettingsProvider.Get();
            appSettingsProvider.Save(settings with { Layout = settings.Layout with { SnapSize = value } });
        }
    }

    public bool LockPosition
    {
        get => appSettingsProvider.Get().Layout.LockPosition;
        set
        {
            var settings = appSettingsProvider.Get();
            appSettingsProvider.Save(settings with { Layout = settings.Layout with { LockPosition = value } });
        }
    }

    public bool LockSize
    {
        get => appSettingsProvider.Get().Layout.LockSize;
        set
        {
            var settings = appSettingsProvider.Get();
            appSettingsProvider.Save(settings with { Layout = settings.Layout with { LockSize = value } });
        }
    }

    // ---------- Network ----------

    public ProxyOption[] ProxyOptions =>
    [
        new(Locale.Settings_Advanced_Proxy_Direct, ""),
        new(Locale.Settings_Advanced_Proxy_System, "system"),
        new(Locale.Settings_Advanced_Proxy_Custom, "custom")
    ];

    /// <summary>
    /// Current proxy mode: Direct (""), System ("system") or Custom ("custom").
    /// </summary>
    public ProxyOption ProxyMode
    {
        get
        {
            var proxy = appSettingsProvider.Get().HttpProxy;
            if (string.IsNullOrEmpty(proxy)) return ProxyOptions[0];
            if (proxy == "system") return ProxyOptions[1];
            return ProxyOptions[2];
        }
        set
        {
            var settings = appSettingsProvider.Get();
            var proxy = value.Value switch
            {
                "system" => "system",
                "custom" => string.IsNullOrWhiteSpace(ProxyCustomUrl) ? "" : ProxyCustomUrl.Trim(),
                _ => ""
            };
            appSettingsProvider.Save(settings with { HttpProxy = proxy });
            this.RaisePropertyChanged(nameof(ProxyCustomUrl));
        }
    }

    /// <summary>
    /// Custom proxy URL (used when <see cref="ProxyMode"/> is Custom).
    /// </summary>
    public string ProxyCustomUrl
    {
        get
        {
            var proxy = appSettingsProvider.Get().HttpProxy;
            return proxy is null or "" or "system" ? "" : proxy;
        }
        set
        {
            var settings = appSettingsProvider.Get();
            appSettingsProvider.Save(settings with { HttpProxy = value });
        }
    }

    /// <summary>
    /// Custom update source URL; empty disables update checks.
    /// </summary>
    public string UpdateUrl
    {
        get => appSettingsProvider.Get().UpdateUrl ?? "";
        set
        {
            var settings = appSettingsProvider.Get();
            appSettingsProvider.Save(settings with { UpdateUrl = string.IsNullOrWhiteSpace(value) ? null : value.Trim() });
        }
    }

    /// <summary>
    /// Notification-area icon visibility. The tray menu and the widget context menu can switch it
    /// too; this row is what keeps it reachable from the settings window alone.
    /// </summary>
    public bool ShowTrayIcon
    {
        get => appSettingsProvider.Get().ShowTrayIcon;
        set
        {
            var settings = appSettingsProvider.Get();
            if (settings.ShowTrayIcon == value) return;
            appSettingsProvider.Save(settings with { ShowTrayIcon = value });
        }
    }
}