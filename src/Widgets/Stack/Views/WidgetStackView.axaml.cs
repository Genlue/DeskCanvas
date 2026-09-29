using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using StackWidgets.Models;
using uWidgets.Core.Interfaces;
using uWidgets.Core.Models;
using uWidgets.Core.Models.Attributes;
using uWidgets.Core.Models.Settings;
using AppTheme = uWidgets.Core.Models.Settings.Theme;
using uWidgets.Views;

namespace StackWidgets.Views;

public partial class WidgetStackView : UserControl, IWidgetSelfRefreshing, IStackWidget
{
    private readonly IWidgetLayoutProvider widgetLayoutProvider;
    private readonly IAssemblyProvider assemblyProvider;
    private readonly IAppSettingsProvider? appSettingsProvider;

    /// <summary>Cached so Unloaded can unsubscribe: an anonymous lambda left on the
    /// process-lifetime appSettingsProvider rooted every instance forever (Gallery previews,
    /// rebuilds), one leaked view per visit.</summary>
    private readonly DataChangedEvent<AppSettings>? onAppDataChanged;

    private WidgetStackModel model;
    private DispatcherTimer? saveDebounceTimer;
    private bool isSavingSelf;

    private readonly Dictionary<int, Border> childCards = new();
    private readonly Dictionary<int, UserControl?> childControls = new();
    private Border? emptyCard;
    private int currentActiveIndex = 0;
    private DispatcherTimer? activeTransitionTimer;
    private int transitionTargetIndex = -1;

    public event EventHandler? IndicatorItemsChanged;

    public WidgetStackModel Model => model;

    public bool CanEditCurrentChild => model.Entries.Count > 0 && CanEditChildAt(model.SelectedIndex);

    public string? CurrentChildTitle
    {
        get
        {
            if (model?.Entries == null || model.Entries.Count == 0) return null;
            if (model.SelectedIndex < 0 || model.SelectedIndex >= model.Entries.Count) return null;
            return model.Entries[model.SelectedIndex].DisplayTitle;
        }
    }

    public IReadOnlyList<StackWidgetIndicatorItem> IndicatorItems
    {
        get
        {
            if (model?.Entries == null || model.Entries.Count <= 1) return Array.Empty<StackWidgetIndicatorItem>();
            var list = new List<StackWidgetIndicatorItem>();
            for (int i = 0; i < model.Entries.Count; i++)
            {
                bool isActive = i == model.SelectedIndex;
                list.Add(new StackWidgetIndicatorItem(
                    i,
                    model.Entries[i].DisplayTitle,
                    isActive,
                    isActive ? 1.0 : 0.30
                ));
            }
            return list;
        }
    }

    public bool IsTransitionActive => activeTransitionTimer != null;

    public WidgetStackView()
        : this(new WidgetStackModel(), null!, null!)
    {
    }

    public WidgetStackView(IWidgetLayoutProvider widgetLayoutProvider, IAssemblyProvider assemblyProvider)
        : this(new WidgetStackModel(), widgetLayoutProvider, assemblyProvider)
    {
    }

    public WidgetStackView(WidgetStackModel model, IWidgetLayoutProvider widgetLayoutProvider, IAssemblyProvider assemblyProvider, IAppSettingsProvider? appSettingsProvider = null)
    {
        this.model = model ?? new WidgetStackModel();
        this.widgetLayoutProvider = widgetLayoutProvider;
        this.assemblyProvider = assemblyProvider;
        this.appSettingsProvider = appSettingsProvider ?? (uWidgets.App.Services?.GetService(typeof(IAppSettingsProvider)) as IAppSettingsProvider) ?? new uWidgets.Core.Services.AppSettingsProvider();

        InitializeComponent();
        Classes.Add("Flush");

        EnsureValidModel();
        RenderCurrentState();

        Loaded += (_, _) =>
        {
            UpdateAllCardStyles();
            RenderCurrentState();
        };

        Unloaded += (_, _) =>
        {
            if (saveDebounceTimer?.IsEnabled == true)
            {
                saveDebounceTimer.Stop();
                SaveModelDirect();
            }

            if (appSettingsProvider != null && onAppDataChanged != null)
            {
                appSettingsProvider.DataChanged -= onAppDataChanged;
            }
        };

        ActualThemeVariantChanged += (_, _) => UpdateAllCardStyles();

        if (this.appSettingsProvider != null)
        {
            onAppDataChanged = (_, _, _) => Dispatcher.UIThread.Post(UpdateAllCardStyles);
            this.appSettingsProvider.DataChanged += onAppDataChanged;
        }

        PropertyChanged += (s, e) =>
        {
            if (e.Property == BoundsProperty)
            {
                UpdateAllCardStyles();
            }
        };
    }

