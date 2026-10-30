using System;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace FixedWidgets.Views.Controls;

/// <summary>
/// The "today" marker of the month grid in the fixed dashboard widget:
/// one filled disc with the day number punched out of it (镂空),
/// or drawn with the numeral centered inside.
/// Both the disc and the numeral share the EXACT same visual center.
/// </summary>
public class HollowTodayMark : Control
{
    public static readonly StyledProperty<bool> IsTodayProperty =
        AvaloniaProperty.Register<HollowTodayMark, bool>(nameof(IsToday));

    public static readonly StyledProperty<bool> HollowProperty =
        AvaloniaProperty.Register<HollowTodayMark, bool>(nameof(Hollow), true);

    public static readonly StyledProperty<string?> TextProperty =
        AvaloniaProperty.Register<HollowTodayMark, string?>(nameof(Text));

    public static readonly StyledProperty<IBrush?> DotBrushProperty =
        AvaloniaProperty.Register<HollowTodayMark, IBrush?>(nameof(DotBrush));

    public static readonly StyledProperty<double> GlyphSizeProperty =
        AvaloniaProperty.Register<HollowTodayMark, double>(nameof(GlyphSize), 56);

    public static readonly StyledProperty<FontWeight> GlyphWeightProperty =
        AvaloniaProperty.Register<HollowTodayMark, FontWeight>(nameof(GlyphWeight), FontWeight.Bold);

    public static readonly StyledProperty<FontFamily?> GlyphFontFamilyProperty =
        AvaloniaProperty.Register<HollowTodayMark, FontFamily?>(nameof(GlyphFontFamily));

    static HollowTodayMark()
    {
        AffectsRender<HollowTodayMark>(
            IsTodayProperty, HollowProperty, TextProperty,
            DotBrushProperty, GlyphSizeProperty, GlyphWeightProperty, GlyphFontFamilyProperty);
    }

    public bool IsToday
    {
        get => GetValue(IsTodayProperty);
        set => SetValue(IsTodayProperty, value);
    }

    public bool Hollow
    {
        get => GetValue(HollowProperty);
        set => SetValue(HollowProperty, value);
    }

    public string? Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public IBrush? DotBrush
    {
        get => GetValue(DotBrushProperty);
        set => SetValue(DotBrushProperty, value);
    }

    public double GlyphSize
    {
        get => GetValue(GlyphSizeProperty);
        set => SetValue(GlyphSizeProperty, value);
    }

    public FontWeight GlyphWeight
    {
        get => GetValue(GlyphWeightProperty);
        set => SetValue(GlyphWeightProperty, value);
    }

