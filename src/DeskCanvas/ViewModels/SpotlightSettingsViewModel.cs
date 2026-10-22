using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using Avalonia;
using Avalonia.Styling;
using DeskCanvas.Core.Interfaces;
using DeskCanvas.Core.Models.Settings;
using DeskCanvas.Core.Services;
using DeskCanvas.Locales;
using DeskCanvas.Services;
using ReactiveUI;

namespace DeskCanvas.ViewModels;

/// <summary>A material choice in the launcher's appearance section (null = follow the global theme).</summary>
public record LauncherSurfaceOption(string Label, SurfaceStyle? Surface)
{
    public override string ToString() => Label;
}

/// <summary>Which of the two summon gestures a recorder is capturing.</summary>
public enum SpotlightHotkeyTarget
{
    /// <summary>应用列表式唤出 (Alt+Space).</summary>
    Launcher,

    /// <summary>搜索栏式唤出 (Alt+Shift+Space).</summary>
    Search,
}

/// <summary>One entry of the 明暗模式 list (跟随全局 / 跟随系统 / 浅色 / 深色).</summary>
public record LauncherDarkModeOption(string Label, LauncherThemeMode Mode)
{
    public override string ToString() => Label;
}

/// <summary>One entry of the 应用排序 list (文件夹顺序 / 名称 / 最近使用 / 使用频率).</summary>
public record LauncherSortOption(string Label, LauncherSortMode Mode)
{
    public override string ToString() => Label;
}

/// <summary>One entry of the 搜索引擎 list; <see cref="Id"/> is the storage key ("google", "custom").</summary>
public record LauncherWebEngineOption(string Label, string Id)
{
    public override string ToString() => Label;
}

/// <summary>One entry of the 浏览器 list; <see cref="Path"/> null = the Windows default browser.</summary>
public record LauncherBrowserOption(string Label, string? Path)
{
    public override string ToString() => Label;
}

/// <summary>
/// One row of the category-lock editor: the chip it binds, its current gesture and a row-local
/// error. The error lives on the row (not on the shared <c>Message</c>) so a rejected key does
/// not move the user away from the box they are working in.
/// </summary>
public sealed class CategoryKeyRowViewModel : INotifyPropertyChanged
{
    private string gestureText;
    private string? error;

    public CategoryKeyRowViewModel(string categoryKey, string label, string gestureText)
    {
        CategoryKey = categoryKey;
        Label = label;
        this.gestureText = gestureText;
    }

    /// <summary>Storage key (<see cref="LauncherCategoryKeys"/>).</summary>
    public string CategoryKey { get; }

    /// <summary>Localized chip label (全部 / 应用 / 图片 …).</summary>
    public string Label { get; }

    /// <summary>The gesture in force; empty means "this category has no key".</summary>
    public string GestureText
    {
        get => gestureText;
        private set
        {
            if (gestureText == value) return;
            gestureText = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(DisplayText));
        }
    }

    /// <summary>What the box shows: the gesture, or the localized "未设置" for an unbound row.</summary>
    public string DisplayText => gestureText.Length == 0
        ? Locale.Settings_Spotlight_CategoryKeys_None
        : gestureText;

    /// <summary>Row-local error ("已用于…" / "需要修饰键…"); cleared on the next successful commit.</summary>
    public string? Error
    {
        get => error;
        set
        {
            if (error == value) return;
            error = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasError));
        }
    }

    public bool HasError => !string.IsNullOrEmpty(error);

    /// <summary>
    /// Whether the ↑ button is live. 全部 never moves (it is the "no filter" entry and stays
    /// first), and the first movable row has nothing above it.
    /// </summary>
    public bool CanMoveUp
    {
        get => canMoveUp;
        private set
        {
            if (canMoveUp == value) return;
            canMoveUp = value;
            OnPropertyChanged();
        }
    }

    /// <summary>Whether the ↓ button is live (false for the last row).</summary>
    public bool CanMoveDown
    {
        get => canMoveDown;
        private set
        {
            if (canMoveDown == value) return;
            canMoveDown = value;
            OnPropertyChanged();
        }
    }

    private bool canMoveUp;
    private bool canMoveDown;

    /// <summary>
    /// The row's arrow state, recomputed by the page after every reorder. Position is the only
    /// thing it depends on, so it is pushed in rather than computed from the collection (the row
    /// has no idea where it sits).
    /// </summary>
    public void SetMoveState(bool up, bool down)
    {
        CanMoveUp = up;
        CanMoveDown = down;
    }

    /// <summary>Apply a committed gesture to the row (the settings page calls this after Save).</summary>
    public void SetGesture(string text) => GestureText = text;

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

/// <summary>
/// 聚焦搜索 settings page: on/off, the summon gesture, the shortcut folder, grid metrics and the
/// per-process blacklist.
/// <para>
/// Everything is written through <see cref="IAppSettingsProvider.Save"/> immediately (the same
/// contract the other settings pages follow); <see cref="SpotlightService"/> picks the change up
/// through <c>DataChanged</c> and re-registers the hotkey / re-indexes the folder, so the page
/// never talks to the registrar itself — except to <i>suspend</i> it while recording a gesture.
/// </para>
/// </summary>
public class SpotlightSettingsViewModel : ReactiveObject, IDisposable
{
    private readonly IAppSettingsProvider settings;
    private readonly SpotlightService? service;
    private readonly FileSearchService? files;

    private string? message;
    private bool attached;
    private double clipboardRetentionDraft;
    private string? recentStatus;
    private string? clipboardStatus;
    private Avalonia.Threading.DispatcherTimer? recentStatusTimer;
    private Avalonia.Threading.DispatcherTimer? clipboardStatusTimer;

    public SpotlightSettingsViewModel(IAppSettingsProvider settings, SpotlightService? service,
        FileSearchService? files = null)
    {
        this.settings = settings;
        this.service = service;
        this.files = files;

        var launcher = settings.Get().EffectiveLauncher;
        columnsDraft = launcher.Columns;
        webResultCountDraft = launcher.EffectiveWebResultCount;
        gapDraft = launcher.GapPercent;
        rowGapDraft = launcher.RowGapPercent;
        heightDraft = launcher.Height;
        nameLinesDraft = launcher.NameLines;
        heightDraft = launcher.Height;
        cornerRadiusDraft = launcher.CornerRadius;
        opacityDraft = launcher.OpacityLevel ?? settings.Get().Theme.OpacityLevel;
        clipboardRetentionDraft = launcher.ClipboardRetentionDays;
        spaceRecentLaunchesCountDraft = launcher.SpaceRecentLaunchesCount;
        spaceRecentLaunchesCountDraft = launcher.SpaceRecentLaunchesCount;
        ResetSearchPlaceholderCommand = ReactiveCommand.Create(ResetSearchPlaceholder);
        SeedGlassDrafts(launcher);

        SurfaceOptions =
        [
            new LauncherSurfaceOption(Locale.Settings_Spotlight_Appearance_FollowGlobal, null),
            new LauncherSurfaceOption(Locale.Settings_Spotlight_Appearance_Acrylic, SurfaceStyle.Acrylic),
            new LauncherSurfaceOption(Locale.Settings_Spotlight_Appearance_LiquidGlass, SurfaceStyle.LiquidGlass),
            new LauncherSurfaceOption(Locale.Settings_Spotlight_Appearance_FullColor, SurfaceStyle.FullColor),
        ];
        selectedSurface = SurfaceOptions.FirstOrDefault(o => o.Surface == launcher.Surface) ?? SurfaceOptions[0];

        DarkModeOptions =
        [
            new LauncherDarkModeOption(Locale.Settings_Spotlight_Appearance_FollowGlobal, LauncherThemeMode.FollowGlobal),
            new LauncherDarkModeOption(Locale.Settings_Spotlight_Appearance_FollowSystem, LauncherThemeMode.FollowSystem),
            new LauncherDarkModeOption(Locale.Settings_Spotlight_Appearance_Light, LauncherThemeMode.Light),
            new LauncherDarkModeOption(Locale.Settings_Spotlight_Appearance_Dark, LauncherThemeMode.Dark),
        ];
        selectedDarkMode = DarkModeOptions.FirstOrDefault(o => o.Mode == launcher.ThemeMode) ?? DarkModeOptions[0];

        SortOptions =
        [
            new LauncherSortOption(Locale.Settings_Spotlight_Sort_Name, LauncherSortMode.Name),
            new LauncherSortOption(Locale.Settings_Spotlight_Sort_LastUsed, LauncherSortMode.LastUsed),
            new LauncherSortOption(Locale.Settings_Spotlight_Sort_MostUsed, LauncherSortMode.MostUsed),
        ];
        selectedSort = SortOptions.FirstOrDefault(o => o.Mode == launcher.SortMode) ?? SortOptions[0];

        // 网页搜索: the preset table is the host's (the 搜索 component's own list cannot be
        // referenced — it is a plugin), and 自定义… is appended so a missing engine is never a
        // dead end.
        WebEngineOptions =
        [
            .. WebSearchEngines.All.Select(engine => new LauncherWebEngineOption(engine.Name, engine.Id)),
            new LauncherWebEngineOption(Locale.Settings_Spotlight_Web_Engine_Custom, LauncherSettings.CustomSearchEngine),
        ];
        selectedWebEngine = WebEngineOptions.FirstOrDefault(o =>
            string.Equals(o.Id, launcher.EffectiveSearchEngine, StringComparison.Ordinal)) ?? WebEngineOptions[0];

        WebBrowserOptions =
        [
            new LauncherBrowserOption(Locale.Settings_Spotlight_Web_Browser_Default, null),
            .. WebBrowsers.Detect().Select(browser => new LauncherBrowserOption(browser.Name, browser.Path)),
        ];
        selectedWebBrowser = WebBrowserOptions.FirstOrDefault(o =>
            string.Equals(o.Path, launcher.EffectiveSearchBrowser, StringComparison.OrdinalIgnoreCase))
            ?? WebBrowserOptions[0];

        RefreshBlacklist();
        RefreshClipboardExcluded();
        RefreshCategoryKeys();
        Attach();
    }

