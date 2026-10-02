using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using DeskCanvas.Core.Interfaces;
using DeskCanvas.Core.Models;
using DeskCanvas.Core.Models.Settings;
using DeskCanvas.Locales;
using DeskCanvas.Views;
using DeskCanvas.Views.Controls;

namespace DeskCanvas.Services;

/// <summary>
/// One widget inside the sidebar. It embeds the very same <see cref="WidgetCard"/> the desktop
/// window uses — so the glass material, adaptive radius, content view, stack indicators and
/// content scaling come from one implementation — but exposes the sidebar-side behaviour:
/// no free-form move/resize, and a "从侧栏移除" entry.
/// <para>
/// The host is the card's DataContext (<see cref="IWidgetCardHost"/>); every property the card
/// template binds to is resolved here. Geometry is the sidebar grid's business: the enclosing
/// <see cref="SidebarGridPanel"/> decides the rectangle (the widget's cell span at the grid's cell
/// size) and this host sizes its card from it in <see cref="ArrangeOverride"/>. The size menu
/// therefore edits the <b>span</b> in cells, never a pixel size.
/// </para>
/// <para>
/// Everything the host reads back about itself — the widget layout and the grid span — is read
/// <b>live</b> from the layout provider. The <see cref="SidebarWidgetEntry"/> it was constructed
/// from is a snapshot, and a snapshot never sees a resize that happened after it was taken.
/// </para>
/// </summary>
public sealed class SidebarWidgetHost : ContentControl, INotifyPropertyChanged, IWidgetCardHost, ISidebarGridItem
{
    /// <summary>Most grid rows a widget may claim (a tall widget may be taller than the grid is wide).</summary>
    private const int MaxSpanRows = 12;

    private readonly SidebarWidgetEntry entry;
    private readonly ISidebarWidgetLayoutProvider provider;
    private readonly IAppSettingsProvider appSettingsProvider;
    private readonly WidgetRuntimeFactory runtime;
    private readonly ProfileService? profileService;
    private readonly Func<Settings> settingsWindow;
    private readonly ISidebarHostCallbacks callbacks;
    private readonly SidebarWindow owner;

    private readonly WidgetCard card = new();
    private UserControl content;
    private readonly bool supportsEdit;
    private IStackWidget? activeStackWidget;
    private bool suspended;

    /// <summary>Last arranged card size (DIPs); 0 until the first layout pass.</summary>
    private double cardWidthDip;
    private double cardHeightDip;

    /// <inheritdoc />
    public new event PropertyChangedEventHandler? PropertyChanged;

