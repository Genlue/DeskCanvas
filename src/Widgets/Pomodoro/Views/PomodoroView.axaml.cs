using System;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Pomodoro.Models;
using Pomodoro.ViewModels;
using uWidgets.Core.Interfaces;
using uWidgets.Core.Models;
using uWidgets.Services;

namespace Pomodoro.Views;

public partial class PomodoroView : UserControl, IWidgetSelfRefreshing
{
    private readonly IWidgetLayoutProvider? layoutProvider;
    private PomodoroViewModel? viewModel;
    private WidgetTier currentTier = (WidgetTier)(-1);

    public PomodoroView() : this(new PomodoroModel(), null) { }

    public PomodoroView(IWidgetLayoutProvider layoutProvider) : this(new PomodoroModel(), layoutProvider) { }

    public PomodoroView(PomodoroModel model) : this(model, null) { }

    public PomodoroView(PomodoroModel model, IWidgetLayoutProvider? layoutProvider)
    {
        this.layoutProvider = layoutProvider;
        viewModel = new PomodoroViewModel(model);
        DataContext = viewModel;

        InitializeComponent();

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        SizeChanged += OnSizeChanged;
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        // OnUnloaded disposes the view model (a running countdown would otherwise tick, beep
        // and spawn toasts forever from a detached view), so a re-mounted cached page rebuilds
        // one here — same pattern as Monitor/Progress.
        if (viewModel is { IsDisposed: true } or null)
        {
            viewModel = new PomodoroViewModel(viewModel?.Model ?? new PomodoroModel());
            DataContext = viewModel;
        }

        if (Bounds.Width > 0 && Bounds.Height > 0)
        {
            ApplySize(Bounds.Size);
        }
    }

    private void OnUnloaded(object? sender, RoutedEventArgs e)
    {
        viewModel?.Dispose();
    }

    private void OnSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (e.NewSize.Width > 0 && e.NewSize.Height > 0)
        {
            ApplySize(e.NewSize);
        }
    }

    private void ApplySize(Size size)
    {
        var tier = ResolveTier(size);
        if (tier == currentTier) return;
        currentTier = tier;

        SmallLayout.IsVisible = tier == WidgetTier.Small;
        WideLayout.IsVisible = tier == WidgetTier.Medium;
        LargeLayout.IsVisible = tier == WidgetTier.Large;
    }

    private WidgetTier ResolveTier(Size size)
    {
        try
        {
            var spanTier = SizeTiers.ResolveTier(this, size);
            if (spanTier != WidgetTier.Other) return spanTier;
        }
        catch { }

        if (size.Width >= 280 && size.Height >= 280)
            return WidgetTier.Large;
        if (size.Width >= 260 && size.Width > size.Height * 1.35)
            return WidgetTier.Medium;

        return WidgetTier.Small;
    }

    public void Refresh(WidgetLayout layout)
    {
        if (layout.Settings is not { } settings || settings.ValueKind != JsonValueKind.Object)
            return;

        try
        {
            var updated = settings.Deserialize<PomodoroModel>();
            if (updated != null)
            {
                viewModel?.UpdateModel(updated);
            }
        }
        catch { }
    }

    private void OnPlayPauseClicked(object? sender, RoutedEventArgs e)
    {
        viewModel?.StartOrPause();
    }

    private void OnResetClicked(object? sender, RoutedEventArgs e)
    {
        viewModel?.Reset();
    }

    private void OnSkipClicked(object? sender, RoutedEventArgs e)
    {
        viewModel?.Skip();
    }

    private void OnSelectPhaseClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string tag)
        {
            if (Enum.TryParse<PomodoroPhase>(tag, out var phase))
            {
                viewModel?.SwitchPhase(phase);
            }
        }
    }

    private void OnSelectPhaseFromFlyout(object? sender, RoutedEventArgs e)
    {
        if (sender is MenuItem item && item.Tag is string tag)
        {
            if (Enum.TryParse<PomodoroPhase>(tag, out var phase))
            {
                viewModel?.SwitchPhase(phase);
            }
        }
    }
}
