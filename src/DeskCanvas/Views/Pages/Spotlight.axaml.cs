using System;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using DeskCanvas.Core.Interfaces;
using DeskCanvas.Locales;
using DeskCanvas.Services;
using DeskCanvas.UI.Controls;
using DeskCanvas.ViewModels;

namespace DeskCanvas.Views.Pages;

/// <summary>
/// 聚焦搜索 settings page. The page owns the <i>interaction</i> only — capturing a gesture,
/// picking a folder, choosing a process — and hands the resulting value to
/// <see cref="SpotlightSettingsViewModel"/>, which persists it. Nothing here touches the
/// registrar or the settings file directly.
/// </summary>
public partial class Spotlight : UserControl
{
    private readonly SpotlightSettingsViewModel viewModel;
    private Window? hostWindow;

    public Spotlight(IAppSettingsProvider appSettingsProvider, SpotlightService? spotlightService = null,
        FileSearchService? fileSearchService = null)
    {
        viewModel = new SpotlightSettingsViewModel(appSettingsProvider, spotlightService, fileSearchService);
        DataContext = viewModel;
        InitializeComponent();

        // The settings window caches pages and detaches the current one on close, so the pair must
        // be able to cycle: Loaded re-subscribes, Unloaded unsubscribes (and re-registers a
        // gesture left suspended by a half-finished recording).
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        viewModel.Attach();

        if (hostWindow != null) hostWindow.Deactivated -= OnHostDeactivated;
        hostWindow = TopLevel.GetTopLevel(this) as Window;
        if (hostWindow != null) hostWindow.Deactivated += OnHostDeactivated;
    }

    private void OnUnloaded(object? sender, RoutedEventArgs e)
    {
        if (hostWindow != null)
        {
            hostWindow.Deactivated -= OnHostDeactivated;
            hostWindow = null;
        }

        viewModel.Dispose();
    }

    /// <summary>
    /// A recorder owns a global keyboard hook; leaving the window focused elsewhere must not leave
    /// it swallowing keystrokes in the background.
    /// </summary>
    private void OnHostDeactivated(object? sender, EventArgs e)
    {
        if (viewModel.IsRecording || viewModel.IsSearchRecording)
            viewModel.CancelRecording();
    }

    // ---- hotkey recording -----------------------------------------------------------

    private void OnRecordHotkey(object? sender, RoutedEventArgs e) => StartRecording(SpotlightHotkeyTarget.Launcher);

    private void OnRecordSearchHotkey(object? sender, RoutedEventArgs e) => StartRecording(SpotlightHotkeyTarget.Search);

    /// <summary>Begin recording one of the two gestures and focus the box that shows it.</summary>
    private void StartRecording(SpotlightHotkeyTarget target)
    {
        if (viewModel.IsRecording || viewModel.IsSearchRecording) return;
        viewModel.BeginRecording(target);
        (target == SpotlightHotkeyTarget.Search ? SearchHotkeyBox : HotkeyBox).Focus();
    }

    private void OnHotkeyBoxPressed(object? sender, PointerPressedEventArgs e)
        => StartRecordingFromBox(SpotlightHotkeyTarget.Launcher);

    private void OnSearchHotkeyBoxPressed(object? sender, PointerPressedEventArgs e)
        => StartRecordingFromBox(SpotlightHotkeyTarget.Search);

    /// <summary>Clicking a field is the discoverable way to start recording it.</summary>
    private void StartRecordingFromBox(SpotlightHotkeyTarget target)
    {
        if (viewModel.IsRecording || viewModel.IsSearchRecording) return;
        StartRecording(target);
    }

    private void OnHotkeyKeyDown(object? sender, KeyEventArgs e) => HandleRecordKey(e, SpotlightHotkeyTarget.Launcher);

    private void OnSearchHotkeyKeyDown(object? sender, KeyEventArgs e) => HandleRecordKey(e, SpotlightHotkeyTarget.Search);

