using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Threading;
using Reminders.Locales;
using Reminders.Models;
using Reminders.Services;
using uWidgets.Core.Interfaces;
using uWidgets.Core.Models.Settings;
using uWidgets.Core.Services;
using uWidgets.Services;

namespace Reminders.Views;

public class ReminderItemViewModel : INotifyPropertyChanged
{
    private bool completed;
    private string title;

    public ReminderItemViewModel(ReminderModel model)
    {
        completed = model.Completed;
        title = model.Title;
    }

    public bool Completed
    {
        get => completed;
        set
        {
            if (completed != value)
            {
                completed = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(TextOpacity));
                OnPropertyChanged(nameof(TextDecorations));
            }
        }
    }

    public string Title
    {
        get => title;
        set
        {
            if (title != value)
            {
                title = value;
                OnPropertyChanged();
            }
        }
    }

    public double TextOpacity => Completed ? 0.45 : 1.0;
    public TextDecorationCollection? TextDecorations => Completed ? Avalonia.Media.TextDecorations.Strikethrough : null;

    public ReminderModel ToModel() => new(Completed, Title);

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? propName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propName));
}

public partial class RemindersPopupWindow : Window
{
    private static RemindersPopupWindow? activePopup;
    private static DateTime lastCloseTime = DateTime.MinValue;

    private RemindersListModel currentModel;
    private readonly Point? spawnScreenCenter;
    private readonly Action<RemindersListModel>? onModelChanged;
    private readonly ObservableCollection<ReminderItemViewModel> items = [];
    private DateTime loadedTime = DateTime.MinValue;

    private ScaleTransform? ZoomTransform => CardBorder.RenderTransform as ScaleTransform;

    public RemindersPopupWindow() : this(new RemindersListModel(null, []), null, null) { }

    public RemindersPopupWindow(
        RemindersListModel model,
        Point? screenCenter = null,
        Action<RemindersListModel>? onModelChanged = null)
    {
        currentModel = model;
        spawnScreenCenter = screenCenter;
        this.onModelChanged = onModelChanged;

        InitializeComponent();

        CardBorder.Opacity = 0.0;
        if (ZoomTransform is { } t)
        {
            t.ScaleX = 0.90;
            t.ScaleY = 0.90;
        }

        ItemsList.ItemsSource = items;

        Loaded += OnWindowLoaded;
        Deactivated += OnWindowDeactivated;
        Closing += OnWindowClosing;
        Closed += OnWindowClosed;
        KeyDown += OnWindowKeyDown;

        TitleBox.Text = !string.IsNullOrWhiteSpace(currentModel.ListName) ? currentModel.ListName : Locale.Reminders_List_Title;

        ApplyTheme();
        PopulateItems();

        PopupLiquidGlassService.PreRenderCompleted += OnPreRenderCompleted;
        RemindersStore.ModelChanged += OnStoreModelChanged;
    }

    public static void ShowPopup(
        RemindersListModel model,
        Point? screenCenter,
        Window? owner = null,
        Action<RemindersListModel>? onModelChanged = null)
    {
        if ((DateTime.UtcNow - lastCloseTime).TotalMilliseconds < 250)
            return;

        if (activePopup != null)
        {
            try { activePopup.Close(); } catch { }
            activePopup = null;
            return;
        }

        var popup = new RemindersPopupWindow(model, screenCenter, onModelChanged);
        activePopup = popup;

        if (owner != null)
            popup.Show(owner);
        else
            popup.Show();

        // Secondary panel: above the widget band, below ordinary application windows.
        WidgetZOrder.PinPanelAboveWidgets(popup);

        popup.Activate();
    }

    private void OnWindowLoaded(object? sender, RoutedEventArgs e)
    {
        loadedTime = DateTime.UtcNow;
        PositionWindow();
        if (LiquidGlassSurfaceControl.IsVisible)
        {
            LiquidGlassSurfaceControl.RequestRender(immediate: true);
        }
        PlayZoomInAnimation();
        NewItemTextBox.Focus();
    }

