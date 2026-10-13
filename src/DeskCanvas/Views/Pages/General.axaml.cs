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
    public General(IAppSettingsProvider appSettingsProvider, UpdateService? updateService = null)
    {
        var viewModel = new GeneralViewModel(appSettingsProvider, updateService);
        DataContext = viewModel;
        InitializeComponent();
        Loaded += (_, _) => viewModel.Attach();
        Unloaded += (_, _) => viewModel.Dispose();
    }
}