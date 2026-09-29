using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Reminders.Locales;
using Reminders.Models;
using Reminders.Services;
using Reminders.ViewModels;
using Reminders.Views.Controls;
using uWidgets.Core.Interfaces;
using uWidgets.Core.Models;
using uWidgets.Services;

namespace Reminders.Views;

public partial class List : UserControl, IWidgetSelfRefreshing
{
    private readonly IWidgetLayoutProvider widgetLayoutProvider;
    private readonly RemindersViewModel viewModel;

    public List(IWidgetLayoutProvider widgetLayoutProvider) 
        : this(RemindersStore.Get(), widgetLayoutProvider) {}
    
    public List(RemindersListModel model, IWidgetLayoutProvider widgetLayoutProvider)
    {
        this.widgetLayoutProvider = widgetLayoutProvider;
        var unifiedModel = RemindersStore.Get();
        viewModel = new RemindersViewModel(unifiedModel);
        Content = new ListSmall(this, viewModel);
        SizeChanged += OnSizeChanged;
        Unloaded += OnUnloaded;
        PointerEntered += OnPointerEntered;
        RemindersStore.ModelChanged += OnStoreModelChanged;
        InitializeComponent();
    }

    private void OnStoreModelChanged(RemindersListModel newModel, object? sender)
    {
        if (ReferenceEquals(sender, this)) return;
        viewModel.Update(newModel);
    }

    /// <inheritdoc />
    public void Refresh(WidgetLayout layout)
    {
        var model = RemindersStore.Get();
        viewModel.Update(model);
    }

    private void OnUnloaded(object? sender, RoutedEventArgs e)
    {
        SizeChanged -= OnSizeChanged;
        Unloaded -= OnUnloaded;
        PointerEntered -= OnPointerEntered;
        RemindersStore.ModelChanged -= OnStoreModelChanged;
    }

    private void OnPointerEntered(object? sender, Avalonia.Input.PointerEventArgs e)
    {
        PreRenderLiquidGlassPopup();
    }

    private void PreRenderLiquidGlassPopup()
    {
        try
        {
            var theme = new uWidgets.Core.Services.AppSettingsProvider().Get().Theme;
            if (theme.UsesRenderedGlass)
            {
                var (screenCenter, _) = GetScreenCenterAndTopLevel();
                var window = VisualRoot as Window;
                var screen = window?.Screens.ScreenFromWindow(window) ?? window?.Screens.Primary;
                bool isDark = ActualThemeVariant == Avalonia.Styling.ThemeVariant.Dark || (theme.DarkMode ?? true);
                PopupLiquidGlassService.RequestPreRender(screenCenter, 400, 490, SpawnCornerRadius ?? 18, theme, isDark, screen, window?.Screens.All);
            }
        }
        catch { }
    }

    private void OnSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        // Phone-style tiers (grid-span based): 2×2 = the classic small card,
        // 4×2 = wide card (counter beside the list), 4×4 = tall card (counter
        // above the list). 1×1 keeps the header-less compact list. Other custom
        // spans keep the historic pixel logic.
        switch (SizeTiers.ResolveTier(this, e.NewSize))
        {
            case WidgetTier.Cell:
                Content = new ListSmall(this, viewModel, compact: true);
                return;
            case WidgetTier.Small:
                Content = new ListSmall(this, viewModel);
                return;
            case WidgetTier.Medium:
                Content = new ListWide(this, viewModel);
                return;
            case WidgetTier.Large:
                Content = new ListLarge(this, viewModel);
                return;
        }

        var size = e.NewSize;
        const int smallSize = 200;
        var small = size is { Width: < smallSize, Height: < smallSize };
        var wide = size.AspectRatio >= 1.5;

        Content = (small, wide) switch
        {
            (true, _) => new ListSmall(this, viewModel),
            (_, true) => new ListWide(this, viewModel),
            _ => new ListLarge(this, viewModel)
        };
    }

    public void ListNameChanged(object? sender, RoutedEventArgs e)
    {
        var listName = (sender as TextBox)!.Text;
        UpdateModel(viewModel.Model with { ListName = listName });
    }

    public void OpenPopupWindow()
    {
        var (screenCenter, _) = GetScreenCenterAndTopLevel();
        var owner = VisualRoot as Window;
        RemindersPopupWindow.ShowPopup(RemindersStore.Get(), screenCenter, owner, UpdateModel, SpawnCornerRadius);
    }

    /// <summary>
    /// The host widget's visual corner radius, so the popup's corners and its pre-rendered
    /// glass match the widget card exactly. Null keeps the popup's own default radius.
    /// </summary>
    private double? SpawnCornerRadius
    {
        get
        {
            var r = (VisualRoot as uWidgets.Views.Widget)?.Radius.TopLeft ?? 0;
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

    private void UpdateModel(RemindersListModel newModel)
    {
        viewModel.Update(newModel);
        RemindersStore.Save(newModel, sender: this);

        try
        {
            var newSettings = JsonSerializer.SerializeToElement(newModel);
            var newLayout = widgetLayoutProvider.Get() with { Settings = newSettings };
            widgetLayoutProvider.Save(newLayout);
        }
        catch { }
    }

    public void CompleteReminder(object? sender, RoutedEventArgs e)
    {
        var reminder = (sender as Button)!.DataContext as ReminderModel;
        var index = viewModel.Reminders.IndexOf(reminder!);
        if (index < 0) return;

        var completed = !viewModel.Reminders[index].Completed;
        if (completed && viewModel.Model.DeleteOnCheck)
        {
            viewModel.Reminders.RemoveAt(index);
        }
        else
        {
            var updated = viewModel.Reminders[index] with { Completed = completed };
            if (completed && index < viewModel.Reminders.Count - 1)
            {
                viewModel.Reminders.RemoveAt(index);
                viewModel.Reminders.Add(updated);
            }
            else
            {
                viewModel.Reminders[index] = updated;
            }
        }
        UpdateModel(viewModel.Model);
    }
    
    public void EditReminder(object? sender, RoutedEventArgs e)
    {
        var text = (sender as TextBox)!.Text;
        var reminder = (sender as TextBox)!.DataContext as ReminderModel;
        var index = viewModel.Reminders.IndexOf(reminder!);
        if (index < 0) return;

        if (string.IsNullOrEmpty(text))
            viewModel.Reminders.RemoveAt(index);
        else
            viewModel.Reminders[index] = viewModel.Reminders[index] with { Title = text };
        
        UpdateModel(viewModel.Model);
    }
    
    public void CreateReminder(object? sender, RoutedEventArgs e)
    {
        var text = (sender as TextBox)!.Text;
        
        if (string.IsNullOrEmpty(text))
            return;

        viewModel.Reminders.Add(new ReminderModel(false, text));

        (sender as TextBox)!.Clear();
        UpdateModel(viewModel.Model);
    }
}