    private void Notify(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private void NotifyAll()
    {
        foreach (var name in new[]
        {
            nameof(GlassMaterial), nameof(ShowsGlassMaterial), nameof(Radius), nameof(WidgetMargin),
            nameof(StackIndicatorsMargin), nameof(WidgetCardBackground), nameof(WidgetOutlineThickness),
            nameof(WidgetOutlineBrush), nameof(HasStackIndicators), nameof(StackIndicators),
            nameof(ShowResizeHandle), nameof(IsFixedWidget), nameof(IsFixed2x1Widget),
            nameof(IsFixedSquareWidget), nameof(IsMapWidget), nameof(IsFreeMode), nameof(SizeMenuTitle),
            nameof(ScaleMenuTitle), nameof(SizeColumnsValue), nameof(SizeRowsValue),
            nameof(ShowNormalEditButton), nameof(ShowStackEditButton),
            nameof(CanEditStackedWidgetChild), nameof(EditStackChildTitle), nameof(Edit), nameof(IsSidebarHost)
        }) Notify(name);
    }

    public SidebarWidgetHost(SidebarWidgetEntry entry, ISidebarWidgetLayoutProvider provider,
        WidgetRuntimeFactory runtime, IAppSettingsProvider appSettingsProvider, ProfileService? profileService,
        Func<Settings> settingsWindow, ISidebarHostCallbacks callbacks, SidebarWindow owner)
    {
        this.entry = entry;
        this.provider = provider;
        this.runtime = runtime;
        this.appSettingsProvider = appSettingsProvider;
        this.profileService = profileService;
        this.settingsWindow = settingsWindow;
        this.callbacks = callbacks;
        this.owner = owner;

        DataContext = this;
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        VerticalContentAlignment = VerticalAlignment.Stretch;

        supportsEdit = runtime.EditFactory(entry.Layout, provider) != null;
        content = runtime.CreateContent(entry.Layout, provider);
        PrepareContent(content);
        card.DataContext = this;
        Content = card;

        provider.DataChanged += OnWidgetLayoutUpdated;
        appSettingsProvider.DataChanged += OnAppSettingsUpdated;

        BuildContextMenu();
    }

    /// <summary>Stable instance id of this sidebar widget.</summary>
    public string InstanceId => entry.InstanceId;

    /// <summary>The sidebar entry this host was built from (a snapshot — see the type remarks).</summary>
    public SidebarWidgetEntry Entry => entry;

    /// <summary>The widget's live layout, read from the provider on every access.</summary>
    public WidgetLayout Layout => provider.Get();

    /// <inheritdoc />
    public bool IsSidebarHost => true;

    /// <summary>
    /// The glass mask a frameless widget publishes, when it publishes one. A frameless widget (the
    /// 无边框时钟) draws its material inside its own glyphs, so the frosted area of the shared sidebar
    /// window has to follow those glyphs rather than the card rectangle (see IFramelessGlassMask).
    /// </summary>
    public IFramelessGlassMask? GlassMask => content as IFramelessGlassMask;

    /// <summary>
    /// The content's own scale. The glyph mask is measured against the widget's unscaled layout, so a
    /// host that maps the spans into its region has to apply this itself.
    /// </summary>
    public double ContentScale => EffectiveContentScale;

    // ---------- Geometry ----------

    /// <summary>
    /// The span this card occupies, in grid cells. An entry stored before the sidebar became a
    /// grid carries no span, so it falls back to the widget's own idiom and is healed on the way
    /// through <see cref="SidebarRules.FitSpan"/> (which also keeps a fixed-size widget on its
    /// legal grid instead of letting a stale pixel size dictate an impossible shape).
    /// </summary>
    public (int Columns, int Rows) Span
    {
        get
        {
            var grid = Math.Max(1, owner.GridColumns);
            var requested = provider.Span ?? DefaultSpan;
            return SidebarRules.FitSpan(requested, SnapOptions, grid);
        }
    }

    /// <inheritdoc />
    (int Columns, int Rows) ISidebarGridItem.GridSpan => Span;

    /// <summary>
    /// The span a widget falls back to when its entry has no recorded one (a configuration written
    /// before the sidebar was a grid). Fixed-size widgets know their base shape; a 1×1 family comes
    /// back as the 2×2 the gallery offers rather than as a postage stamp.
    /// </summary>
    private (int Columns, int Rows) DefaultSpan
    {
        get
        {
            if (FixedSizeWidget is { AllowedBaseSpans.Count: > 0 } fixedWidget)
            {
                var baseSpan = fixedWidget.AllowedBaseSpans[0];
                return baseSpan is { Columns: 1, Rows: 1 } ? (2, 2) : baseSpan;
            }

            return (2, 2);
        }
    }

    /// <summary>
    /// The spans a resize may land on. Empty for ordinary widgets (any span fits); a fixed-size
    /// widget or the map offers exactly the presets its own menu shows, so a resize can never ask
    /// the content for a shape it cannot draw.
    /// </summary>
    private IReadOnlyList<(int Columns, int Rows)> SnapOptions
    {
        get
        {
            var presets = WidgetSizePresets.For(FixedSizeWidget, IsMapWidget);
            return presets.Count == 0 ? [] : [.. presets.Select(preset => (preset.Columns, preset.Rows))];
        }
    }

    /// <summary>Re-lay out this card (the widget's content changed its natural size).</summary>
    private void RefreshLayout()
    {
        InvalidateMeasure();
        InvalidateArrange();
        owner.RelayoutGrid();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        // The grid panel measures every child with the exact cell block it computed, so the
        // requested size simply is that block — reporting anything else would make the panel's
        // packing disagree with what is actually drawn.
        base.MeasureOverride(availableSize);

        var width = double.IsFinite(availableSize.Width) ? availableSize.Width : 0;
        var height = double.IsFinite(availableSize.Height) ? availableSize.Height : 0;
        return new Size(width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        ApplyCardSize(finalSize.Width, finalSize.Height);
        return base.ArrangeOverride(finalSize);
    }

    /// <summary>Size the card, content presenter, radius and content scale for a card box.</summary>
    private void ApplyCardSize(double width, double height)
    {
        width = Math.Max(1, width);
        height = Math.Max(1, height);

        // Recorded before the radius is resolved: the adaptive radius heuristic is a function of
        // the card's real size.
        cardWidthDip = width;
        cardHeightDip = height;

        var frameless = content is IFramelessWidget;

        card.CardBorderControl.Width = width;
        card.CardBorderControl.Height = height;

        var r = Radius.TopLeft;
        card.CardBorderControl.CornerRadius = new CornerRadius(r);

        var outline = WidgetOutlineThickness;
        var innerW = frameless ? width : Math.Max(1, width - outline.Left - outline.Right);
        var innerH = frameless ? height : Math.Max(1, height - outline.Top - outline.Bottom);

        card.ContentPresenterControl.Width = innerW;
        card.ContentPresenterControl.Height = innerH;
        card.ContentPresenterControl.Clip = frameless
            ? null
            : new RectangleGeometry(new Rect(0, 0, innerW, innerH), Math.Max(0, r - outline.Left), Math.Max(0, r - outline.Left));

        var scale = EffectiveContentScale;
        if (Math.Abs(scale - 1.0) < 0.001)
        {
            card.ContentPresenterControl.RenderTransform = null;
        }
        else
        {
            card.ContentPresenterControl.RenderTransform = new ScaleTransform(scale, scale);
            card.ContentPresenterControl.RenderTransformOrigin = new RelativePoint(0.5, 0.5, RelativeUnit.Relative);
        }

        NotifyAll();
    }

    private void PrepareContent(UserControl control)
    {
        var frameless = control is IFramelessWidget;
        if (frameless) control.Classes.Add("Frameless");
        if ((control is IFixedSizeWidget && control.GetType().Name == "AggregateView") || control.GetType().Name == "Note")
            control.Classes.Add("Flush");

        card.ContentPresenterControl.Content = control;
        AttachStackWidget(control);
        BuildContextMenu();
        NotifyAll();
    }

    // ---------- Card surface (bound by WidgetCard) ----------

    public Theme GlassMaterial => appSettingsProvider.Get().Theme;

    public bool ShowsGlassMaterial => !(content is IFramelessWidget) && GlassMaterial.UsesRenderedGlass;

    public CornerRadius Radius
    {
        get
        {
            // The sidebar's own radius setting when it has one, otherwise the desktop chain for this
            // screen (per-screen radius, then the global one) — resolved by the window so the card,
            // the native region and the shadow all read the same number.
            var baseRadius = owner.WidgetRadiusDip;
            if (GlassMaterial.UseNativeFrame || content is IFramelessWidget) return new CornerRadius(0);

            var (columns, rows) = Span;
            // Before the first arrange the cell size is not known yet; the span alone already picks
            // the right branch, so the preview and the settled card agree.
            var cardW = cardWidthDip > 0 ? cardWidthDip : columns * owner.CellSideDip - owner.GutterDip;
            var cardH = cardHeightDip > 0 ? cardHeightDip : rows * owner.CellSideDip - owner.GutterDip;

            var r = WidgetSurfaceMetrics.ResolveEffectiveRadius(baseRadius, columns, rows, cardW, cardH, 0);
            return new CornerRadius(r / owner.Scaling);
        }
    }

    /// <summary>The sidebar grid places the cards — no grid margin inside the card.</summary>
    public Thickness WidgetMargin => new(0);

    public Thickness StackIndicatorsMargin => new(0, 0, 1, 0);

    public bool IsFreeMode => false;

    public bool ShowResizeHandle => false;

    private bool IsSelfFraming =>
        content.GetType().Name is "Note" or "MapView"
        || Layout.SubType is "Note" or "MapView"
        || Layout.Type is "Notes" or "Map";

    public Thickness WidgetOutlineThickness =>
        WidgetSurfaceMetrics.OutlineThickness(GlassMaterial, content is IFramelessWidget, IsStackWidget, IsSelfFraming);

    public IBrush? WidgetOutlineBrush
    {
        get
        {
            if (IsSelfFraming && GlassMaterial.IsColorful) return null;
            if (GlassMaterial.OutlineWidth <= 0)
                return GlassMaterial.IsColorful && WidgetOutlineThickness.Left > 0
                    ? new SolidColorBrush(Colors.White, 0.55)
                    : null;
            return WidgetSurfaceMetrics.OutlineBrush(GlassMaterial,
                cardWidthDip > 0 ? cardWidthDip : Math.Max(1, Layout.Width),
                cardHeightDip > 0 ? cardHeightDip : Math.Max(1, Layout.Height),
                content is IFramelessWidget, IsStackWidget, IsSelfFraming);
        }
    }

    public IBrush WidgetCardBackground
    {
        get
        {
            if (content is IFramelessWidget || IsStackWidget) return Brushes.Transparent;
            var theme = GlassMaterial;
            var variant = ActualThemeVariant;
            if (theme.IsColorful)
            {
                if (IsSelfFraming) return Brushes.Transparent;
                if (this.TryFindResource("WidgetBackground", variant, out var cb) && cb is IBrush cbrush)
                    return cbrush;
                return variant == Avalonia.Styling.ThemeVariant.Dark
                    ? new SolidColorBrush(Color.Parse("#1C1C1E"))
                    : Brushes.White;
            }
            return this.TryFindResource("WidgetBackground", variant, out var res) && res is IBrush brush
                ? brush
                : Brushes.Transparent;
        }
    }

    public string SizeMenuTitle => $"{Locale.Widget_Size_Grid} · {Span.Columns}×{Span.Rows}";

    public string ScaleMenuTitle => $"{Locale.Widget_Scale} · {EffectiveContentScale:0.##}×";

    // ---------- Content / stack ----------

    private double EffectiveContentScale => Layout.ContentScale ?? 1.0;

    public IFixedSizeWidget? FixedSizeWidget => content as IFixedSizeWidget;
    public bool IsFixedWidget => FixedSizeWidget != null;
    public bool IsFixed2x1Widget => WidgetSizePresets.IsAggregate(FixedSizeWidget);
    public bool IsFixedSquareWidget => WidgetSizePresets.IsSquare(FixedSizeWidget);
    public bool IsMapWidget => content.GetType().Name == "MapView";

    public bool IsStackWidget =>
        StackWidget != null || Layout.Type is "Stack" or "StackWidgets" || Layout.SubType is "WidgetStackView" or "WidgetStack";

    public bool ShowEditButton => supportsEdit;
    public string Edit => $"{Locale.Widget_Edit} \"{Layout.Type}\"";
    public bool ShowNormalEditButton => !IsStackWidget && ShowEditButton;
    public bool ShowStackEditButton => IsStackWidget && ShowEditButton;
    public bool CanEditStackedWidgetChild => StackWidget?.CanEditCurrentChild == true;
    public string EditStackChildTitle => $"{Locale.Widget_Edit} \"{StackWidget?.CurrentChildTitle ?? ""}\"";

    public IStackWidget? StackWidget => activeStackWidget ?? (card.ContentPresenterControl.Content as IStackWidget);

    public bool HasStackIndicators => StackWidget != null && (StackWidget.IndicatorItems?.Count ?? 0) > 1;

    public IReadOnlyList<StackWidgetIndicatorItem>? StackIndicators => StackWidget?.IndicatorItems;

    private void AttachStackWidget(object? control)
    {
        if (activeStackWidget != null)
        {
            activeStackWidget.IndicatorItemsChanged -= OnStackIndicatorsChanged;
            activeStackWidget = null;
        }

        if (control is IStackWidget stack)
        {
            activeStackWidget = stack;
            activeStackWidget.IndicatorItemsChanged += OnStackIndicatorsChanged;
        }
    }

    private void OnStackIndicatorsChanged(object? sender, EventArgs e)
    {
        Notify(nameof(HasStackIndicators));
        Notify(nameof(StackIndicators));
        Notify(nameof(CanEditStackedWidgetChild));
        Notify(nameof(EditStackChildTitle));
    }

    /// <inheritdoc />
    public void OnStackIndicatorClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: int index } && StackWidget != null)
            StackWidget.SwitchToIndex(index);
    }

    /// <inheritdoc />
    public void OnStackIndicatorsWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if (StackWidget is not { } stack || StackIndicators is not { Count: > 1 } list) return;
        if (stack.IsTransitionActive) { e.Handled = true; return; }

        var currentIndex = list.FirstOrDefault(i => i.IsActive)?.Index ?? 0;
        if (e.Delta.Y > 0) { stack.SwitchToIndex((currentIndex - 1 + list.Count) % list.Count); e.Handled = true; }
        else if (e.Delta.Y < 0) { stack.SwitchToIndex((currentIndex + 1) % list.Count); e.Handled = true; }
    }

    /// <inheritdoc />
    public void OnResizeHandlePressed(object? sender, PointerPressedEventArgs e)
    {
        // The sidebar sizes widgets from the grid (span × cell), so the corner handle is hidden;
        // this is a no-op kept for the shared card contract.
    }

    // ---------- Span (the sidebar's "size") ----------

    public decimal? SizeColumnsValue
    {
        get => Span.Columns;
        set
        {
            if (value is not { } target) return;
            var (columns, rows) = Span;
            var next = Math.Clamp((int)Math.Round(target), 1, Math.Max(1, owner.GridColumns));
            if (next == columns) return;
            ApplySpan(next, rows);
        }
    }

    public decimal? SizeRowsValue
    {
        get => Span.Rows;
        set
        {
            if (value is not { } target) return;
            var (columns, rows) = Span;
            var next = Math.Clamp((int)Math.Round(target), 1, MaxSpanRows);
            if (next == rows) return;
            ApplySpan(columns, next);
        }
    }

    public void SetFixedSpanPreset(string spanTag)
    {
        if (WidgetSizePresets.ParseTag(spanTag) is { } span)
            ApplySpan(span.Columns, span.Rows);
    }

    /// <summary>
    /// Store a new span for this card. The request is fitted to the grid and to the widget's own
    /// legal spans first, and the stored value is what comes back — so the menu, the persistence and
    /// the drawn card can never disagree.
    /// </summary>
    public void ApplySpan(int columns, int rows)
    {
        var fitted = SidebarRules.FitSpan((columns, rows), SnapOptions, Math.Max(1, owner.GridColumns));
        if (fitted == Span) return;

        // Persist through the provider, which refuses when the entry is gone (profile switch /
        // removal) — the host must not resurrect an entry the layout no longer owns.
        if (!provider.SaveSpan(fitted.Columns, fitted.Rows)) return;

        RefreshLayout();
    }

    /// <summary>Open the "自定义跨度" dialog (the grid's equivalent of a custom pixel size).</summary>
    public void OpenSpanDialog() =>
        WithAutoCloseSuppressed(() =>
        {
            var (columns, rows) = Span;
            return new CustomSizeDialog(columns, rows, (c, r) => ApplySpan(c, r),
                title: "自定义跨度 (网格格数)",
                firstLabel: "列",
                secondLabel: "行",
                minFirst: 1,
                maxFirst: Math.Max(1, owner.GridColumns),
                minSecond: 1,
                maxSecond: MaxSpanRows,
                resetFirst: Math.Min(2, Math.Max(1, owner.GridColumns)),
                resetSecond: 2);
        });

    public void OpenScaleDialog() =>
        WithAutoCloseSuppressed(() =>
        {
            var dialog = new ScaleDialog(EffectiveContentScale, value =>
            {
                if (Layout.ContentScale == value) return;
                SaveLayout(Layout with { ContentScale = value });
            });
            return dialog;
        });

    private void SaveLayout(WidgetLayout layout) => provider.Save(layout);

    // ---------- Context menu ----------

    private void BuildContextMenu()
    {
        var menu = new ContextMenu { Classes = { "WidgetContextMenu" } };

        menu.Items.Add(Menu(Locale.Sidebar_RemoveFromBar, () => callbacks.RequestRemove(provider.ScreenId, InstanceId)));

        if (ShowNormalEditButton)
            menu.Items.Add(Menu(Edit, EditWidget));
        else if (ShowStackEditButton)
            menu.Items.Add(Menu("编辑重叠组件…", EditWidget));

        menu.Items.Add(BuildSizeMenu());
        menu.Items.Add(Menu(ScaleMenuTitle, OpenScaleDialog));
        menu.Items.Add(BuildProfileMenu());

        menu.Items.Add(new Separator());
        menu.Items.Add(Menu(Locale.Settings, () => settingsWindow().ShowAndActivate()));

        menu.Opened += (_, _) => owner.AutoCloseSuppressed = true;
        menu.Closed += (_, _) => owner.AutoCloseSuppressed = false;
        ContextMenu = menu;
    }

    private static readonly StreamGeometry CheckmarkGeometry =
        StreamGeometry.Parse("M9 16.17L4.83 12l-1.42 1.41L9 19 21 7l-1.41-1.41z");

    /// <summary>In-sidebar profile switcher (same contents as the desktop widget menu).</summary>
    private MenuItem BuildProfileMenu()
    {
        var root = new MenuItem { Header = Locale.Profiles_MenuTitle };
        if (profileService == null) return root;

        var profiles = profileService.GetProfiles();
        var active = profileService.GetActiveProfile();

        foreach (var name in profiles)
        {
            var isActive = string.Equals(name, active, StringComparison.OrdinalIgnoreCase);
            var item = new MenuItem
            {
                Header = name,
                Icon = isActive ? new PathIcon { Data = CheckmarkGeometry, Width = 12, Height = 12 } : null
            };
            var target = name;
            item.Click += (_, _) => profileService.SwitchProfile(target);
            root.Items.Add(item);
        }

        root.Items.Add(new Separator());

        var manage = new MenuItem { Header = Locale.Profiles_Manage ?? "Manage profiles\u2026" };
        manage.Click += (_, _) =>
        {
            var window = settingsWindow();
            window.Show();
            window.Activate();
        };
        root.Items.Add(manage);
        return root;
    }

    private MenuItem BuildSizeMenu()
    {
        var root = new MenuItem { Header = SizeMenuTitle };
        root.Items.Add(Menu("自定义跨度…", OpenSpanDialog));

        var presets = WidgetSizePresets.For(FixedSizeWidget, IsMapWidget);
        if (presets.Count > 0)
        {
            root.Items.Add(new Separator());
            foreach (var preset in presets)
                root.Items.Add(Menu(preset.Label, () => SetFixedSpanPreset(preset.Tag)));
        }

        root.Items.Add(new Separator());
        root.Items.Add(Stepper(Locale.Widget_Size_Columns, nameof(SizeColumnsValue), Math.Max(1, owner.GridColumns)));
        root.Items.Add(Stepper(Locale.Widget_Size_Rows, nameof(SizeRowsValue), MaxSpanRows));
        return root;
    }

    private MenuItem Stepper(string label, string property, int maximum)
    {
        var input = new NumericUpDown
        {
            Width = 110,
            Minimum = 1,
            Maximum = maximum,
            Increment = 1,
            FormatString = "F0",
            ParsingNumberStyle = System.Globalization.NumberStyles.Integer
        };
        input.Bind(NumericUpDown.ValueProperty, new Avalonia.Data.Binding(property) { Source = this });

        var grid = new Avalonia.Controls.Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        var text = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center };
        Avalonia.Controls.Grid.SetColumn(input, 1);
        grid.Children.Add(text);
        grid.Children.Add(input);

        return new MenuItem { Header = grid, StaysOpenOnClick = true };
    }

    private static MenuItem Menu(string header, Action action)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => action();
        return item;
    }

    private void EditWidget()
    {
        var factory = runtime.EditFactory(Layout, provider);
        var edit = factory?.Invoke();
        if (edit == null) return;

        owner.AutoCloseSuppressed = true;
        edit.Closed += (_, _) => owner.AutoCloseSuppressed = false;
        edit.Show();
    }

    /// <summary>Show a dialog while the sidebar's focus-loss auto-close stays suppressed.</summary>
    private void WithAutoCloseSuppressed(Func<Window> create)
    {
        owner.AutoCloseSuppressed = true;
        Window dialog;
        try
        {
            dialog = create();
        }
        catch
        {
            owner.AutoCloseSuppressed = false;
            throw;
        }

        dialog.Closed += (_, _) => owner.AutoCloseSuppressed = false;
        _ = dialog.ShowDialog(owner);
    }

    // ---------- Change reactions ----------

    private void OnWidgetLayoutUpdated(object? sender, WidgetLayout? oldLayout, WidgetLayout newLayout)
    {
        if (!Equals(oldLayout?.Settings, newLayout.Settings))
        {
            if (card.ContentPresenterControl.Content is IWidgetSelfRefreshing selfRefreshing)
            {
                selfRefreshing.Refresh(newLayout);
            }
            else
            {
                var next = runtime.CreateContent(newLayout, provider);
                PrepareContent(next);
            }
        }

        // Content scale (and any other layout-visible setting) changed: re-apply the card box at
        // the size it already occupies. The span is untouched here — it is the grid's business.
        if (cardWidthDip > 0)
            ApplyCardSize(cardWidthDip, cardHeightDip);
        else
            RefreshLayout();
    }

    private void OnAppSettingsUpdated(object? sender, AppSettings? oldData, AppSettings newData)
    {
        if (oldData?.Theme != newData.Theme || oldData?.Dimensions != newData.Dimensions)
        {
            BuildContextMenu();
            if (cardWidthDip > 0) ApplyCardSize(cardWidthDip, cardHeightDip);
            else RefreshLayout();
        }
    }

    /// <summary>Release what the widget content opts to release (fullscreen suspension).</summary>
    public void SuspendContent()
    {
        if (suspended) return;
        suspended = true;
        (card.ContentPresenterControl.Content as IWidgetSuspendable)?.Suspend();
    }

    /// <summary>Rebuild whatever <see cref="SuspendContent"/> released.</summary>
    public void ResumeContent()
    {
        if (!suspended) return;
        suspended = false;
        (card.ContentPresenterControl.Content as IWidgetSuspendable)?.Resume();
    }

    /// <summary>Detach every subscription (the host is being removed).</summary>
    public void Detach()
    {
        provider.DataChanged -= OnWidgetLayoutUpdated;
        appSettingsProvider.DataChanged -= OnAppSettingsUpdated;
        AttachStackWidget(null);
    }
}

/// <summary>The callbacks a sidebar widget host needs from its owning service.</summary>
public interface ISidebarHostCallbacks
{
    /// <summary>Remove the widget with this instance id from the sidebar on the given screen.</summary>
    void RequestRemove(string screenId, string instanceId);
}
