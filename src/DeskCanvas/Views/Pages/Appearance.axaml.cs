using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using DeskCanvas.Core.Interfaces;
using DeskCanvas.Services;
using DeskCanvas.ViewModels;
using DeskCanvas.Views.Controls;

namespace DeskCanvas.Views.Pages;

public partial class Appearance : UserControl
{
    private readonly IAppSettingsProvider appSettingsProvider;

    public Appearance(IAppSettingsProvider appSettingsProvider)
    {
        this.appSettingsProvider = appSettingsProvider;
        // The 失焦自动切换 rows need the live activation state for their status line. The
        // watcher is a DI singleton; at design/build time it may not exist — the view model
        // then degrades to the static "未启用" status.
        var focusWatch = App.Services?.GetService(typeof(FocusWatchService)) as FocusWatchService;
        DataContext = new AppearanceViewModel(appSettingsProvider, focusWatch);
        InitializeComponent();
    }

    private void ApplySolidToLight(object? sender, RoutedEventArgs e) =>
        ((AppearanceViewModel)DataContext!).ApplySolidToLight();

    private void CommitOpacityLevel(object? sender, LabeledSliderValueCommittedEventArgs e) =>
        ((AppearanceViewModel)DataContext!).CommitOpacityLevel();

    private void CommitThemeSliders(object? sender, LabeledSliderValueCommittedEventArgs e) =>
        ((AppearanceViewModel)DataContext!).CommitThemeSliders();

    private void RetrySettingsSave(object? sender, RoutedEventArgs e) =>
        ((AppearanceViewModel)DataContext!).RetrySettingsSave();

    private void ReloadSettings(object? sender, RoutedEventArgs e) =>
        ((AppearanceViewModel)DataContext!).ReloadSettings();

    private void ApplySolidToDark(object? sender, RoutedEventArgs e) =>
        ((AppearanceViewModel)DataContext!).ApplySolidToDark();

    private void ResetLiquidGlass(object? sender, RoutedEventArgs e) =>
        ((AppearanceViewModel)DataContext!).ResetLiquidGlass();

    private void RefreshLiquidGlassWallpaper(object? sender, RoutedEventArgs e) =>
        ((AppearanceViewModel)DataContext!).RefreshLiquidGlassWallpaper();

    private void OpenWallpaperAlign(object? sender, RoutedEventArgs e) =>
        new WallpaperAlignDialog(appSettingsProvider).Show();

    /// <summary>Remove one 失焦自动切换 allowlist entry (the chip's button carries the name).</summary>
    private void RemoveFocusProcess(object? sender, RoutedEventArgs e)
    {
        if (DataContext is AppearanceViewModel viewModel && sender is Button { Tag: string name })
            viewModel.RemoveFocusProcess(name);
    }

    /// <summary>Pick running processes from the process dialog and append them to the allowlist.</summary>
    private async void AddFocusProcess(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not AppearanceViewModel viewModel) return;
        if (TopLevel.GetTopLevel(this) is not Window owner) return;

        var dialog = new ProcessPickerDialog();
        var selection = await dialog.ShowDialog<string[]?>(owner);
        if (selection is { Length: > 0 })
            viewModel.AddFocusProcesses(selection);
    }
}
