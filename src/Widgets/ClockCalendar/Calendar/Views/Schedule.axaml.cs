using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Calendar.Models;
using Calendar.Services;
using Calendar.ViewModels;
using Calendar.Views.Controls;
using DeskCanvas.Contracts;
using DeskCanvas.Core.Interfaces;
using DeskCanvas.Core.Models;
using DeskCanvas.Core.Models.Settings;
using DeskCanvas.Runtime;
using DeskCanvas.Services;
using DeskCanvas.Views;

namespace Calendar.Views;

public partial class Schedule : UserControl, IWidgetSelfRefreshing, IWidgetSurfaceHints, IWidgetActivityConsumer
{
    public string? ColorfulBackgroundResourceKey => "CalendarCardBackground";

    private readonly IWidgetLayoutProvider? widgetLayoutProvider;
    private readonly ScheduleViewModel viewModel;
    private readonly IWidgetSession? session;
    private WidgetTier currentTier = WidgetTier.Other;
    private IBrush? monochromeBrush;
    private IAppSettingsProvider? subscribedSettings;

    public Schedule() : this(null, null) { }

    public Schedule(IWidgetLayoutProvider? widgetLayoutProvider) : this(widgetLayoutProvider, null) { }

    public Schedule(IWidgetLayoutProvider? widgetLayoutProvider, IWidgetSession? session)
    {
        this.widgetLayoutProvider = widgetLayoutProvider;
        this.session = session;
        var unifiedModel = ScheduleStore.Get();
        viewModel = new ScheduleViewModel(unifiedModel, session);

        Content = new ScheduleSmall(this, viewModel);
        SizeChanged += OnSizeChanged;
        Unloaded += OnUnloaded;
        Loaded += OnLoaded;
        ScheduleStore.ModelChanged += OnStoreModelChanged;

        InitializeComponent();

        SpotlightBridge.EnsureRegistered();
    }

    public void OnSessionActivity(ActivitySnapshot activity) => viewModel.OnSessionActivity(activity);

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        viewModel.Start();
        UpdateMonochromeBrush();
        subscribedSettings = PanelAppearanceResolver.TryResolveSettingsProvider();
        if (subscribedSettings != null)
        {
            subscribedSettings.DataChanged -= OnSettingsDataChanged;
            subscribedSettings.DataChanged += OnSettingsDataChanged;
        }
        ActualThemeVariantChanged -= OnThemeChanged;
        ActualThemeVariantChanged += OnThemeChanged;
        if (Application.Current != null)
        {
            Application.Current.ResourcesChanged -= OnAppResourcesChanged;
            Application.Current.ResourcesChanged += OnAppResourcesChanged;
        }
    }

    private void OnUnloaded(object? sender, RoutedEventArgs e)
    {
        viewModel.Stop();
        SizeChanged -= OnSizeChanged;
        Unloaded -= OnUnloaded;
        Loaded -= OnLoaded;
        ScheduleStore.ModelChanged -= OnStoreModelChanged;
        ActualThemeVariantChanged -= OnThemeChanged;
        if (Application.Current != null)
            Application.Current.ResourcesChanged -= OnAppResourcesChanged;
        if (subscribedSettings != null)
            subscribedSettings.DataChanged -= OnSettingsDataChanged;
        subscribedSettings = null;
    }

    private void OnSettingsDataChanged(object? sender, AppSettings? oldSettings, AppSettings newSettings) =>
        UpdateMonochromeBrush();

    private void OnThemeChanged(object? sender, EventArgs e) => UpdateMonochromeBrush();

    private void OnAppResourcesChanged(object? sender, ResourcesChangedEventArgs e) => UpdateMonochromeBrush();

    private void UpdateMonochromeBrush()
    {
        IBrush? brush = null;
        try
        {
            var theme = PanelAppearanceResolver.ResolveEffectiveTheme(
                PanelAppearanceResolver.TryResolveSettingsProvider(),
                TopLevel.GetTopLevel(this) as Window);
            if (theme is { IsColorful: false, Monochrome: true })
                brush = ThemeScopeResolver.ResolveBrush(this, "SystemControlForegroundAccentBrush", ActualThemeVariant);
        }
        catch { }

        if (ReferenceEquals(monochromeBrush, brush)) return;
        monochromeBrush = brush;
        viewModel.SetMonochromeBrush(brush);
    }

    private void OnStoreModelChanged(ScheduleModel newModel, object? sender)
    {
        if (ReferenceEquals(sender, this)) return;
        viewModel.Update(newModel);
    }

    public void Refresh(WidgetLayout layout)
    {
        var model = ScheduleStore.Get();
        viewModel.Update(model);
    }

    private void OnSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        var tier = SizeTiers.ResolveTier(this, e.NewSize);
        if (tier == currentTier && Content != null) return;
        currentTier = tier;

        switch (tier)
        {
            case WidgetTier.Cell:
            case WidgetTier.Small:
                Content = new ScheduleSmall(this, viewModel);
                break;
            case WidgetTier.Medium:
                Content = new ScheduleMedium(this, viewModel);
                break;
            case WidgetTier.Large:
                Content = new ScheduleLarge(this, viewModel);
                break;
            default:
                var size = e.NewSize;
                var small = size is { Width: < 220, Height: < 220 };
                var wide = size.AspectRatio >= 1.4;
                Content = (small, wide) switch
                {
                    (true, _) => new ScheduleSmall(this, viewModel),
                    (_, true) => new ScheduleMedium(this, viewModel),
                    _ => new ScheduleLarge(this, viewModel)
                };
                break;
        }
    }

    public void OpenPanel(Point? screenCenter = null, string? targetEventId = null)
    {
        if (!screenCenter.HasValue && TopLevel.GetTopLevel(this) is { } topLevel)
        {
            var centerLocal = new Point(Bounds.Width / 2, Bounds.Height / 2);
            var rootPoint = this.TranslatePoint(centerLocal, topLevel);
            if (rootPoint.HasValue)
            {
                var screenPoint = topLevel.PointToScreen(rootPoint.Value);
                screenCenter = new Point(screenPoint.X, screenPoint.Y);
            }
        }

        var owner = TopLevel.GetTopLevel(this) as Window;
        var radius = (TopLevel.GetTopLevel(this) as Widget)?.Radius.TopLeft ?? 0;
        SchedulePanelWindow.ShowPanel(screenCenter, owner, targetEventId, radius > 0 ? radius : null);
    }
}