    // ---- state ---------------------------------------------------------------------

    public string SearchPlaceholder
    {
        get => settings.Get().EffectiveLauncher.SearchPlaceholder ?? string.Empty;
        set
        {
            if (string.Equals(value, settings.Get().EffectiveLauncher.SearchPlaceholder, StringComparison.Ordinal)) return;
            Save(launcher => launcher with { SearchPlaceholder = string.IsNullOrWhiteSpace(value) ? null : value.Trim() });
            this.RaisePropertyChanged();
        }
    }

    public ICommand ResetSearchPlaceholderCommand { get; }

    public void ResetSearchPlaceholder()
    {
        SearchPlaceholder = string.Empty;
    }

    public bool SpaceShowsRecentLaunches
    {
        get => settings.Get().EffectiveLauncher.SpaceShowsRecentLaunches;
        set
        {
            if (value == SpaceShowsRecentLaunches) return;
            Save(launcher => launcher with { SpaceShowsRecentLaunches = value });
            this.RaisePropertyChanged();
            this.RaisePropertyChanged(nameof(IsSpaceRecentLaunchesCountEditable));
        }
    }

    public bool IsSpaceRecentLaunchesCountEditable => SpaceShowsRecentLaunches;

    private double spaceRecentLaunchesCountDraft;

    public double SpaceRecentLaunchesCount
    {
        get => spaceRecentLaunchesCountDraft;
        set
        {
            var count = Math.Clamp(
                Math.Round(value),
                LauncherSettings.MinSpaceRecentLaunchesCount,
                LauncherSettings.MaxSpaceRecentLaunchesCount);
            if (Math.Abs(count - spaceRecentLaunchesCountDraft) < 0.001) return;
            spaceRecentLaunchesCountDraft = count;
            this.RaisePropertyChanged();
        }
    }

    public void CommitSpaceRecentLaunchesCount()
    {
        var count = (int)Math.Round(spaceRecentLaunchesCountDraft);
        if (count == settings.Get().EffectiveLauncher.SpaceRecentLaunchesCount) return;
        Save(launcher => launcher with { SpaceRecentLaunchesCount = count });
    }

    public bool Enabled
    {
        get => settings.Get().EffectiveLauncher.Enabled;
        set
        {
            if (value == Enabled) return;
            Save(launcher => launcher with { Enabled = value });
            this.RaisePropertyChanged();
            RaiseStatusChanged();
        }
    }

    /// <summary>
    /// Take over combinations <c>RegisterHotKey</c> cannot own (Win+S and friends) with the
    /// low-level keyboard hook. Off by default: the hook sees every keystroke and removes the
    /// combination from the system UI, so this is an explicit, administrator-grade choice.
    /// </summary>
    public bool TakeOverSystemHotkeys
    {
        get => settings.Get().EffectiveLauncher.TakeOverSystemHotkeys;
        set
        {
            if (value == TakeOverSystemHotkeys) return;
            Save(launcher => launcher with { TakeOverSystemHotkeys = value });
            this.RaisePropertyChanged();
            RaiseStatusChanged();
        }
    }

    /// <summary>The stored gesture text ("Alt+Space"). Writing it validates and saves.</summary>
    public string HotkeyText
    {
        get => settings.Get().EffectiveLauncher.EffectiveHotkey;
        set
        {
            if (!TrySetHotkey(value)) return;
        }
    }

    /// <summary>Which gesture the recorder is currently capturing (null = idle).</summary>
    private SpotlightHotkeyTarget? recordingTarget;

    /// <summary>True while the recorder is waiting for the list gesture.</summary>
    public bool IsRecording => recordingTarget == SpotlightHotkeyTarget.Launcher;

    /// <summary>True while the recorder is waiting for the search-bar gesture.</summary>
    public bool IsSearchRecording => recordingTarget == SpotlightHotkeyTarget.Search;

    public string RecordButtonText => IsRecording
        ? Locale.Settings_Spotlight_Hotkey_Recording
        : Locale.Settings_Spotlight_Hotkey_Record;

    public string SearchRecordButtonText => IsSearchRecording
        ? Locale.Settings_Spotlight_Hotkey_Recording
        : Locale.Settings_Spotlight_Hotkey_Record;

    /// <summary>The second gesture's stored text (搜索栏式唤出).</summary>
    public string SearchHotkeyText
    {
        get => settings.Get().EffectiveLauncher.EffectiveSearchHotkey;
        set
        {
            if (!TrySetSearchHotkey(value)) return;
        }
    }

    /// <summary>Registration state of the search gesture, spelled out like <see cref="StatusText"/>.</summary>
    public string SearchStatusText => service?.SearchStatus switch
    {
        SpotlightService.HotkeyStatus.Registered =>
            string.Format(Locale.Settings_Spotlight_Status_Registered, service.SearchGesture?.Text ?? SearchHotkeyText),
        SpotlightService.HotkeyStatus.TakenOver =>
            string.Format(Locale.Settings_Spotlight_Status_TakenOver, service.SearchGesture?.Text ?? SearchHotkeyText),
        SpotlightService.HotkeyStatus.Conflict => Locale.Settings_Spotlight_Status_SearchConflict,
        SpotlightService.HotkeyStatus.InvalidGesture => Locale.Settings_Spotlight_Status_Invalid,
        SpotlightService.HotkeyStatus.Unavailable => Locale.Settings_Spotlight_Status_Unavailable,
        _ => Locale.Settings_Spotlight_Status_Disabled,
    };

    /// <summary>Registration state, spelled out so a conflict is actionable instead of silent.</summary>
    public string StatusText => service?.Status switch
    {
        SpotlightService.HotkeyStatus.Registered =>
            string.Format(Locale.Settings_Spotlight_Status_Registered, service.Gesture?.Text ?? HotkeyText),
        SpotlightService.HotkeyStatus.TakenOver =>
            string.Format(Locale.Settings_Spotlight_Status_TakenOver, service.Gesture?.Text ?? HotkeyText),
        SpotlightService.HotkeyStatus.Conflict => Locale.Settings_Spotlight_Status_Conflict,
        SpotlightService.HotkeyStatus.InvalidGesture => Locale.Settings_Spotlight_Status_Invalid,
        SpotlightService.HotkeyStatus.Unavailable => Locale.Settings_Spotlight_Status_Unavailable,
        _ => Locale.Settings_Spotlight_Status_Disabled,
    };

    /// <summary>True when the gesture is live — drives the status dot colour.</summary>
    public bool IsHotkeyLive =>
        service?.Status is SpotlightService.HotkeyStatus.Registered
            or SpotlightService.HotkeyStatus.TakenOver;

    public string FolderPath => settings.Get().EffectiveLauncher.EffectiveFolder;

    // ---- grid metrics: draft while dragging, committed once (开发规范 §3.3) --------------

    private double columnsDraft;
    private double webResultCountDraft;
    private double gapDraft;
    private double rowGapDraft;
    private double nameLinesDraft;
    private double heightDraft;

    public double Columns
    {
        get => columnsDraft;
        set
        {
            var columns = Math.Round(value);
            if (Math.Abs(columns - columnsDraft) < 0.001) return;
            columnsDraft = columns;
            this.RaisePropertyChanged();
            this.RaisePropertyChanged(nameof(GapPreview));
        }
    }

    /// <summary>
    /// 竖直方向最大高度 in DIP (320–1200).
    /// </summary>
    public double Height
    {
        get => heightDraft;
        set
        {
            var height = Math.Round(value);
            if (Math.Abs(height - heightDraft) < 0.001) return;
            heightDraft = height;
            this.RaisePropertyChanged();
        }
    }

    /// <summary>Draw the app name under each grid icon.</summary>
    public bool ShowNames
    {
        get => settings.Get().EffectiveLauncher.ShowNames;
        set
        {
            if (value == ShowNames) return;
            Save(launcher => launcher with { ShowNames = value });
            this.RaisePropertyChanged();
            this.RaisePropertyChanged(nameof(GapPreview));
        }
    }

    /// <summary>
    /// The gutter between cells, as a percentage of a cell. There is deliberately no icon-size
    /// setting: the icon is the cell minus this gutter, so the grid always divides the row exactly.
    /// </summary>
    public double GapPercent
    {
        get => gapDraft;
        set
        {
            var gap = Math.Round(value);
            if (Math.Abs(gap - gapDraft) < 0.001) return;
            gapDraft = gap;
            this.RaisePropertyChanged();
            this.RaisePropertyChanged(nameof(GapPreview));
        }
    }

