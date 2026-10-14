using System;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Styling;
using DeskCanvas.Core.Models.Settings;
using DeskCanvas.Locales;
using DeskCanvas.Services;

namespace DeskCanvas.Views.Controls;

/// <summary>
/// The shared chrome of a secondary panel: backdrop, card, glass surface, header (title + drag
/// area + business actions + the shared close button), the single business content presenter and
/// an optional footer.
/// <list type="bullet">
/// <item>The nine popups used to declare this whole structure in their own AXAML — a Border, a
/// <see cref="LiquidGlassSurface"/>, a title row and a close button each — and to recompute the
/// material in their own <c>ApplyTheme</c>. Both now live here and in
/// <see cref="PanelAppearanceResolver"/>.</item>
/// <item>The shell never decides the material: the host resolves a <see cref="PanelAppearance"/>
/// and hands it over through <see cref="SurfaceAppearance"/>. Plugin-specific colours are applied
/// by the plugin afterwards and are never overwritten here.</item>
/// <item>The card starts at <c>Opacity = 0</c> so a late template application cannot flash an
/// unstyled card; the panel window reveals it with the shared open transition.</item>
/// <item>Closing is a request, never an action: <see cref="CloseRequested"/> bubbles to the
/// <c>SecondaryPanelWindow</c>, which runs the single shared exit animation. The shell never
/// casts its host to <see cref="Window"/>.</item>
/// </list>
/// </summary>
public class SecondaryPanelShell : ContentControl
{
    /// <summary>Card fill, written by the appearance resolver (see <see cref="SurfaceAppearance"/>).</summary>
    public static readonly StyledProperty<IBrush?> CardBackgroundProperty =
        AvaloniaProperty.Register<SecondaryPanelShell, IBrush?>(nameof(CardBackground));

    /// <summary>Card hairline, written by the appearance resolver.</summary>
    public static readonly StyledProperty<IBrush?> CardBorderBrushProperty =
        AvaloniaProperty.Register<SecondaryPanelShell, IBrush?>(nameof(CardBorderBrush));

    /// <summary>The optics handed to <c>PART_GlassSurface</c>; null while glass is off.</summary>
    public static readonly StyledProperty<Theme?> GlassMaterialProperty =
        AvaloniaProperty.Register<SecondaryPanelShell, Theme?>(nameof(GlassMaterial));

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

    // CornerRadius comes from TemplatedControl (the shared "host surface" radius contract); the
    // shell only supplies its default through the ControlTheme.

    /// <summary>The host's material result. Setting it decomposes into the paintable properties.</summary>
    public static readonly StyledProperty<PanelAppearance?> SurfaceAppearanceProperty =
        AvaloniaProperty.Register<SecondaryPanelShell, PanelAppearance?>(nameof(SurfaceAppearance));

    /// <summary>Bubbles to the panel window: run the shared exit animation and close.</summary>
    public static readonly RoutedEvent<RoutedEventArgs> CloseRequestedEvent =
        RoutedEvent.Register<SecondaryPanelShell, RoutedEventArgs>(nameof(CloseRequested), RoutingStrategies.Bubble);

    static SecondaryPanelShell()
    {
        SurfaceAppearanceProperty.Changed.AddClassHandler<SecondaryPanelShell>(OnSurfaceAppearanceChanged);
    }

    public IBrush? CardBackground { get => GetValue(CardBackgroundProperty); set => SetValue(CardBackgroundProperty, value); }
    public IBrush? CardBorderBrush { get => GetValue(CardBorderBrushProperty); set => SetValue(CardBorderBrushProperty, value); }
    public Theme? GlassMaterial { get => GetValue(GlassMaterialProperty); set => SetValue(GlassMaterialProperty, value); }
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
    public PanelAppearance? SurfaceAppearance { get => GetValue(SurfaceAppearanceProperty); set => SetValue(SurfaceAppearanceProperty, value); }

    public event EventHandler<RoutedEventArgs>? CloseRequested
    {
        add => AddHandler(CloseRequestedEvent, value);
        remove => RemoveHandler(CloseRequestedEvent, value);
    }

    // ---- template parts -------------------------------------------------------------------
    // Exposed as properties so the panel window (and the foundation checks) reach them without a
    // name-scope lookup: derived windows no longer own CardBorder / LiquidGlassSurfaceControl /
    // CloseButton fields, the shell does.

    /// <summary>The window area outside the card: the only in-window "dismiss" surface.</summary>
    public Panel? Backdrop { get; private set; }

    /// <summary>The single animation and clipping boundary.</summary>
    public Border? CardBorder { get; private set; }

    /// <summary>The single shell glass surface (business content may host its own).</summary>
    public LiquidGlassSurface? GlassSurface { get; private set; }

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

    private static void OnSurfaceAppearanceChanged(SecondaryPanelShell shell, AvaloniaPropertyChangedEventArgs args)
    {
        if (args.NewValue is not PanelAppearance appearance) return;

        // Decomposition only — business brushes the plugin set on its OWN controls are untouched,
        // and nothing here writes back into SurfaceAppearance (no notification loop).
        shell.CardBackground = appearance.CardBackground;
        shell.CardBorderBrush = appearance.CardBorderBrush;
        shell.GlassMaterial = appearance.GlassMaterial;
        shell.UseRenderedGlass = appearance.UseRenderedGlass;
        shell.TitleForeground = appearance.TitleBrush;
        shell.SubtitleForeground = appearance.SubtitleBrush;
        shell.IconForeground = appearance.IconBrush;
        shell.CornerRadius = new CornerRadius(appearance.CornerRadius);
    }

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        DetachParts();
        base.OnApplyTemplate(e);

        Backdrop = e.NameScope.Find<Panel>("PART_Backdrop");
        CardBorder = e.NameScope.Find<Border>("PART_CardBorder");
        GlassSurface = e.NameScope.Find<LiquidGlassSurface>("PART_GlassSurface");
        HeaderDragArea = e.NameScope.Find<Control>("PART_HeaderDragArea");
        CloseButton = e.NameScope.Find<Button>("PART_CloseButton");
        ContentHost = e.NameScope.Find<ContentPresenter>("PART_ContentPresenter");

        // The shared drag contract (W2): only an unhandled left press inside the header moves the
        // window, and buttons/text inputs/selectors/sliders inside it are excluded automatically.
        if (HeaderDragArea != null)
            PanelDragBehavior.SetIsDragHandle(HeaderDragArea, true);

        if (CloseButton != null)
        {
            // Localized at template-application time so a culture switch after the ControlTheme
            // was parsed still names the button correctly (x:Static would freeze the first value).
            AutomationProperties.SetName(CloseButton, Locale.SecondaryPanel_Close);
            ToolTip.SetTip(CloseButton, Locale.SecondaryPanel_Close);
            CloseButton.Click += OnCloseButtonClick;
        }

        if (Backdrop != null)
            Backdrop.PointerPressed += OnBackdropPointerPressed;

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
        GlassSurface = null;
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
}
