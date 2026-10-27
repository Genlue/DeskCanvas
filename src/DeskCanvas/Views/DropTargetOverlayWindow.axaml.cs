using System;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace DeskCanvas.Views;

/// <summary>
/// 落点白框 — the drop-target outline: one reusable, click-through, never-activating overlay
/// window that draws the exact card rectangle a release would snap the dragged widget into.
/// <para>
/// A top-level window of its own rather than a layer inside the dragged widget, for two reasons.
/// The widget window <b>is</b> the cell: a target on the neighbouring cell would be clipped away
/// by it, while the outline has to be visible over the card being dragged. And the native move
/// loop keeps the widget window glued to the cursor, so only a separate window can stay put on
/// the cell that is being previewed.
/// </para>
/// <para>
/// It must never take part in the interaction. <c>WS_EX_TRANSPARENT</c> makes it invisible to
/// hit-testing (WM_MOVING keeps reaching the widget), <c>WS_EX_NOACTIVATE</c> keeps the focus
/// where it was — activating anything would end the move loop mid-drag — and
/// <c>WS_EX_TOOLWINDOW</c> keeps it out of the taskbar and Alt-Tab. <c>ShowActivated="False"</c>
/// and <c>Topmost</c> are the Avalonia half of the same contract.
/// </para>
/// <para>
/// Geometry is the CARD, not the window: the widget window spans the whole grid cell while the
/// visible card is inset by <see cref="Widget.WidgetMargin"/> (zero when frameless) and rounded by
/// <see cref="Widget.Radius"/>. The caller deflates the snapped rect and passes the device-pixel
/// radius, so the outline sits on the card's own edge — anything else reads as "the cell will
/// land here", which is not what the user is aiming with.
/// </para>
/// <para>
/// The strokes are white while the drop is allowed and a soft red while 禁止组件重叠 will refuse
/// it. That is the whole visual contract of a refusal: the same outline in the same place, so the
/// user reads "not this cell" rather than "the preview moved".
/// </para>
/// </summary>
public partial class DropTargetOverlayWindow : Window
{
    /// <summary>
    /// Halo width outside the card edge, in DIP. Wide enough for the faint stroke to read as
    /// light rather than as a second border, narrow enough that the whole overlay stays inside
    /// the gap between two neighbouring cards.
    /// </summary>
    private const double GlowPad = 7;

    /// <summary>Stroke width of both outlines, in DIP.</summary>
    private const double StrokeDip = 3;

    /// <summary>
    /// The two stroke colours, in the order (outline, glow). The red pair is the same two alphas
    /// as the white pair, so the refused state changes the hue and nothing else: a drop the
    /// release will refuse reads as the same outline in a warning colour, not as a different
    /// shape or weight. White is the resting state and the axaml default, so the permitted drop
    /// is byte-identical to what it looked like before 禁止组件重叠 had a preview.
    /// </summary>
    private static readonly IBrush OutlineBrush = Brush.Parse("#F2FFFFFF");
    private static readonly IBrush GlowBrush = Brush.Parse("#26FFFFFF");
    private static readonly IBrush BlockedOutlineBrush = Brush.Parse("#F2FF7A7A");
    private static readonly IBrush BlockedGlowBrush = Brush.Parse("#26FF7A7A");

    private const int GWL_EXSTYLE = -20;
    private const uint WS_EX_TRANSPARENT = 0x00000020;
    private const uint WS_EX_TOOLWINDOW = 0x00000080;
    private const uint WS_EX_NOACTIVATE = 0x08000000;
    private const uint WS_EX_APPWINDOW = 0x00040000;

    /// <summary>The card rectangle last asked for, so a DPI change can re-derive the DIP layout.</summary>
    private PixelRect cardRect;
    private int cardRadius;

    /// <summary>
    /// Whether the last placement was refused. Kept alongside the rect because
    /// <see cref="OnOverlayScalingChanged"/> re-applies the layout from scratch: without it a
    /// cross-DPI drag would repaint a refused outline white while the release still refuses.
    /// </summary>
    private bool cardBlocked;

    public DropTargetOverlayWindow()
    {
        InitializeComponent();

        // Applied through the creation-time style callback rather than after Show(): a window that
        // is visible for even one message without NOACTIVATE can take the foreground, and taking
        // the foreground ends the very move loop that asked for the outline.
        if (OperatingSystem.IsWindows())
            Win32Properties.AddWindowStylesCallback(this, (style, exStyle) => (style, ForceOverlayStyles(exStyle)));

        ScalingChanged += OnOverlayScalingChanged;
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        // Belt and braces: the callback above owns the bits, but Avalonia recomputes the extended
        // style on every show and a stale APPWINDOW bit would put the overlay in the taskbar.
        ApplyOverlayStyles();
    }

