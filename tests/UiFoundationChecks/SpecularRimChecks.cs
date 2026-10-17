using System;
using System.IO;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using DeskCanvas.Views.Controls;
using SkiaSharp;

namespace UiFoundationChecks;

/// <summary>
/// 高光 rim shared by the non-sampled materials (W12): the <see cref="SpecularRim"/> math must
/// match the liquid-glass shader's <c>|dot(edgeNormal, lightDirection)|</c> block, and the
/// <see cref="SpecularHighlight"/> control must actually paint that rim — the old 描边 ring is
/// gone, so this is now the only outline highlight 毛玻璃 / 全彩 have.
/// </summary>
internal static partial class Program
{
    private static void CheckSpecularRim()
    {
        Console.WriteLine("--- 高光 specular rim (毛玻璃 / 全彩) ---");

        // The shader's light convention: 90° = +Y (down), so at 90° the top and bottom edges
        // are fully lit and the left/right edges are dark.
        Check(Near(SpecularRim.Intensity(0, -1, 90), 1.0), "90°: top edge is fully lit");
        Check(Near(SpecularRim.Intensity(0, 1, 90), 1.0), "90°: bottom edge is fully lit");
        Check(Near(SpecularRim.Intensity(-1, 0, 90), 0.0), "90°: left edge is dark");
        Check(Near(SpecularRim.Intensity(1, 0, 90), 0.0), "90°: right edge is dark");

        // 45° is the historic liquid-glass light: the top-left and bottom-right corners are the
        // bright ends of the double rim, the other two corners are dark.
        const double diagonal = 0.7071067811865476;
        Check(Near(SpecularRim.Intensity(-diagonal, -diagonal, 45), 1.0), "45°: top-left corner is fully lit");
        Check(Near(SpecularRim.Intensity(diagonal, diagonal, 45), 1.0), "45°: bottom-right corner is fully lit");
        Check(Near(SpecularRim.Intensity(diagonal, -diagonal, 45), 0.0), "45°: top-right corner is dark");
        Check(Near(SpecularRim.Intensity(-diagonal, diagonal, 45), 0.0), "45°: bottom-left corner is dark");

        Check(Near(SpecularRim.Intensity(-0.5, -0.5, 0), SpecularRim.Intensity(-0.5, -0.5, 360)),
            "0° and 360° are the same light direction");
        Check(Near(SpecularRim.Intensity(0, -1, 90, falloff: 2), 1.0), "the falloff exponent keeps full brightness on-axis");

        // Real paint: a 100×100 card, radius 10, opaque black behind the rim, so every pixel the
        // rim touches is measurable. Angle 90 lights the top edge and leaves the left edge black.
        using var vertical = RenderRim(strength: 100, angle: 90);
        Check(vertical != null, "the rim control renders a bitmap");
        if (vertical == null) return;

        Check(Brightness(vertical, 50, 0) > 180, "90°: the top edge is painted white");
        Check(Brightness(vertical, 0, 50) < 20, "90°: the left edge stays unpainted");
        Check(Brightness(vertical, 50, 50) < 5, "the card interior stays untouched");
        Check(Brightness(vertical, 0, 0) < 5, "the rounded corner clips the rim (no paint outside the card)");

        using var horizontal = RenderRim(strength: 100, angle: 0);
        Check(horizontal != null && Brightness(horizontal, 0, 50) > 180,
            "0°: the left edge is painted white instead");
        Check(horizontal != null && Brightness(horizontal, 50, 0) < 20,
            "0°: the top edge is no longer lit (the rim follows the angle)");

        using var half = RenderRim(strength: 50, angle: 90);
        Check(half != null && Brightness(half, 50, 0) is > 60 and < 190,
            "50% strength paints a mid-alpha rim (not on/off)");

        using var off = RenderRim(strength: 0, angle: 90);
        Check(off != null && Brightness(off, 50, 0) < 5, "0% strength paints nothing");

        // The removed 描边 fields must stay out of the theme model — a settings file that still
        // carries them is simply ignored instead of resurrecting the old ring.
        Check(typeof(DeskCanvas.Core.Models.Settings.Theme).GetProperty("OutlineWidth") == null
              && typeof(DeskCanvas.Core.Models.Settings.Theme).GetProperty("OutlineColor") == null,
            "Theme no longer exposes OutlineColor / OutlineWidth");

        // Old appSettings.json files carry both the removed inputs and the computed
        // EffectiveOutlineColor; loading must keep working (default JSON options ignore them).
        var legacy = System.Text.Json.JsonSerializer.Deserialize<DeskCanvas.Core.Models.Settings.Theme>(
            """{"DarkMode":false,"AccentColor":null,"OpacityLevel":0.4,"Monochrome":true,"UseNativeFrame":false,"FontFamily":"Inter","Surface":2,"OutlineColor":"#80FFFFFF","OutlineWidth":2,"SolidBackgroundDark":"#2E2E2E","SolidBackgroundLight":"#FFFFFF","EffectiveOutlineColor":"#80FFFFFF"}""");
        Check(legacy != null,
            "an old settings file with 描边 fields still deserializes (the fields are ignored)");
        Check(legacy != null && legacy.EffectiveSurface == DeskCanvas.Core.Models.Settings.SurfaceStyle.LiquidGlass,
            "the ignored 描边 fields do not shift the material or the remaining theme fields");
    }

    private static bool Near(double actual, double expected) => Math.Abs(actual - expected) < 1e-6;

    private static SKBitmap? RenderRim(double strength, double angle)
    {
        const int side = 100;
        using var target = new RenderTargetBitmap(new PixelSize(side, side), new Vector(96, 96));
        using (var context = target.CreateDrawingContext(false))
        {
            context.DrawRectangle(Brushes.Black, null, new Rect(0, 0, side, side));
            var rim = new SpecularHighlight
            {
                Width = side,
                Height = side,
                CornerRadius = new CornerRadius(10),
                Strength = strength,
                Angle = angle
            };
            rim.Measure(new Size(side, side));
            rim.Arrange(new Rect(0, 0, side, side));
            rim.Render(context);
        }

        using var stream = new MemoryStream();
        target.Save(stream);
        return SKBitmap.Decode(stream.ToArray());
    }

    /// <summary>Perceived brightness of one pixel (0-255), the average of its RGB channels.</summary>
    private static double Brightness(SKBitmap bitmap, int x, int y)
    {
        var color = bitmap.GetPixel(x, y);
        return (color.Red + color.Green + color.Blue) / 3.0;
    }
}
