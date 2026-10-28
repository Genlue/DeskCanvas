using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Calendar.Views.Controls;

/// <summary>
/// Draws the 日期 face directly from text ink bounds. The reference card's proportions are
/// fractions of the card, not line-box fractions: a TextBlock's line box is much taller than the
/// glyphs, so laying the header/day/lunar column out with ordinary text blocks makes the three
/// pieces drift apart as the card is resized. Measuring each string at a reference size and
/// painting the outline geometry at the target ink rectangle keeps the reference layout exact at
/// any widget size and DPI.
/// </summary>
public class DateFace : Control
{
    public static readonly StyledProperty<string?> HeaderProperty =
        AvaloniaProperty.Register<DateFace, string?>(nameof(Header));

    public static readonly StyledProperty<string?> DayProperty =
        AvaloniaProperty.Register<DateFace, string?>(nameof(Day));

    public static readonly StyledProperty<string?> LunarProperty =
        AvaloniaProperty.Register<DateFace, string?>(nameof(Lunar));

    public static readonly StyledProperty<FontFamily> FontFamilyProperty =
        AvaloniaProperty.Register<DateFace, FontFamily>(nameof(FontFamily), FontFamily.Default);

    public static readonly StyledProperty<IBrush?> HeaderBrushProperty =
        AvaloniaProperty.Register<DateFace, IBrush?>(nameof(HeaderBrush), Brushes.White);

    public static readonly StyledProperty<IBrush?> DayBrushProperty =
        AvaloniaProperty.Register<DateFace, IBrush?>(nameof(DayBrush), Brushes.White);

    public static readonly StyledProperty<IBrush?> LunarBrushProperty =
        AvaloniaProperty.Register<DateFace, IBrush?>(nameof(LunarBrush), Brushes.White);

    public static readonly StyledProperty<bool> ShowLunarProperty =
        AvaloniaProperty.Register<DateFace, bool>(nameof(ShowLunar), true);

    /// <summary>Reference size for the per-em ink metrics; it does not affect the rendered size.</summary>
    private const double ReferenceFontSize = 100;

    // Proportions measured from the rectified reference card (see the widget docs).
    private const double HeaderInkRatio = 0.115;
    private const double HeaderCenterRatio = 0.208;
    private const double BodyInkRatio = 0.455;
    private const double BodyCenterRatio = 0.602;
    private const double LunarLineGapRatio = 0.012;
    private const double LunarGapRatio = 0.045;
    private const double MaxHeaderWidthRatio = 0.88;
    private const double MaxGroupWidthRatio = 0.84;

    static DateFace()
    {
        AffectsRender<DateFace>(
            HeaderProperty,
            DayProperty,
            LunarProperty,
            FontFamilyProperty,
            HeaderBrushProperty,
            DayBrushProperty,
            LunarBrushProperty,
            ShowLunarProperty);
    }

    public string? Header
    {
        get => GetValue(HeaderProperty);
        set => SetValue(HeaderProperty, value);
    }

    public string? Day
    {
        get => GetValue(DayProperty);
        set => SetValue(DayProperty, value);
    }

    public string? Lunar
    {
        get => GetValue(LunarProperty);
        set => SetValue(LunarProperty, value);
    }

    public FontFamily FontFamily
    {
        get => GetValue(FontFamilyProperty);
        set => SetValue(FontFamilyProperty, value);
    }

    public IBrush? HeaderBrush
    {
        get => GetValue(HeaderBrushProperty);
        set => SetValue(HeaderBrushProperty, value);
    }

    public IBrush? DayBrush
    {
        get => GetValue(DayBrushProperty);
        set => SetValue(DayBrushProperty, value);
    }

    public IBrush? LunarBrush
    {
        get => GetValue(LunarBrushProperty);
        set => SetValue(LunarBrushProperty, value);
    }

