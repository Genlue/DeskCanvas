using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using DeskCanvas.Core.Models.Settings;
using DeskCanvas.Services;

namespace DeskCanvas.Views.Controls;

/// <summary>
/// The sidebar's diffuse shadow, drawn as the bottom layer of the sidebar window.
/// <para>
/// It is a single horizontal wash in the <b>window's</b> coordinate space (which is why the
/// sidebar window is widened by <see cref="SidebarShadowSettings.EffectiveExtendDip"/> while the
/// cards keep their configured width — the tail needs somewhere to go):
/// </para>
/// <list type="bullet">
/// <item>from the right edge inwards the wash is <b>flat</b> at <see cref="SidebarShadowSettings.EffectiveOpacity"/>,
/// so it sits evenly behind the cards;</item>
/// <item>it starts to weaken at the leftmost card's left edge (shiftable with
/// <see cref="SidebarShadowSettings.FadeStartOffsetDip"/>) and fades to nothing over
/// <see cref="SidebarShadowSettings.EffectiveFeatherDip"/> DIPs;</item>
/// <item>that fade ends <see cref="SidebarShadowSettings.EffectiveExtendDip"/> DIPs to the
/// <i>left</i> of the leftmost card — the sidebar's own left edge — which is what makes it read as
/// a panel floating over the desktop rather than as a strip of loose cards.</item>
/// </list>
/// <para>
/// The layer itself never takes pointer input — it is decoration. Note that the overhang it sits in
/// is nonetheless part of the sidebar window, so it is no more click-through than the strip itself
/// already is (the window's root panel covers both).
/// </para>
/// </summary>
public sealed class SidebarShadowLayer : Control
{
    private SidebarShadowSettings? shadow;
    private double anchorXDip;
    private LinearGradientBrush? brush;
    private double brushWidth;
    private double brushHeight;

    public SidebarShadowLayer()
    {
        IsHitTestVisible = false;
    }

    /// <summary>
    /// Point the layer at the current shadow settings and the leftmost card's left edge
    /// (in window-relative DIPs; pass the sidebar's own left edge when the sidebar is empty).
    /// </summary>
    public void Configure(SidebarShadowSettings? settings, double anchorX)
    {
        var changed = !Equals(shadow, settings) || Math.Abs(anchorXDip - anchorX) > 0.01;
        shadow = settings;
        anchorXDip = anchorX;
        if (!changed) return;

        brush = null;
        InvalidateVisual();
    }

    /// <summary>
    /// The layer is born inside a window whose size is not known until it is laid out, and the very
    /// first render can therefore arrive with a zero-width bound and draw nothing. Invalidating on
    /// every size change is what turns that empty first pass into a painted one — without it the
    /// cached-image the compositor holds stays blank until some unrelated control invalidates the
    /// window, which is exactly the "the shadow only shows up a moment later" symptom.
    /// </summary>
    protected override Size ArrangeOverride(Size finalSize)
    {
        if (Math.Abs(brushWidth - finalSize.Width) > 0.01 || Math.Abs(brushHeight - finalSize.Height) > 0.01)
        {
            brushWidth = finalSize.Width;
            brushHeight = finalSize.Height;
            brush = null;
            InvalidateVisual();
        }
        return base.ArrangeOverride(finalSize);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        brush = null;
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        if (shadow is not { } settings || !settings.IsVisible) return;

        var width = Bounds.Width;
        var height = Bounds.Height;
        if (width < 1 || height < 1) return;

        context.DrawRectangle(brush ??= BuildBrush(settings, width), null, new Rect(0, 0, width, height));
    }

    private LinearGradientBrush BuildBrush(SidebarShadowSettings settings, double width)
    {
        var color = Color.TryParse(settings.EffectiveColor, out var parsed) ? parsed : Colors.Black;
        var alpha = (byte)Math.Round(Math.Clamp(settings.EffectiveOpacity, 0, 1) * 255);
        var opaque = Color.FromArgb(alpha, color.R, color.G, color.B);
        var clear = Color.FromArgb(0, color.R, color.G, color.B);

        // The geometry rule itself lives in SidebarRules so it can be verified without a window.
        var span = SidebarRules.ResolveShadow(width, anchorXDip, settings);

        return new LinearGradientBrush
        {
            StartPoint = new RelativePoint(span.FadeEnd, 0.5, RelativeUnit.Absolute),
            EndPoint = new RelativePoint(width, 0.5, RelativeUnit.Absolute),
            GradientStops =
            {
                new GradientStop(clear, 0),
                new GradientStop(opaque, span.HoldStop),
                new GradientStop(opaque, 1)
            }
        };
    }
}
