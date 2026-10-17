using System;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Media;
using Avalonia.Threading;
using Clock.Models;
using Clock.Views;
using DeskCanvas.Core.Models.Settings;
using DeskCanvas.Services;
using DeskCanvas.Views.Controls;

namespace UiFoundationChecks;

/// <summary>
/// Glass sampling-origin checks (RC2, 3.8.3): every wallpaper-sampling consumer resolves its
/// origin through the transform-free layout walk (<see cref="GlassSamplingOrigin.Resolve"/>) by
/// default, so an ancestor RenderTransform — the panel shell's open/close zoom, a widget's content
/// scale — can never shift the wallpaper region a card samples.
/// <list type="bullet">
/// <item><b>Default flip</b>: a bare LiquidGlassSurface ignores ancestor transforms without any
/// explicit property set (the shell's old opt-in is now the pipeline-wide default; the raw
/// <c>PointToScreen</c> path remains available by setting the property to false).</item>
/// <item><b>Walk = raw when nothing is transformed</b>: with no ancestor transform the walk is
/// exactly the old path, so flipping the default cannot disturb a position that was already
/// correct.</item>
/// <item><b>Content scale</b>: a surface nested under a scaled ancestor samples the AT-REST origin
/// while the raw path demonstrably shifts — the position-dependent 采样错位 regression.</item>
/// <item><b>Frameless clock</b>: the clock's glyph frame goes through the same shared geometry
/// (<see cref="GlassSamplingOrigin.FrameDesktopGeometry"/>) and inherits the same
/// transform-immunity; its numbers stay the physical-pixel values it always computed
/// (originScale 1, no quality multiplier).</item>
/// </list>
/// </summary>
internal static partial class Program
{
    private static void CheckGlassSamplingOrigin()
    {
        Console.WriteLine("--- glass sampling origin: layout walk is the default for every consumer ---");

        CheckWalkEqualsRawWithoutTransforms();
        CheckDefaultIgnoresAncestorTransform();
        CheckContentScaleCannotShiftTheOrigin();
        CheckFramelessClockUsesSharedOrigin();
    }

    private static void CheckDefaultIgnoresAncestorTransform()
    {
        var surface = new LiquidGlassSurface();
        Check(surface.IgnoresAncestorTransform,
            "a bare LiquidGlassSurface ignores ancestor render transforms by default (3.8.3 flipped the opt-in)");
    }

    private static PixelPoint InvokeWalk(LiquidGlassSurface surface)
    {
        var walk = typeof(LiquidGlassSurface).GetMethod(
            "DesktopPositionIgnoringRenderTransforms", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("DesktopPositionIgnoringRenderTransforms not found");
        return walk.Invoke(surface, Array.Empty<object?>()) is PixelPoint p ? p : default;
    }

    private static void CheckWalkEqualsRawWithoutTransforms()
    {
        var host = new Window { Width = 360, Height = 240, WindowDecorations = WindowDecorations.None };
        var surface = new LiquidGlassSurface { Width = 120, Height = 80 };
        host.Content = surface;
        host.Show();
        Dispatcher.UIThread.RunJobs();

        Check(InvokeWalk(surface) == surface.PointToScreen(default),
            "with no ancestor transform the walk equals the raw PointToScreen (the flip is a no-op there)");

        host.Close();
        Dispatcher.UIThread.RunJobs();
    }

    private static void CheckContentScaleCannotShiftTheOrigin()
    {
        // Mirrors Widget.Scale(): a ScaleTransform on the content presenter, centered, active
        // whenever ContentScale ≠ 1. The widget-shell glass is a SIBLING of that presenter and was
        // never affected; surfaces inside the scaled content (plugin-provided glass) and the
        // frameless clock were — with an offset proportional to the distance from the scale
        // anchor, hence "some positions off, others fine".
        var host = new Window { Width = 400, Height = 300, WindowDecorations = WindowDecorations.None };
        var presenter = new ContentPresenter { Width = 200, Height = 150 };
        var nested = new LiquidGlassSurface { Width = 100, Height = 60 };
        presenter.Content = nested;
        host.Content = presenter;
        host.Show();
        Dispatcher.UIThread.RunJobs();

        var atRest = InvokeWalk(nested);

        presenter.RenderTransform = new ScaleTransform(0.7, 0.7);
        presenter.RenderTransformOrigin = new RelativePoint(0.5, 0.5, RelativeUnit.Relative);
        Dispatcher.UIThread.RunJobs();

        Check(nested.PointToScreen(default) != atRest,
            "sanity: the raw PointToScreen really shifts under the 0.7 content scale");
        Check(InvokeWalk(nested) == atRest,
            "under a scaled ancestor the walk stays at the at-rest origin (the position-dependent 采样错位 fix)");

        presenter.RenderTransform = null;
        Dispatcher.UIThread.RunJobs();
        Check(InvokeWalk(nested) == atRest && nested.PointToScreen(default) == atRest,
            "at rest the walk and the raw path agree again");

        host.Close();
        Dispatcher.UIThread.RunJobs();
    }

    private static void CheckFramelessClockUsesSharedOrigin()
    {
        // The clock's frame builder must go through the shared geometry: the sampled origin equals
        // GlassSamplingOrigin.FrameDesktopGeometry's answer exactly, and a scaled ancestor (the
        // widget content scale) cannot move it — while the raw path demonstrably would have.
        // SurfaceOverride = Solid keeps the render pipeline out of the headless run; this check is
        // about the frame's geometry, not its pixels.
        var host = new Window { Width = 420, Height = 200, WindowDecorations = WindowDecorations.None };
        var clock = new FramelessDigital(new FramelessClockModel(SurfaceOverride: SurfaceStyle.Acrylic))
        {
            Width = 380,
            Height = 160,
        };
        var wrapper = new ContentPresenter { Content = clock };
        host.Content = wrapper;
        host.Show();
        Dispatcher.UIThread.RunJobs();

        var prepare = typeof(FramelessDigital).GetMethod(
            "PrepareGlassRenderInputs", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("FramelessDigital.PrepareGlassRenderInputs not found");

        (float X, float Y) Origin()
        {
            var result = prepare.Invoke(clock, new object[] { DateTime.Now });
            var frame = (LiquidGlassRenderer.Frame)(result!.GetType().GetField("Item1")!.GetValue(result)!);
            return (frame.DesktopX, frame.DesktopY);
        }

        var restOrigin = Origin();
        var expected = GlassSamplingOrigin.FrameDesktopGeometry(host, clock, 1.0);
        Check(Math.Abs(restOrigin.X - expected.DesktopX) < 0.01 && Math.Abs(restOrigin.Y - expected.DesktopY) < 0.01,
            "the clock's frame origin equals the shared geometry helper (one source of truth with the surface)");

        var restRaw = clock.PointToScreen(default);
        wrapper.RenderTransform = new ScaleTransform(0.8, 0.8);
        wrapper.RenderTransformOrigin = new RelativePoint(0.5, 0.5, RelativeUnit.Relative);
        Dispatcher.UIThread.RunJobs();

        Check(clock.PointToScreen(default) != restRaw,
            "sanity: the raw PointToScreen really shifts under the 0.8 wrapper scale");
        Check(Origin() == restOrigin,
            "a scaled ancestor cannot shift the clock's sampled origin (the clock inherited the fix)");

        wrapper.RenderTransform = null;
        Dispatcher.UIThread.RunJobs();
        Check(Origin() == restOrigin, "at rest the clock's origin is unchanged again");

        host.Close();
        Dispatcher.UIThread.RunJobs();
    }
}
