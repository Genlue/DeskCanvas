using Avalonia.Controls;
using Avalonia.Interactivity;
using DeskCanvas.Core.Interfaces;
using DeskCanvas.ViewModels;

namespace DeskCanvas.Views.Pages;

public partial class Appearance : UserControl
{
    private readonly IAppSettingsProvider appSettingsProvider;

    public Appearance(IAppSettingsProvider appSettingsProvider)
    {
        this.appSettingsProvider = appSettingsProvider;
        DataContext = new AppearanceViewModel(appSettingsProvider);
        InitializeComponent();
    }

    private void ApplySolidToLight(object? sender, RoutedEventArgs e) =>
        ((AppearanceViewModel)DataContext!).ApplySolidToLight();

    private void ApplySolidToDark(object? sender, RoutedEventArgs e) =>
        ((AppearanceViewModel)DataContext!).ApplySolidToDark();

    private void ResetLiquidGlass(object? sender, RoutedEventArgs e) =>
        ((AppearanceViewModel)DataContext!).ResetLiquidGlass();

    private void RefreshLiquidGlassWallpaper(object? sender, RoutedEventArgs e) =>
        ((AppearanceViewModel)DataContext!).RefreshLiquidGlassWallpaper();

    private void ToggleOpticsExpanded(object? sender, RoutedEventArgs e)
    {
        if (DataContext is AppearanceViewModel viewModel)
            viewModel.OpticsExpanded = !viewModel.OpticsExpanded;
    }

    private void OpenWallpaperAlign(object? sender, RoutedEventArgs e) =>
        new WallpaperAlignDialog(appSettingsProvider).Show();
}