    /// <summary>
    /// The vertical gutter between rows, as a percentage of a cell. Separate from
    /// <see cref="GapPercent"/> on purpose: with 应用名称 on, the label block already makes a row
    /// taller, and the vertical gutter on top of it is the one thing that decides whether the grid
    /// reads as "breathing" or "stretched".
    /// </summary>
    public double RowGapPercent
    {
        get => rowGapDraft;
        set
        {
            var rowGap = Math.Round(value);
            if (Math.Abs(rowGap - rowGapDraft) < 0.001) return;
            rowGapDraft = rowGap;
            this.RaisePropertyChanged();
            this.RaisePropertyChanged(nameof(GapPreview));
        }
    }

    /// <summary>
    /// How many lines a tile name may occupy (1–3). Every tile reserves this many lines, so the
    /// icons of a row stay on one baseline whatever the names do.
    /// </summary>
    public double NameLines
    {
        get => nameLinesDraft;
        set
        {
            var lines = Math.Round(value);
            if (Math.Abs(lines - nameLinesDraft) < 0.001) return;
            nameLinesDraft = lines;
            this.RaisePropertyChanged();
            this.RaisePropertyChanged(nameof(GapPreview));
        }
    }

    /// <summary>
    /// What the two sliders currently produce, spelled out: the user cannot see the derived icon
    /// size anywhere else, and "18%" alone does not say what a tile will look like.
    /// </summary>
    public string GapPreview
    {
        get
        {
            var launcher = settings.Get().EffectiveLauncher;
            var metrics = LauncherGridMetrics.Compute(
                launcher.Width - 36, (int)Math.Round(columnsDraft), (int)Math.Round(gapDraft),
                launcher.ShowNames, (int)Math.Round(rowGapDraft), (int)Math.Round(nameLinesDraft));
            return string.Format(Locale.Settings_Spotlight_Gap_Preview,
                metrics.IconSize, metrics.Gap, metrics.CellWidth, metrics.RowHeight);
        }
    }

    /// <summary>
    /// Persist the grid sliders together. Called from the sliders' <c>ValueCommitted</c>, never
    /// from the value-changed path: a drag would otherwise write the settings file on every tick.
    /// </summary>
    public void CommitGridMetrics()
    {
        var current = settings.Get().EffectiveLauncher;
        var columns = (int)Math.Round(columnsDraft);
        var gap = (int)Math.Round(gapDraft);
        var rowGap = (int)Math.Round(rowGapDraft);
        var nameLines = (int)Math.Round(nameLinesDraft);
        var height = Math.Round(heightDraft);

        if (columns == current.Columns && gap == current.GapPercent
            && rowGap == current.RowGapPercent && nameLines == current.NameLines
            && Math.Abs(height - current.Height) < 0.001) return;

        Save(launcher => launcher with
        {
            Columns = columns,
            GapPercent = gap,
            RowGapPercent = rowGap,
            NameLines = nameLines,
            Height = height,
        });
    }

    public ObservableCollection<string> Blacklist { get; } = [];

    public bool IsBlacklistEmpty => Blacklist.Count == 0;

    public bool BlockFullscreen
    {
        get => settings.Get().EffectiveLauncher.BlockFullscreen;
        set
        {
            if (value == BlockFullscreen) return;
            Save(launcher => launcher with { BlockFullscreen = value });
            this.RaisePropertyChanged();
        }
    }

    /// <summary>Transient feedback line ("added chrome", "already listed", "no process found").</summary>
    public string? Message
    {
        get => message;
        private set
        {
            if (message == value) return;
            message = value;
            this.RaisePropertyChanged();
            this.RaisePropertyChanged(nameof(HasMessage));
        }
    }

    public bool HasMessage => !string.IsNullOrEmpty(message);

    // ---- appearance: independent theme / radius / liquid glass ----------------------

    private LauncherSurfaceOption selectedSurface = null!;
    private LauncherDarkModeOption selectedDarkMode = null!;
    private LauncherSortOption selectedSort = null!;
    private LauncherWebEngineOption selectedWebEngine = null!;
    private LauncherBrowserOption selectedWebBrowser = null!;

    public IReadOnlyList<LauncherSurfaceOption> SurfaceOptions { get; }

    public IReadOnlyList<LauncherDarkModeOption> DarkModeOptions { get; }

    /// <summary>Follow the application theme. Off = the fields below override it one at a time.</summary>
    public bool UseGlobalTheme
    {
        get => settings.Get().EffectiveLauncher.UseGlobalTheme;
        set
        {
            if (value == UseGlobalTheme) return;
            Save(launcher => launcher with { UseGlobalTheme = value });
            this.RaisePropertyChanged();
            RaiseAppearanceChanged();
        }
    }

    /// <summary>True when the appearance section shows its overrides.</summary>
    public bool IsAppearanceOverridden => !UseGlobalTheme;

    public LauncherSurfaceOption SelectedSurface
    {
        get => selectedSurface;
        set
        {
            if (value == null || ReferenceEquals(value, selectedSurface)) return;
            selectedSurface = value;
            Save(launcher => launcher with { Surface = value.Surface });
            this.RaisePropertyChanged();
            RaiseAppearanceChanged();
        }
    }

    public LauncherDarkModeOption SelectedDarkMode
    {
        get => selectedDarkMode;
        set
        {
            if (value == null || ReferenceEquals(value, selectedDarkMode)) return;
            selectedDarkMode = value;
            Save(launcher => launcher with { ThemeMode = value.Mode });
            this.RaisePropertyChanged();
            this.RaisePropertyChanged(nameof(BackgroundColorHint));
            RaiseAppearanceChanged();
        }
    }

    /// <summary>The app grid's order (文件夹顺序 / 名称 / 最近使用 / 使用频率).</summary>
    public IReadOnlyList<LauncherSortOption> SortOptions { get; }

    public LauncherSortOption SelectedSort
    {
        get => selectedSort;
        set
        {
            if (value == null || ReferenceEquals(value, selectedSort)) return;
            selectedSort = value;
            Save(launcher => launcher with { SortMode = value.Mode });
            this.RaisePropertyChanged();
        }
    }

    // ---- 网页搜索 (the 在浏览器中搜索 row) ------------------------------------------

    /// <summary>The preset engines, plus 自定义… as the last entry.</summary>
    public IReadOnlyList<LauncherWebEngineOption> WebEngineOptions { get; }

    /// <summary>默认浏览器 first, then every browser discovered on this machine.</summary>
    public IReadOnlyList<LauncherBrowserOption> WebBrowserOptions { get; }

    /// <summary>Show the 在浏览器中搜索 row for every query.</summary>
    public bool WebSearch
    {
        get => settings.Get().EffectiveLauncher.WebSearch;
        set
        {
            if (value == WebSearch) return;
            Save(launcher => launcher with { WebSearch = value });
            this.RaisePropertyChanged();
            this.RaisePropertyChanged(nameof(IsWebSearchEnabled));
        }
    }

    /// <summary>The engine / browser rows only matter while the web row is on.</summary>
    public bool IsWebSearchEnabled => WebSearch;

    /// <summary>
    /// How many real results the launcher lists under the 在浏览器中搜索 row (0 = only the action
    /// row). Each hit is one network request per query, so this is also the cost knob.
    /// <para>
    /// A draft while dragging, committed once — the same rule the grid sliders follow. Writing the
    /// settings file on every tick would re-run the launcher's own query dozens of times.
    /// </para>
    /// </summary>
    public double WebResultCount
    {
        get => webResultCountDraft;
        set
        {
            var count = Math.Clamp(
                Math.Round(value),
                LauncherSettings.MinWebResultCount,
                LauncherSettings.MaxWebResultCount);
            if (Math.Abs(count - webResultCountDraft) < 0.001) return;
            webResultCountDraft = count;
            this.RaisePropertyChanged();
        }
    }

    /// <summary>Persist 网页结果条数 (called from the slider's ValueCommitted).</summary>
    public void CommitWebResultCount()
    {
        var count = (int)Math.Round(webResultCountDraft);
        if (count == settings.Get().EffectiveLauncher.EffectiveWebResultCount) return;
        Save(launcher => launcher with { WebResultCount = count });
    }

    public LauncherWebEngineOption SelectedWebEngine
    {
        get => selectedWebEngine;
        set
        {
            if (value == null || ReferenceEquals(value, selectedWebEngine)) return;
            selectedWebEngine = value;
            Save(launcher => launcher with { SearchEngine = value.Id });
            this.RaisePropertyChanged();
            this.RaisePropertyChanged(nameof(IsCustomWebEngine));
            RaiseWebPreviewChanged();
        }
    }

    /// <summary>True while 自定义… is the selected engine — the template box is only then relevant.</summary>
    public bool IsCustomWebEngine =>
        string.Equals(selectedWebEngine.Id, LauncherSettings.CustomSearchEngine, StringComparison.Ordinal);

    /// <summary>The custom URL template. Stored as typed; validity is reported, never silently fixed.</summary>
    public string SearchEngineUrl
    {
        get => settings.Get().EffectiveLauncher.SearchEngineUrl ?? string.Empty;
        set
        {
            if (string.Equals(value, settings.Get().EffectiveLauncher.SearchEngineUrl, StringComparison.Ordinal)) return;
            Save(launcher => launcher with { SearchEngineUrl = value });
            this.RaisePropertyChanged();
            RaiseWebPreviewChanged();
        }
    }