    private void HandleRecordKey(KeyEventArgs e, SpotlightHotkeyTarget target)
    {
        if (target == SpotlightHotkeyTarget.Launcher ? !viewModel.IsRecording : !viewModel.IsSearchRecording) return;

        // Escape cancels the recording instead of being recorded as a gesture.
        if (e.Key == Key.Escape)
        {
            viewModel.CancelRecording();
            e.Handled = true;
            return;
        }

        if (IsModifierOnly(e.Key))
        {
            // Keep waiting: the user is still holding the modifiers down.
            e.Handled = true;
            return;
        }

        var text = LauncherKeyText.Describe(e.KeyModifiers, e.Key);
        if (text == null)
        {
            e.Handled = true;
            return;
        }

        viewModel.ApplyCapturedGesture(text, target);
        e.Handled = true;
    }

    private void OnResetHotkey(object? sender, RoutedEventArgs e) => viewModel.ResetHotkey();

    private void OnResetSearchHotkey(object? sender, RoutedEventArgs e) => viewModel.ResetSearchHotkey();

    private void OnPreview(object? sender, RoutedEventArgs e) => viewModel.Preview();

    private static bool IsModifierOnly(Key key) => key
        is Key.LeftCtrl or Key.RightCtrl
        or Key.LeftAlt or Key.RightAlt
        or Key.LeftShift or Key.RightShift
        or Key.LWin or Key.RWin;

    // ---- folder --------------------------------------------------------------------

    private void OnOpenFolder(object? sender, RoutedEventArgs e) => viewModel.OpenFolder();

    private async void OnChangeFolder(object? sender, RoutedEventArgs e)
    {
        var owner = TopLevel.GetTopLevel(this) as Window;
        var storage = owner?.StorageProvider;
        if (owner == null || storage == null) return;

        try
        {
            IStorageFolder? start = null;
            try { start = await storage.TryGetFolderFromPathAsync(viewModel.FolderPath); }
            catch { /* a folder that no longer exists just means "no start location" */ }

            var folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = Locale.Settings_Spotlight_Folder_PickTitle,
                AllowMultiple = false,
                SuggestedStartLocation = start,
            });

            if (folders.Count == 0) return;

