using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace UiFoundationChecks;

/// <summary>
/// Icon <b>centring</b> checks (开发规范 §4.4).
/// <list type="bullet">
/// <item><b>The invariant.</b> Every bundled mark must be square and centred in its own bounds. A
/// <c>Path</c> stretches its geometry to those bounds and anchors them at the box's <b>top-left</b>
/// (Avalonia has no centring alignment on <c>Shape.Stretch</c>), so a 14 × 20 mark inside an 11 × 11
/// box lands 1.65 DIP left of centre — the reported 剪贴板「置顶图标不在圆形中央」. Framing the ink on
/// a square canvas (<c>GlyphGeometry.Square</c>) makes the stretch fit both axes exactly, which is
/// why the invariant is "square bounds" rather than "the drawing looks about right".</item>
/// <item><b>The measurement.</b> Each mark is rendered into a 28 DIP box and its ink bounding box is
/// measured from the pixels; the centre must sit on the box's pixel centre within 0.75 px. This
/// catches both classes of defect: a non-square canvas (offset grows with the aspect difference) and
/// an arc whose <c>Bounds</c> includes its control points (the magnifier used to be 4.5 DIP off).</item>
/// </list>
/// Headless-environment limits (asserted by humans, never faked here): the real DPI rounding of a
/// mark inside a themed button, and the optical (as opposed to geometric) centring judgement.
/// </summary>
internal static partial class Program
{
    /// <summary>Every mark the app ships in a shared catalog, with the catalog name for the log.</summary>
    private static IEnumerable<(string Name, Geometry Data)> SharedMarks()
    {
        yield return ("tool.text", Tools.Models.ToolGlyphs.Text);
        yield return ("tool.image", Tools.Models.ToolGlyphs.Image);
        yield return ("tool.files", Tools.Models.ToolGlyphs.Files);
        yield return ("tool.clipboard", Tools.Models.ToolGlyphs.Clipboard);
        yield return ("tool.pin", Tools.Models.ToolGlyphs.Pin);
        yield return ("tool.copy", Tools.Models.ToolGlyphs.Copy);
        yield return ("tool.close", Tools.Models.ToolGlyphs.Close);
        yield return ("tool.search", Tools.Models.ToolGlyphs.Search);

        yield return ("app.clock", DeskCanvas.UI.Controls.CommonGlyphs.Clock);
        yield return ("app.sun", DeskCanvas.UI.Controls.CommonGlyphs.Sun);
        yield return ("app.chart", DeskCanvas.UI.Controls.CommonGlyphs.Chart);
        yield return ("app.checkCircle", DeskCanvas.UI.Controls.CommonGlyphs.CheckCircle);
        yield return ("app.apps", DeskCanvas.UI.Controls.CommonGlyphs.Apps);
        yield return ("app.wrench", DeskCanvas.UI.Controls.CommonGlyphs.Wrench);
        yield return ("app.puzzle", DeskCanvas.UI.Controls.CommonGlyphs.Puzzle);
        yield return ("app.pencil", DeskCanvas.UI.Controls.CommonGlyphs.Pencil);
        yield return ("app.ruler", DeskCanvas.UI.Controls.CommonGlyphs.Ruler);
        yield return ("app.frame", DeskCanvas.UI.Controls.CommonGlyphs.Frame);
        yield return ("app.sparkle", DeskCanvas.UI.Controls.CommonGlyphs.Sparkle);
        yield return ("app.trash", DeskCanvas.UI.Controls.CommonGlyphs.Trash);
        yield return ("app.globe", DeskCanvas.UI.Controls.CommonGlyphs.Globe);
        yield return ("app.dice", DeskCanvas.UI.Controls.CommonGlyphs.Dice);
        yield return ("app.heartOutline", DeskCanvas.UI.Controls.CommonGlyphs.HeartOutline);
        yield return ("app.heartFilled", DeskCanvas.UI.Controls.CommonGlyphs.HeartFilled);
        yield return ("app.starOutline", DeskCanvas.UI.Controls.CommonGlyphs.StarOutline);
        yield return ("app.starFilled", DeskCanvas.UI.Controls.CommonGlyphs.StarFilled);
        yield return ("app.copy", DeskCanvas.UI.Controls.CommonGlyphs.Copy);
        yield return ("app.document", DeskCanvas.UI.Controls.CommonGlyphs.Document);
        yield return ("app.search", DeskCanvas.UI.Controls.CommonGlyphs.Search);
        yield return ("app.bolt", DeskCanvas.UI.Controls.CommonGlyphs.Bolt);
        yield return ("app.check", DeskCanvas.UI.Controls.CommonGlyphs.Check);
        yield return ("app.music", DeskCanvas.UI.Controls.CommonGlyphs.Music);
    }

