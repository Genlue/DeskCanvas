namespace Folders.Services;

/// <summary>
/// Device-pixel snapping for the resting geometry of the single-icon widgets (icon size and the
/// empty-state frame with its "+" glyph).
/// </summary>
/// <remarks>
/// Every length in these cards is fractional once the display is scaled: on a 125 % desktop the
/// 1×1 card is 72 DIP = 90 px, the icon/placeholder is 70 % of it (50 DIP = 62.5 px), the frame's
/// border is 1.5 DIP = 1.875 px, its padding 6 DIP = 7.5 px and the glyph 22 DIP = 27.5 px.
/// Avalonia rounds every arranged rectangle to whole device pixels on its own and rounds halves
/// up, so each fractional length hands the child another half pixel toward the bottom-right. The
/// errors stack through the nesting (card → frame → glyph) and are plainly visible on the empty
/// state, where a symmetric "+" sits in a symmetric frame: measured on the real 125 % desktop the
/// glyph had 18 px of air on its left against 17 px on its right inside a 63 px frame, and the
/// frame itself stood one pixel further from the card's left edge than from its right.
/// <para>
/// The cure is not to fight the rounding but to stop feeding it fractions: a child whose device
/// size has the same parity as the card's is centred with integral margins on all four sides, so
/// no rounding happens at any level and the whole stack keeps the same centre.
/// </para>
/// <para>
/// What is left after this is the card's own placement, not the icon's: the widget margin is
/// fractional at 125 % (10 DIP = 12.5 px), so the card's device rectangle starts on a half pixel
/// and every edge inside it is drawn half a pixel to the right of it. That is a host property
/// shared by all widgets (it is also why the card rim is a hairline on one monitor position and a
/// soft line on the next) and cannot be fixed from inside a widget view; the geometry here is
/// exactly centred on the card's rectangle.
/// </para>
/// </remarks>
internal static class IconPixelSnap
{
    /// <summary>Frame border, mirroring the <c>BorderThickness</c> in SingleFile/SingleLink.axaml.</summary>
    public const double FrameBorder = 1.5;

    /// <summary>Frame padding, mirroring the <c>Padding</c> in SingleFile/SingleLink.axaml.</summary>
    public const double FramePadding = 6.0;

    /// <summary>Glyph box, mirroring the <c>PathIcon</c> size in SingleFile/SingleLink.axaml.</summary>
    public const double Glyph = 22.0;

    /// <summary>
    /// The empty-state frame, every length on the device-pixel grid (DIPs, ready to assign).
    /// </summary>
    public readonly record struct Frame(double Side, double Border, double Padding, double GlyphSide);

    /// <summary>Snaps the empty-state frame and its glyph to whole device pixels.</summary>
    /// <param name="cardSide">Side of the card the frame is centred on, in DIPs.</param>
    /// <param name="wantedSide">The percentage-derived frame side, in DIPs.</param>
    /// <param name="scaling">Render scaling of the hosting window (1.0 when unknown).</param>
    public static Frame EmptyFrame(double cardSide, double wantedSide, double scaling)
    {
        var side = MatchParity(ToPixels(wantedSide, scaling), ToPixels(cardSide, scaling));

        var border = Math.Max(1, ToPixels(FrameBorder, scaling));
        var padding = Math.Max(0, ToPixels(FramePadding, scaling));
        var inner = Math.Max(0, side - 2 * (border + padding));

        var glyph = ToPixels(Glyph, scaling);
        // The glyph is centred in the padding box, so that box's parity is what decides whether
        // the two margins can be equal — even when the glyph is wider than the box and spills
        // symmetrically over it (the shipped look at small 图标大小: the frame does not clip, so
        // the "+" simply fills it). Only skip the parity step when there is no padding box at all.
        if (inner > 0) glyph = MatchParity(glyph, inner);

        return new Frame(ToDips(side, scaling), ToDips(border, scaling),
                         ToDips(padding, scaling), ToDips(glyph, scaling));
    }

    /// <summary>Logical length → whole device pixels. Halves round up, like Avalonia's layout.</summary>
    private static int ToPixels(double dips, double scaling)
        => Math.Max(0, (int)Math.Round(dips * Scale(scaling), MidpointRounding.AwayFromZero));

    /// <summary>Whole device pixels → logical length.</summary>
    private static double ToDips(int pixels, double scaling) => pixels / Scale(scaling);

    private static double Scale(double scaling) => scaling > 0 ? scaling : 1.0;

    /// <summary>
    /// Nudges a pixel count by one when its parity differs from the reference: an even reference
    /// centres an even child on whole pixels, an odd one an odd child. Shrinks rather than grows
    /// so the snapped size never exceeds the 图标大小 the user asked for.
    /// </summary>
    private static int MatchParity(int value, int reference)
        => ((reference - value) & 1) == 0 ? value : (value > 2 ? value - 1 : value + 1);
}