    private void PositionWindow()
    {
        Screen? screen = null;
        if (spawnScreenCenter.HasValue)
        {
            screen = Screens.ScreenFromPoint(new PixelPoint(
                (int)Math.Round(spawnScreenCenter.Value.X),
                (int)Math.Round(spawnScreenCenter.Value.Y)));
        }
        screen ??= Screens.Primary;
        if (screen == null) return;

        double scale = screen.Scaling > 0 ? screen.Scaling : 1.0;
        double physWidth = Width * scale;
        double physHeight = Height * scale;

        double targetX;
        double targetY;

        if (spawnScreenCenter.HasValue)
        {
            targetX = spawnScreenCenter.Value.X - physWidth / 2.0;
            targetY = spawnScreenCenter.Value.Y - physHeight / 2.0;
        }
        else
        {
            targetX = screen.WorkingArea.X + (screen.WorkingArea.Width - physWidth) / 2.0;
            targetY = screen.WorkingArea.Y + (screen.WorkingArea.Height - physHeight) / 2.0;
        }

        var work = screen.WorkingArea;
        double margin = 16 * scale;
        targetX = Math.Clamp(targetX, work.X + margin, work.X + Math.Max(0, work.Width - physWidth - margin));
        targetY = Math.Clamp(targetY, work.Y + margin, work.Y + Math.Max(0, work.Height - physHeight - margin));

        Position = new PixelPoint((int)Math.Round(targetX), (int)Math.Round(targetY));
    }

    private void PlayZoomInAnimation()
    {
        CardBorder.Transitions = new Transitions
        {
            new DoubleTransition
            {
                Property = Visual.OpacityProperty,
                Duration = TimeSpan.FromMilliseconds(200),
                Easing = new CubicEaseOut()
            }
        };

        if (ZoomTransform is { } transform)
        {
            transform.Transitions = new Transitions
            {
                new DoubleTransition
                {
                    Property = ScaleTransform.ScaleXProperty,
                    Duration = TimeSpan.FromMilliseconds(220),
                    Easing = new BackEaseOut()
                },
                new DoubleTransition
                {
                    Property = ScaleTransform.ScaleYProperty,
                    Duration = TimeSpan.FromMilliseconds(220),
                    Easing = new BackEaseOut()
                }
            };
            transform.ScaleX = 1.0;
            transform.ScaleY = 1.0;
        }

        CardBorder.Opacity = 1.0;
    }

