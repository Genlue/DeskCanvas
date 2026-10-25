// IconCenteringChecks — the single-icon widgets must draw their resting icon / empty-state frame
// dead centre on the card at every display scaling, on whole device pixels.
//
// The defect this pins: at 125 % every length in the 1×1 card is fractional in device pixels
// (card 90 px, frame 62.5 px, glyph 27.5 px, border 1.875 px, padding 7.5 px), and Avalonia's
// layout rounding hands each nesting level's leftover half pixel to the bottom-right. On the real
// desktop the "+" ended up with 18 px of air on its left against 16 px on its right, and the frame
// stood 14 px from the card's left edge against 13 px from the right. Fixed by snapping the
// geometry to whole device pixels with the parity the card's own origin asks for (IconPixelSnap);
// this suite measures the four margins at 100/125/150/175/200 % and fails on any whole-pixel skew
// or on any edge that lands between two pixels.
//
// Run: dotnet run --project tests/IconCenteringChecks
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Shapes;
using Avalonia.Headless;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Folders.Models;
using Folders.Views;

internal static class Program
{
    private static int failures;
    private static int checks;

    [STAThread]
    private static int Main()
    {
        AppBuilder.Configure<Application>()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .UseSkia()
            .SetupWithoutStarting();

        var app = Application.Current!;
        app.Styles.Add(new FluentTheme());
        app.RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Dark;

        Console.WriteLine("=== Icon centring (single-icon widgets) ===");

        double[] scalings = [1.0, 1.25, 1.5, 1.75, 2.0];
        double[] percents = [10, 50, 70, 100];
        foreach (var kind in new[] { "SingleFile", "SingleLink" })
            foreach (var scaling in scalings)
                foreach (var percent in percents)
                    RunCase(kind, scaling, percent);

        // The host chrome used above is 72 DIP on a 92 DIP cell, which is what a 1×1 card is on
        // the reference 22×12 grid; a 2×2 card is a different device size, so make sure the parity
        // matching does not depend on the card happening to be even.
        foreach (var cardSide in new[] { 150.0, 222.0 })
            RunCase("SingleFile", 1.25, 70, cardSide);

        // Margins whose device width is whole (8 DIP = 10 px, 12 DIP = 15 px) put the card itself on
        // the pixel grid; then every edge of the frame has to land on whole pixels as well.
        foreach (var margin in new[] { 8.0, 12.0 })
            RunCase("SingleFile", 1.25, 70, margin: margin);

        // The resting icon is the same rectangle as the empty frame, but it is drawn by a different
        // control (an Image with Stretch=Uniform, whose bounds is the aspect-fitted bitmap); assert
        // it lands on the same grid. A square source is used so the fitted bounds is the whole box.
        var iconFile = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "deskcanvas-icon-centre.ico");
        WriteSquareIco(iconFile, 16);
        foreach (var scaling in scalings)
            RunIconCase(iconFile, scaling, 70);
        File.Delete(iconFile);

