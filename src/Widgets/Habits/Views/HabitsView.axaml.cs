using System;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using DeskCanvas.Core.Interfaces;
using DeskCanvas.Core.Models;
using DeskCanvas.Services;
using Habits.Models;
using Habits.Services;
using Habits.ViewModels;
using Habits.Views.Controls;

namespace Habits.Views;

public partial class HabitsView : UserControl, IWidgetSelfRefreshing, IWidgetSurfaceHints
{
    public string? ColorfulBackgroundResourceKey => "HabitsCardBackground";

    private readonly IWidgetLayoutProvider widgetLayoutProvider;
    private readonly HabitsViewModel viewModel;
    private HabitsModel currentModel;
    private Type? currentCardType;

    public HabitsView(IWidgetLayoutProvider widgetLayoutProvider)
        : this(new HabitsModel(), widgetLayoutProvider) { }

    public HabitsView(HabitsModel model, IWidgetLayoutProvider widgetLayoutProvider)
    {
        this.widgetLayoutProvider = widgetLayoutProvider;
        currentModel = model;

        var data = HabitsStore.Get();
        viewModel = new HabitsViewModel(model, data);

        InitializeComponent();
        UpdateCardContent();

        SizeChanged += OnSizeChanged;
        Unloaded += OnUnloaded;
        HabitsStore.ModelChanged += OnStoreModelChanged;
    }

    private void OnStoreModelChanged(HabitsDataModel newData, object? sender)
    {
        if (ReferenceEquals(sender, this)) return;
        viewModel.UpdateData(newData);
    }

    public void Refresh(WidgetLayout layout)
    {
        try
        {
            if (layout.Settings.HasValue && layout.Settings.Value.ValueKind == JsonValueKind.Object)
            {
                var settings = layout.Settings.Value.Deserialize<HabitsModel>(HabitsStore.SharedOptions);
                if (settings != null)
                {
                    currentModel = settings;
                    viewModel.UpdateSettings(settings);
                }
            }
        }
        catch { }

        var data = HabitsStore.Get();
        viewModel.UpdateData(data);
        UpdateCardContent();
    }

    private void OnUnloaded(object? sender, RoutedEventArgs e)
    {
        SizeChanged -= OnSizeChanged;
        Unloaded -= OnUnloaded;
        HabitsStore.ModelChanged -= OnStoreModelChanged;
    }

    private void OnSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        UpdateCardContent(e.NewSize);
    }

    public void UpdateCardContent(Size? size = null)
    {
        var currentSize = size ?? Bounds.Size;
        var tier = SizeTiers.ResolveTier(this, currentSize);

        // In 1x1 (Cell), render single target focus card because 1x1 cell is only ~80x80
        var targetType = tier == WidgetTier.Cell
            ? typeof(HabitsFocusCard)
            : currentModel.ViewMode switch
            {
                HabitsViewMode.Focus => typeof(HabitsFocusCard),
                HabitsViewMode.Heatmap => typeof(HabitsHeatmapCard),
                _ => typeof(HabitsListCard)
            };

        // Reuse existing card instance when the required card type hasn't changed.
        // Prevents destructive teardown and recreation of the card and visual tree on every resize/layout pass.
        if (Content != null && currentCardType == targetType)
        {
            return;
        }

        currentCardType = targetType;
        Content = targetType == typeof(HabitsFocusCard)
            ? new HabitsFocusCard(this, viewModel)
            : targetType == typeof(HabitsHeatmapCard)
                ? new HabitsHeatmapCard(this, viewModel)
                : new HabitsListCard(this, viewModel);
    }

    public void SaveFocusedHabit(string? habitId)
    {
        currentModel = currentModel with { FocusedHabitId = habitId };
        try
        {
            var newJson = JsonSerializer.SerializeToElement(currentModel, HabitsStore.SharedOptions);
            var newLayout = widgetLayoutProvider.Get() with { Settings = newJson };
            widgetLayoutProvider.Save(newLayout);
        }
        catch { }
    }

    public void OpenPopupWindow()
    {
        var (screenCenter, _) = GetScreenCenterAndTopLevel();
        var owner = VisualRoot as Window;
        HabitsPopupWindow.ShowPopup(
            HabitsStore.Get(),
            screenCenter,
            owner,
            onDataChanged: newModel =>
            {
                viewModel.UpdateData(newModel);
                HabitsStore.Save(newModel, sender: this);
            },
            SpawnCornerRadius);
    }

    private double? SpawnCornerRadius
    {
        get
        {
            var r = (VisualRoot as DeskCanvas.Views.Widget)?.Radius.TopLeft ?? 0;
            return r > 0 ? r : null;
        }
    }

    private (Point? ScreenCenter, TopLevel? TopLevel) GetScreenCenterAndTopLevel()
    {
        if (VisualRoot is Visual rootVisual && VisualRoot is TopLevel topLevel)
        {
            var bounds = Bounds;
            var centerLocal = new Point(bounds.Width / 2, bounds.Height / 2);
            var rootPoint = this.TranslatePoint(centerLocal, rootVisual);
            if (rootPoint.HasValue)
            {
                var screenPoint = topLevel.PointToScreen(rootPoint.Value);
                return (new Point(screenPoint.X, screenPoint.Y), topLevel);
            }
        }
        return (null, null);
    }

    public void UpdateSettings(HabitsModel newSettings)
    {
        currentModel = newSettings;
        viewModel.UpdateSettings(newSettings);
        UpdateCardContent();

        try
        {
            var newJson = JsonSerializer.SerializeToElement(newSettings, HabitsStore.SharedOptions);
            var newLayout = widgetLayoutProvider.Get() with { Settings = newJson };
            widgetLayoutProvider.Save(newLayout);
        }
        catch { }
    }
}
