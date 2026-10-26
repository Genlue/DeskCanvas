using DeskCanvas.Services.GlassCapture;

/// <summary>
/// The sampling <b>geometry</b> the glass depends on — the part of the capture flow that is pure
/// arithmetic and can therefore be pinned without a desktop.
/// <para>
/// Three things are checked, and each one is a way the material has gone wrong in the past:
/// </para>
/// <list type="number">
///   <item><description><b>Host selection.</b> The wallpaper surface is chosen structurally (the
///   topmost visible window below the desktop-icon host that covers the sampling area), not from a
///   fixed class list. Getting this wrong is what makes the glass sample the wrong object — a
///   wallpaper-engine preview, or the icon host with the icons baked in.</description></item>
///   <item><description><b>Region union.</b> The sampler captures the union of the glass rectangles
///   plus their blur margin, snapped to a coarse grid. The margin is not decoration: the shader
///   refracts up to <c>3σ</c> outside the card, and a patch that stopped at the card edge clamps to
///   it, which reads as a smeared border.</description></item>
///   <item><description><b>Rectangle algebra.</b> Every conversion between virtual-desktop and
///   monitor-local pixels goes through it, so an inverted or off-by-one intersection would silently
///   shift the sampled neighbourhood.</description></item>
/// </list>
/// </summary>
internal static class GlassSamplingCheck
{
    private static int failures;

    public static int Run()
    {
        failures = 0;
        Console.WriteLine("=== 采样几何 (host band / region union / rect algebra) ===");

        RectAlgebra();
        HostSelection();
        RegionUnion();

        Console.WriteLine(failures == 0 ? "glass sampling check passed" : $"{failures} SAMPLING GEOMETRY CHECK(S) FAILED");
        return failures;
    }

    private static void RectAlgebra()
    {
        var a = new CaptureRect(100, 100, 400, 300);
        var b = new CaptureRect(350, 250, 700, 500);

        Check(a.Intersect(b) == new CaptureRect(350, 250, 400, 300), "intersection is the overlap");
        Check(a.Intersect(new CaptureRect(500, 500, 600, 600)).IsEmpty, "a disjoint intersection is empty");
        Check(a.Union(b) == new CaptureRect(100, 100, 700, 500), "union is the bounding box");
        Check(a.Union(default) == a && default(CaptureRect).Union(a) == a, "union with empty is the identity");
        Check(a.Inflate(10, 20) == new CaptureRect(90, 80, 410, 320), "inflate grows both axes independently");
        Check(a.Offset(-100, -100) == new CaptureRect(0, 0, 300, 200), "offset translates");
        Check(a.Contains(a) && a.Contains(new CaptureRect(150, 150, 200, 200)), "contains accepts inner rects");
        Check(!a.Contains(b), "contains rejects overlapping rects");
        Check(a.Contains(default) == false, "contains rejects the empty rect");

        // Coverage is what the host threshold is expressed in; a 50%-covered area must not pass 0.6.
        var interest = new CaptureRect(0, 0, 100, 100);
        Check(Math.Abs(new CaptureRect(0, 0, 100, 50).CoverageOf(interest) - 0.5) < 1e-9,
            "coverage is area-relative (half-covered = 0.5)");
        Check(Math.Abs(new CaptureRect(0, 0, 100, 100).CoverageOf(interest) - 1.0) < 1e-9,
            "a full cover is 1.0");
        Check(new CaptureRect(0, 0, 100, 100).CoverageOf(default) == 0.0,
            "an empty interest covers nothing rather than dividing by zero");
    }