    public FontFamily? GlyphFontFamily
    {
        get => GetValue(GlyphFontFamilyProperty);
        set => SetValue(GlyphFontFamilyProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        if (!IsToday) return;

        var side = Math.Min(Bounds.Width, Bounds.Height);
        if (side <= 0) return;

        var brush = DotBrush;
        if (brush == null) return;

        var glyph = BuildNumberGeometry(out var center);

        var maxRadiusX = Math.Min(center.X, Bounds.Width - center.X);
        var maxRadiusY = Math.Min(center.Y, Bounds.Height - center.Y);
        var maxRadius = Math.Min(maxRadiusX, maxRadiusY);
        var radius = Math.Min(side * 0.45, maxRadius - 1.0);

        if (!Hollow)
        {
            // Solid filled disc + centered numeral, strictly sharing the exact same center. The
            // numeral follows the disc's polarity: the 黑白/背景色 monochrome ramps drive the disc
            // to a light color in the dark variant (pure white for 黑白, the light-mode background
            // for 背景色), where the historic hard-coded white numeral vanished into it.
            context.DrawEllipse(brush, null, center, radius, radius);
            if (glyph != null)
            {
                context.DrawGeometry(BuildNumeralBrush(brush), null, glyph);
            }
            return;
        }

        var disc = new EllipseGeometry(new Rect(center.X - radius, center.Y - radius, radius * 2.0, radius * 2.0));
        Geometry marker = glyph == null
            ? disc
            : new CombinedGeometry(GeometryCombineMode.Exclude, disc, glyph);

        context.DrawGeometry(brush, null, marker);

        // A hairline along the edge of the hole, in the colour that contrasts the disc: the
        // numeral stays transparent (the surface behind shows through it) but can no longer
        // disappear when that surface happens to match the disc colour — which is what a white
        // marker disc over a bright wallpaper used to look like.
        if (glyph != null)
        {
            var rim = BuildHoleRimBrush(brush);
            context.DrawGeometry(null, new Pen(rim, Math.Max(1.0, radius * 0.11)), glyph);
        }
    }

    /// <summary>
    /// The hairline colour for the punched-out numeral: the opposite of the disc's brightness,
    /// at partial alpha, so it reads as the edge of a hole rather than as an outline.
    /// </summary>
    private static IBrush BuildHoleRimBrush(IBrush discBrush)
    {
        if (discBrush is not ISolidColorBrush solid) return new SolidColorBrush(Colors.White, 0.35);

        var color = solid.Color;
        var luminance = (0.2126 * color.R + 0.7152 * color.G + 0.0722 * color.B) / 255.0;
        return luminance > 0.55
            ? new SolidColorBrush(Colors.Black, 0.32)
            : new SolidColorBrush(Colors.White, 0.38);
    }

    /// <summary>Near-black numeral for light discs (the app's dark label color, as in ClockDigitalTimeBrush).</summary>
    private static readonly IBrush DarkNumeral = new SolidColorBrush(Color.FromRgb(0x1C, 0x1C, 0x1E));

    /// <summary>
    /// The numeral painted on the solid disc, at full opacity: white on a dark disc, near-black on
    /// a light one — the same luminance split <see cref="BuildHoleRimBrush"/> uses for the hole
    /// rim. The disc color is theme-driven (accent slot, custom hex, or a monochrome ramp slot),
    /// so no fixed foreground stays readable on all of them.
    /// </summary>
    private static IBrush BuildNumeralBrush(IBrush discBrush)
    {
        if (discBrush is not ISolidColorBrush solid) return Brushes.White;

        var color = solid.Color;
        var luminance = (0.2126 * color.R + 0.7152 * color.G + 0.0722 * color.B) / 255.0;
        return luminance > 0.55 ? DarkNumeral : Brushes.White;
    }

    /// <summary>
    /// Builds the number geometry using the exact same baseline and line-height origin as
    /// a centered TextBlock, while strictly centering the visual glyph bounding box with
    /// the marker disc center in both X and Y.
    /// </summary>
    private Geometry? BuildNumberGeometry(out Point center)
    {
        center = new Point(Bounds.Width / 2.0, Bounds.Height / 2.0);
        if (string.IsNullOrEmpty(Text)) return null;

        var typeface = new Typeface(GlyphFontFamily ?? FontFamily.Default, FontStyle.Normal, GlyphWeight);
        var formatted = new FormattedText(
            Text,
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            typeface,
            GlyphSize,
            Brushes.Black);

        var top = (Bounds.Height - formatted.Height) / 2.0;
        var defaultOriginX = (Bounds.Width - formatted.Width) / 2.0;

        var testGlyph = formatted.BuildGeometry(new Point(defaultOriginX, top));
        if (testGlyph == null) return null;

        var bounds = testGlyph.Bounds;
        var testCenterX = (bounds.Left + bounds.Right) / 2.0;
        var glyphCenterY = (bounds.Top + bounds.Bottom) / 2.0;

        var targetCenterX = Bounds.Width / 2.0;
        var dx = targetCenterX - testCenterX;
        var finalOrigin = new Point(defaultOriginX + dx, top);

        var finalGlyph = formatted.BuildGeometry(finalOrigin);
        if (finalGlyph == null) return null;

        center = new Point(targetCenterX, glyphCenterY);
        return finalGlyph;
    }
}
