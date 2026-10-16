using System;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;

namespace DeskCanvas.UI.Controls;

/// <summary>
/// The shared chrome of a secondary panel: backdrop, card, glass surface slot, header (title + drag
/// area + business actions + the shared close button), the single business content presenter and
/// an optional footer.
/// <list type="bullet">
/// <item>The popups used to declare this whole structure in their own AXAML — a Border, a glass
/// surface, a title row and a close button each — and to recompute the material in their own
/// <c>ApplyTheme</c>. Both now live here and in the host's appearance resolver.</item>
/// <item>The shell never decides the material: the host resolves an appearance and hands it over
/// through the host-typed <c>SurfaceAppearance</c> property of the
/// <c>DeskCanvas.Views.Controls.SecondaryPanelShell</c> facade (the appearance and theme models
/// are host types this library must not know). Plugin-specific colours are applied by the plugin
/// afterwards and are never overwritten here.</item>
/// <item>The glass surface is a <b>slot</b>, not a template part this library instantiates: the
/// host facade provides the platform surface through <see cref="GlassSurface"/> (typed to the
/// <see cref="GlassSurfaceBase"/> port — one production implementation, the host's
/// <c>LiquidGlassSurface</c>). The shell mirrors <see cref="UseRenderedGlass"/> and the card's
/// corner radius onto the surface, so the surface follows the card across template
/// re-applications without the shell knowing the optics.</item>
/// <item>The card starts at <c>Opacity = 0</c> so a late template application cannot flash an
/// unstyled card; the panel window reveals it with the shared open transition.</item>
/// <item>Closing is a request, never an action: <see cref="CloseRequested"/> bubbles to the
/// panel window, which runs the single shared exit animation. The shell never casts its host to
/// <see cref="Window"/>.</item>
/// </list>
/// </summary>
public class SecondaryPanelShell : ContentControl
{
    /// <summary>Card fill, written by the host appearance resolver.</summary>
    public static readonly StyledProperty<IBrush?> CardBackgroundProperty =
        AvaloniaProperty.Register<SecondaryPanelShell, IBrush?>(nameof(CardBackground));

    /// <summary>Card hairline, written by the host appearance resolver.</summary>
    public static readonly StyledProperty<IBrush?> CardBorderBrushProperty =
        AvaloniaProperty.Register<SecondaryPanelShell, IBrush?>(nameof(CardBorderBrush));

    /// <summary>Whether the rendered glass paints the card (液态玻璃 / 新液态玻璃 only).</summary>
    public static readonly StyledProperty<bool> UseRenderedGlassProperty =
        AvaloniaProperty.Register<SecondaryPanelShell, bool>(nameof(UseRenderedGlass));

    /// <summary>Header title foreground (semantic, resolved per variant).</summary>
    public static readonly StyledProperty<IBrush?> TitleForegroundProperty =
        AvaloniaProperty.Register<SecondaryPanelShell, IBrush?>(nameof(TitleForeground));

    /// <summary>Header subtitle / secondary text foreground.</summary>
    public static readonly StyledProperty<IBrush?> SubtitleForegroundProperty =
        AvaloniaProperty.Register<SecondaryPanelShell, IBrush?>(nameof(SubtitleForeground));

    /// <summary>Icon-only chrome foreground (the shared close button).</summary>
    public static readonly StyledProperty<IBrush?> IconForegroundProperty =
        AvaloniaProperty.Register<SecondaryPanelShell, IBrush?>(nameof(IconForeground));

    public static readonly StyledProperty<string?> TitleProperty =
        AvaloniaProperty.Register<SecondaryPanelShell, string?>(nameof(Title));

    public static readonly StyledProperty<string?> SubtitleProperty =
        AvaloniaProperty.Register<SecondaryPanelShell, string?>(nameof(Subtitle));

    /// <summary>Replaces the default title block when a panel builds its header dynamically.</summary>
    public static readonly StyledProperty<object?> HeaderContentProperty =
        AvaloniaProperty.Register<SecondaryPanelShell, object?>(nameof(HeaderContent));

    /// <summary>Business header buttons (favourite, refresh, category…), placed next to the close button.</summary>
    public static readonly StyledProperty<object?> HeaderActionsProperty =
        AvaloniaProperty.Register<SecondaryPanelShell, object?>(nameof(HeaderActions));

    /// <summary>Optional footer / status line.</summary>
    public static readonly StyledProperty<object?> FooterContentProperty =
        AvaloniaProperty.Register<SecondaryPanelShell, object?>(nameof(FooterContent));