            var path = folders[0].TryGetLocalPath();
            if (!string.IsNullOrWhiteSpace(path)) viewModel.SetFolder(path);
        }
        catch
        {
            // A cancelled / unavailable picker must not take the settings window down.
        }
    }

    // ---- grid metrics --------------------------------------------------------------

    private void OnGridMetricsCommitted(object? sender, LabeledSliderValueCommittedEventArgs e)
        => viewModel.CommitGridMetrics();

    // ---- file search ----------------------------------------------------------------

    private void OnStartEverything(object? sender, RoutedEventArgs e) => viewModel.StartEverything();

    private void OnRefreshIndex(object? sender, RoutedEventArgs e) => viewModel.RefreshIndex();

    private void OnAppearanceCommitted(object? sender, LabeledSliderValueCommittedEventArgs e)
        => viewModel.CommitAppearance();

    private void OnGlassCommitted(object? sender, LabeledSliderValueCommittedEventArgs e)
        => viewModel.CommitGlass();

    // ---- blacklist -----------------------------------------------------------------

    /// <summary>
    /// Open the shared process picker (the same dialog 失焦自动切换 uses): it lists every running
    /// process that owns a visible, titled window, is searchable, and is multi-select. The user
    /// picks the apps to silence; nothing is guessed from whatever happens to be in front.
    /// </summary>
    private async void OnPickProcesses(object? sender, RoutedEventArgs e)
    {
        var owner = TopLevel.GetTopLevel(this) as Window;
        if (owner == null) return;

        try
        {
            var selection = await new ProcessPickerDialog().ShowDialog<string[]?>(owner);
            if (selection is { Length: > 0 }) viewModel.AddProcesses(selection);
        }
        catch
        {
            // A picker that fails to open must not take the settings window down.
        }
    }

    private void OnAddManualProcess(object? sender, RoutedEventArgs e)
    {
        viewModel.AddProcess(ProcessBox.Text);
        ProcessBox.Text = string.Empty;
    }

    private void OnRemoveProcess(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: string processName })
            viewModel.RemoveProcess(processName);
    }

    // ---- category lock keys (类别快捷键) ---------------------------------------------

    /// <summary>The row whose box is waiting for a key press (null = not recording).</summary>
    private CategoryKeyRowViewModel? capturingCategoryRow;

    private void OnRecordCategoryKey(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control control || control.DataContext is not CategoryKeyRowViewModel row) return;

        // The record button and the box live in the same row grid; the box is the row's only
        // TextBox, so it can be found without naming a template part. Parent covers the template
        // as written; the visual-ancestor walk survives a theme that wraps the button.
        var rowGrid = control.Parent as Panel
                      ?? control.GetVisualAncestors().OfType<Panel>().FirstOrDefault();
        var box = rowGrid?.Children.OfType<TextBox>().FirstOrDefault();
        BeginCategoryCapture(row, box);
    }

    private void OnCategoryKeyBoxPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is TextBox box && box.DataContext is CategoryKeyRowViewModel row)
            BeginCategoryCapture(row, box);
    }

    private void BeginCategoryCapture(CategoryKeyRowViewModel row, TextBox? box)
    {
        capturingCategoryRow = row;
        box?.Focus();
    }

    /// <summary>
    /// Record one category gesture. Escape cancels; a bare letter/digit is rejected by the view
    /// model (it would swallow typing), so the row shows the rule instead of binding it.
    /// </summary>
    private void OnCategoryKeyDown(object? sender, KeyEventArgs e)
    {
        if (sender is not TextBox box || box.DataContext is not CategoryKeyRowViewModel row) return;
        if (!ReferenceEquals(capturingCategoryRow, row)) return;

        if (e.Key == Key.Escape)
        {
            capturingCategoryRow = null;
            e.Handled = true;
            return;
        }

        if (IsModifierOnly(e.Key))
        {
            e.Handled = true;
            return;
        }

        var text = LauncherKeyText.Describe(e.KeyModifiers, e.Key);
        if (text == null)
        {
            e.Handled = true;
            return;
        }

        if (viewModel.TrySetCategoryKey(row, text)) capturingCategoryRow = null;
        e.Handled = true;
    }

    private void OnClearCategoryKey(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: CategoryKeyRowViewModel row })
            viewModel.ClearCategoryKey(row);
    }

    /// <summary>↑ / ↓ in the row's leading column: the same list is both the key map and the order.</summary>
    private void OnMoveCategoryUp(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: CategoryKeyRowViewModel row })
            viewModel.MoveCategory(row, -1);
    }

    private void OnMoveCategoryDown(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: CategoryKeyRowViewModel row })
            viewModel.MoveCategory(row, +1);
    }

    private void OnResetCategoryKeys(object? sender, RoutedEventArgs e) => viewModel.ResetCategoryKeys();

    private void OnResetOrbSlots(object? sender, RoutedEventArgs e) => viewModel.ResetOrbSlots();

    /// <summary>One write per slider release, not per tick (the grid sliders' own rule).</summary>
    private void OnWebResultCountCommitted(object? sender, LabeledSliderValueCommittedEventArgs e)
        => viewModel.CommitWebResultCount();

    // ---- results: recents -----------------------------------------------------------

    private void OnSpaceRecentLaunchesCountCommitted(object? sender, LabeledSliderValueCommittedEventArgs e)
        => viewModel.CommitSpaceRecentLaunchesCount();

    private void OnClearUsage(object? sender, RoutedEventArgs e) => viewModel.ClearUsage();

    // ---- clipboard ------------------------------------------------------------------

    private void OnClipboardRetentionCommitted(object? sender, LabeledSliderValueCommittedEventArgs e)
        => viewModel.CommitClipboardRetention();

    private void OnClearClipboard(object? sender, RoutedEventArgs e) => viewModel.ClearClipboard();

    private void OnAddClipboardExclusion(object? sender, RoutedEventArgs e)
    {
        viewModel.AddClipboardExclusion(ClipboardExcludedBox.Text);
        ClipboardExcludedBox.Text = string.Empty;
    }

    private void OnRemoveClipboardExclusion(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: string name })
            viewModel.RemoveClipboardExclusion(name);
    }
}
