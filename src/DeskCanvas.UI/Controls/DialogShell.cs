using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Media;

namespace DeskCanvas.UI.Controls;

/// <summary>
/// The shared chrome of a <b>dialog</b> window: the card every dialog is drawn on, the header strip
/// that moves the window, the single business content slot and an optional footer.
/// <para>
/// Why it exists: the host's eleven <c>WindowRole.Dialog</c> windows each declared their own card
/// Border — same fill, same hairline, same radius, same padding, copied five times — and two of them
/// (缩放比例 / 壁纸对齐) also declared a native acrylic layer under a card with an 8 DIP margin. That
/// margin is what the user sees as "外围多一圈毛玻璃": the window itself is blurred, the card sits
/// 8 DIP inside it, and the ring between them is the blur showing through. The two dialogs also
/// failed to move: their drag handler sat inside a window that had handed its caption area to the
/// OS (<c>ExtendClientAreaToDecorationsHint</c>), so the press never reached it.
/// </para>
/// <para>
/// This shell is the single dialog chrome. It is deliberately <b>material-agnostic</b>, exactly like
/// <see cref="SecondaryPanelShell"/>: the host resolves the fill, the hairline and the foregrounds
/// per theme variant and assigns them here (see <c>src/DeskCanvas/Styles/Controls/DialogShell.axaml</c>).
/// The card owns a transparent shadow gutter instead of a second surface, so a dialog is one card
/// floating over the desktop — never a card inside a blurred window.
/// </para>
/// <list type="bullet">
/// <item><b>Moving.</b> <c>PART_HeaderDragArea</c> carries the shared
/// <see cref="PanelDragBehavior"/> drag handle, so a left press on the title strip moves the window
/// while buttons, text boxes, selectors, sliders and marked-interactive subtrees keep their input.
/// The host window must NOT extend its client area into a caption for this to work — the OS would
/// swallow the press first.</item>
/// <item><b>Content.</b> Title / Subtitle / <see cref="HeaderContent"/> (a fully custom header) /
/// <see cref="HeaderActions"/> (buttons on the header's right) / <see cref="Content"/> /
/// <see cref="FooterContent"/>. Everything inside is business; nothing here knows about it.</item>
/// <item><b>Closing.</b> The shell never closes anything: the host window owns Escape and its own
/// action buttons.</item>
/// </list>
/// </summary>
public class DialogShell : ContentControl
{
    /// <summary>Card fill, written by the host's dialog style (the menu surface family).</summary>
    public static readonly StyledProperty<IBrush?> CardBackgroundProperty =
        AvaloniaProperty.Register<DialogShell, IBrush?>(nameof(CardBackground));

    /// <summary>Card hairline, written by the host's dialog style.</summary>
    public static readonly StyledProperty<IBrush?> CardBorderBrushProperty =
        AvaloniaProperty.Register<DialogShell, IBrush?>(nameof(CardBorderBrush));

    /// <summary>Header title foreground (semantic, resolved per variant).</summary>
    public static readonly StyledProperty<IBrush?> TitleForegroundProperty =
        AvaloniaProperty.Register<DialogShell, IBrush?>(nameof(TitleForeground));

    /// <summary>Header subtitle / secondary text foreground.</summary>
    public static readonly StyledProperty<IBrush?> SubtitleForegroundProperty =
        AvaloniaProperty.Register<DialogShell, IBrush?>(nameof(SubtitleForeground));

    public static readonly StyledProperty<string?> TitleProperty =
        AvaloniaProperty.Register<DialogShell, string?>(nameof(Title));

    public static readonly StyledProperty<string?> SubtitleProperty =
        AvaloniaProperty.Register<DialogShell, string?>(nameof(Subtitle));

    /// <summary>Replaces the default title block when a dialog builds its header itself.</summary>
    public static readonly StyledProperty<object?> HeaderContentProperty =
        AvaloniaProperty.Register<DialogShell, object?>(nameof(HeaderContent));

    /// <summary>Header buttons (确定 / 取消 / 保存…), placed at the header's right end.</summary>
    public static readonly StyledProperty<object?> HeaderActionsProperty =
        AvaloniaProperty.Register<DialogShell, object?>(nameof(HeaderActions));

    /// <summary>Optional footer / status line, below the content.</summary>
    public static readonly StyledProperty<object?> FooterContentProperty =
        AvaloniaProperty.Register<DialogShell, object?>(nameof(FooterContent));

    /// <summary>The card's inner padding. Complex pages may raise or lower it explicitly.</summary>
    public static readonly StyledProperty<Thickness> ContentPaddingProperty =
        AvaloniaProperty.Register<DialogShell, Thickness>(nameof(ContentPadding));

    /// <summary>
    /// Transparent room around the card, reserved for its drop shadow. It must stay transparent —
    /// this is the whole difference between the shared shell and the old "card inside an acrylic
    /// window" (the 8 DIP blurred ring).
    /// </summary>
    public static readonly StyledProperty<Thickness> ShadowGutterProperty =
        AvaloniaProperty.Register<DialogShell, Thickness>(nameof(ShadowGutter));

    /// <summary>Card shadow; the default lives in the shared template.</summary>
    public static readonly StyledProperty<BoxShadows> CardShadowProperty =
        AvaloniaProperty.Register<DialogShell, BoxShadows>(nameof(CardShadow));