    /// <summary>Shared content padding; a complex page may override it explicitly.</summary>
    public static readonly StyledProperty<Thickness> ContentPaddingProperty =
        AvaloniaProperty.Register<SecondaryPanelShell, Thickness>(nameof(ContentPadding));

    /// <summary>
    /// The glass surface slot: the host facade provides the platform surface (the single
    /// production implementation of the <see cref="GlassSurfaceBase"/> port). Null is legal —
    /// the shell renders its chrome with an unpainted card, which is how the library stays
    /// standalone.
    /// </summary>
    public static readonly StyledProperty<GlassSurfaceBase?> GlassSurfaceProperty =
        AvaloniaProperty.Register<SecondaryPanelShell, GlassSurfaceBase?>(nameof(GlassSurface));

    /// <summary>
    /// Localized accessible name / tooltip for the shared close button. Embedding apps inject
    /// their locale here; the neutral default is English (the shared template must not carry
    /// host locale literals). The host facade refreshes it right before every template
    /// application, so a culture switch after start-up still names the button correctly.
    /// </summary>
    public static readonly StyledProperty<string?> CloseButtonLabelProperty =
        AvaloniaProperty.Register<SecondaryPanelShell, string?>(nameof(CloseButtonLabel));

    /// <summary>Bubbles to the panel window: run the shared exit animation and close.</summary>
    public static readonly RoutedEvent<RoutedEventArgs> CloseRequestedEvent =
        RoutedEvent.Register<SecondaryPanelShell, RoutedEventArgs>(nameof(CloseRequested), RoutingStrategies.Bubble);

    static SecondaryPanelShell()
    {
        UseRenderedGlassProperty.Changed.AddClassHandler<SecondaryPanelShell>(OnUseRenderedGlassChanged);
        GlassSurfaceProperty.Changed.AddClassHandler<SecondaryPanelShell>(OnGlassSurfaceChanged);
    }

    public IBrush? CardBackground { get => GetValue(CardBackgroundProperty); set => SetValue(CardBackgroundProperty, value); }
    public IBrush? CardBorderBrush { get => GetValue(CardBorderBrushProperty); set => SetValue(CardBorderBrushProperty, value); }
    public bool UseRenderedGlass { get => GetValue(UseRenderedGlassProperty); set => SetValue(UseRenderedGlassProperty, value); }
    public IBrush? TitleForeground { get => GetValue(TitleForegroundProperty); set => SetValue(TitleForegroundProperty, value); }
    public IBrush? SubtitleForeground { get => GetValue(SubtitleForegroundProperty); set => SetValue(SubtitleForegroundProperty, value); }
    public IBrush? IconForeground { get => GetValue(IconForegroundProperty); set => SetValue(IconForegroundProperty, value); }

    public string? Title { get => GetValue(TitleProperty); set => SetValue(TitleProperty, value); }
    public string? Subtitle { get => GetValue(SubtitleProperty); set => SetValue(SubtitleProperty, value); }
    public object? HeaderContent { get => GetValue(HeaderContentProperty); set => SetValue(HeaderContentProperty, value); }
    public object? HeaderActions { get => GetValue(HeaderActionsProperty); set => SetValue(HeaderActionsProperty, value); }
    public object? FooterContent { get => GetValue(FooterContentProperty); set => SetValue(FooterContentProperty, value); }
    public Thickness ContentPadding { get => GetValue(ContentPaddingProperty); set => SetValue(ContentPaddingProperty, value); }
    public GlassSurfaceBase? GlassSurface { get => GetValue(GlassSurfaceProperty); set => SetValue(GlassSurfaceProperty, value); }
    public string? CloseButtonLabel { get => GetValue(CloseButtonLabelProperty); set => SetValue(CloseButtonLabelProperty, value); }

    public event EventHandler<RoutedEventArgs>? CloseRequested
    {
        add => AddHandler(CloseRequestedEvent, value);
        remove => RemoveHandler(CloseRequestedEvent, value);
    }

    // ---- template parts -------------------------------------------------------------------
    // Exposed as properties so the panel window (and the foundation checks) reach them without a
    // name-scope lookup: derived windows no longer own CardBorder / CloseButton fields, the shell
    // does. (The glass surface is a settable slot property — see GlassSurface.)

    /// <summary>The window area outside the card: the only in-window "dismiss" surface.</summary>
    public Panel? Backdrop { get; private set; }

    /// <summary>The single animation and clipping boundary.</summary>
    public Border? CardBorder { get; private set; }

    /// <summary>The hit-testable header strip that moves the window.</summary>
    public Control? HeaderDragArea { get; private set; }

