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
using StackWidgets.Views.Controls;
using DeskCanvas.Core.Interfaces;
using DeskCanvas.Core.Models;
using DeskCanvas.Core.Models.Attributes;
using DeskCanvas.Core.Models.Settings;
using DeskCanvas.Services;
using DeskCanvas.Views.Controls;
using AppTheme = DeskCanvas.Core.Models.Settings.Theme;
using DeskCanvas.Views;

namespace StackWidgets.Views;

/// <summary>
/// 重叠组件视图。除既有协议外，还实现 <see cref="IWidgetOverlayProvider"/>：
/// 向宿主提供下沉到本插件的分页圆点装饰（StackIndicatorStrip）与布局提示，
/// 宿主不再持有 Stack 专属 XAML 与放置逻辑（W10 §9.2）。
/// </summary>
public partial class WidgetStackView : UserControl, IWidgetSelfRefreshing, IStackWidget, IWidgetSuspendable, IWidgetOverlayProvider, IFocusThemedContent
{
    private readonly IWidgetLayoutProvider widgetLayoutProvider;
    private readonly IAssemblyProvider assemblyProvider;
    private readonly IAppSettingsProvider? appSettingsProvider;

    /// <summary>下沉后的分页圆点装饰。每个视图实例只创建一次（构造时），
    /// 通过 Overlay 交给宿主；不允许多窗口共享同一 visual child。</summary>
    private readonly StackIndicatorStrip indicatorStrip;

    /// <summary>Cached so Unloaded can unsubscribe: an anonymous lambda left on the
    /// process-lifetime appSettingsProvider rooted every instance forever (Gallery previews,
    /// rebuilds), one leaked view per visit.</summary>
    private readonly DataChangedEvent<AppSettings>? onAppDataChanged;

    private WidgetStackModel model;
    private DispatcherTimer? saveDebounceTimer;
    private bool isSavingSelf;

    private readonly Dictionary<int, Border> childCards = new();
    private readonly Dictionary<int, UserControl?> childControls = new();