    private void EnsureValidModel()
    {
        if (model == null)
        {
            model = new WidgetStackModel();
        }

        if (model.Entries == null)
        {
            model = model with { Entries = [] };
        }

        if (model.Entries.Count > 0)
        {
            if (model.SelectedIndex < 0 || model.SelectedIndex >= model.Entries.Count)
            {
                model = model with { SelectedIndex = Math.Clamp(model.SelectedIndex, 0, model.Entries.Count - 1) };
            }
        }
        else
        {
            model = model with { SelectedIndex = 0 };
        }
    }

    public void RenderCurrentState()
    {
        EnsureValidModel();
        UpdateDots();
        EnsureAllChildrenLoaded();
    }

    private void UpdateDots()
    {
        try
        {
            IndicatorItemsChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[WidgetStack] UpdateDots error: {ex.Message}");
        }
    }

    private void EnsureAllChildrenLoaded()
    {
        EnsureValidModel();
        if (WidgetsHost == null) return;

        if (model?.Entries == null || model.Entries.Count == 0)
        {
            EnsureEmptyCard();
            if (emptyCard != null)
            {
                emptyCard.IsVisible = true;
                if (!WidgetsHost.Children.Contains(emptyCard))
                {
                    WidgetsHost.Children.Add(emptyCard);
                }
            }

            // Hide any leftover child cards
            foreach (var card in childCards.Values)
            {
                if (card != null) card.IsVisible = false;
            }
            return;
        }

        if (emptyCard != null)
        {
            emptyCard.IsVisible = false;
        }

        for (int i = 0; i < model.Entries.Count; i++)
        {
            GetOrCreateChildCard(i);
        }

        currentActiveIndex = model.SelectedIndex;
        EnforceOnlyActiveVisible(currentActiveIndex);
    }

    private void EnforceOnlyActiveVisible(int activeIndex)
    {
        if (model?.Entries == null || model.Entries.Count == 0)
        {
            if (emptyCard != null) emptyCard.IsVisible = true;
            return;
        }

        if (emptyCard != null) emptyCard.IsVisible = false;

        for (int i = 0; i < model.Entries.Count; i++)
        {
            if (childCards.TryGetValue(i, out var card) && card != null)
            {
                bool isActive = (i == activeIndex);
                card.IsVisible = isActive;
                card.IsHitTestVisible = isActive;
                card.Opacity = 1.0;
                card.RenderTransform = null;
                card.ZIndex = isActive ? 1 : 0;
            }
        }
    }

    private Border? GetOrCreateChildCard(int index)
    {
        if (index < 0 || model?.Entries == null || index >= model.Entries.Count) return null;

        if (!childCards.TryGetValue(index, out var card) || card == null)
        {
            var entry = model.Entries[index];
            var control = CreateChildControl(index, entry);
            if (control == null) return null;

            childControls[index] = control;

            if (entry.ContentScale.HasValue && Math.Abs(entry.ContentScale.Value - 1.0) > 0.001)
            {
                control.RenderTransform = new ScaleTransform(entry.ContentScale.Value, entry.ContentScale.Value);
                control.RenderTransformOrigin = new RelativePoint(0.5, 0.5, RelativeUnit.Relative);
            }

            card = new Border
            {
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Stretch,
                CornerRadius = ResolveCornerRadius(),
                Background = ResolveCardBackground(entry.ViewTypeName),
                BorderThickness = ResolveOutlineThickness(entry.ViewTypeName),
                BorderBrush = ResolveOutlineBrush(entry.ViewTypeName),
                ClipToBounds = true,
                Child = control
            };

            childCards[index] = card;
            if (!WidgetsHost.Children.Contains(card))
            {
                WidgetsHost.Children.Add(card);
            }
        }
        else if (!WidgetsHost.Children.Contains(card))
        {
            WidgetsHost.Children.Add(card);
        }

        return card;
    }