    /// <summary>The shared close button.</summary>
    public Button? CloseButton { get; private set; }

    /// <summary>The single business content presenter.</summary>
    public ContentPresenter? ContentHost { get; private set; }

    /// <summary>True once <see cref="OnApplyTemplate"/> has run (parts are reachable).</summary>
    public bool IsTemplateApplied { get; private set; }

    /// <summary>
    /// Raised after every template application. The panel window reconnects its card animation
    /// here, which is why the connection must be idempotent. (Deliberately not named
    /// <c>TemplateApplied</c>: that would hide the base control's event.)
    /// </summary>
    public event EventHandler? TemplateConnected;

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        DetachParts();
        base.OnApplyTemplate(e);

        Backdrop = e.NameScope.Find<Panel>("PART_Backdrop");
        CardBorder = e.NameScope.Find<Border>("PART_CardBorder");
        HeaderDragArea = e.NameScope.Find<Control>("PART_HeaderDragArea");
        CloseButton = e.NameScope.Find<Button>("PART_CloseButton");
        ContentHost = e.NameScope.Find<ContentPresenter>("PART_ContentPresenter");

        // The shared drag contract (W2): only an unhandled left press inside the header moves the
        // window, and buttons/text inputs/selectors/sliders inside it are excluded automatically.
        if (HeaderDragArea != null)
            PanelDragBehavior.SetIsDragHandle(HeaderDragArea, true);

        if (CloseButton != null)
        {
            // Read at template-application time so a culture switch after start-up still names
            // the button correctly; the embedding app refreshes the label property (the host
            // facade does it right before calling base.OnApplyTemplate).
            var label = CloseButtonLabel ?? "Close";
            AutomationProperties.SetName(CloseButton, label);
            ToolTip.SetTip(CloseButton, label);
            CloseButton.Click += OnCloseButtonClick;
        }

        if (Backdrop != null)
            Backdrop.PointerPressed += OnBackdropPointerPressed;

        // A late-assigned or re-applied surface must adopt the card's current state.
        SyncGlassSurfaceToCard();

        IsTemplateApplied = true;
        TemplateConnected?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Release the parts of the template being replaced (a re-application must not stack handlers).</summary>
    private void DetachParts()
    {
        IsTemplateApplied = false;

        if (CloseButton != null)
        {
            CloseButton.Click -= OnCloseButtonClick;
            CloseButton = null;
        }

        if (Backdrop != null)
        {
            Backdrop.PointerPressed -= OnBackdropPointerPressed;
            Backdrop = null;
        }

        if (HeaderDragArea != null)
        {
            PanelDragBehavior.SetIsDragHandle(HeaderDragArea, false);
            HeaderDragArea = null;
        }

        CardBorder = null;
        ContentHost = null;
    }

    private void OnCloseButtonClick(object? sender, RoutedEventArgs e) => RaiseCloseRequested();

    /// <summary>
    /// A press on the window area outside the card dismisses the panel. Narrowed to a press whose
    /// source really is <c>PART_Backdrop</c>: the legacy <c>e.Source is Panel or Grid</c> tests
    /// treated content blank space as a dismiss area too.
    /// </summary>
    private void OnBackdropPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!ReferenceEquals(e.Source, Backdrop)) return;
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;

        RaiseCloseRequested();
        e.Handled = true;
    }

    private void RaiseCloseRequested() => RaiseEvent(new RoutedEventArgs(CloseRequestedEvent));

    // ---- glass surface mirroring ----------------------------------------------------------

    private static void OnUseRenderedGlassChanged(SecondaryPanelShell shell, AvaloniaPropertyChangedEventArgs args)
    {
        if (args.NewValue is bool use) shell.SyncGlassSurfaceToCard(use);
    }

    private static void OnGlassSurfaceChanged(SecondaryPanelShell shell, AvaloniaPropertyChangedEventArgs args)
        => shell.SyncGlassSurfaceToCard();

    private void SyncGlassSurfaceToCard() => SyncGlassSurfaceToCard(UseRenderedGlass);

    private void SyncGlassSurfaceToCard(bool useRenderedGlass)
    {
        if (GlassSurface is not { } surface) return;
        surface.IsVisible = useRenderedGlass;
        surface.CornerRadius = CornerRadius;
    }

    /// <summary>Keep the port surface's radius in phase with the card (spawn radius, resolved appearance).</summary>
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.Property == TemplatedControl.CornerRadiusProperty && GlassSurface is { } surface)
            surface.CornerRadius = CornerRadius;
    }
}