    public LauncherBrowserOption SelectedWebBrowser
    {
        get => selectedWebBrowser;
        set
        {
            if (value == null || ReferenceEquals(value, selectedWebBrowser)) return;
            selectedWebBrowser = value;
            Save(launcher => launcher with { SearchBrowser = value.Path });
            this.RaisePropertyChanged();
            RaiseWebPreviewChanged();
        }
    }

    /// <summary>
    /// The address the current combination would open for a sample query, spelled out. "Which
    /// engine" and "which browser" are two choices that only meet in one place, and this is where
    /// the user can see them meet before pressing Enter in the launcher.
    /// </summary>
    public string WebPreviewText
    {
        get
        {
            var launcher = settings.Get().EffectiveLauncher;
            var engine = WebSearchEngines.Resolve(launcher);
            var browser = WebBrowsers.Resolve(launcher.EffectiveSearchBrowser);
            return string.Format(
                Locale.Settings_Spotlight_Web_Preview,
                browser.Name,
                engine.UrlFor(Locale.Settings_Spotlight_Web_SampleQuery));
        }
    }

    /// <summary>
    /// A custom template that cannot carry the query. Shown only for the custom engine: a preset
    /// that does not resolve is the host's problem (it falls back), a custom one is the user's.
    /// </summary>
    public string? WebTemplateError
    {
        get
        {
            if (!IsCustomWebEngine) return null;
            var raw = settings.Get().EffectiveLauncher.SearchEngineUrl;
            if (string.IsNullOrWhiteSpace(raw)) return null;
            return settings.Get().EffectiveLauncher.EffectiveSearchEngineUrl == null
                ? Locale.Settings_Spotlight_Web_Custom_Invalid
                : null;
        }
    }

    public bool HasWebTemplateError => WebTemplateError != null;

    /// <summary>Re-render the sample line after any of the three web choices (or a custom URL) moved.</summary>
    private void RaiseWebPreviewChanged()
    {
        this.RaisePropertyChanged(nameof(WebPreviewText));
        this.RaisePropertyChanged(nameof(WebTemplateError));
        this.RaisePropertyChanged(nameof(HasWebTemplateError));
    }

    /// <summary>
    /// Play the 180 ms fade + micro-scale when the overlay opens. On by default; off is for users
    /// who summon it dozens of times a day and want the card to be there instantly.
    /// </summary>
    public bool AnimateSummon
    {
        get => settings.Get().EffectiveLauncher.AnimateSummon;
        set
        {
            if (value == AnimateSummon) return;
            Save(launcher => launcher with { AnimateSummon = value });
            this.RaisePropertyChanged();
        }
    }

    /// <summary>Follow <c>Dimensions.Radius</c> (the widget card radius) instead of the slider.</summary>
    public bool FollowWidgetRadius
    {
        get => settings.Get().EffectiveLauncher.UseWidgetCornerRadius;
        set
        {
            if (value == FollowWidgetRadius) return;
            Save(launcher => launcher with { UseWidgetCornerRadius = value });
            this.RaisePropertyChanged();
            this.RaisePropertyChanged(nameof(IsCornerRadiusEditable));
        }
    }

    public bool IsCornerRadiusEditable => !FollowWidgetRadius;

    private double cornerRadiusDraft;
    private double opacityDraft;

    /// <summary>Card corner radius in DIP (0–100). Draft while dragging; committed on release.</summary>
    public double CornerRadius
    {
        get => cornerRadiusDraft;
        set
        {
            var radius = Math.Round(value);
            if (Math.Abs(radius - cornerRadiusDraft) < 0.001) return;
            cornerRadiusDraft = radius;
            this.RaisePropertyChanged();
        }
    }

    /// <summary>Card opacity in percent. Draft while dragging; committed on release.</summary>
    public double OpacityPercent
    {
        get => opacityDraft * 100.0;
        set
        {
            var percent = Math.Round(value);
            if (Math.Abs(percent - opacityDraft * 100.0) < 0.001) return;
            opacityDraft = percent / 100.0;
            this.RaisePropertyChanged();
        }
    }

    /// <summary>Persist the radius and opacity sliders (never from the value-changed path).</summary>
    public void CommitAppearance()
    {
        var current = settings.Get().EffectiveLauncher;
        var radius = (int)Math.Round(cornerRadiusDraft);
        var opacity = Math.Round(opacityDraft, 2);

        var radiusChanged = Math.Abs(radius - current.CornerRadius) > 0.001;
        var opacityChanged = Math.Abs(opacity - (current.OpacityLevel ?? settings.Get().Theme.OpacityLevel)) > 0.001;
        if (!radiusChanged && !opacityChanged) return;

        Save(launcher => launcher with
        {
            CornerRadius = radiusChanged ? radius : launcher.CornerRadius,
            OpacityLevel = opacityChanged ? opacity : launcher.OpacityLevel,
        });
    }

    // ---- liquid glass: the launcher's own pipeline ---------------------------------

    /// <summary>Sample the live screen behind the overlay instead of the wallpaper.</summary>
    public bool GlassSampleScreen
    {
        get => settings.Get().EffectiveLauncher.GlassSampleScreen;
        set
        {
            if (value == GlassSampleScreen) return;
            Save(launcher => launcher with { GlassSampleScreen = value });
            this.RaisePropertyChanged();
            this.RaisePropertyChanged(nameof(IsGlassIntervalEditable));
        this.RaisePropertyChanged(nameof(ScreenSamplingStatusText));
        }
    }

    /// <summary>The interval slider only means anything while real-time sampling is on.</summary>
    public bool IsGlassIntervalEditable => GlassSampleScreen;

    private double glassIntervalDraft;

    /// <summary>Screen re-capture period in milliseconds (draft; committed on release).</summary>
    public double GlassSampleInterval
    {
        get => glassIntervalDraft;
        set
        {
            var interval = Math.Round(value);
            if (Math.Abs(interval - glassIntervalDraft) < 0.001) return;
            glassIntervalDraft = interval;
            this.RaisePropertyChanged();
        }
    }

    /// <summary>The liquid-glass section only matters while the effective material is glass.</summary>
    public bool IsGlassSectionVisible => EffectiveTheme().UsesRenderedGlass;

    /// <summary>
    /// Whether the 液态玻璃 block is unfolded. View state only (the same rule as 应用主题's optics
    /// fold): the section is expert tuning, and the launcher is usable without opening it once.
    /// </summary>
    public bool GlassSectionExpanded
    {
        get => glassSectionExpanded;
        set
        {
            if (glassSectionExpanded == value) return;
            glassSectionExpanded = value;
            this.RaisePropertyChanged();
        }
    }

    private bool glassSectionExpanded;

    /// <summary>
    /// Whether the screen pipeline is actually producing frames. "It still looks like the
    /// wallpaper" has exactly three causes — sampling off, no frame captured, or a frame that is
    /// genuinely the desktop — and this line tells them apart without a debugger.
    /// </summary>
    public string ScreenSamplingStatusText
    {
        get
        {
            if (!GlassSampleScreen) return Locale.Settings_Spotlight_Glass_Sample_Off;
            // CaptureCount and HasFrame are updated under the same lock, so counting frames is the
            // single honest answer to "has a screen frame ever been taken".
            if (ScreenSnapshotProvider.CaptureCount == 0) return Locale.Settings_Spotlight_Glass_Sample_None;
            return string.Format(Locale.Settings_Spotlight_Glass_Sample_Ok,
                ScreenSnapshotProvider.CaptureCount, ScreenSnapshotProvider.LastFrameMean);
        }
    }

    private double clarityDraft, blurDraft, refractionDraft, highlightDraft, vibrancyDraft, dispersionDraft;

    public double GlassClarity
    {
        get => clarityDraft;
        set => SetGlassDraft(ref clarityDraft, value);
    }

    public double GlassBlur
    {
        get => blurDraft;
        set => SetGlassDraft(ref blurDraft, value);
    }

    public double GlassRefraction
    {
        get => refractionDraft;
        set => SetGlassDraft(ref refractionDraft, value);
    }

    public double GlassHighlight
    {
        get => highlightDraft;
        set => SetGlassDraft(ref highlightDraft, value);
    }

    public double GlassVibrancy
    {
        get => vibrancyDraft;
        set => SetGlassDraft(ref vibrancyDraft, value);
    }

    public double GlassDispersion
    {
        get => dispersionDraft;
        set => SetGlassDraft(ref dispersionDraft, value);
    }

    private void SetGlassDraft(ref double field, double value)
    {
        var rounded = Math.Round(value);
        if (Math.Abs(rounded - field) < 0.001) return;
        field = rounded;
        this.RaisePropertyChanged();
    }

    /// <summary>
    /// Persist the six optics sliders as one override record. Anything the launcher has not
    /// overridden yet starts from the global glass parameters, so a single slider move never
    /// silently resets the others to their defaults.
    /// </summary>
    public void CommitGlass()
    {
        var current = settings.Get().EffectiveLauncher;
        // The baseline is the launcher's own record (or its defaults) — never the widget material,
        // so moving one slider cannot silently import the app's glass settings.
        var baseline = current.EffectiveLiquidGlass;

        var next = (baseline with
        {
            BackdropClarity = clarityDraft,
            Blur = blurDraft,
            Refraction = refractionDraft,
            Highlight = highlightDraft,
            Vibrancy = vibrancyDraft,
            Dispersion = dispersionDraft,
        }).Normalize();

        // The sampling interval is a launcher setting (it belongs to this panel's pipeline), not
        // part of the shared optics record — but it is committed from the same release.
        var interval = (int)Math.Round(glassIntervalDraft);
        var intervalChanged = interval != current.GlassSampleInterval;

        if (next == current.LiquidGlass && !intervalChanged) return;

        Save(launcher => (launcher with
        {
            LiquidGlass = next,
            GlassSampleInterval = intervalChanged ? interval : launcher.GlassSampleInterval,
        }).Normalize());
    }