    private UserControl? CreateChildControl(int index, StackedWidgetEntry entry)
    {
        try
        {
            if (assemblyProvider == null) return null;
            var assembly = assemblyProvider.LoadAssembly(entry.AssemblyName);
            var widgetInfo = assembly
                .GetCustomAttributes<WidgetInfoAttribute>()
                .FirstOrDefault(a => a.ViewType.Name == entry.ViewTypeName);

            if (widgetInfo == null) return null;

            object? childModel = null;
            if (widgetInfo.ModelType != null)
            {
                if (!string.IsNullOrEmpty(entry.SettingsJson))
                {
                    try { childModel = JsonSerializer.Deserialize(entry.SettingsJson, widgetInfo.ModelType); } catch { }
                }

                if (childModel == null)
                {
                    try { childModel = Activator.CreateInstance(widgetInfo.ModelType); } catch { }
                }
            }

            var childLayoutProvider = new StackedChildLayoutProvider((e, json) =>
            {
                UpdateChildSettings(index, json, forceRecreateIfNotSelfRefreshing: false);
            }, entry, widgetLayoutProvider);

            List<object> args = [];
            if (NeedsWidgetLayoutProvider(widgetInfo.ViewType))
                args.Add(childLayoutProvider);
            if (childModel != null)
                args.Add(childModel);

            var control = assemblyProvider.Activate(widgetInfo.ViewType, args.ToArray()) as UserControl;
            return control;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[WidgetStack] Failed to create child control: {ex.Message}");
            return null;
        }
    }

    private void EnsureEmptyCard()
    {
        if (emptyCard != null)
        {
            UpdateCardStyle(emptyCard, null);
            return;
        }

        var stackIcon = new PathIcon
        {
            Data = Geometry.Parse("M4 6h16M4 12h16M4 18h16"),
            Width = 24,
            Height = 24,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center
        };

        var titleBlock = new TextBlock
        {
            Text = "空白重叠卡片",
            FontSize = 12,
            FontWeight = FontWeight.SemiBold,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center
        };

        var hintBlock = new TextBlock
        {
            Text = "右键编辑以添加组件",
            FontSize = 10,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            Opacity = 0.8
        };

        var panel = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Vertical,
            Spacing = 6,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            Opacity = 0.35,
            Children = { stackIcon, titleBlock, hintBlock }
        };