    /// <summary>
    /// True when the header has anything to show (a title, a subtitle, a custom header or header
    /// actions). A message-only dialog — a confirm/info alert — has none, and then the header row is
    /// not painted at all: an empty strip above the message reads as a layout accident.
    /// <para>
    /// While it is false the <b>card itself</b> becomes the drag handle, so an alert is still
    /// movable by its blank areas (buttons and inputs keep their input through the shared
    /// <see cref="PanelDragBehavior"/> exclusions). It deliberately flips back off when a header
    /// exists: dragging from the content would fight text selection inside it.
    /// </para>
    /// </summary>
    public static readonly DirectProperty<DialogShell, bool> HasHeaderProperty =
        AvaloniaProperty.RegisterDirect<DialogShell, bool>(nameof(HasHeader), shell => shell.hasHeader);

    public IBrush? CardBackground { get => GetValue(CardBackgroundProperty); set => SetValue(CardBackgroundProperty, value); }
    public IBrush? CardBorderBrush { get => GetValue(CardBorderBrushProperty); set => SetValue(CardBorderBrushProperty, value); }
    public IBrush? TitleForeground { get => GetValue(TitleForegroundProperty); set => SetValue(TitleForegroundProperty, value); }
    public IBrush? SubtitleForeground { get => GetValue(SubtitleForegroundProperty); set => SetValue(SubtitleForegroundProperty, value); }
    public string? Title { get => GetValue(TitleProperty); set => SetValue(TitleProperty, value); }
    public string? Subtitle { get => GetValue(SubtitleProperty); set => SetValue(SubtitleProperty, value); }
    public object? HeaderContent { get => GetValue(HeaderContentProperty); set => SetValue(HeaderContentProperty, value); }
    public object? HeaderActions { get => GetValue(HeaderActionsProperty); set => SetValue(HeaderActionsProperty, value); }
    public object? FooterContent { get => GetValue(FooterContentProperty); set => SetValue(FooterContentProperty, value); }
    public Thickness ContentPadding { get => GetValue(ContentPaddingProperty); set => SetValue(ContentPaddingProperty, value); }
    public Thickness ShadowGutter { get => GetValue(ShadowGutterProperty); set => SetValue(ShadowGutterProperty, value); }
    public BoxShadows CardShadow { get => GetValue(CardShadowProperty); set => SetValue(CardShadowProperty, value); }

    // Starts false: a dialog that really has a header sets Title/Subtitle/HeaderContent and
    // UpdateHasHeader turns it on. A message-only alert therefore paints no empty strip.
    private bool hasHeader;

    /// <summary>Whether the header row is painted (see <see cref="HasHeaderProperty"/>).</summary>
    public bool HasHeader => hasHeader;

    // ---- template parts -------------------------------------------------------------------
    // Exposed so the host window (and the foundation checks) can reach them without a name-scope
    // lookup: a dialog window owns no card of its own, the shell does.

    /// <summary>The single clipping boundary and shadow carrier.</summary>
    public Border? CardBorder { get; private set; }

    /// <summary>The hit-testable header strip that moves the window.</summary>
    public Control? HeaderDragArea { get; private set; }

    /// <summary>The single business content presenter.</summary>
    public ContentPresenter? ContentHost { get; private set; }

    /// <summary>True once <see cref="OnApplyTemplate"/> has run (parts are reachable).</summary>
    public bool IsTemplateApplied { get; private set; }

    /// <summary>
    /// Raised after every template application. A window that needs the parts (focus, hotkeys)
    /// listens here instead of assuming the constructor ran after layout.
    /// </summary>
    public event EventHandler? TemplateConnected;

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        DetachParts();
        base.OnApplyTemplate(e);

        CardBorder = e.NameScope.Find<Border>("PART_CardBorder");
        HeaderDragArea = e.NameScope.Find<Control>("PART_HeaderDragArea");
        ContentHost = e.NameScope.Find<ContentPresenter>("PART_ContentPresenter");

        // The shared drag contract: only an unhandled left press inside the header moves the
        // window, and interactive controls inside it are excluded automatically. A headerless
        // dialog hands the same contract to its card (see HasHeaderProperty).
        SyncDragHandle();

        IsTemplateApplied = true;
        TemplateConnected?.Invoke(this, EventArgs.Empty);
    }

    /// <inheritdoc />
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == TitleProperty || change.Property == SubtitleProperty
            || change.Property == HeaderContentProperty || change.Property == HeaderActionsProperty)
        {
            UpdateHasHeader();
        }
    }

    /// <summary>Recompute <see cref="HasHeader"/> from the four inputs that feed it.</summary>
    private void UpdateHasHeader()
    {
        var next = HeaderContent != null
                   || HeaderActions != null
                   || !string.IsNullOrWhiteSpace(Title)
                   || !string.IsNullOrWhiteSpace(Subtitle);
        if (next == hasHeader) return;

        SetAndRaise(HasHeaderProperty, ref hasHeader, next);
        SyncDragHandle();
    }

    /// <summary>
    /// Point the drag handle at the header (normal) or at the card (a headerless alert). Runs on
    /// every template application and whenever the header appears or disappears, and always clears
    /// the other target first so the two can never both move the window.
    /// </summary>
    private void SyncDragHandle()
    {
        if (HeaderDragArea != null) PanelDragBehavior.SetIsDragHandle(HeaderDragArea, hasHeader);
        if (CardBorder != null) PanelDragBehavior.SetIsDragHandle(CardBorder, !hasHeader);
    }

    /// <summary>Release the parts of the template being replaced (a re-application must not stack handlers).</summary>
    private void DetachParts()
    {
        IsTemplateApplied = false;

        if (HeaderDragArea != null)
        {
            PanelDragBehavior.SetIsDragHandle(HeaderDragArea, false);
            HeaderDragArea = null;
        }

        if (CardBorder != null)
            PanelDragBehavior.SetIsDragHandle(CardBorder, false);

        CardBorder = null;
        ContentHost = null;
    }
}