    /// <summary>The theme the launcher would render with right now (override applied).</summary>
    private Theme EffectiveTheme()
    {
        var global = PanelAppearanceResolver.ResolveEffectiveTheme(settings, window: null);
        var launcher = settings.Get().EffectiveLauncher;
        return launcher.ResolveTheme(global, ResolveIsDark(launcher, global));
    }

    /// <summary>
    /// The launcher's own light/dark mode, from the settings page (no window of its own): the app's
    /// variant stands in for "what the widgets show", and Windows' variant for 跟随系统.
    /// </summary>
    private static bool ResolveIsDark(LauncherSettings launcher, Theme global)
    {
        var appIsDark = Application.Current?.ActualThemeVariant == ThemeVariant.Dark;
        return launcher.ResolveIsDark(global, appIsDark, PanelAppearanceResolver.IsSystemDark(appIsDark));
    }

    private void SeedGlassDrafts(LauncherSettings launcher)
    {
        // The launcher's own optics, never the widget material's: an unset record means the
        // launcher defaults, so the sliders show what the card is really using.
        var glass = launcher.EffectiveLiquidGlass;
        clarityDraft = glass.BackdropClarity;
        blurDraft = glass.Blur;
        refractionDraft = glass.Refraction;
        highlightDraft = glass.Highlight;
        vibrancyDraft = glass.Vibrancy;
        dispersionDraft = glass.Dispersion;
        glassIntervalDraft = launcher.GlassSampleInterval;
    }

    private void RaiseAppearanceChanged()
    {
        this.RaisePropertyChanged(nameof(IsAppearanceOverridden));
        this.RaisePropertyChanged(nameof(IsGlassSectionVisible));
        this.RaisePropertyChanged(nameof(BackgroundColorHint));
    }

    // ---- file search ---------------------------------------------------------------

    /// <summary>Also search files while the user types.</summary>
    public bool FileSearch
    {
        get => settings.Get().EffectiveLauncher.FileSearch;
        set
        {
            if (value == FileSearch) return;
            Save(launcher => launcher with { FileSearch = value });
            this.RaisePropertyChanged();
            this.RaisePropertyChanged(nameof(IsFileSearchEnabled));
        }
    }

    /// <summary>The Everything sub-options only matter while file search is on.</summary>
    public bool IsFileSearchEnabled => FileSearch;

    /// <summary>Start Everything minimised when a query needs its index.</summary>
    public bool AutoStartEverything
    {
        get => settings.Get().EffectiveLauncher.AutoStartEverything;
        set
        {
            if (value == AutoStartEverything) return;
            Save(launcher => launcher with { AutoStartEverything = value });
            this.RaisePropertyChanged();
        }
    }

    /// <summary>
    /// One line about the engine that would answer right now. Read live from the services, so the
    /// page tells the truth after the user starts or closes Everything instead of caching a state.
    /// </summary>
    public string FileSearchStatusText
    {
        get
        {
            if (files == null) return string.Empty;

            // A window is not a working connection: report what the last query actually got.
            if (files.IsEverythingRunning)
            {
                return files.EverythingStatus switch
                {
                    EverythingStatus.AccessDenied => Locale.Settings_Spotlight_Files_Status_Elevated,
                    EverythingStatus.Failed => Locale.Settings_Spotlight_Files_Status_Unresponsive,
                    _ => Locale.Settings_Spotlight_Files_Status_Connected,
                };
            }

            if (files.EverythingStatus == EverythingStatus.AccessDenied)
                return Locale.Settings_Spotlight_Files_Status_Elevated;
            if (files.EverythingPath == null) return Locale.Settings_Spotlight_Files_Status_NotInstalled;
            return Locale.Settings_Spotlight_Files_Status_NotRunning;
        }
    }

    /// <summary>False when Everything is not installed — the button would do nothing.</summary>
    public bool CanStartEverything => files?.EverythingPath != null && !(files?.IsEverythingRunning ?? true);

    /// <summary>Start the Everything client (the index is only reachable while it runs).</summary>
    public void StartEverything()
    {
        if (files == null) return;

        var started = files.StartEverything();
        Message = started ? null : Locale.Settings_Spotlight_Files_Status_NotInstalled;
        this.RaisePropertyChanged(nameof(FileSearchStatusText));
        this.RaisePropertyChanged(nameof(CanStartEverything));
    }

    /// <summary>Drop the built-in folder index so the next query re-reads the folders.</summary>
    public void RefreshIndex()
    {
        files?.InvalidateIndex();
        this.RaisePropertyChanged(nameof(FileSearchStatusText));
    }

    // ---- results behaviour: recents / preview --------------------------------------

    /// <summary>Show the most recently used apps as the empty-query row.</summary>
    public bool RecentApps
    {
        get => settings.Get().EffectiveLauncher.RecentApps;
        set
        {
            if (value == RecentApps) return;
            Save(launcher => launcher with { RecentApps = value });
            this.RaisePropertyChanged();
        }
    }

    /// <summary>Show the preview pane next to the selected result.</summary>
    public bool PreviewBar
    {
        get => settings.Get().EffectiveLauncher.PreviewBar;
        set
        {
            if (value == PreviewBar) return;
            Save(launcher => launcher with { PreviewBar = value });
            this.RaisePropertyChanged();
        }
    }

    /// <summary>Transient feedback after the usage store was cleared (auto-hides after 3 s).</summary>
    public string? RecentStatusText => recentStatus;

    public bool HasRecentStatus => recentStatus != null;

    /// <summary>Wipe every usage record (launch counts, pins, quick keys).</summary>
    public void ClearUsage()
    {
        service?.Usage.Clear();
        FlashRecentStatus(Locale.Settings_Spotlight_Recent_Cleared);
    }

    private void FlashRecentStatus(string text)
    {
        recentStatus = text;
        this.RaisePropertyChanged(nameof(RecentStatusText));
        this.RaisePropertyChanged(nameof(HasRecentStatus));

        if (recentStatusTimer == null)
        {
            recentStatusTimer = new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            recentStatusTimer.Tick += (_, _) =>
            {
                recentStatusTimer.Stop();
                recentStatus = null;
                this.RaisePropertyChanged(nameof(RecentStatusText));
                this.RaisePropertyChanged(nameof(HasRecentStatus));
            };
        }
        recentStatusTimer.Stop();
        recentStatusTimer.Start();
    }

    // ---- category lock keys (类别快捷键) -------------------------------------------

    /// <summary>One row per chip, in bar order (the same order the F-keys select).</summary>
    public ObservableCollection<CategoryKeyRowViewModel> CategoryKeys { get; } = [];

    /// <summary>
    /// Whether the 类别快捷键 block is unfolded. View state only, like 应用主题's optics fold: it
    /// says nothing about how the launcher behaves and is not written to the settings file.
    /// </summary>
    public bool CategoryKeysExpanded
    {
        get => categoryKeysExpanded;
        set
        {
            if (categoryKeysExpanded == value) return;
            categoryKeysExpanded = value;
            this.RaisePropertyChanged();
        }
    }

    private bool categoryKeysExpanded;

    /// <summary>
    /// Rebuild the rows from the settings: every category gets a row, bound or not, so the page
    /// always shows the complete F-key map instead of only the customized entries. The row order
    /// is the chip order the launcher draws — this list doubles as the 类别排序 editor.
    /// </summary>
    public void RefreshCategoryKeys()
    {
        var launcher = settings.Get().EffectiveLauncher;
        var hotkeys = launcher.EffectiveCategoryHotkeys;

        CategoryKeys.Clear();
        foreach (var key in launcher.EffectiveCategoryOrder)
        {
            var label = CategoryLabel(key);
            CategoryKeys.Add(new CategoryKeyRowViewModel(
                key, label, hotkeys.TryGetValue(key, out var text) ? text : string.Empty));
        }

        RefreshCategoryMoveState();
    }

    /// <summary>
    /// Move one category one slot up / down and persist the whole order. 全部 is nailed to the
    /// first slot: it is "no filter", not a category of results, and every empty-query path
    /// assumes it sits there.
    /// </summary>
    public void MoveCategory(CategoryKeyRowViewModel row, int delta)
    {
        if (string.Equals(row.CategoryKey, LauncherCategoryKeys.All, StringComparison.Ordinal)) return;

        var index = CategoryKeys.IndexOf(row);
        if (index < 0) return;

        // 1, not 0: slot 0 is 全部, which never moves.
        var target = index + delta;
        if (target < 1 || target >= CategoryKeys.Count) return;

        row.Error = null;
        CategoryKeys.Move(index, target);
        RefreshCategoryMoveState();

        // Two visible rows swapped: the whole order is written, so a later reload cannot depend on
        // the order this page happened to build its list in.
        Save(launcher => launcher.WithCategoryOrder(CategoryKeys.Select(item => item.CategoryKey)));
    }