        emptyCard = new Border
        {
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Stretch,
            CornerRadius = ResolveCornerRadius(),
            Background = ResolveDefaultCardBackground(),
            BorderThickness = ResolveOutlineThickness(null),
            BorderBrush = ResolveOutlineBrush(null),
            ClipToBounds = true,
            Child = panel
        };
    }

    private void UpdateCardStyle(Border card, string? viewTypeName)
    {
        card.CornerRadius = ResolveCornerRadius();
        card.Background = viewTypeName != null ? ResolveCardBackground(viewTypeName) : ResolveDefaultCardBackground();
        card.BorderThickness = ResolveOutlineThickness(viewTypeName);
        card.BorderBrush = ResolveOutlineBrush(viewTypeName);
    }

    private void UpdateAllCardStyles()
    {
        if (emptyCard != null)
        {
            UpdateCardStyle(emptyCard, null);
        }

        if (model?.Entries != null)
        {
            for (int i = 0; i < model.Entries.Count; i++)
            {
                if (childCards.TryGetValue(i, out var card) && card != null)
                {
                    UpdateCardStyle(card, model.Entries[i].ViewTypeName);
                }
            }
        }
    }

    private CornerRadius ResolveCornerRadius()
    {
        if (this.TryFindResource("WidgetCardCornerRadius", out var cr) && cr is CornerRadius r)
            return r;
        if (Application.Current?.TryFindResource("WidgetCardCornerRadius", out var acr) == true && acr is CornerRadius ar)
            return ar;
        var rad = appSettingsProvider?.Get()?.Dimensions?.Radius ?? 16;
        return new CornerRadius(rad);
    }

    private IBrush ResolveDefaultCardBackground()
    {
        var theme = appSettingsProvider?.Get()?.Theme;
        var variant = ActualThemeVariant;
        bool isDark = variant == ThemeVariant.Dark || (theme?.DarkMode ?? false);

        if (theme?.IsColorful == true)
        {
            if (this.TryFindResource("WidgetBackground", variant, out var cb) && cb is IBrush cbrush)
                return cbrush;
            return isDark
                ? new SolidColorBrush(Color.Parse("#1C1C1E"))
                : Brushes.White;
        }

        if (theme?.UsesRenderedGlass == true)
            return Brushes.Transparent;

        var darkColorHex = theme?.EffectiveSolidBackgroundDark ?? AppTheme.DefaultSolidBackgroundDark;
        var lightColorHex = theme?.EffectiveSolidBackgroundLight ?? AppTheme.DefaultSolidBackgroundLight;
        var baseColor = isDark ? ParseColor(darkColorHex, "#2E2E2E") : ParseColor(lightColorHex, "#FFFFFF");
        var opacity = Math.Clamp(theme?.OpacityLevel ?? 1.0, 0.0, 1.0);
        return new SolidColorBrush(baseColor, opacity);
    }

    private IBrush ResolveCardBackground(string viewTypeName)
    {
        var theme = appSettingsProvider?.Get()?.Theme;
        var variant = ActualThemeVariant;
        bool isDark = variant == ThemeVariant.Dark || (theme?.DarkMode ?? false);

        if (theme?.IsColorful == true)
        {
            if (viewTypeName is "Note" or "MapView")
            {
                return Brushes.Transparent;
            }

            if (viewTypeName == "AnalogI")
            {
                // Clock Style 1 (AnalogI):
                // Outer perimeter background is always authentic dark mode charcoal (#1C1C1E)
                return new SolidColorBrush(Color.Parse("#1C1C1E"));
            }
            if (viewTypeName == "Forecast")
            {
                if (this.TryFindResource("WeatherCardBackground", variant, out var wcb) && wcb is IBrush wb)
                    return wb;
                return BuildWeatherCardBackground(isDark);
            }
            if (viewTypeName is "Progress" or "ProgressView")
            {
                if (this.TryFindResource("ProgressCardBackground", variant, out var pcb) && pcb is IBrush pb)
                    return pb;
            }

            if (this.TryFindResource("WidgetBackground", variant, out var cb) && cb is IBrush cbrush)
                return cbrush;

            return isDark
                ? new SolidColorBrush(Color.Parse("#1C1C1E"))
                : Brushes.White;
        }

        if (theme?.UsesRenderedGlass == true)
            return Brushes.Transparent;

        var darkColorHex = theme?.EffectiveSolidBackgroundDark ?? AppTheme.DefaultSolidBackgroundDark;
        var lightColorHex = theme?.EffectiveSolidBackgroundLight ?? AppTheme.DefaultSolidBackgroundLight;
        var baseColor = isDark ? ParseColor(darkColorHex, "#2E2E2E") : ParseColor(lightColorHex, "#FFFFFF");
        var opacity = Math.Clamp(theme?.OpacityLevel ?? 1.0, 0.0, 1.0);
        return new SolidColorBrush(baseColor, opacity);
    }

    private static LinearGradientBrush BuildWeatherCardBackground(bool isDark)
    {
        return new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(Color.Parse(isDark ? "#121E31" : "#1B71BE"), 0.0),
                new GradientStop(Color.Parse(isDark ? "#1E314F" : "#509CEB"), 1.0)
            }
        };
    }

    private static Color ParseColor(string hex, string fallbackHex) =>
        Color.TryParse(hex, out var color) ? color : Color.Parse(fallbackHex);

    private Thickness ResolveOutlineThickness(string? viewTypeName)
    {
        var theme = appSettingsProvider?.Get()?.Theme;
        if (theme == null || theme.UseNativeFrame) return new Thickness(0);

        bool isOutlined = (theme.IsGlass && theme.OutlineWidth > 0) || theme.IsColorful;
        if (!isOutlined) return new Thickness(0);

        if (theme.IsColorful && (viewTypeName is "Note" or "MapView"))
            return new Thickness(0);

        var width = Math.Clamp(theme.OutlineWidth, 0, 6);
        if (width <= 0 && theme.IsColorful)
            return new Thickness(1);

        return new Thickness(width);
    }

    private IBrush? ResolveOutlineBrush(string? viewTypeName)
    {
        var theme = appSettingsProvider?.Get()?.Theme;
        if (theme == null || theme.UseNativeFrame) return null;

        bool isOutlined = (theme.IsGlass && theme.OutlineWidth > 0) || theme.IsColorful;
        if (!isOutlined) return null;

        if (theme.IsColorful && (viewTypeName is "Note" or "MapView"))
            return null;

        if (theme.OutlineWidth > 0)
        {
            var size = new Size(
                Bounds.Width > 0 ? Bounds.Width : (widgetLayoutProvider?.Get()?.Width ?? 150),
                Bounds.Height > 0 ? Bounds.Height : (widgetLayoutProvider?.Get()?.Height ?? 150)
            );
            return BuildConicOutlineBrush(theme, size);
        }

        if (theme.IsColorful)
        {
            if (this.TryFindResource("WidgetCardBorderBrush", out var res) && res is IBrush b)
                return b;
            var isDark = ActualThemeVariant == ThemeVariant.Dark || (theme.DarkMode ?? false);
            return isDark ? new SolidColorBrush(Color.Parse("#25FFFFFF")) : new SolidColorBrush(Color.Parse("#15000000"));
        }

        return null;
    }

    private static ConicGradientBrush BuildConicOutlineBrush(Theme theme, Size size)
    {
        var width = Math.Max(1, size.Width);
        var height = Math.Max(1, size.Height);
        var cornerAngle = Math.Atan2(width / 2.0, height / 2.0) * 180.0 / Math.PI;
        var startAngle = 360.0 - cornerAngle;
        var color = Color.TryParse(theme.EffectiveOutlineColor, out var parsed)
            ? parsed
            : Color.Parse(uWidgets.Core.Models.Settings.Theme.DefaultOutlineColor);
        var clear = Colors.Transparent;

        return new ConicGradientBrush
        {
            Angle = startAngle,
            Center = RelativePoint.Center,
            GradientStops =
            {
                new GradientStop(color, 0),
                new GradientStop(clear, 2 * cornerAngle / 360.0),
                new GradientStop(color, 0.5),
                new GradientStop(clear, 0.5 + 2 * cornerAngle / 360.0),
                new GradientStop(color, 1.0)
            }
        };
    }

    private static bool NeedsWidgetLayoutProvider(Type type) =>
        type.GetConstructors().Any(c => c.GetParameters().Any(p => p.ParameterType == typeof(IWidgetLayoutProvider)));

    public bool CanEditChildAt(int index)
    {
        if (model?.Entries == null || index < 0 || index >= model.Entries.Count) return false;
        var entry = model.Entries[index];
        return GetEditModelViewType(entry) != null;
    }

    public Type? GetEditModelViewType(StackedWidgetEntry entry)
    {
        try
        {
            if (assemblyProvider == null || string.IsNullOrEmpty(entry.AssemblyName)) return null;
            var assembly = assemblyProvider.LoadAssembly(entry.AssemblyName);
            if (assembly == null) return null;
            var widgetInfo = assembly
                .GetCustomAttributes<WidgetInfoAttribute>()
                .FirstOrDefault(a => a.ViewType?.Name == entry.ViewTypeName);
            return widgetInfo?.EditModelViewType;
        }
        catch
        {
            return null;
        }
    }

    public void EditCurrentChild(object? ownerWindow = null)
    {
        if (model?.Entries == null || model.SelectedIndex < 0 || model.SelectedIndex >= model.Entries.Count) return;
        EditChildAt(model.SelectedIndex, ownerWindow);
    }

    public void EditChildAt(int index, object? ownerWindow = null)
    {
        if (model?.Entries == null || index < 0 || index >= model.Entries.Count) return;
        var entry = model.Entries[index];
        var editType = GetEditModelViewType(entry);
        if (editType == null) return;

        var childLayoutProvider = new StackedChildLayoutProvider((e, json) =>
        {
            UpdateChildSettings(index, json, forceRecreateIfNotSelfRefreshing: true);
        }, entry, widgetLayoutProvider);

        var editControl = (UserControl)assemblyProvider.Activate(editType, childLayoutProvider);
        var editWindow = new EditWidget(childLayoutProvider, editControl);

        if (ownerWindow is Window win)
        {
            editWindow.ShowDialog(win);
        }
        else
        {
            editWindow.Show();
        }
    }

    public void SwitchToIndex(int index)
    {
        try
        {
            if (model?.Entries == null || model.Entries.Count <= 1) return;
            if (index < 0 || index >= model.Entries.Count || index == currentActiveIndex) return;

            // Stop any previous running transition
            if (activeTransitionTimer != null)
            {
                activeTransitionTimer.Stop();
                activeTransitionTimer = null;
                if (transitionTargetIndex >= 0 && transitionTargetIndex < model.Entries.Count)
                {
                    EnforceOnlyActiveVisible(transitionTargetIndex);
                }
            }

            int fromIndex = currentActiveIndex;
            int toIndex = index;
            bool forward = toIndex > fromIndex;

            currentActiveIndex = toIndex;
            transitionTargetIndex = toIndex;
            model = model with { SelectedIndex = toIndex };
            RequestSaveModel();
            UpdateDots();

            var fromCard = GetOrCreateChildCard(fromIndex);
            var toCard = GetOrCreateChildCard(toIndex);

            if (toCard == null || fromCard == null || fromCard == toCard || WidgetsHost == null)
            {
                EnforceOnlyActiveVisible(toIndex);
                return;
            }

            double height = WidgetsHost.Bounds.Height;
            if (height <= 0) height = Bounds.Height;
            if (height <= 0) height = widgetLayoutProvider?.Get()?.Height ?? 0;
            if (height <= 0) height = 150;

            // Immediately ensure any third card is hidden and has no transforms
            for (int i = 0; i < model.Entries.Count; i++)
            {
                if (i != fromIndex && i != toIndex && childCards.TryGetValue(i, out var other) && other != null)
                {
                    other.IsVisible = false;
                    other.RenderTransform = null;
                    other.Opacity = 0.0;
                }
            }

            // OUTGOING CARD: on top (ZIndex 10), starting at (0, 0) and Opacity 1.0
            var fromTransform = new TranslateTransform(0, 0);
            fromCard.RenderTransform = fromTransform;
            fromCard.IsVisible = true;
            fromCard.IsHitTestVisible = false;
            fromCard.Opacity = 1.0;
            fromCard.ZIndex = 10;

            // INCOMING CARD: underneath (ZIndex 5), slightly offset by 15% parallax, starting Opacity 0.4
            double toOffset = forward ? (height * 0.15) : (-height * 0.15);
            var toTransform = new TranslateTransform(0, toOffset);
            toCard.RenderTransform = toTransform;
            toCard.IsVisible = true;
            toCard.IsHitTestVisible = false;
            toCard.Opacity = 0.4;
            toCard.ZIndex = 5;

            const double durationMs = 240.0;
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();

            var timer = new DispatcherTimer(DispatcherPriority.Render)
            {
                Interval = TimeSpan.FromMilliseconds(10)
            };
            activeTransitionTimer = timer;

            timer.Tick += (_, _) =>
            {
                try
                {
                    if (activeTransitionTimer != timer)
                    {
                        timer.Stop();
                        return;
                    }

                    double elapsed = stopwatch.Elapsed.TotalMilliseconds;
                    double progress = Math.Clamp(elapsed / durationMs, 0.0, 1.0);
                    // CubicEaseOut curve
                    double ease = 1.0 - Math.Pow(1.0 - progress, 3.0);

                    // Outgoing card slides away (up or down) and dissolves away
                    double slideTarget = forward ? -height * 0.85 : height * 0.85;
                    fromTransform.Y = slideTarget * ease;
                    fromCard.Opacity = Math.Clamp(1.0 - ease, 0.0, 1.0);

                    // Incoming card gently rises into place and fades in
                    toTransform.Y = toOffset * (1.0 - ease);
                    toCard.Opacity = Math.Clamp(0.4 + 0.6 * ease, 0.0, 1.0);

                    if (progress >= 1.0)
                    {
                        timer.Stop();
                        if (activeTransitionTimer == timer)
                        {
                            activeTransitionTimer = null;
                            transitionTargetIndex = -1;
                        }

                        // Offscreen now: hide outgoing card
                        fromCard.IsVisible = false;
                        fromCard.Opacity = 1.0;
                        fromCard.RenderTransform = null;

                        toCard.RenderTransform = null;
                        toCard.IsVisible = true;
                        toCard.IsHitTestVisible = true;
                        toCard.Opacity = 1.0;
                        toCard.ZIndex = 1;

                        EnforceOnlyActiveVisible(toIndex);
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[WidgetStack] Transition tick error: {ex.Message}");
                    timer.Stop();
                    if (activeTransitionTimer == timer) activeTransitionTimer = null;
                    EnforceOnlyActiveVisible(toIndex);
                }
            };

            timer.Start();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[WidgetStack] SwitchToIndex error: {ex.Message}");
            EnforceOnlyActiveVisible(index);
        }
    }

    private void OnDotClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is int index)
        {
            SwitchToIndex(index);
        }
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);

        try
        {
            if (model?.Entries == null || !model.AllowWheelSwitch || model.Entries.Count <= 1) return;

            // 仅在圆点处可用滚轮切页：主体上的滚轮原样留给子组件自己的滚动行为，
            // 切页只由右侧圆点上的滚轮（Widget 宿主的 OnStackIndicatorsWheelChanged）承担。
            if (model.WheelSwitchOnlyOnDots) return;

            if (activeTransitionTimer != null)
            {
                e.Handled = true;
                return;
            }

            if (e.Delta.Y > 0)
            {
                // Up wheel -> previous widget
                var nextIndex = (model.SelectedIndex - 1 + model.Entries.Count) % model.Entries.Count;
                SwitchToIndex(nextIndex);
                e.Handled = true;
            }
            else if (e.Delta.Y < 0)
            {
                // Down wheel -> next widget
                var nextIndex = (model.SelectedIndex + 1) % model.Entries.Count;
                SwitchToIndex(nextIndex);
                e.Handled = true;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[WidgetStack] OnPointerWheelChanged error: {ex.Message}");
        }
    }

    public void UpdateChildSettings(int index, string? newSettingsJson, bool forceRecreateIfNotSelfRefreshing = false)
    {
        if (index < 0 || index >= model.Entries.Count) return;
        var entry = model.Entries[index];
        var updatedEntry = entry with { SettingsJson = newSettingsJson };
        var entries = model.Entries.ToList();
        entries[index] = updatedEntry;
        model = model with { Entries = entries };
        SaveModelDirect();

        if (childControls.TryGetValue(index, out var ctrl))
        {
            if (ctrl is IWidgetSelfRefreshing selfRefreshing)
            {
                var curLayout = widgetLayoutProvider?.Get();
                if (curLayout != null)
                {
                    var childLayout = new WidgetLayout(updatedEntry.AssemblyName, updatedEntry.ViewTypeName,
                        curLayout.X, curLayout.Y, curLayout.Width, curLayout.Height,
                        string.IsNullOrEmpty(newSettingsJson) ? null : JsonDocument.Parse(newSettingsJson).RootElement.Clone(),
                        curLayout.ContentScale);
                    selfRefreshing.Refresh(childLayout);
                }
            }
            else if (forceRecreateIfNotSelfRefreshing)
            {
                if (childCards.TryGetValue(index, out var oldCard) && oldCard != null)
                {
                    WidgetsHost.Children.Remove(oldCard);
                }
                childCards.Remove(index);
                childControls.Remove(index);
                var newCard = GetOrCreateChildCard(index);
                if (newCard != null)
                {
                    bool isActive = (index == currentActiveIndex);
                    newCard.IsVisible = isActive;
                    newCard.IsHitTestVisible = isActive;
                    newCard.Opacity = 1.0;
                    newCard.ZIndex = isActive ? 1 : 0;
                    newCard.RenderTransform = null;
                }
            }
        }
    }

    public void UpdateFromSettings(WidgetStackModel newModel)
    {
        saveDebounceTimer?.Stop();
        WidgetsHost.Children.Clear();
        childCards.Clear();
        childControls.Clear();
        emptyCard = null;
        model = newModel;
        SaveModelDirect();
        RenderCurrentState();
        UpdateAllCardStyles();
    }

    private void RequestSaveModel()
    {
        if (saveDebounceTimer == null)
        {
            saveDebounceTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(400)
            };
            saveDebounceTimer.Tick += (_, _) =>
            {
                saveDebounceTimer.Stop();
                SaveModelDirect();
            };
        }
        saveDebounceTimer.Stop();
        saveDebounceTimer.Start();
    }

    private void SaveModelDirect()
    {
        try
        {
            isSavingSelf = true;
            var curLayout = widgetLayoutProvider?.Get();
            if (curLayout == null) return;
            var json = JsonSerializer.SerializeToElement(model);
            widgetLayoutProvider?.Save(curLayout with { Settings = json });
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[WidgetStack] SaveModel error: {ex.Message}");
        }
        finally
        {
            isSavingSelf = false;
        }
    }

    public void Refresh(WidgetLayout layout)
    {
        if (isSavingSelf) return;

        var newModel = layout.GetModel<WidgetStackModel>();
        if (newModel == null) return;

        // If the stack entries are structurally equal (same widget views in same order)
        if (AreEntriesStructurallyEqual(model.Entries, newModel.Entries))
        {
            for (int i = 0; i < newModel.Entries.Count; i++)
            {
                var oldEntry = model.Entries[i];
                var newEntry = newModel.Entries[i];
                if (oldEntry.SettingsJson != newEntry.SettingsJson)
                {
                    if (childControls.TryGetValue(i, out var ctrl) && ctrl is IWidgetSelfRefreshing selfRefreshing)
                    {
                        var childLayout = new WidgetLayout(newEntry.AssemblyName, newEntry.ViewTypeName,
                            layout.X, layout.Y, layout.Width, layout.Height,
                            string.IsNullOrEmpty(newEntry.SettingsJson) ? null : JsonDocument.Parse(newEntry.SettingsJson).RootElement.Clone(),
                            layout.ContentScale);
                        selfRefreshing.Refresh(childLayout);
                    }
                    else
                    {
                        if (childCards.TryGetValue(i, out var oldCard) && oldCard != null)
                        {
                            WidgetsHost.Children.Remove(oldCard);
                        }
                        childCards.Remove(i);
                        childControls.Remove(i);
                        GetOrCreateChildCard(i);
                    }
                }
            }

            var oldSelectedIndex = model.SelectedIndex;
            model = newModel;
            UpdateDots();

            if (model.SelectedIndex != oldSelectedIndex)
            {
                SwitchToIndex(model.SelectedIndex);
            }
            return;
        }

        // Recreate if widgets were added/removed/reordered
        model = newModel;
        WidgetsHost.Children.Clear();
        childCards.Clear();
        childControls.Clear();
        emptyCard = null;
        RenderCurrentState();
        UpdateAllCardStyles();
    }

    private static bool AreEntriesStructurallyEqual(List<StackedWidgetEntry>? a, List<StackedWidgetEntry>? b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a == null || b == null) return false;
        if (a.Count != b.Count) return false;

        for (int i = 0; i < a.Count; i++)
        {
            if (a[i].AssemblyName != b[i].AssemblyName ||
                a[i].ViewTypeName != b[i].ViewTypeName)
            {
                return false;
            }
        }
        return true;
    }
}

