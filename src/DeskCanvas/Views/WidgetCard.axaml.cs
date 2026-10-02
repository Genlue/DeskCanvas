using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace DeskCanvas.Views;

/// <summary>
/// The shared widget card: glass surface, card background, adaptive corner radius, content
/// presenter, stack pagination dots and the free-mode resize handle — everything that is
/// identical between a desktop widget and a sidebar widget.
/// <para>
/// Input handling is forwarded to the host (the window's DataContext implements
/// <see cref="IWidgetCardHost"/>), so the card itself stays agnostic about where it lives.
/// </para>
/// </summary>
public partial class WidgetCard : UserControl
{
    public WidgetCard()
    {
        InitializeComponent();
    }

    /// <summary>The host that owns this card, derived from the inherited data context.</summary>
    public IWidgetCardHost? Host => DataContext as IWidgetCardHost;

    /// <summary>The card's outer border (sized by the host to the grid cell minus the widget margin).</summary>
    public Border CardBorderControl => CardBorder;

    /// <summary>The content presenter (holds the widget view; also the content-scale anchor).</summary>
    public ContentPresenter ContentPresenterControl => CardPresenter;

    /// <summary>The glass surface (also used by the host to force a re-render after structural changes).</summary>
    public Controls.LiquidGlassSurface GlassControl => CardGlass;

    private void OnStackIndicatorClicked(object? sender, RoutedEventArgs e) =>
        Host?.OnStackIndicatorClicked(sender, e);

    private void OnStackIndicatorsWheelChanged(object? sender, PointerWheelEventArgs e) =>
        Host?.OnStackIndicatorsWheelChanged(sender, e);

    private void OnResizeHandlePressed(object? sender, PointerPressedEventArgs e) =>
        Host?.OnResizeHandlePressed(sender, e);
}