    /// <summary>
    /// Push the arrows' enabled state. Index 0 is 全部 (never movable), so the first movable row is
    /// index 1 and it has nothing above it.
    /// </summary>
    private void RefreshCategoryMoveState()
    {
        for (var i = 0; i < CategoryKeys.Count; i++)
        {
            var row = CategoryKeys[i];
            var movable = !string.Equals(row.CategoryKey, LauncherCategoryKeys.All, StringComparison.Ordinal);
            row.SetMoveState(movable && i > 1, movable && i < CategoryKeys.Count - 1);
        }
    }

    /// <summary>
    /// Commit a recorded gesture to one category. Rejects an unparseable gesture and a duplicate
    /// binding (the overlay could only honour one of the two, and silently disabling the other
    /// would look like a lost setting), writing the error onto the row.
    /// </summary>
    public bool TrySetCategoryKey(CategoryKeyRowViewModel row, string? gesture)
    {
        var text = gesture?.Trim() ?? string.Empty;

        // Re-recording the same gesture is a no-op: saving would rebuild the row list under the
        // user's focus for nothing.
        if (string.Equals(row.GestureText, text, StringComparison.OrdinalIgnoreCase))
        {
            row.Error = null;
            return true;
        }

        if (text.Length > 0 && !GlobalHotkeyGesture.TryParse(text, out _, out _))
        {
            row.Error = Locale.Settings_Spotlight_CategoryKeys_Invalid;
            return false;
        }

        if (text.Length > 0)
        {
            var owner = CategoryKeys.FirstOrDefault(other =>
                !ReferenceEquals(other, row)
                && string.Equals(other.GestureText, text, StringComparison.OrdinalIgnoreCase));
            if (owner != null)
            {
                row.Error = string.Format(Locale.Settings_Spotlight_CategoryKeys_Taken, text, owner.Label);
                return false;
            }
        }

        row.Error = null;
        row.SetGesture(text);
        Save(launcher => launcher.WithCategoryHotkey(row.CategoryKey, text));
        return true;
    }

    /// <summary>Clear one category's key (the row's ✕).</summary>
    public void ClearCategoryKey(CategoryKeyRowViewModel row)
    {
        row.Error = null;
        row.SetGesture(string.Empty);
        Save(launcher => launcher.WithCategoryHotkey(row.CategoryKey, null));
    }

    /// <summary>Restore the F1–F7 defaults for every category.</summary>
    public void ResetCategoryKeys()
    {
        Save(launcher => launcher.WithoutCategoryHotkeys());
        RefreshCategoryKeys();
    }

    /// <summary>Localized chip label for a storage key.</summary>
    private static string CategoryLabel(string key) => key switch
    {
        LauncherCategoryKeys.All => Locale.Spotlight_Category_All,
        LauncherCategoryKeys.Apps => Locale.Spotlight_Category_Apps,
        LauncherCategoryKeys.Images => Locale.Spotlight_Category_Images,
        LauncherCategoryKeys.Files => Locale.Spotlight_Category_Files,
        LauncherCategoryKeys.Folders => Locale.Spotlight_Category_Folders,
        LauncherCategoryKeys.Widgets => Locale.Spotlight_Category_Widgets,
        LauncherCategoryKeys.Web => Locale.Spotlight_Category_Web,
        _ => Locale.Spotlight_Category_Clipboard,
    };

    // ---- clipboard history ----------------------------------------------------------

    /// <summary>Record copied text and links into the local clipboard history.</summary>
    public bool ClipboardHistory
    {
        get => settings.Get().EffectiveLauncher.ClipboardHistory;
        set
        {
            if (value == ClipboardHistory) return;
            Save(launcher => launcher with { ClipboardHistory = value });
            this.RaisePropertyChanged();
            this.RaisePropertyChanged(nameof(ClipboardCountText));
        }
    }

    /// <summary>
    /// How many entries the history holds right now. A stopped or absent service and a switched
    /// off feature both read as 0 — the line must not advertise entries while the feature is off.
    /// </summary>
    public string ClipboardCountText
    {
        get
        {
            var count = service?.Clipboard is { } clipboard && settings.Get().EffectiveLauncher.ClipboardHistory
                ? clipboard.Entries.Count
                : 0;
            return string.Format(Locale.Settings_Spotlight_Clipboard_Count, count);
        }
    }

    /// <summary>Slider draft in days (1–30); committed on release like the other sliders.</summary>
    public double ClipboardRetentionDays
    {
        get => clipboardRetentionDraft;
        set
        {
            var days = Math.Round(value);
            if (Math.Abs(days - clipboardRetentionDraft) < 0.001) return;
            clipboardRetentionDraft = days;
            this.RaisePropertyChanged();
            this.RaisePropertyChanged(nameof(ClipboardRetentionUnit));
        }
    }

    /// <summary>The slider's unit label: the localized "{0} days" format.</summary>
    public string ClipboardRetentionUnit
        => string.Format(Locale.Settings_Spotlight_Clipboard_Days, (int)Math.Round(clipboardRetentionDraft));

    /// <summary>Persist the retention slider (from <c>ValueCommitted</c>, never the drag path).</summary>
    public void CommitClipboardRetention()
    {
        var days = Math.Clamp((int)Math.Round(clipboardRetentionDraft),
            LauncherSettings.MinClipboardRetentionDays, LauncherSettings.MaxClipboardRetentionDays);
        if (days == settings.Get().EffectiveLauncher.ClipboardRetentionDays) return;

        Save(launcher => launcher with { ClipboardRetentionDays = days });
    }

    /// <summary>Transient feedback after the history was cleared (auto-hides after 3 s).</summary>
    public string? ClipboardStatusText => clipboardStatus;

    public bool HasClipboardStatus => clipboardStatus != null;

    /// <summary>Wipe the clipboard history (memory + disk file).</summary>
    public void ClearClipboard()
    {
        service?.Clipboard.Clear();
        this.RaisePropertyChanged(nameof(ClipboardCountText));
        FlashClipboardStatus(Locale.Settings_Spotlight_Clipboard_Cleared);
    }

    private void FlashClipboardStatus(string text)
    {
        clipboardStatus = text;
        this.RaisePropertyChanged(nameof(ClipboardStatusText));
        this.RaisePropertyChanged(nameof(HasClipboardStatus));

        if (clipboardStatusTimer == null)
        {
            clipboardStatusTimer = new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            clipboardStatusTimer.Tick += (_, _) =>
            {
                clipboardStatusTimer.Stop();
                clipboardStatus = null;
                this.RaisePropertyChanged(nameof(ClipboardStatusText));
                this.RaisePropertyChanged(nameof(HasClipboardStatus));
            };
        }
        clipboardStatusTimer.Stop();
        clipboardStatusTimer.Start();
    }

    public ObservableCollection<string> ClipboardExcluded { get; } = [];

    public bool IsClipboardExcludedEmpty => ClipboardExcluded.Count == 0;

    /// <summary>Add an app to the clipboard exclusion list (same rules as the blacklist).</summary>
    public void AddClipboardExclusion(string? name)
    {
        var normalized = LauncherSettings.NormalizeProcessName(name);
        if (normalized == null)
        {
            Message = Locale.Settings_Spotlight_Blacklist_Invalid;
            return;
        }

        if (settings.Get().EffectiveLauncher.IsClipboardExcluded(normalized))
        {
            Message = string.Format(Locale.Settings_Spotlight_Blacklist_Already, normalized);
            return;
        }

        Save(launcher => launcher with { ClipboardExcluded = [.. launcher.EffectiveClipboardExcluded, normalized] });
        Message = string.Format(Locale.Settings_Spotlight_Blacklist_Added, normalized);
    }

    /// <summary>Remove an app from the clipboard exclusion list.</summary>
    public void RemoveClipboardExclusion(string? name)
    {
        var normalized = LauncherSettings.NormalizeProcessName(name);
        if (normalized == null) return;

        Save(launcher =>
        {
            var remaining = launcher.EffectiveClipboardExcluded
                .Where(entry => !string.Equals(
                    LauncherSettings.NormalizeProcessName(entry), normalized, StringComparison.Ordinal))
                .ToList();
            return launcher with { ClipboardExcluded = remaining };
        });
        Message = null;
    }

    private void RefreshClipboardExcluded()
    {
        var current = settings.Get().EffectiveLauncher.EffectiveClipboardExcluded;

        if (ClipboardExcluded.SequenceEqual(current, StringComparer.Ordinal))
        {
            this.RaisePropertyChanged(nameof(IsClipboardExcludedEmpty));
            return;
        }

        ClipboardExcluded.Clear();
        foreach (var entry in current) ClipboardExcluded.Add(entry);

        this.RaisePropertyChanged(nameof(IsClipboardExcludedEmpty));
    }

    // ---- actions -------------------------------------------------------------------

    /// <summary>
    /// Start recording: the hotkey is unregistered first, otherwise pressing the combination being
    /// recorded would summon the launcher and pull focus out of the settings window.
    /// </summary>
    public void BeginRecording()
    {
        BeginRecording(SpotlightHotkeyTarget.Launcher);
    }

    /// <summary>Start recording one gesture. The hotkeys are unregistered while the recorder runs.</summary>
    public void BeginRecording(SpotlightHotkeyTarget target)
    {
        if (recordingTarget != null) return;

        // Set the target before the capture hook starts: the hook reports on the UI thread, and a
        // very fast key press must not arrive before the view model knows which field it belongs to.
        recordingTarget = target;
        Message = null;
        service?.BeginCapture();
        RaiseRecordingChanged();
    }