    private static void CheckGlyphCentring()
    {
        Console.WriteLine("--- icon centring: square canvas + pixel measurement (开发规范 §4.4) ---");

        // ---- 1. The invariant: square bounds, centre on the canvas centre ------------------
        foreach (var (name, data) in SharedMarks())
        {
            var bounds = data.Bounds;
            Check(bounds.Width > 0 && DeskCanvas.UI.Controls.GlyphGeometry.IsSquare(data),
                $"{name}: square bounds ({bounds.Width:F2}x{bounds.Height:F2})");
        }

        // ---- 2. The pixels: the ink lands on the box centre -------------------------------
        // A 28 DIP icon-button box, the size every .dc-icon-button / .icon-btn uses.
        const double box = 28;
        var expected = (box - 1) / 2;   // pixel centres: 0..27 → 13.5
        var offCentre = new List<string>();

        foreach (var (name, data) in SharedMarks())
        {
            var (cx, cy, w, h) = MeasureInk(data, box);
            if (w <= 0 || h <= 0)
            {
                offCentre.Add($"{name}: drew nothing");
                continue;
            }

            if (Math.Abs(cx - expected) > 0.75 || Math.Abs(cy - expected) > 0.75)
                offCentre.Add($"{name}: ink centre ({cx:F1},{cy:F1}) vs button centre ({expected:F1},{expected:F1})");
        }

        Check(offCentre.Count == 0,
            offCentre.Count == 0
                ? $"every mark's ink is centred in a {box:F0} DIP button box"
                : "marks off centre: " + string.Join("; ", offCentre));

        // ---- 3. The stroked arrow (picture alignment) centres too -------------------------
        // It is stroked, so it is measured with a stroke — the fill measurement above cannot see it.
        var (ax, ay, aw, ah) = MeasureInk(DeskCanvas.UI.Controls.CommonGlyphs.ArrowNorthEast, box, stroked: true);
        Check(aw > 0 && ah > 0 && Math.Abs(ax - expected) <= 0.75 && Math.Abs(ay - expected) <= 0.75,
            $"app.arrow (stroked) is centred in a {box:F0} DIP box ({(aw > 0 ? $"{ax:F1},{ay:F1}" : "nothing drawn")})");
    }

    /// <summary>Render one mark into a square box and return the ink's bounding box centre and size.</summary>
    private static (double Cx, double Cy, int W, int H) MeasureInk(Geometry data, double box, bool stroked = false)
    {
        var path = new Avalonia.Controls.Shapes.Path
        {
            Data = data,
            Width = box,
            Height = box,
            Stretch = Stretch.Uniform,
        };
        if (stroked)
        {
            path.Stroke = Brushes.White;
            path.StrokeThickness = 1.6;
            path.StrokeLineCap = PenLineCap.Round;
        }
        else
        {
            path.Fill = Brushes.White;
        }

        path.Measure(new Size(box, box));
        path.Arrange(new Rect(0, 0, box, box));
        path.UpdateLayout();

        var size = (int)box;
        var bitmap = new RenderTargetBitmap(new PixelSize(size, size), new Vector(96, 96));
        bitmap.Render(path);

        var pixels = new int[size * size];
        var handle = System.Runtime.InteropServices.GCHandle.Alloc(
            pixels, System.Runtime.InteropServices.GCHandleType.Pinned);
        try
        {
            bitmap.CopyPixels(new PixelRect(0, 0, size, size), handle.AddrOfPinnedObject(),
                pixels.Length * 4, size * 4);
        }
        finally
        {
            handle.Free();
        }

        int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1;
        for (var y = 0; y < size; y++)
        for (var x = 0; x < size; x++)
        {
            if (((pixels[y * size + x] >> 24) & 255) < 40) continue;
            minX = Math.Min(minX, x);
            maxX = Math.Max(maxX, x);
            minY = Math.Min(minY, y);
            maxY = Math.Max(maxY, y);
        }

        return maxX < 0 ? (0, 0, 0, 0) : ((minX + maxX) / 2.0, (minY + maxY) / 2.0, maxX - minX + 1, maxY - minY + 1);
    }
}