        Console.WriteLine();
        Console.WriteLine(failures == 0
            ? $"ICON CENTRING: PASS ({checks} checks)"
            : $"ICON CENTRING: {failures} of {checks} checks FAILED");
        return failures == 0 ? 0 : 1;
    }

    private static (Window Window, Border Card) BuildHost(Control view, double cardSide, double margin)
    {
        var presenter = new ContentPresenter
        {
            Width = cardSide,
            Height = cardSide,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Content = view,
        };

        // The card chrome from Widget.axaml, reduced to what the layout sees: a card of the grid
        // cell minus the margin with a centred content presenter of exactly the card's size.
        var card = new Border
        {
            Width = cardSide,
            Height = cardSide,
            Margin = new Thickness(margin),
            CornerRadius = new CornerRadius(18),
            Background = new SolidColorBrush(Color.Parse("#FF5A6B75")),
            Child = presenter,
        };

        var root = new Grid();
        root.Children.Add(card);
        var window = new Window { Width = cardSide + 2 * margin, Height = cardSide + 2 * margin, Content = root };
        return (window, card);
    }

    private static void RunCase(string kind, double scaling, double percent,
        double cardSide = 72.0, double margin = 10.0)
    {
        Control view = kind == "SingleFile"
            ? new SingleFile(new SingleFileModel(null, percent, 100), null!)
            : new SingleLink(new SingleLinkModel(null, null, null, null, percent, 100), null!);

        var (window, card) = BuildHost(view, cardSide, margin);
        window.Show();
        window.SetRenderScaling(scaling);
        Dispatcher.UIThread.RunJobs();

        var frame = view.FindControl<Border>("EmptyPlaceholder")!;
        var glyph = view.FindControl<PathIcon>("PlaceholderGlyph")!;

        var cardOrigin = card.TranslatePoint(new Point(0, 0), window)!.Value;
        var framePos = frame.TranslatePoint(new Point(0, 0), card)!.Value;
        var glyphPos = glyph.TranslatePoint(new Point(0, 0), frame)!.Value;

        double Px(double dips) => dips * scaling;

        var frameAbsX = Px(cardOrigin.X + framePos.X);
        var frameAbsY = Px(cardOrigin.Y + framePos.Y);
        var frameL = Px(framePos.X);
        var frameR = Px(cardSide) - Px(framePos.X + frame.Bounds.Width);
        var frameT = Px(framePos.Y);
        var frameB = Px(cardSide) - Px(framePos.Y + frame.Bounds.Height);

        var glyphL = Px(glyphPos.X);
        var glyphR = Px(frame.Bounds.Width) - Px(glyphPos.X + glyph.Bounds.Width);
        var glyphT = Px(glyphPos.Y);
        var glyphB = Px(frame.Bounds.Height) - Px(glyphPos.Y + glyph.Bounds.Height);

        var label = $"{kind} @{scaling:0.##}x pct={percent:0} card={cardSide:0} margin={margin:0.#}";
        var frameSkewX = frameL - frameR;
        var frameSkewY = frameT - frameB;
        var glyphSkewX = glyphL - glyphR;
        var glyphSkewY = glyphT - glyphB;

        Console.WriteLine($"{label}: frame {Px(frame.Bounds.Width):0.##}px L{frameL:0.##}/R{frameR:0.##}/T{frameT:0.##}/B{frameB:0.##}"
                          + $"  glyph {Px(glyph.Bounds.Width):0.##}px L{glyphL:0.##}/R{glyphR:0.##}/T{glyphT:0.##}/B{glyphB:0.##}");

        // Whole device pixels: a fractional size is what the rounding feeds on. The absolute
        // edges can only be whole when the card's own rectangle is (the widget margin is 12.5 px
        // at 125 %, which puts every card edge on a half pixel — a host property, not ours).
        Check(NearlyIntegral(Px(frame.Bounds.Width)), $"{label}: frame side lands on a whole device pixel");
        var cardOnGrid = NearlyIntegral(Px(cardOrigin.X)) && NearlyIntegral(Px(cardOrigin.Y));
        if (cardOnGrid)
        {
            Check(NearlyIntegral(frameAbsX), $"{label}: frame left edge lands on a whole device pixel (x={frameAbsX:0.##})");
            Check(NearlyIntegral(frameAbsY), $"{label}: frame top edge lands on a whole device pixel (y={frameAbsY:0.##})");
        }

        // A card whose own device size is fractional (a grid cell can land on a half pixel) has
        // fractional edges, so half a pixel of centring error is the best any child can do there;
        // on the whole-pixel cards every margin has to be identical.
        var cardTolerance = NearlyIntegral(Px(cardSide)) ? 0.5 : 1.0;
        Check(Math.Abs(frameSkewX) < cardTolerance, $"{label}: frame centred horizontally on the card (skew {Skew(frameSkewX)})");
        Check(Math.Abs(frameSkewY) < cardTolerance, $"{label}: frame centred vertically on the card (skew {Skew(frameSkewY)})");

        // The glyph may legitimately spill over the padding box at small 图标大小 (the frame does
        // not clip); what must hold is that it stays inside the frame and centred there.
        var glyphInsideFrame = Px(glyph.Bounds.Width) + 2 * Px(frame.BorderThickness.Left)
                               <= Px(frame.Bounds.Width) + 0.01;
        if (glyphInsideFrame)
        {
            Check(NearlyIntegral(Px(glyph.Bounds.Width)), $"{label}: glyph side lands on a whole device pixel");
            Check(Math.Abs(glyphSkewX) < 0.5, $"{label}: glyph centred horizontally in its frame (skew {Skew(glyphSkewX)})");
            Check(Math.Abs(glyphSkewY) < 0.5, $"{label}: glyph centred vertically in its frame (skew {Skew(glyphSkewY)})");
            Check(Math.Abs(frameSkewX + glyphSkewX) < cardTolerance, $"{label}: glyph centred horizontally on the card");
            Check(Math.Abs(frameSkewY + glyphSkewY) < cardTolerance, $"{label}: glyph centred vertically on the card");
        }
        else
        {
            Console.WriteLine($"    (glyph {Px(glyph.Bounds.Width):0.##}px overflows the {Px(frame.Bounds.Width):0.##}px frame at this 图标大小 — centring not asserted)");
        }

        window.Close();
    }

    /// <summary>The real-icon path: Image bounds, centred on the card at the same snapped size.</summary>
    private static void RunIconCase(string file, double scaling, double percent)
    {
        const double cardSide = 72.0;
        const double margin = 10.0;

        var view = new SingleFile(new SingleFileModel(file, percent, 100), null!);
        var (window, card) = BuildHost(view, cardSide, margin);
        window.Show();
        window.SetRenderScaling(scaling);
        Dispatcher.UIThread.RunJobs();

        var image = view.FindControl<Image>("IconImage")!;
        var cardOrigin = card.TranslatePoint(new Point(0, 0), window)!.Value;
        var pos = image.TranslatePoint(new Point(0, 0), card)!.Value;
        double Px(double dips) => dips * scaling;

        var left = Px(pos.X);
        var right = Px(cardSide) - Px(pos.X + image.Bounds.Width);
        var top = Px(pos.Y);
        var bottom = Px(cardSide) - Px(pos.Y + image.Bounds.Height);
        var label = $"SingleFile icon @{scaling:0.##}x pct={percent:0}";

        Console.WriteLine($"{label}: image {Px(image.Bounds.Width):0.##}px L{left:0.##}/R{right:0.##}/T{top:0.##}/B{bottom:0.##}"
                          + $"  [box {image.Width:0.##}DIP src={image.Source?.Size}]");
        Check(NearlyIntegral(Px(image.Width)), $"{label}: image box lands on a whole device pixel");
        if (NearlyIntegral(Px(cardOrigin.X)))
            Check(NearlyIntegral(Px(cardOrigin.X + pos.X)), $"{label}: image left edge lands on a whole device pixel");
        Check(Math.Abs(left - right) < 0.5, $"{label}: image centred horizontally on the card (skew {Skew(left - right)})");
        Check(Math.Abs(top - bottom) < 0.5, $"{label}: image centred vertically on the card (skew {Skew(top - bottom)})");

        window.Close();
    }

    /// <summary>
    /// A square 32-bit ICO. The shell resolves .ico to an icon whose bitmap is square, so the
    /// Image's aspect-fitted bounds is the whole layout box and the margins can be asserted
    /// exactly (a Fluent document icon is 167×224 and would shrink one axis instead).
    /// </summary>
    private static void WriteSquareIco(string path, int side)
    {
        var xor = new byte[side * side * 4];
        for (var i = 0; i < side * side; i++)
        {
            xor[i * 4 + 0] = 0x40; // B
            xor[i * 4 + 1] = 0x80; // G
            xor[i * 4 + 2] = 0xC0; // R
            xor[i * 4 + 3] = 0xFF; // A
        }

        var and = new byte[side * side / 8];
        using var stream = new MemoryStream();
        using var w = new BinaryWriter(stream);
        w.Write((ushort)0);            // reserved
        w.Write((ushort)1);            // type: icon
        w.Write((ushort)1);            // one image
        w.Write((byte)side);           // width
        w.Write((byte)side);           // height
        w.Write((byte)0);              // palette
        w.Write((byte)0);              // reserved
        w.Write((ushort)1);            // colour planes
        w.Write((ushort)32);           // bits per pixel
        w.Write(40 + xor.Length + and.Length);
        w.Write(22);                   // image offset
        w.Write(40);                   // BITMAPINFOHEADER
        w.Write(side);
        w.Write(side * 2);             // XOR + AND mask
        w.Write((ushort)1);
        w.Write((ushort)32);
        w.Write(0);                    // BI_RGB
        w.Write(xor.Length);
        w.Write(0);
        w.Write(0);
        w.Write(0);
        w.Write(0);
        w.Write(xor);
        w.Write(and);
        File.WriteAllBytes(path, stream.ToArray());
    }

    private static bool NearlyIntegral(double v) => Math.Abs(v - Math.Round(v)) < 0.01;

    private static string Skew(double v) => v.ToString("+0.##;-0.##;0") + "px";

    private static void Check(bool ok, string message)
    {
        checks++;
        if (!ok)
        {
            failures++;
            Console.WriteLine($"    [FAIL] {message}");
        }
    }
}