    /// <summary>Stop recording without changing the gesture.</summary>
    public void CancelRecording()
    {
        if (recordingTarget == null) return;
        recordingTarget = null;
        service?.EndCapture();
        RaiseRecordingChanged();
    }

    private void RaiseRecordingChanged()
    {
        this.RaisePropertyChanged(nameof(IsRecording));
        this.RaisePropertyChanged(nameof(IsSearchRecording));
        this.RaisePropertyChanged(nameof(RecordButtonText));
        this.RaisePropertyChanged(nameof(SearchRecordButtonText));
    }

    /// <summary>
    /// Apply a captured gesture. Returns false (with <see cref="Message"/> set) when the
    /// combination is unusable — a bare letter would swallow typing system-wide.
    /// </summary>
    public bool ApplyCapturedGesture(string? text, SpotlightHotkeyTarget target = SpotlightHotkeyTarget.Launcher)
    {
        // The hook recorder and the window's KeyDown handler are two paths to the same gesture.
        // Whichever arrives first wins; the other must not restart recording (the previous
        // defensive BeginRecording call did exactly that).
        if (recordingTarget == null) return false;

        recordingTarget = null;
        var accepted = target == SpotlightHotkeyTarget.Search
            ? TrySetSearchHotkey(text)
            : TrySetHotkey(text);
        service?.EndCapture();
        RaiseRecordingChanged();

        if (!accepted)
        {
            // TrySet* already explained why; make sure the field still shows the live value.
            this.RaisePropertyChanged(target == SpotlightHotkeyTarget.Search
                ? nameof(SearchHotkeyText)
                : nameof(HotkeyText));
        }

        return accepted;
    }

    /// <summary>Validate and store a gesture. Returns false when it cannot be registered.</summary>
    public bool TrySetHotkey(string? text)
    {
        if (!GlobalHotkeyGesture.TryParse(text, out var gesture, out var error))
        {
            Message = error switch
            {
                "modifier-required" => Locale.Settings_Spotlight_Hotkey_NeedModifier,
                "unknown-key" => Locale.Settings_Spotlight_Hotkey_UnknownKey,
                "missing-key" => Locale.Settings_Spotlight_Hotkey_MissingKey,
                "reserved" => Locale.Settings_Spotlight_Hotkey_Reserved,
                _ => Locale.Settings_Spotlight_Hotkey_Invalid,
            };
            return false;
        }

        var canonical = gesture.Text;
        if (canonical == HotkeyText)
        {
            Message = null;
            return true;
        }

        Save(launcher => launcher with { Hotkey = canonical });
        Message = null;
        this.RaisePropertyChanged(nameof(HotkeyText));
        RaiseStatusChanged();
        return true;
    }

    /// <summary>Reset the gesture to the shipped default (Alt+Space).</summary>
    public void ResetHotkey() => TrySetHotkey(LauncherSettings.DefaultHotkey);

    /// <summary>Validate and store the <b>search-bar</b> gesture. Returns false when unusable.</summary>
    public bool TrySetSearchHotkey(string? text)
    {
        if (!GlobalHotkeyGesture.TryParse(text, out var gesture, out var error))
        {
            Message = error switch
            {
                "modifier-required" => Locale.Settings_Spotlight_Hotkey_NeedModifier,
                "unknown-key" => Locale.Settings_Spotlight_Hotkey_UnknownKey,
                "missing-key" => Locale.Settings_Spotlight_Hotkey_MissingKey,
                "reserved" => Locale.Settings_Spotlight_Hotkey_Reserved,
                _ => Locale.Settings_Spotlight_Hotkey_Invalid,
            };
            return false;
        }

        var canonical = gesture.Text;
        // The two gestures open two different surfaces; the same combination would only ever
        // reach one of them, so it is rejected here instead of failing registration later.
        if (string.Equals(canonical, HotkeyText, StringComparison.OrdinalIgnoreCase))
        {
            Message = Locale.Settings_Spotlight_SearchHotkey_Same;
            return false;
        }

        if (string.Equals(canonical, SearchHotkeyText, StringComparison.OrdinalIgnoreCase))
        {
            Message = null;
            return true;
        }

        Save(launcher => launcher with { SearchHotkey = canonical });
        Message = null;
        this.RaisePropertyChanged(nameof(SearchHotkeyText));
        RaiseStatusChanged();
        return true;
    }

    /// <summary>Reset the search gesture to the shipped default (Alt+Shift+Space).</summary>
    public void ResetSearchHotkey() => TrySetSearchHotkey(LauncherSettings.DefaultSearchHotkey);

    // ---- 背景色: its own follow switch, independent of 跟随全局主题 -------------------

    /// <summary>
    /// Take the card background from the global theme's 纯色背景色 pair — the half is chosen by the
    /// launcher's own 明暗模式, not by the widgets' mode.
    /// </summary>
    public bool FollowGlobalBackground
    {
        get => settings.Get().EffectiveLauncher.FollowGlobalBackground;
        set
        {
            if (value == FollowGlobalBackground) return;
            Save(launcher => launcher with { FollowGlobalBackground = value });
            this.RaisePropertyChanged();
            this.RaisePropertyChanged(nameof(IsBackgroundColorEditable));
            this.RaisePropertyChanged(nameof(BackgroundColorLight));
            this.RaisePropertyChanged(nameof(BackgroundColorDark));
            this.RaisePropertyChanged(nameof(BackgroundColorHint));
        }
    }

    /// <summary>The colour pickers only matter when the launcher paints its own background.</summary>
    public bool IsBackgroundColorEditable => !FollowGlobalBackground;

    /// <summary>
    /// What the card paints right now, spelled out: while following, the pickers are disabled, and
    /// this line is the only way to see which global colour the launcher's own mode selects.
    /// </summary>
    public string BackgroundColorHint
    {
        get
        {
            var global = PanelAppearanceResolver.ResolveEffectiveTheme(settings, window: null);
            var launcher = settings.Get().EffectiveLauncher;
            var isDark = ResolveIsDark(launcher, global);
            return string.Format(
                Locale.Settings_Spotlight_Background_Current_Hint,
                isDark ? Locale.Settings_Spotlight_Appearance_Dark : Locale.Settings_Spotlight_Appearance_Light,
                launcher.ResolveBackgroundColor(global, isDark));
        }
    }

    /// <summary>Custom light-mode card background (#RRGGBB). Alpha stays the material's job.</summary>
    public string? BackgroundColorLight
    {
        get => settings.Get().EffectiveLauncher.BackgroundColorLight;
        set
        {
            if (settings.Get().EffectiveLauncher.BackgroundColorLight == value) return;
            Save(launcher => launcher with { BackgroundColorLight = value });
            this.RaisePropertyChanged();
            this.RaisePropertyChanged(nameof(BackgroundColorHint));
        }
    }

    /// <summary>Custom dark-mode card background (#RRGGBB). Alpha stays the material's job.</summary>
    public string? BackgroundColorDark
    {
        get => settings.Get().EffectiveLauncher.BackgroundColorDark;
        set
        {
            if (settings.Get().EffectiveLauncher.BackgroundColorDark == value) return;
            Save(launcher => launcher with { BackgroundColorDark = value });
            this.RaisePropertyChanged();
            this.RaisePropertyChanged(nameof(BackgroundColorHint));
        }
    }

    /// <summary>
    /// Add several processes at once (the process picker is multi-select). One save for the whole
    /// batch — the picker can return a dozen names and each <c>Save</c> rewrites the file.
    /// </summary>
    public void AddProcesses(IEnumerable<string>? names)
    {
        if (names == null) return;

        var current = settings.Get();
        var launcher = current.EffectiveLauncher;

        var added = 0;
        var skipped = 0;

        foreach (var name in names)
        {
            var normalized = LauncherSettings.NormalizeProcessName(name);
            if (normalized == null) continue;

            if (launcher.IsBlacklisted(normalized))
            {
                skipped++;
                continue;
            }

            launcher = launcher.WithProcessAdded(normalized);
            added++;
        }

        if (added == 0)
        {
            Message = skipped > 0
                ? Locale.Settings_Spotlight_Blacklist_AlreadyAll
                : Locale.Settings_Spotlight_Blacklist_Invalid;
            return;
        }

        settings.Save(current with { Launcher = launcher });
        Message = string.Format(Locale.Settings_Spotlight_Blacklist_AddedMany, added);
    }

    public void AddProcess(string? name)
    {
        var normalized = LauncherSettings.NormalizeProcessName(name);
        if (normalized == null)
        {
            Message = Locale.Settings_Spotlight_Blacklist_Invalid;
            return;
        }

        if (settings.Get().EffectiveLauncher.IsBlacklisted(normalized))
        {
            Message = string.Format(Locale.Settings_Spotlight_Blacklist_Already, normalized);
            return;
        }

        Save(launcher => launcher.WithProcessAdded(normalized));
        Message = string.Format(Locale.Settings_Spotlight_Blacklist_Added, normalized);
    }

    public void RemoveProcess(string? name)
    {
        var normalized = LauncherSettings.NormalizeProcessName(name);
        if (normalized == null) return;

        Save(launcher => launcher.WithProcessRemoved(normalized));
        Message = null;
    }

    public void OpenFolder() => LauncherCatalog.OpenFolder(FolderPath);