    private void ApplyTheme()
    {
        Theme theme;
        try
        {
            theme = new AppSettingsProvider().Get().Theme;
        }
        catch
        {
            theme = new Theme(DarkMode: true, AccentColor: null, OpacityLevel: 0.8, Monochrome: false, UseNativeFrame: false, FontFamily: "Inter");
        }

        bool isDark = ActualThemeVariant == ThemeVariant.Dark || (theme.DarkMode ?? true);

        if (theme.UsesRenderedGlass)
        {
            TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
            if (LiquidGlassWallpaper.LiveSamplingEnabled)
            {
                LiquidGlassSurfaceControl.Material = theme;
                LiquidGlassSurfaceControl.CornerRadius = CardBorder.CornerRadius;
                LiquidGlassSurfaceControl.IsVisible = true;
                LiquidGlassBgImage.IsVisible = false;
                LiquidGlassOverlay.IsVisible = false;
                CardBorder.Background = Brushes.Transparent;
                CardBorder.BorderBrush = new SolidColorBrush(Color.FromArgb(90, 255, 255, 255));
                LiquidGlassSurfaceControl.RequestRender(immediate: true);
            }
            else
            {
                LiquidGlassSurfaceControl.IsVisible = false;
                LiquidGlassBgImage.IsVisible = true;
                LiquidGlassOverlay.IsVisible = false;
                CardBorder.Background = Brushes.Transparent;
                CardBorder.BorderBrush = new SolidColorBrush(Color.FromArgb(90, 255, 255, 255));

                var screen = spawnScreenCenter.HasValue ? Screens.ScreenFromPoint(new PixelPoint((int)spawnScreenCenter.Value.X, (int)spawnScreenCenter.Value.Y)) : Screens.Primary;
                var bmp = PopupLiquidGlassService.GetCachedBitmapFor(spawnScreenCenter, Width, Height, screen, Screens.All);

                if (bmp != null)
                {
                    LiquidGlassBgImage.Source = bmp;
                }
                else
                {
                    CardBorder.Background = new SolidColorBrush(isDark ? Color.FromArgb(40, 28, 28, 32) : Color.FromArgb(40, 245, 245, 248));
                    _ = TriggerDirectLiquidGlassRender(theme, isDark, screen);
                }
            }
        }
        else if (theme.IsColorful)
        {
            LiquidGlassSurfaceControl.IsVisible = false;
            TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
            LiquidGlassBgImage.IsVisible = false;
            LiquidGlassOverlay.IsVisible = false;
            CardBorder.Background = new SolidColorBrush(isDark ? Color.Parse("#1C1C1E") : Color.Parse("#FFFFFF"));
            CardBorder.BorderBrush = new SolidColorBrush(isDark ? Color.FromArgb(60, 255, 255, 255) : Color.FromArgb(40, 0, 0, 0));
        }
        else if (theme.EffectiveSurface == SurfaceStyle.Solid)
        {
            LiquidGlassSurfaceControl.IsVisible = false;
            TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
            LiquidGlassBgImage.IsVisible = false;
            LiquidGlassOverlay.IsVisible = false;
            var hex = isDark ? theme.EffectiveSolidBackgroundDark : theme.EffectiveSolidBackgroundLight;
            var baseColor = Color.TryParse(hex, out var parsed) ? parsed : (isDark ? Color.FromRgb(46, 46, 46) : Colors.White);
            byte alpha = (byte)Math.Clamp(Math.Round(theme.OpacityLevel * 255), 40, 255);
            CardBorder.Background = new SolidColorBrush(Color.FromArgb(alpha, baseColor.R, baseColor.G, baseColor.B));
            CardBorder.BorderBrush = new SolidColorBrush(isDark ? Color.FromArgb(40, 255, 255, 255) : Color.FromArgb(30, 0, 0, 0));
        }
        else // Acrylic (毛玻璃)
        {
            LiquidGlassSurfaceControl.IsVisible = false;
            TransparencyLevelHint = [WindowTransparencyLevel.AcrylicBlur];
            LiquidGlassBgImage.IsVisible = false;
            LiquidGlassOverlay.IsVisible = false;
            byte alpha = (byte)Math.Clamp(Math.Round(theme.OpacityLevel * 220), 40, 240);
            CardBorder.Background = new SolidColorBrush(isDark ? Color.FromArgb(alpha, 28, 28, 32) : Color.FromArgb(alpha, 245, 245, 248));
            CardBorder.BorderBrush = new SolidColorBrush(isDark ? Color.FromArgb(55, 255, 255, 255) : Color.FromArgb(35, 0, 0, 0));
        }

        IBrush textBrush = isDark ? Brushes.White : new SolidColorBrush(Color.FromRgb(30, 30, 30));
        IBrush subTextBrush = isDark ? new SolidColorBrush(Color.FromArgb(200, 255, 255, 255)) : new SolidColorBrush(Color.FromArgb(180, 0, 0, 0));
        TitleBox.Foreground = textBrush;
        CloseButton.Foreground = subTextBrush;
        ClearCompletedButton.Foreground = subTextBrush;
    }

