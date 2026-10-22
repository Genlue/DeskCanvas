using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Interactivity;
using DeskCanvas.Core.Interfaces;
using DeskCanvas.Services;
using DeskCanvas.ViewModels;

namespace DeskCanvas.Views.Pages;

public partial class General : UserControl
{
    private readonly GeneralViewModel viewModel;

    public General(IAppSettingsProvider appSettingsProvider, UpdateService? updateService = null)
    {
        viewModel = new GeneralViewModel(appSettingsProvider, updateService);
        DataContext = viewModel;
        InitializeComponent();
        Loaded += (_, _) => viewModel.Attach();
        Unloaded += (_, _) => viewModel.Dispose();
    }

    /// <summary>Geocode the typed city and offer the hits in the picker.</summary>
    private async void OnSearchRegionClicked(object? sender, RoutedEventArgs e)
    {
        await viewModel.SearchRegionAsync();
    }

    /// <summary>Forget the 默认地区 so every widget falls back to its own location.</summary>
    private void OnClearRegionClicked(object? sender, RoutedEventArgs e)
    {
        viewModel.ClearDefaultRegion();
    }
}