    /// <summary>Point the launcher at another folder (called by the page's folder picker).</summary>
    public void SetFolder(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;

        var trimmed = path.Trim();
        if (string.Equals(trimmed, FolderPath, StringComparison.OrdinalIgnoreCase)) return;

        Save(launcher => launcher with { Folder = trimmed });
        this.RaisePropertyChanged(nameof(FolderPath));
    }

    /// <summary>Summon the overlay once, bypassing the blacklist (a preview from the settings page).</summary>
    public void Preview() => service?.Toggle(bypassBlacklist: true);

    // ---- plumbing -------------------------------------------------------------------

    public void Attach()
    {
        if (attached) return;
        attached = true;

        settings.DataChanged += OnSettingsChanged;
        if (service != null)
        {
            service.StateChanged += OnServiceStateChanged;
            service.GestureCaptured += OnGestureCaptured;
            service.GestureCaptureCancelled += OnGestureCaptureCancelled;
            // Clipboard events may fire on any thread; the handler marshals to the UI thread
            // itself (same contract as StateChanged below).
            service.Clipboard.Changed += OnClipboardChanged;
        }
    }

    public void Dispose()
    {
        if (!attached) return;
        attached = false;

        settings.DataChanged -= OnSettingsChanged;
        if (service != null)
        {
            service.StateChanged -= OnServiceStateChanged;
            service.GestureCaptured -= OnGestureCaptured;
            service.GestureCaptureCancelled -= OnGestureCaptureCancelled;
            service.Clipboard.Changed -= OnClipboardChanged;
        }

        // A page closed mid-recording must not leave the gestures unregistered.
        if (IsRecording || IsSearchRecording)
        {
            recordingTarget = null;
            service?.EndCapture();
            RaiseRecordingChanged();
        }

        // The transient "cleared" lines must not resurrect themselves on a stale tick.
        recentStatusTimer?.Stop();
        clipboardStatusTimer?.Stop();
    }

    private void Save(Func<LauncherSettings, LauncherSettings> mutate)
    {
        var current = settings.Get();
        var next = mutate(current.EffectiveLauncher);
        if (next == current.EffectiveLauncher) return;

        settings.Save(current with { Launcher = next });
    }

    private void OnSettingsChanged(object? sender, AppSettings? oldData, AppSettings newData)
    {
        RefreshBlacklist();
        RefreshClipboardExcluded();
        RefreshCategoryKeys();

        var launcher = settings.Get().EffectiveLauncher;
        cornerRadiusDraft = launcher.CornerRadius;
        opacityDraft = launcher.OpacityLevel ?? settings.Get().Theme.OpacityLevel;
        columnsDraft = launcher.Columns;
        webResultCountDraft = launcher.EffectiveWebResultCount;
        gapDraft = launcher.GapPercent;
        rowGapDraft = launcher.RowGapPercent;
        clipboardRetentionDraft = launcher.ClipboardRetentionDays;
        SeedGlassDrafts(launcher);
        selectedSurface = SurfaceOptions.FirstOrDefault(o => o.Surface == launcher.Surface) ?? SurfaceOptions[0];
        selectedDarkMode = DarkModeOptions.FirstOrDefault(o => o.Mode == launcher.ThemeMode) ?? DarkModeOptions[0];
        selectedSort = SortOptions.FirstOrDefault(o => o.Mode == launcher.SortMode) ?? SortOptions[0];
        selectedWebEngine = WebEngineOptions.FirstOrDefault(o =>
            string.Equals(o.Id, launcher.EffectiveSearchEngine, StringComparison.Ordinal)) ?? WebEngineOptions[0];
        selectedWebBrowser = WebBrowserOptions.FirstOrDefault(o =>
            string.Equals(o.Path, launcher.EffectiveSearchBrowser, StringComparison.OrdinalIgnoreCase))
            ?? WebBrowserOptions[0];

        this.RaisePropertyChanged(nameof(SearchPlaceholder));
        this.RaisePropertyChanged(nameof(SpaceShowsRecentLaunches));
        this.RaisePropertyChanged(nameof(IsSpaceRecentLaunchesCountEditable));
        this.RaisePropertyChanged(nameof(SpaceRecentLaunchesCount));
        this.RaisePropertyChanged(nameof(Enabled));
        this.RaisePropertyChanged(nameof(TakeOverSystemHotkeys));
        this.RaisePropertyChanged(nameof(BlockFullscreen));
        this.RaisePropertyChanged(nameof(HotkeyText));
        this.RaisePropertyChanged(nameof(FolderPath));
        this.RaisePropertyChanged(nameof(UseGlobalTheme));
        this.RaisePropertyChanged(nameof(SelectedSurface));
        this.RaisePropertyChanged(nameof(SelectedDarkMode));
        this.RaisePropertyChanged(nameof(SelectedSort));
        this.RaisePropertyChanged(nameof(SelectedWebEngine));
        this.RaisePropertyChanged(nameof(SelectedWebBrowser));
        this.RaisePropertyChanged(nameof(WebSearch));
        this.RaisePropertyChanged(nameof(IsWebSearchEnabled));
        this.RaisePropertyChanged(nameof(IsCustomWebEngine));
        this.RaisePropertyChanged(nameof(SearchEngineUrl));
        RaiseWebPreviewChanged();
        this.RaisePropertyChanged(nameof(AnimateSummon));
        this.RaisePropertyChanged(nameof(FollowGlobalBackground));
        this.RaisePropertyChanged(nameof(IsBackgroundColorEditable));
        this.RaisePropertyChanged(nameof(BackgroundColorLight));
        this.RaisePropertyChanged(nameof(BackgroundColorDark));
        this.RaisePropertyChanged(nameof(BackgroundColorHint));
        this.RaisePropertyChanged(nameof(FollowWidgetRadius));
        this.RaisePropertyChanged(nameof(IsCornerRadiusEditable));
        this.RaisePropertyChanged(nameof(CornerRadius));
        this.RaisePropertyChanged(nameof(OpacityPercent));
        this.RaisePropertyChanged(nameof(ShowNames));
        this.RaisePropertyChanged(nameof(Columns));
        this.RaisePropertyChanged(nameof(WebResultCount));
        this.RaisePropertyChanged(nameof(Height));
        this.RaisePropertyChanged(nameof(GapPercent));
        this.RaisePropertyChanged(nameof(RowGapPercent));
        this.RaisePropertyChanged(nameof(GapPreview));
        this.RaisePropertyChanged(nameof(GlassSampleScreen));
        this.RaisePropertyChanged(nameof(IsGlassIntervalEditable));
        this.RaisePropertyChanged(nameof(GlassSampleInterval));
        this.RaisePropertyChanged(nameof(FileSearch));
        this.RaisePropertyChanged(nameof(IsFileSearchEnabled));
        this.RaisePropertyChanged(nameof(AutoStartEverything));
        this.RaisePropertyChanged(nameof(FileSearchStatusText));
        this.RaisePropertyChanged(nameof(CanStartEverything));
        this.RaisePropertyChanged(nameof(RecentApps));
        this.RaisePropertyChanged(nameof(PreviewBar));
        this.RaisePropertyChanged(nameof(ClipboardHistory));
        this.RaisePropertyChanged(nameof(ClipboardCountText));
        this.RaisePropertyChanged(nameof(ClipboardRetentionDays));
        this.RaisePropertyChanged(nameof(ClipboardRetentionUnit));
        this.RaisePropertyChanged(nameof(GlassClarity));
        this.RaisePropertyChanged(nameof(GlassBlur));
        this.RaisePropertyChanged(nameof(GlassRefraction));
        this.RaisePropertyChanged(nameof(GlassHighlight));
        this.RaisePropertyChanged(nameof(GlassVibrancy));
        this.RaisePropertyChanged(nameof(GlassDispersion));
        RaiseAppearanceChanged();
        RaiseStatusChanged();
    }

    private void OnClipboardChanged()
        => Avalonia.Threading.Dispatcher.UIThread.Post(
            () => this.RaisePropertyChanged(nameof(ClipboardCountText)));

    /// <summary>A combination captured by the service's hook recorder.</summary>
    private void OnGestureCaptured(string text)
    {
        if (recordingTarget is not { } target) return;
        ApplyCapturedGesture(text, target);
    }

    /// <summary>Escape pressed while the hook recorder was waiting.</summary>
    private void OnGestureCaptureCancelled()
    {
        if (recordingTarget == null) return;
        CancelRecording();
    }

    private void OnServiceStateChanged()
        => Avalonia.Threading.Dispatcher.UIThread.Post(RaiseStatusChanged);

    private void RaiseStatusChanged()
    {
        this.RaisePropertyChanged(nameof(StatusText));
        this.RaisePropertyChanged(nameof(SearchStatusText));
        this.RaisePropertyChanged(nameof(IsHotkeyLive));
        this.RaisePropertyChanged(nameof(RecordButtonText));
        this.RaisePropertyChanged(nameof(SearchRecordButtonText));
    }

    private void RefreshBlacklist()
    {
        var current = settings.Get().EffectiveLauncher.EffectiveBlacklist;

        if (Blacklist.SequenceEqual(current, StringComparer.Ordinal))
        {
            this.RaisePropertyChanged(nameof(IsBlacklistEmpty));
            return;
        }

        Blacklist.Clear();
        foreach (var entry in current) Blacklist.Add(entry);

        this.RaisePropertyChanged(nameof(IsBlacklistEmpty));
    }
}