    private static void HostSelection()
    {
        var interest = new CaptureRect(0, 0, 2560, 1440);
        var full = interest;
        var none = IntPtr.Zero;

        // A dynamic wallpaper engine: its window sits in the band below the icons and covers the
        // area, so it must win over Progman even though Progman also covers it.
        var engine = new WallpaperCandidate(new IntPtr(2), full, Visible: true, WallpaperClass: true,
            HostsDesktopIcons: false, IsProgman: false, BelowIconsHost: true);
        var progman = new WallpaperCandidate(new IntPtr(1), full, Visible: true, WallpaperClass: true,
            HostsDesktopIcons: true, IsProgman: true, BelowIconsHost: false);
        Check(WallpaperHostDiscovery.SelectHost(new[] { engine, progman }, interest) == 0,
            "the topmost visible band window wins over Progman");

        // A wallpaper-engine *preview* window (small, in the band) must not be mistaken for the
        // desktop surface — the coverage threshold is what rejects it.
        var preview = engine with { Window = new IntPtr(3), Bounds = new CaptureRect(0, 0, 320, 180) };
        Check(WallpaperHostDiscovery.SelectHost(new[] { preview, progman }, interest) == 1,
            "a band window that does not cover the area falls through to Progman");

        // Hidden or cloaked windows are not the wallpaper, whatever their class.
        Check(WallpaperHostDiscovery.SelectHost(new[] { engine with { Visible = false }, progman }, interest) == 1,
            "an invisible band window is skipped");
        Check(WallpaperHostDiscovery.SelectHost(new[] { progman with { Visible = false } }, interest) == -1,
            "no visible candidate selects nothing");

        // The icon host itself is never a band surface: its frame has the icons drawn into it.
        var iconHost = engine with { HostsDesktopIcons = true };
        Check(WallpaperHostDiscovery.SelectHost(new[] { iconHost }, interest) == -1,
            "an icon-hosting window is not a band surface");

        Check(WallpaperHostDiscovery.SelectHost(Array.Empty<WallpaperCandidate>(), interest) == -1,
            "an empty candidate list selects nothing");
        Check(WallpaperHostDiscovery.SelectHost(new[] { progman }, default) == -1,
            "an empty interest area selects nothing");
    }

    private static void RegionUnion()
    {
        var virtualScreen = GlassInterest.VirtualScreen;

        GlassInterest.Reset();
        Check(GlassInterest.SamplingRegion() == virtualScreen,
            "with nothing reported the sampler falls back to the whole virtual screen");

        // Two cards far apart: the union spans both, and each gets its own blur margin.
        var left = new CaptureRect(100, 100, 400, 300);
        var right = new CaptureRect(1200, 700, 1500, 900);
        GlassInterest.Report("left", left, 64, screenSampling: false, fullDesktop: false);
        GlassInterest.Report("right", right, 64, screenSampling: false, fullDesktop: false);

        var union = GlassInterest.SamplingRegion();
        Check(union.Contains(left.Inflate(64, 64)) && union.Contains(right.Inflate(64, 64)),
            "the sampling region covers both cards plus their blur margin");
        Check(union.Left % 32 == 0 && union.Top % 32 == 0 && union.Right % 32 == 0 && union.Bottom % 32 == 0,
            $"the sampling region is snapped to a 32 px grid ({union})");
        Check(union.Area < virtualScreen.Area,
            $"two cards sample less than the whole screen ({union.Area} < {virtualScreen.Area} px)");

        // A preview card draws a synthetic desktop, so it must force the whole screen even though a
        // real card is also reported.
        GlassInterest.Report("preview", new CaptureRect(70, 40, 190, 130), 48, screenSampling: false, fullDesktop: true);
        Check(GlassInterest.SamplingRegion() == virtualScreen,
            "a surface with a synthetic desktop forces the whole virtual screen");
        GlassInterest.Remove("preview");

        // The screen bucket is separate: a wallpaper-sampling card must not widen the launcher's
        // sampling area.
        Check(GlassInterest.ScreenSamplingRegion() == virtualScreen,
            "the screen bucket ignores wallpaper-sampling surfaces");
        GlassInterest.Report("panel", new CaptureRect(600, 300, 1600, 900), 64, screenSampling: true, fullDesktop: false);
        var screenRegion = GlassInterest.ScreenSamplingRegion();
        Check(screenRegion.Contains(new CaptureRect(600, 300, 1600, 900)) && screenRegion.Area < virtualScreen.Area,
            $"the screen bucket samples the panel's neighbourhood ({screenRegion})");

        GlassInterest.Remove("left");
        GlassInterest.Remove("right");
        GlassInterest.Remove("panel");
        Check(GlassInterest.Count == 0 && GlassInterest.SamplingRegion() == virtualScreen,
            "removing every surface restores the whole-screen fallback");
    }

    private static void Check(bool condition, string label)
    {
        if (!condition)
        {
            failures++;
            Console.WriteLine($"  [FAIL] {label}");
            return;
        }

        Console.WriteLine($"  PASS: {label}");
    }
}