    /// <summary>
    /// Place the outline on <paramref name="card"/> — the card's own rectangle, i.e. the snapped
    /// window rect deflated by the widget margin.
    /// </summary>
    /// <param name="card">Card rectangle, physical screen pixels.</param>
    /// <param name="cornerRadius">The card's corner radius, physical pixels.</param>
    /// <param name="scaling">Scaling of the screen the card lands on (window sizes are DIPs).</param>
    /// <param name="blocked">Whether a release here would be refused; selects the red strokes.</param>
    internal void Place(PixelRect card, int cornerRadius, double scaling, bool blocked = false)
    {
        cardRect = card;
        cardRadius = cornerRadius;
        cardBlocked = blocked;
        Apply(scaling > 0 ? scaling : 1.0);
    }

    private void Apply(double scaling)
    {
        // The pad is rounded to whole device pixels and converted BACK to DIP, so the outline's
        // inset and the window's inflation are the same physical distance — using the raw 7 DIP
        // for the margin would put the stroke a fraction of a pixel off the card at 125%/150%.
        var pad = (int)Math.Round(GlowPad * scaling);
        var padDip = pad / scaling;
        var radiusDip = cardRadius / scaling;

        Outline.CornerRadius = new CornerRadius(radiusDip);
        Outline.BorderThickness = new Thickness(StrokeDip);
        // Inset by exactly the pad the window was inflated by: this is what puts the main stroke
        // on the card's edge instead of on the halo's.
        Outline.Margin = new Thickness(padDip);
        Outline.BorderBrush = cardBlocked ? BlockedOutlineBrush : OutlineBrush;

        Glow.CornerRadius = new CornerRadius(radiusDip + padDip);
        Glow.BorderThickness = new Thickness(StrokeDip);
        Glow.BorderBrush = cardBlocked ? BlockedGlowBrush : GlowBrush;

        Position = new PixelPoint(cardRect.X - pad, cardRect.Y - pad);
        Width = Math.Max(1, cardRect.Width + 2 * pad) / scaling;
        Height = Math.Max(1, cardRect.Height + 2 * pad) / scaling;
    }

    /// <summary>
    /// A cross-DPI drag re-scales this window when it lands on the target monitor, which can lag
    /// the placement by a message. Re-deriving the DIP layout from the live scaling keeps the
    /// stroke at 3 DIP and the outline on the card's edge instead of off by the DPI ratio.
    /// </summary>
    private void OnOverlayScalingChanged(object? sender, EventArgs e)
    {
        if (!IsVisible) return;
        var scaling = RenderScaling > 0 ? RenderScaling : 1.0;
        Apply(scaling);
    }

    /// <summary>
    /// The extended style the overlay must carry: click-through, never activated, never in the
    /// taskbar/Alt-Tab. APPWINDOW is cleared because it outranks TOOLWINDOW and Avalonia sets it
    /// from <c>ShowInTaskbar</c> whenever it recomputes the style.
    /// </summary>
    private static uint ForceOverlayStyles(uint exStyle) =>
        (exStyle | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW) & ~WS_EX_APPWINDOW;

    private void ApplyOverlayStyles()
    {
        if (!OperatingSystem.IsWindows()) return;
        var handle = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (handle == IntPtr.Zero) return;

        var exStyle = unchecked((uint)GetWindowLong(handle, GWL_EXSTYLE));
        var wanted = ForceOverlayStyles(exStyle);
        if (wanted == exStyle) return;
        SetWindowLongPtr(handle, GWL_EXSTYLE, new IntPtr(unchecked((int)wanted)));
    }

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", EntryPoint = "SetWindowLong", SetLastError = true)]
    private static extern int SetWindowLong32(IntPtr hWnd, int nIndex, int dwNewLong);

    /// <summary>SetWindowLongPtr that falls back to SetWindowLong on 32-bit.</summary>
    private static IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong) =>
        IntPtr.Size == 8
            ? SetWindowLongPtr64(hWnd, nIndex, dwNewLong)
            : new IntPtr(SetWindowLong32(hWnd, nIndex, unchecked((int)dwNewLong.ToInt64())));
}
