using Avalonia;
using Avalonia.Controls;
using DeskCanvas.Services;

namespace DeskCanvas.Views;

/// <summary>
/// Invisible 1×1 helper window that stays alive for the whole app lifetime and
/// serves as the <see cref="Screens"/> anchor for the display monitor (Avalonia
/// needs a TopLevel to enumerate screens; none may exist before the first widget
/// or the settings window is shown). Placed far off-screen, removed from Alt-Tab
/// and transparent, so it never disturbs the desktop.
/// </summary>
public sealed class WidgetAnchorWindow : Window
{
    public WidgetAnchorWindow()
    {
        Width = 1;
        Height = 1;
        ShowInTaskbar = false;
        SystemDecorations = SystemDecorations.None;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        Opacity = 0;
    }

    /// <summary>Create the native window at an off-screen position (invisible) and keep it alive.</summary>
    public void ShowAnchored()
    {
        Position = new PixelPoint(-32000, -32000);
        Show();
        Position = new PixelPoint(-32000, -32000);
        InteropService.RemoveWindowFromAltTab(this);
    }
}