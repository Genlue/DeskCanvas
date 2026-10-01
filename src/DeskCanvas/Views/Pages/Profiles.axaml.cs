using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using DeskCanvas.Services;
using DeskCanvas.ViewModels;

namespace DeskCanvas.Views.Pages;

public partial class Profiles : UserControl
{
    private readonly ProfilesViewModel viewModel;

    public Profiles(ProfileService profileService)
    {
        viewModel = new ProfilesViewModel(profileService);
        DataContext = viewModel;
        InitializeComponent();
    }

    private Window? GetParentWindow() => TopLevel.GetTopLevel(this) as Window;

    private async void OnNewProfileClicked(object? sender, RoutedEventArgs e)
    {
        if (GetParentWindow() is { } win)
            await viewModel.CreateProfileAsync(win);
    }

    private async void OnImportProfileClicked(object? sender, RoutedEventArgs e)
    {
        if (GetParentWindow() is { } win)
            await viewModel.ImportProfileAsync(win);
    }

    private void OnSwitchClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: ProfileItemViewModel item })
            viewModel.SwitchProfile(item);
    }

    private async void OnRenameClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: ProfileItemViewModel item } && GetParentWindow() is { } win)
            await viewModel.RenameProfileAsync(item, win);
    }

    private async void OnDuplicateClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: ProfileItemViewModel item } && GetParentWindow() is { } win)
            await viewModel.DuplicateProfileAsync(item, win);
    }

    private async void OnExportClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: ProfileItemViewModel item } && GetParentWindow() is { } win)
            await viewModel.ExportProfileAsync(item, win);
    }

    private async void OnDeleteClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: ProfileItemViewModel item } && GetParentWindow() is { } win)
            await viewModel.DeleteProfileAsync(item, win);
    }
}