    /// <summary>Hides the lunar column and centres the day when off.</summary>
    public bool ShowLunar
    {
        get => GetValue(ShowLunarProperty);
        set => SetValue(ShowLunarProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        var width = Bounds.Width;
        var height = Bounds.Height;
        if (width <= 0 || height <= 0) return;

        DrawHeader(context, width, height);
        DrawBody(context, width, height);
    }

    private void DrawHeader(DrawingContext context, double width, double height)
    {
        if (string.IsNullOrWhiteSpace(Header)) return;

        var typeface = new Typeface(FontFamily, FontStyle.Normal, FontWeight.Bold);
        var metrics = MeasureInk(Header, typeface);
        if (metrics.Height <= 0 || metrics.Width <= 0) return;

        var fontSize = height * HeaderInkRatio / metrics.Height;
        var tightWidth = fontSize * metrics.Width;
        var maxWidth = width * MaxHeaderWidthRatio;
        if (tightWidth > maxWidth)
            fontSize *= maxWidth / tightWidth;

        var shape = BuildShape(Header, typeface, fontSize);
        if (shape.IsEmpty) return;

        DrawShape(
            context,
            shape,
            width / 2 - shape.Bounds.Width / 2,
            height * HeaderCenterRatio - shape.Bounds.Height / 2,
            HeaderBrush);
    }

    private void DrawBody(DrawingContext context, double width, double height)
    {
        if (string.IsNullOrWhiteSpace(Day)) return;

        var day = Day.Trim();
        var lunar = ShowLunar ? Lunar?.Trim() ?? string.Empty : string.Empty;
        var dayTypeface = new Typeface(FontFamily, FontStyle.Normal, FontWeight.SemiBold);
        var lunarTypeface = new Typeface(FontFamily, FontStyle.Normal, FontWeight.Bold);

        var dayMetrics = MeasureInk(day, dayTypeface);
        if (dayMetrics.Height <= 0 || dayMetrics.Width <= 0) return;

        var dayFontSize = height * BodyInkRatio / dayMetrics.Height;
        var gap = lunar.Length > 0 ? height * LunarGapRatio : 0;

        var lunarGlyphs = SplitGraphemes(lunar);
        var lunarTargetHeight = height * (lunarGlyphs.Count >= 5 ? 0.5 : BodyInkRatio);
        var lunarWidthPerEm = 0.0;
        var lunarHeightPerEm = 0.0;
        foreach (var glyph in lunarGlyphs)
        {
            var metrics = MeasureInk(glyph, lunarTypeface);
            lunarWidthPerEm = Math.Max(lunarWidthPerEm, metrics.Width);
            lunarHeightPerEm = Math.Max(lunarHeightPerEm, metrics.Height);
        }

        // Four-character lunar dates are the reference case. Leap months add a fifth glyph
        // ("闰腊月十六"); give that column a little more height. In both cases the font is
        // fitted so the glyphs never collide: each line gets a real vertical gap.
        var lunarFontSize = 0.0;
        if (lunarGlyphs.Count > 0 && lunarHeightPerEm > 0)
        {
            var lineGap = height * LunarLineGapRatio;
            var inkBudget = Math.Max(0, lunarTargetHeight - lineGap * (lunarGlyphs.Count - 1));
            lunarFontSize = inkBudget / lunarGlyphs.Count / lunarHeightPerEm;
        }

        var estimatedWidth = dayFontSize * dayMetrics.Width
                             + gap
                             + lunarFontSize * lunarWidthPerEm;
        var maxWidth = width * MaxGroupWidthRatio;
        if (estimatedWidth > maxWidth)
        {
            var scale = maxWidth / estimatedWidth;
            dayFontSize *= scale;
            lunarFontSize *= scale;
        }

        var dayShape = BuildShape(day, dayTypeface, dayFontSize);
        if (dayShape.IsEmpty) return;

        var lunarShapes = new List<TextShape>(lunarGlyphs.Count);
        var lunarColumnWidth = 0.0;
        foreach (var glyph in lunarGlyphs)
        {
            var shape = BuildShape(glyph, lunarTypeface, lunarFontSize);
            if (shape.IsEmpty) continue;
            lunarShapes.Add(shape);
            lunarColumnWidth = Math.Max(lunarColumnWidth, shape.Bounds.Width);
        }

        var groupWidth = dayShape.Bounds.Width + gap + lunarColumnWidth;
        var groupLeft = (width - groupWidth) / 2;
        var bodyCenterY = height * BodyCenterRatio;

        // Day: its ink centre is the body's vertical centre.
        DrawShape(
            context,
            dayShape,
            groupLeft,
            bodyCenterY - dayShape.Bounds.Height / 2,
            DayBrush);

        if (lunarShapes.Count == 0) return;

        // Lunar date: one glyph per line, with the visible ink column filling the same height
        // budget as the day number. Spacing derives from that budget so a five-character
        // "闰腊月十六" stays balanced instead of running into the header or the empty bottom.
        var maxGlyphHeight = 0.0;
        foreach (var shape in lunarShapes)
            maxGlyphHeight = Math.Max(maxGlyphHeight, shape.Bounds.Height);

        var step = lunarShapes.Count > 1
            ? Math.Max(0, (lunarTargetHeight - maxGlyphHeight) / (lunarShapes.Count - 1))
            : 0;

        var top = double.MaxValue;
        var bottom = double.MinValue;
        for (var i = 0; i < lunarShapes.Count; i++)
        {
            var centerOffset = (i - (lunarShapes.Count - 1) / 2.0) * step;
            top = Math.Min(top, centerOffset - lunarShapes[i].Bounds.Height / 2);
            bottom = Math.Max(bottom, centerOffset + lunarShapes[i].Bounds.Height / 2);
        }

        var shift = bodyCenterY - (top + bottom) / 2;
        var lunarLeft = groupLeft + dayShape.Bounds.Width + gap;
        for (var i = 0; i < lunarShapes.Count; i++)
        {
            var shape = lunarShapes[i];
            var centerOffset = (i - (lunarShapes.Count - 1) / 2.0) * step + shift;
            DrawShape(
                context,
                shape,
                lunarLeft + (lunarColumnWidth - shape.Bounds.Width) / 2,
                centerOffset - shape.Bounds.Height / 2,
                LunarBrush);
        }
    }

    private static (double Width, double Height) MeasureInk(string text, Typeface typeface)
    {
        var formatted = new FormattedText(
            text,
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            typeface,
            ReferenceFontSize,
            Brushes.White);
        var bounds = formatted.BuildGeometry(default)?.Bounds ?? default;
        return (bounds.Width / ReferenceFontSize, formatted.Extent / ReferenceFontSize);
    }

    private static TextShape BuildShape(string text, Typeface typeface, double fontSize)
    {
        var formatted = new FormattedText(
            text,
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            typeface,
            fontSize,
            Brushes.White);
        var geometry = formatted.BuildGeometry(default);
        return geometry is null ? default : new TextShape(geometry, geometry.Bounds);
    }

    private static void DrawShape(
        DrawingContext context,
        TextShape shape,
        double left,
        double top,
        IBrush? brush)
    {
        if (shape.IsEmpty) return;

        using (context.PushTransform(Matrix.CreateTranslation(left - shape.Bounds.X, top - shape.Bounds.Y)))
            context.DrawGeometry(brush ?? Brushes.White, null, shape.Geometry!);
    }

    private static List<string> SplitGraphemes(string text)
    {
        var glyphs = new List<string>(text.Length);
        foreach (var rune in text.EnumerateRunes())
            glyphs.Add(rune.ToString());
        return glyphs;
    }

    private readonly record struct TextShape(Geometry? Geometry, Rect Bounds)
    {
        public bool IsEmpty => Geometry is null || Bounds.Width <= 0 || Bounds.Height <= 0;
    }
}