    private async Task TriggerDirectLiquidGlassRender(Theme theme, bool isDark, Screen? screen)
    {
        try
        {
            var bmp = await PopupLiquidGlassService.RenderDirectAsync(
                spawnScreenCenter,
                Width,
                Height,
                CardBorder.CornerRadius.TopLeft,
                theme,
                isDark,
                screen,
                Screens.All);

            if (bmp != null)
            {
                LiquidGlassBgImage.Source = bmp;
                CardBorder.Background = Brushes.Transparent;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[RemindersPopupWindow] TriggerDirectLiquidGlassRender failed: {ex.Message}");
        }
    }

    private void OnPreRenderCompleted()
    {
        if (LiquidGlassWallpaper.LiveSamplingEnabled) return;
        if (LiquidGlassBgImage.IsVisible)
        {
            var screen = spawnScreenCenter.HasValue ? Screens.ScreenFromPoint(new PixelPoint((int)spawnScreenCenter.Value.X, (int)spawnScreenCenter.Value.Y)) : Screens.Primary;
            var bmp = PopupLiquidGlassService.GetCachedBitmapFor(spawnScreenCenter, Width, Height, screen, Screens.All);
            if (bmp != null)
            {
                LiquidGlassBgImage.Source = bmp;
                CardBorder.Background = Brushes.Transparent;
            }
        }
    }

    private void PopulateItems()
    {
        items.Clear();
        foreach (var r in currentModel.Reminders)
            items.Add(new ReminderItemViewModel(r));

        UpdateStatsAndVisibility();
    }

    private void UpdateStatsAndVisibility()
    {
        int total = items.Count;
        int completed = items.Count(i => i.Completed);

        SubtitleText.Text = $"共 {total} 项" + (completed > 0 ? $" · {completed} 项已完成" : "");
        EmptyPanel.IsVisible = total == 0;
        ClearCompletedButton.IsVisible = completed > 0;
    }

    private void CommitChanges()
    {
        var updatedList = items.Select(i => i.ToModel()).ToList();
        currentModel = currentModel with { Reminders = updatedList };
        onModelChanged?.Invoke(currentModel);
        UpdateStatsAndVisibility();
    }

    // ---------- 条目交互 ----------

    private void OnItemCheckClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.DataContext is ReminderItemViewModel item)
        {
            item.Completed = !item.Completed;
            if (item.Completed && currentModel.DeleteOnCheck)
            {
                items.Remove(item);
            }
            else if (item.Completed)
            {
                var index = items.IndexOf(item);
                if (index >= 0 && index < items.Count - 1)
                {
                    items.RemoveAt(index);
                    items.Add(item);
                }
            }
            CommitChanges();
        }
    }

    private void OnItemTextLostFocus(object? sender, RoutedEventArgs e)
    {
        if (sender is TextBox tb && tb.DataContext is ReminderItemViewModel item)
        {
            var text = tb.Text?.Trim() ?? "";
            if (string.IsNullOrEmpty(text))
            {
                items.Remove(item);
            }
            else
            {
                item.Title = text;
            }
            CommitChanges();
        }
    }

    private void OnItemTextKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            TopLevel.GetTopLevel(this)?.FocusManager?.ClearFocus();
            e.Handled = true;
        }
    }

    private void OnItemDeleteClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.DataContext is ReminderItemViewModel item)
        {
            items.Remove(item);
            CommitChanges();
        }
    }

    private void OnClearCompletedClicked(object? sender, RoutedEventArgs e)
    {
        var toRemove = items.Where(i => i.Completed).ToList();
        foreach (var item in toRemove)
            items.Remove(item);

        CommitChanges();
    }

    // ---------- 新建待办 ----------

    private void OnNewItemKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            AddNewItem();
            e.Handled = true;
        }
    }

    private void OnAddClicked(object? sender, RoutedEventArgs e)
    {
        AddNewItem();
    }

    private void AddNewItem()
    {
        var text = NewItemTextBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(text))
            return;

        var newItem = new ReminderItemViewModel(new ReminderModel(false, text));
        items.Add(newItem);
        NewItemTextBox.Clear();
        CommitChanges();

        Dispatcher.UIThread.Post(() => NewItemTextBox.Focus());
    }

    // ---------- 标题修改 ----------

    private void OnTitleLostFocus(object? sender, RoutedEventArgs e)
    {
        var title = TitleBox.Text?.Trim();
        if (title != currentModel.ListName)
        {
            currentModel = currentModel with { ListName = title };
            onModelChanged?.Invoke(currentModel);
        }
    }

    private void OnTitleKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            TopLevel.GetTopLevel(this)?.FocusManager?.ClearFocus();
            e.Handled = true;
        }
    }

    // ---------- 窗口生命周期 ----------

    private void OnCloseClicked(object? sender, RoutedEventArgs e)
    {
        Close();
    }

    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
        }
    }

    private void OnWindowDeactivated(object? sender, EventArgs e)
    {
        if ((DateTime.UtcNow - loadedTime).TotalMilliseconds > 300)
        {
            Close();
        }
    }

    private void OnWindowClosing(object? sender, CancelEventArgs e)
    {
        lastCloseTime = DateTime.UtcNow;
    }

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        LiquidGlassSurfaceControl.IsVisible = false;
        RemindersStore.ModelChanged -= OnStoreModelChanged;
        PopupLiquidGlassService.PreRenderCompleted -= OnPreRenderCompleted;
        if (activePopup == this)
            activePopup = null;
    }

    private void OnStoreModelChanged(RemindersListModel newModel, object? sender)
    {
        if (Equals(currentModel, newModel)) return;
        currentModel = newModel;
        TitleBox.Text = !string.IsNullOrWhiteSpace(currentModel.ListName) ? currentModel.ListName : Locale.Reminders_List_Title;
        PopulateItems();
    }
}