    // 多彩（全彩）卡片的固定 macOS 底色（P2 挂账，待语义键收敛后统一）。与
    // ColorfulResources.WidgetBackground 的 Dark 值同源，但不从资源解析——多彩分支是
    // 文档化的显式回退保证：per-state 多个 surface 字典同时加载时，别的 material 的
    // WidgetBackground 可能遮蔽共享键。也不可换 SolidBackgroundDark/Light——那是用户
    // 可调的纯色主题底色（默认 #2E2E2E），值不同。
    private static readonly Color ColorfulCardBackgroundDark = Color.Parse("#1C1C1E");
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
            return model.Entries[model.SelectedIndex].GetLocalizedTitle();
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
                    model.Entries[i].GetLocalizedTitle(),
                    isActive,
                    isActive ? 1.0 : 0.30
                ));
            }
            return list;
        }
    }

    public bool IsTransitionActive => activeTransitionTimer != null;

    /// <summary>常态隐藏小圆点: the host reveals the dots only while switching or hovering.</summary>
    public bool AutoHideIndicators => model.AutoHideDots;

    // ---- W10: 装饰下沉（IWidgetOverlayProvider） -------------------------------------------

    /// <summary>分页圆点装饰；构造时创建一次，重复取用返回同一实例。</summary>
    public Control? Overlay => indicatorStrip;

    /// <summary>
    /// 与迁移前 StackIndicatorsMargin 语义等价的放置提示：常态卡片外右缘，
    /// 原生 Acrylic（UsesNativeBlur 且无原生窗框）时内移进卡片；Region 方案沿用
    /// “内移后卡片矩形已覆盖圆点”，不需要把装饰矩形并进窗口区域。
    /// </summary>
    public WidgetOverlayHints OverlayHints { get; } = new()
    {
        Placement = WidgetOverlayPlacement.OutsideCardRight,
        RightReservedWidth = 13,
        InsetOnNativeBlur = true,
        MergeBoundsIntoWindowRegion = false,
        RelayoutOnBoundsChange = true,
        HitTestableWhenHidden = true,
    };

    public void OnOverlayLayoutChanged(in WidgetOverlayLayoutContext context)
    {
        // 圆点条自适配（右对齐、垂直居中），无需按卡片几何额外重排。
    }

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
        this.appSettingsProvider = appSettingsProvider ?? (DeskCanvas.App.Services?.GetService(typeof(IAppSettingsProvider)) as IAppSettingsProvider) ?? new DeskCanvas.Core.Services.AppSettingsProvider();
        indicatorStrip = new StackIndicatorStrip(this);

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

    /// <summary>
    /// Fullscreen suspension, forwarded to the child cards.
    /// <para>
    /// The host only ever sees this stack (it is the widget's content), so before the forward the
    /// children — every one of which already knows how to release its rebuildable graphics — kept
    /// their timers, polls and caches alive behind a fullscreen application. Children are all
    /// created up front (<see cref="EnsureAllChildrenLoaded"/>), so the forward reaches hidden
    /// and active cards alike: nothing in the stack can be seen while every screen is covered,
    /// and each child restores itself on <see cref="Resume"/> without changing switching or the
    /// transition animation.
    /// </para>
    /// </summary>
    private bool suspended;

    public void Suspend()
    {
        if (suspended) return;
        suspended = true;

        foreach (var control in childControls.Values)
        {
            ForwardSuspend(control, suspend: true);
        }
    }

    public void Resume()
    {
        if (!suspended) return;
        suspended = false;

        foreach (var control in childControls.Values)
        {
            ForwardSuspend(control, suspend: false);
        }
    }

    private static void ForwardSuspend(UserControl? control, bool suspend)
    {
        if (control is not IWidgetSuspendable child) return;
        try
        {
            if (suspend) child.Suspend();
            else child.Resume();
        }
        catch (Exception ex)
        {
            // One misbehaving child must not abort the suspension of the whole stack.
            System.Diagnostics.Debug.WriteLine($"[WidgetStack] child {(suspend ? "Suspend" : "Resume")} failed: {ex.Message}");
        }
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

            // A child created while the desktop is covered (a settings-driven rebuild, a layout
            // change) must not start sampling behind the fullscreen application.
            if (suspended) ForwardSuspend(control, suspend: true);

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
                Background = ResolveCardBackground(control),
                ClipToBounds = true,
                Child = WrapWithHighlight(control)
            };

            UpdateHighlight(card);
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
            UpdateCardStyle(emptyCard, null, null);
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
            Text = Locales.Locale.EmptyCard_Title,
            FontSize = 12,
            FontWeight = FontWeight.SemiBold,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center
        };

        var hintBlock = new TextBlock
        {
            Text = Locales.Locale.EmptyCard_Hint,
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
            ClipToBounds = true,
            Child = WrapWithHighlight(panel)
        };

        UpdateHighlight(emptyCard);
    }

    private void UpdateCardStyle(Border card, string? viewTypeName, UserControl? control)
    {
        card.CornerRadius = ResolveCornerRadius();
        card.Background = viewTypeName != null ? ResolveCardBackground(control) : ResolveDefaultCardBackground();
        UpdateHighlight(card);
    }

    private void UpdateAllCardStyles()
    {
        if (emptyCard != null)
        {
            UpdateCardStyle(emptyCard, null, null);
        }

        if (model?.Entries != null)
        {
            for (int i = 0; i < model.Entries.Count; i++)
            {
                if (childCards.TryGetValue(i, out var card) && card != null)
                {
                    childControls.TryGetValue(i, out var control);
                    UpdateCardStyle(card, model.Entries[i].ViewTypeName, control);
                }
            }
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// 失焦自动切换: the host window pokes this when the focus state swaps the window's
    /// effective theme — the regular settings-changed path does not fire for focus transitions,
    /// and the inner cards resolve their own backgrounds.
    /// </remarks>
    public void OnFocusThemeChanged() => Dispatcher.UIThread.Post(UpdateAllCardStyles);

    private CornerRadius ResolveCornerRadius()
    {
        if (ThemeScopeResolver.TryFindResource<CornerRadius>(this, "WidgetCardCornerRadius", ActualThemeVariant, out var cr))
            return cr;
        var rad = appSettingsProvider?.Get()?.Dimensions?.Radius ?? 16;
        return new CornerRadius(rad);
    }

    private IBrush ResolveDefaultCardBackground()
    {
        // 失焦自动切换: the inner cards belong to the widget's window, so they follow that
        // window's effective theme (per-state surface material) — not the shared theme.
        var theme = FocusThemeCoordinator.ResolveVisualTheme(this) ?? appSettingsProvider?.Get()?.Theme;
        var variant = ActualThemeVariant;
        bool isDark = variant == ThemeVariant.Dark || (theme?.DarkMode ?? false);

        if (theme?.IsColorful == true)
        {
            // The palette's own values, computed rather than resolved from the shared
            // WidgetBackground resource — with per-state materials several surface dictionaries
            // are loaded at once and another material's brush could shadow it.
            return isDark
                ? new SolidColorBrush(ColorfulCardBackgroundDark)
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

    private IBrush ResolveCardBackground(UserControl? control)
    {
        // 失焦自动切换: same rule as ResolveDefaultCardBackground — the window's effective theme.
        var theme = FocusThemeCoordinator.ResolveVisualTheme(this) ?? appSettingsProvider?.Get()?.Theme;
        var variant = ActualThemeVariant;
        bool isDark = variant == ThemeVariant.Dark || (theme?.DarkMode ?? false);

        if (theme?.IsColorful == true)
        {
            // 与独立宿主（Widget.WidgetCardBackground）完全同源：子组件视图通过
            // IWidgetSurfaceHints.ColorfulBackgroundResourceKey 声明自己的多彩卡片画刷
            // （表盘-1 的 ClockCardBackground、天气的 WeatherCardBackground 等都走这一条路），
            // 从该子组件自己的资源上下文按明暗变体解析；解析不到（旧版 DLL 未实现接口）
            // 再退回与宿主相同的通用回退。重叠内的组件外观必须与独立摆放完全一致，
            // 因此这里不得按视图类型名分支（doc/开发规范.md §4.3）。
            if (control is IWidgetSurfaceHints hints
                && hints.ColorfulBackgroundResourceKey is { Length: > 0 } key
                && ThemeScopeResolver.TryFindResource<IBrush>(control, key, variant, out var hintedBrush)
                && hintedBrush != null)
            {
                return hintedBrush;
            }

            // Explicit fallback guarantee for 多彩: pure white (light) / charcoal (dark) —
            // the palette's own values, computed rather than resolved from the shared
            // WidgetBackground resource — with per-state materials several surface dictionaries
            // are loaded at once and another material's brush could shadow it.
            return isDark
                ? new SolidColorBrush(ColorfulCardBackgroundDark)
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

    private static Color ParseColor(string hex, string fallbackHex) =>
        Color.TryParse(hex, out var color) ? color : Color.Parse(fallbackHex);

    /// <summary>
    /// Puts the card content and its specular rim in one panel, in the same order as the host
    /// card (content first, rim above). The rim itself is resolved by <see cref="UpdateHighlight"/>
    /// — it stays in the tree for every material so switching themes never rebuilds a card.
    /// </summary>
    private static Panel WrapWithHighlight(Control content)
    {
        var panel = new Panel();
        panel.Children.Add(content);
        panel.Children.Add(new SpecularHighlight());
        return panel;
    }

    /// <summary>
    /// Applies the shared 高光 optics to one child card: 毛玻璃 / 全彩 draw the host's rim,
    /// 液态玻璃 leaves it to its wallpaper-sampled shader, and a native frame draws no card rim.
    /// </summary>
    private void UpdateHighlight(Border card)
    {
        if (card.Child is not Panel panel) return;
        var highlight = panel.Children.OfType<SpecularHighlight>().FirstOrDefault();
        if (highlight == null) return;

        var theme = FocusThemeCoordinator.ResolveVisualTheme(this) ?? appSettingsProvider?.Get()?.Theme;
        var glass = theme?.EffectiveLiquidGlass;
        highlight.CornerRadius = card.CornerRadius;
        highlight.Strength = glass?.Highlight ?? 0;
        highlight.Angle = glass?.HighlightAngle ?? 90;
        highlight.IsVisible = theme != null && !theme.UseNativeFrame && !theme.UsesRenderedGlass;
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
                        // Transition is over: IsTransitionActive just turned false, so the
                        // host must re-evaluate the auto-hidden dots (they may need to
                        // disappear again when the pointer is no longer over the widget).
                        UpdateDots();
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[WidgetStack] Transition tick error: {ex.Message}");
                    timer.Stop();
                    if (activeTransitionTimer == timer) activeTransitionTimer = null;
                    EnforceOnlyActiveVisible(toIndex);
                    UpdateDots();
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