/// <summary>
/// Child layout provider that wraps the parent stack widget layout and routes settings
/// mutations back to the parent stack model.
/// </summary>
public class StackedChildLayoutProvider(Action<StackedWidgetEntry, string?> onSaveSettings, StackedWidgetEntry entry, IWidgetLayoutProvider parentLayoutProvider)
    : IWidgetLayoutProvider
{
    public string ScreenId
    {
        get => parentLayoutProvider.ScreenId;
        set => parentLayoutProvider.ScreenId = value;
    }

    public event DataChangedEvent<WidgetLayout>? DataChanging;
    public event DataChangedEvent<WidgetLayout>? DataChanged;

    public WidgetLayout Get()
    {
        var parent = parentLayoutProvider.Get();
        JsonElement? settings = null;
        if (!string.IsNullOrEmpty(entry.SettingsJson))
        {
            try { settings = JsonDocument.Parse(entry.SettingsJson).RootElement.Clone(); } catch { }
        }
        return new WidgetLayout(entry.AssemblyName, entry.ViewTypeName, parent.X, parent.Y, parent.Width, parent.Height, settings, entry.ContentScale ?? parent.ContentScale);
    }

    public void Save(WidgetLayout data)
    {
        DataChanging?.Invoke(this, null, data);
        var settingsJson = data.Settings?.GetRawText();
        onSaveSettings(entry, settingsJson);
        DataChanged?.Invoke(this, null, data);
    }

    public void Remove()
    {
    }
}
