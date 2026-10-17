using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Threading;
using ReactiveUI;
using DeskCanvas.Core;
using DeskCanvas.Core.Interfaces;
using DeskCanvas.Core.Models;
using DeskCanvas.Core.Models.Settings;
using DeskCanvas.Core.Services;
using DeskCanvas.Locales;
using DeskCanvas.Services;

namespace DeskCanvas.ViewModels;

/// <summary>
/// An HTTP proxy option shown in the proxy picker.
/// </summary>
public record ProxyOption(string Label, string Value)
{
    public override string ToString() => Label;
}

/// <summary>
/// An 最大化或全屏时 option shown in the fullscreen-behaviour picker.
/// </summary>
public record FullscreenBehaviorOption(string Label, FullscreenBehavior Value)
{
    public override string ToString() => Label;
}

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

    /// <summary>True while subscribed to the change sources (guards the Attach/Dispose pair).</summary>
    private bool attached;

    public AdvancedViewModel(IAppSettingsProvider appSettingsProvider, ILayoutProvider layoutProvider, DisplayMonitorService displayMonitor, ProfileService? profileService = null)
    {
        this.appSettingsProvider = appSettingsProvider;
        this.layoutProvider = layoutProvider;
        this.displayMonitor = displayMonitor;
        this.profileService = profileService;

        Attach();
    }

    /// <summary>
    /// Subscribe to the sources whose changes this page mirrors. Called once at construction and
    /// again every time the page is <i>re-attached</i>; idempotent, so a Loaded firing twice
    /// cannot double-subscribe.
    /// <para>
    /// The re-attach case is real: the settings window caches its pages, and its close path
    /// detaches the current page to release its resources (a hidden window would otherwise keep
    /// the whole page tree, subscriptions included, alive). The cached instance comes back on
    /// the next open, and without this it would never see another change.
    /// </para>
    /// </summary>
    public void Attach()
    {
        if (attached) return;
        attached = true;

        layoutProvider.DataChanged += OnLayoutDataChanged;
        appSettingsProvider.DataChanged += OnAppSettingsDataChanged;
        displayMonitor.ScreensChanged += OnScreensChanged;
        if (profileService != null)
        {
            profileService.ActiveProfileChanged += OnActiveProfileChanged;
        }
    }

    /// <summary>
    /// Unsubscribe (the page left the tree). The cached page re-subscribes through
    /// <see cref="Attach"/> when it is attached again; idempotent.
    /// </summary>
    public void Dispose()
    {
        if (!attached) return;
        attached = false;

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
        this.RaisePropertyChanged(nameof(ScreenTargets));
        this.RaisePropertyChanged(nameof(SelectedScreenTarget));
        RaiseGridProperties();
        this.RaisePropertyChanged(nameof(LockPosition));
        this.RaisePropertyChanged(nameof(ProxyMode));
        this.RaisePropertyChanged(nameof(ProxyCustomUrl));
        this.RaisePropertyChanged(nameof(UpdateUrl));
        this.RaisePropertyChanged(nameof(ShowTrayIcon));
        this.RaisePropertyChanged(nameof(SelectedFullscreenBehavior));
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

    // ---------- Grid ----------

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

    // ---------- Positioning ----------

    public bool LockPosition
    {
        get => appSettingsProvider.Get().Layout.LockPosition;
        set
        {
            var settings = appSettingsProvider.Get();
            appSettingsProvider.Save(settings with { Layout = settings.Layout with { LockPosition = value } });
        }
    }

    // ---------- Network ----------

    private bool customProxySelected;

    public ProxyOption[] ProxyOptions =>
    [
        new(Locale.Settings_Advanced_Proxy_Auto, "auto"),
        new(Locale.Settings_Advanced_Proxy_Direct, "direct"),
        new(Locale.Settings_Advanced_Proxy_System, "system"),
        new(Locale.Settings_Advanced_Proxy_Custom, "custom")
    ];

    /// <summary>
    /// Current proxy mode: Auto ("auto"), Direct ("direct"), System ("system") or Custom ("custom").
    /// </summary>
    public ProxyOption ProxyMode
    {
        get
        {
            var proxy = appSettingsProvider.Get().HttpProxy;
            var mode = ProxySettings.GetMode(proxy);
            if (customProxySelected && mode != ProxyModeKind.Custom)
                return ProxyOptions[3];

            return mode switch
            {
                ProxyModeKind.Auto => ProxyOptions[0],
                ProxyModeKind.Direct => ProxyOptions[1],
                ProxyModeKind.System => ProxyOptions[2],
                ProxyModeKind.Custom => ProxyOptions[3],
                _ => ProxyOptions[0]
            };
        }
        set
        {
            if (value == null) return;
            var settings = appSettingsProvider.Get();
            if (value.Value == "custom")
            {
                customProxySelected = true;
                var customUrl = ProxyCustomUrl.Trim();
                if (!string.IsNullOrWhiteSpace(customUrl))
                {
                    appSettingsProvider.Save(settings with { HttpProxy = customUrl });
                }
                this.RaisePropertyChanged(nameof(ProxyMode));
                this.RaisePropertyChanged(nameof(ProxyCustomUrl));
                return;
            }

            customProxySelected = false;
            var proxy = value.Value switch
            {
                "direct" => "direct",
                "system" => "system",
                _ => "auto"
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
            return ProxySettings.GetMode(proxy) == ProxyModeKind.Custom ? (proxy ?? "") : "";
        }
        set
        {
            var trimmed = value?.Trim() ?? "";
            var settings = appSettingsProvider.Get();
            if (!string.IsNullOrWhiteSpace(trimmed))
            {
                customProxySelected = true;
                appSettingsProvider.Save(settings with { HttpProxy = trimmed });
            }
            else if (customProxySelected)
            {
                appSettingsProvider.Save(settings with { HttpProxy = "auto" });
            }
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

    // ---------- Fullscreen ----------

    /// <summary>The three 最大化或全屏时 choices, in picker order.</summary>
    public FullscreenBehaviorOption[] FullscreenOptions =>
    [
        new(Locale.Settings_Advanced_Fullscreen_Hide, FullscreenBehavior.Hide),
        new(Locale.Settings_Advanced_Fullscreen_Pause, FullscreenBehavior.Pause),
        new(Locale.Settings_Advanced_Fullscreen_None, FullscreenBehavior.None)
    ];

    /// <summary>
    /// What the host does with the widgets while every attached screen is covered by a
    /// fullscreen or maximized application. Saving lets the host re-apply the choice
    /// immediately while the desktop is covered (the App-level wiring listens for it).
    /// </summary>
    public FullscreenBehaviorOption SelectedFullscreenBehavior
    {
        get
        {
            var current = appSettingsProvider.Get().FullscreenBehavior ?? FullscreenBehavior.Hide;
            return FullscreenOptions.First(option => option.Value == current);
        }
        set
        {
            if (value is null) return;
            var settings = appSettingsProvider.Get();
            if ((settings.FullscreenBehavior ?? FullscreenBehavior.Hide) == value.Value) return;
            appSettingsProvider.Save(settings with { FullscreenBehavior = value.Value });
        }
    }

    // ---------- Data folder ----------

    /// <summary>
    /// The data folder in use. Read-only here: it is resolved once per process by
    /// <see cref="Const"/>, so a change is written by <see cref="DataFolderService"/> and applied
    /// by restarting the app.
    /// </summary>
    public string DataFolderPath => DataFolderService.Current;

    /// <summary>
    /// Localised description of the rule that picked <see cref="DataFolderPath"/> — the folder
    /// depends on the build, the exe location and the environment, and this makes it visible
    /// instead of a guess.
    /// </summary>
    public string DataFolderSourceText => DataFolderService.Source switch
    {
        DataFolderSource.Default => Locale.Settings_Advanced_DataFolder_Source_Default,
        DataFolderSource.Portable => Locale.Settings_Advanced_DataFolder_Source_Portable,
        DataFolderSource.PortableDev => Locale.Settings_Advanced_DataFolder_Source_PortableDev,
        DataFolderSource.ExePointer => Locale.Settings_Advanced_DataFolder_Source_ExePointer,
        DataFolderSource.AnchorPointer => Locale.Settings_Advanced_DataFolder_Source_AnchorPointer,
        DataFolderSource.EnvironmentVariable => Locale.Settings_Advanced_DataFolder_Source_Environment,
        DataFolderSource.TestHarness => Locale.Settings_Advanced_DataFolder_Source_TestHarness,
        _ => Locale.Settings_Advanced_DataFolder_Source_Default
    };

    /// <summary>
    /// False while the folder is pinned by the environment or by the harness sandbox: both outrank
    /// the saved pointer, so offering a picker would silently write a setting that never takes
    /// effect.
    /// </summary>
    public bool CanChangeDataFolder =>
        DataFolderService.Source is not (DataFolderSource.EnvironmentVariable or DataFolderSource.TestHarness);

    /// <summary>True when a saved user choice is in effect — the only case "restore default" undoes.</summary>
    public bool CanResetDataFolder => DataFolderService.IsUserPinned;

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