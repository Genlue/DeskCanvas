using System;
using System.Threading;
using System.Threading.Tasks;
using SkiaSharp;
using DeskCanvas.Core.Models.Settings;

namespace DeskCanvas.Services;

/// <summary>
/// Renders a cached static material, never widget content.
///
/// This is a lens, not a blur panel — the defining difference between Apple's
/// Liquid Glass and plain frosted glassmorphism. The rounded-rectangle distance
/// field gives a continuous outward normal and an inward depth; the depth drives
/// three overlapping bands:
///
///   • the <b>lens ring</b>  — inward displacement over <c>lensW</c> px at the rim,
///     with a squared falloff, so the backdrop visibly bends and magnifies around
///     the edge while the centre stays clear;
///   • the <b>edge reflection</b> — a much wider, softer band that adds three times
///     the displacement, so content near the rim is dragged across and folds back
///     on itself (the inverted echo iOS shows at the top and bottom of a pill);
///   • the <b>edge guard</b> — the outermost few px taper the total displacement to
///     zero, which both calms sub-pixel noise and creates the fold, because the
///     displacement then rises faster than the pixel position.
///
/// On top: a crisp, lighting-independent rim line (the 1 px glass edge iOS draws
/// around every element), vibrancy (saturation lift) plus an adaptive frost that
/// lightens dark backdrops, a very faint prismatic split at the rim, a flat-pane
/// normal for the specular sheen, and a soft inner shadow on the side away from
/// the light. Parameters follow a replica measured against iOS 26 (see
/// <c>docs</c> / the 项目解构报告 §18).
/// </summary>
public static class LiquidGlassRenderer
{
    /// <summary>Builds a card-local backdrop bitmap for the GPU shader path.</summary>
    public static SKBitmap? CreateBackdrop(Frame frame, WallpaperSnapshot wallpaper)
    {
        try
        {
            var source = wallpaper.CachedBitmap;
            if (source == null && wallpaper.ImageBytes != null) source = SKBitmap.Decode(wallpaper.ImageBytes);
            if (source == null) return null;
            var sigma = (float)frame.Theme.EffectiveLiquidGlass.Blur * frame.Scale / 8f;
            var radius = Math.Clamp(frame.Radius * frame.Scale, 0f, Math.Min(frame.Width, frame.Height) / 2f);
            var pad = (int)Math.Ceiling(Math.Max(sigma * 3f, 16f));
            using var surface = SKSurface.Create(new SKImageInfo(frame.Width + 2 * pad, frame.Height + 2 * pad));
            surface.Canvas.Clear(wallpaper.Background);
            using var filter = sigma > 0 ? SKImageFilter.CreateBlur(sigma, sigma, SKShaderTileMode.Clamp) : null;
            using var paint = new SKPaint { IsAntialias = true, FilterQuality = SKFilterQuality.High, ImageFilter = filter };
            surface.Canvas.Save();
            surface.Canvas.Translate(pad, pad);
            DrawWallpaper(surface.Canvas, source, paint, frame, wallpaper);
            surface.Canvas.Restore();
            using var image = surface.Snapshot();
            var result = new SKBitmap(new SKImageInfo(frame.Width, frame.Height, SKColorType.Bgra8888, SKAlphaType.Premul));
            image.ReadPixels(result.Info, result.GetPixels(), result.RowBytes, pad, pad);
            result.SetImmutable();
            if (!ReferenceEquals(source, wallpaper.CachedBitmap)) source.Dispose();
            return result;
        }
        catch { return null; }
    }
    public record Frame(int Width, int Height, float Scale, float Radius,
        float DesktopX, float DesktopY, float DesktopWidth, float DesktopHeight,
        float ScreenX, float ScreenY, float ScreenWidth, float ScreenHeight,
        Theme Theme, bool Dark, bool SettingsSurface = false, float PixelScale = 1,
        int Columns = 0, int Rows = 0, float GlyphOpacity = 1f);

    /// <summary>Rim displacement in DIPs at refraction = 100%. Prominent optical lens magnification.</summary>
    internal const float LensDips = 32f;

    /// <summary>Crisp rim-line width in DIPs. Modern iOS distinct 1.6 dp glass stroke.</summary>
    private const float RimLineDips = 1.6f;

    /// <summary>Vibrancy: how much the glass boosts the backdrop's chroma. Shared with the GPU path.</summary>
    internal const float Saturation = 1.20f;

    /// <summary>Adaptive frost: floor and lift to keep dark backdrops legible without milky veil.</summary>
    internal const float FrostFloor = 0.015f;
    internal const float FrostLift = 0.07f;
    internal const float FrostCap = 0.09f;

    /// <summary>Highlight slider value that reproduces the measured iOS material.</summary>
    internal const double HighlightReference = 65.0;

    /// <summary>
    /// Optics scaling parameters tailored for compact widget sizes (1x1 and 1xN/Nx1 strips).
    /// </summary>
    public record AdaptiveOptics(
        float EdgeScale,
        float ShiftScale,
        float SpreadMult,
        float InnerMult,
        float RimDips,
        float MaxLensFrac,
        float RimBaseLight,
        float RimDirLight,
        float RimLightScale);

    /// <summary>
    /// Evaluates adaptive size tier and scaling factors for compact widgets.
    /// In 1x1 widgets (or <= 110 DIP squares) and 1-grid strips (1xN or Nx1), the edge refraction
    /// width, lens displacement shift, and specular spread are proportionally moderated so that
    /// the central area remains clear, calm, and readable without aggressive optical warping.
    /// </summary>
    public static AdaptiveOptics GetAdaptiveOptics(Frame frame, float minSideDip, float maxSideDip)
    {
        // 1. 1x1 small widget (single cell tile, e.g. weather temp, single dial, folder shortcut, or <= 110 DIP square)
        bool is1x1 = (frame.Columns == 1 && frame.Rows == 1) || (frame.Columns == 0 && minSideDip <= 110f && maxSideDip <= 115f);
        if (is1x1)
        {
            return new AdaptiveOptics(
                EdgeScale: 0.58f,
                ShiftScale: 0.68f,
                SpreadMult: 1.40f,
                InnerMult: 1.30f,
                RimDips: 1.20f,
                MaxLensFrac: 0.28f,
                RimBaseLight: 0.28f,
                RimDirLight: 0.62f,
                RimLightScale: 1.10f);
        }

        // 2. 1-grid strip widget (1xN or Nx1, e.g. 2x1, 3x1, 4x1 search bar, 1x2, 1x4, or short edge <= 130 DIP)
        bool is1Strip = (frame.Columns == 1 || frame.Rows == 1) || (frame.Columns == 0 && minSideDip <= 130f);
        if (is1Strip)
        {
            return new AdaptiveOptics(
                EdgeScale: 0.75f,
                ShiftScale: 0.82f,
                SpreadMult: 1.60f,
                InnerMult: 1.45f,
                RimDips: 1.40f,
                MaxLensFrac: 0.35f,
                RimBaseLight: 0.29f,
                RimDirLight: 0.66f,
                RimLightScale: 1.18f);
        }

        // 3. Standard and large cards (2x2, 4x2, 4x4, etc.): 100% full scale
        return new AdaptiveOptics(
            EdgeScale: 1.0f,
            ShiftScale: 1.0f,
            SpreadMult: 1.80f,
            InnerMult: 1.60f,
            RimDips: 1.60f,
            MaxLensFrac: 0.45f,
            RimBaseLight: 0.30f,
            RimDirLight: 0.70f,
            RimLightScale: 1.25f);
    }

    /// <summary>Render one background to PNG. Safe on worker threads.</summary>
    public static byte[] Render(Frame frame, WallpaperSnapshot wallpaper)
    {
        using var bitmap = RenderBitmap(frame, wallpaper);
        if (bitmap == null) return [];
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    /// <summary>
    /// Render one background. Safe on worker threads; the <b>caller owns</b> the result.
    /// <para>
    /// The glass surface draws this bitmap straight onto the canvas through the Skia lease, so the
    /// CPU material no longer pays a PNG encode on the worker and a decode on the render thread for
    /// every frame. <see cref="Render"/> keeps the encoded form for the offline comparisons.
    /// </para>
    /// </summary>
    public static SKBitmap? RenderBitmap(Frame frame, WallpaperSnapshot wallpaper)
    {
        var optics = frame.Theme.EffectiveLiquidGlass;
        var scale = frame.Scale;
        var width = frame.Width;
        var height = frame.Height;
        var sigma = (float)optics.Blur * scale / 8f;
        var radius = Math.Clamp(frame.Radius * scale, 0f, Math.Min(width, height) / 2f);

        // Adaptive optics scaling for compact 1x1 widgets and 1-grid strips (1xN or Nx1)
        var widthDip = width / scale;
        var heightDip = height / scale;
        var minSideDip = Math.Min(widthDip, heightDip);
        var maxSideDip = Math.Max(widthDip, heightDip);
        var adaptive = GetAdaptiveOptics(frame, minSideDip, maxSideDip);

        // In iOS, the lens is strictly an edge meniscus/bezel. The center of the glass
        // is 100% flat and crystal-clear (zero displacement, zero X-crease).
        var maxLensWidth = Math.Max(0.5f, Math.Min(width, height) * adaptive.MaxLensFrac);
        var minLensWidth = Math.Min(2.5f * scale, maxLensWidth);
        // EdgeWidth 0 = no lens ring at all: the material then only diffuses, dyes and
        // lights the rim. Everything downstream has to cope with a zero-width band.
        var lensWidth = optics.EdgeWidth <= 0
            ? 0f
            : Math.Clamp((float)optics.EdgeWidth * scale * adaptive.EdgeScale, minLensWidth, maxLensWidth);
        var lensShift = (float)(optics.Refraction / 100.0) * LensDips * scale * adaptive.ShiftScale;
        var rimLineWidth = MathF.Max(adaptive.RimDips * scale, 0.65f);
        var dispStrength = (float)(optics.Dispersion / 100.0);

        // ---- 柔光玻璃 (soft glow glass) ------------------------------------------
        // Same optical core as 液态玻璃 — the BevelField normals, the vibrancy and the
        // light model are untouched — but tuned for a gentle, luminous look. This is
        // where the Android library's strengths are adopted without giving up the
        // sharper local model: a wide, shallow lens instead of a meniscus ring, a
        // diffused halo instead of a hairline highlight, and an optional seven-tap
        // spectrum dispersion. With Glow/Spectrum at 0 and soft == false the LiquidGlass
        // path below is bit-for-bit what it always was.
        var soft = frame.Theme.IsSoftGlow;
        var glowStrength = soft ? (float)Math.Clamp(optics.Glow, 0, 100) / 100f : 0f;
        var spectrumStrength = soft ? (float)Math.Clamp(optics.Spectrum, 0, 100) / 100f : 0f;
        var rimWidth = soft ? rimLineWidth * 7f : rimLineWidth;
        if (soft)
        {
            // Wide and shallow: 1.55x the refraction band at less than half the
            // displacement, so the backdrop is pulled gently instead of showing a
            // visible lens ring around the edge. (With the lens switched off entirely
            // there is nothing to widen.)
            if (lensWidth > 0f) lensWidth = Math.Clamp(lensWidth * 1.55f, minLensWidth, maxLensWidth);
            lensShift *= 0.45f;
        }

        // 染色扩散: how far the soft material's edge dye reaches inwards. It scales the dye
        // band's width and the interior (ambient) share of the aura; at 0 the colour stays in
        // the outermost band and nothing washes over the content.
        var dyeSpread = soft ? (float)Math.Clamp(optics.DyeSpread, 0, 100) / 100f : 0f;

        // The dye band of 柔光玻璃 is deliberately wider than the lens: the colour is a soft
        // wash along the rim, not a thin dyed line — and it must survive EdgeWidth = 0.
        var dyeBandFraction = LiquidGlassSettings.MinDyeBandFraction
                              + (LiquidGlassSettings.MaxDyeBandFraction - LiquidGlassSettings.MinDyeBandFraction) * dyeSpread;
        var auraWidth = soft ? Math.Max(lensWidth, minSideDip * (float)dyeBandFraction) : lensWidth;
        var invLens = lensWidth > 0f ? 1f / lensWidth : 0f;
        var invAura = auraWidth > 0f ? 1f / auraWidth : 0f;

        var field = new BevelField(width, height, radius);
        var pad = (int)Math.Ceiling(Math.Max(sigma * 3f, Math.Max(lensWidth + 8f, lensShift * 1.3f + 16f)));
        var info = new SKImageInfo(width + 2 * pad, height + 2 * pad);
        using var backdrop = SKSurface.Create(info);
        var canvas = backdrop.Canvas;
        canvas.Clear(wallpaper.Background);
        SKBitmap? image = wallpaper.CachedBitmap;
        bool ownsBitmap = false;
        if (image == null && wallpaper.ImageBytes != null)
        {
            try
            {
                image = SKBitmap.Decode(wallpaper.ImageBytes);
                ownsBitmap = true;
            }
            catch { }
        }

        try
        {
            if (image != null)
            {
                using var filter = sigma > 0 ? SKImageFilter.CreateBlur(sigma, sigma, SKShaderTileMode.Clamp) : null;
                using var paint = new SKPaint { IsAntialias = true, FilterQuality = SKFilterQuality.High, ImageFilter = filter };
                canvas.Save();
                canvas.Translate(pad, pad);
                DrawWallpaper(canvas, image, paint, frame, wallpaper);
                canvas.Restore();
            }
        }
        finally
        {
            if (ownsBitmap)
            {
                image?.Dispose();
            }
        }

        using var background = backdrop.Snapshot();
        var colorHex = frame.Dark ? frame.Theme.EffectiveSolidBackgroundDark : frame.Theme.EffectiveSolidBackgroundLight;
        if (!SKColor.TryParse(colorHex, out var coating)) coating = frame.Dark ? new SKColor(46, 46, 46) : SKColors.White;
        var opacity = double.IsFinite(frame.Theme.OpacityLevel) ? Math.Clamp(frame.Theme.OpacityLevel, 0, 1) : 0.18;
        var tint = (float)opacity;
        var edgeTint = (float)(optics.EdgeTint / 100.0);
        var highlightFactor = (float)(optics.Highlight / HighlightReference);

        // Light direction: 225° means light comes from top-left (cos 225° = -0.707, sin 225° = -0.707)
        var angle = optics.LightAngle * Math.PI / 180.0;
        var lx = (float)Math.Cos(angle);
        var ly = (float)Math.Sin(angle);

        // 3D light vector for specular meniscus highlights
        var l3x = lx * 0.65f;
        var l3y = ly * 0.65f;
        var l3z = 0.76f;

        using var source = SKBitmap.FromImage(background);
        var sourcePixels = source.Pixels;

        // 柔光玻璃: the edge dye reads a *bloomed* colour field (see AuraField) instead of the
        // pixel under the rim, so a small coloured patch spreads along the edge like a light
        // source instead of stopping dead where the patch ends.
        var aura = soft ? AuraField.Build(sourcePixels, info.Width, info.Height, pad, width, height) : null;
        var pixels = new SKColor[width * height];
        var parallel = new ParallelOptions { MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount / 2, 2, 8) };

        Parallel.For(0, height, parallel, y =>
        {
            var yCoord = y + 0.5f;
            // Subtle ambient vertical luster: top of glass catches ambient skylight
            // (+2.5%; 柔光玻璃 lifts it to +4% — the pane reads as softly lit).
            var ambientLuster = MathF.Max(0f, 1f - yCoord / height) * (soft ? 0.04f : 0.025f);

            for (var x = 0; x < width; x++)
            {
                var xCoord = x + 0.5f;
                var bevel = field.Evaluate(xCoord, yCoord);
                var depth = bevel.Depth;

                // Displacement: prominent optical lens at the rim, 0 in center
                var shift = Displacement(depth, lensWidth, lensShift);
                var sx = x + pad - bevel.Nx * shift;
                var sy = y + pad - bevel.Ny * shift;

                // Chromatic dispersion (prismatic split):
                // Physically, dispersion is wavelength-dependent refraction (ΔD ∝ shift) along
                // the curved meniscus bezel. It peaks in the curved bezel region and tapers
                // smoothly to 0 at the outer border (no edge tearing) and inner flat center.
                var rimT = (lensWidth > 0f && depth < lensWidth) ? Math.Clamp(depth / lensWidth, 0f, 1f) : 1f;
                var rimFalloff = (1f - rimT) * (1f - rimT);
                var dispShape = MathF.Sin(MathF.PI * rimT) * rimFalloff / 0.35f;
                var dispScale = (shift * 0.07f + 0.95f * scale) * dispShape;
                var split = dispStrength * dispScale;
                var middle = Sample(sx, sy);
                var red = middle;
                var blue = middle;
                // The soft recipe halves the lens displacement, so the prismatic split is
                // boosted to stay visible: net ≈ 0.7x of 液态玻璃, but spread over seven
                // taps, which reads as a finer rainbow instead of a hard RGB edge.
                var tintSplit = soft ? split * 1.60f : split;
                if (tintSplit > 0.002f)
                {
                    red = Sample(sx + bevel.Nx * tintSplit, sy + bevel.Ny * tintSplit);
                    blue = Sample(sx - bevel.Nx * tintSplit, sy - bevel.Ny * tintSplit);
                }

                // Channel values before vibrancy/frost, so the optional spectrum blend
                // below can mix them in linear order with the three-tap split.
                float channelRed = red.Red, channelGreen = middle.Green, channelBlue = blue.Blue;

                // 柔光玻璃: full-spectrum dispersion (Android LiquidGlassView's seven
                // taps — red/orange/yellow/green/cyan/blue/violet, weights normalised
                // per channel). Blended in over the three-tap split by Spectrum.
                if (spectrumStrength > 0.01f && tintSplit > 0.002f)
                {
                    var stepX = bevel.Nx * tintSplit;
                    var stepY = bevel.Ny * tintSplit;
                    var t1 = Sample(sx + stepX, sy + stepY);                 // red
                    var t2 = Sample(sx + stepX * (2f / 3f), sy + stepY * (2f / 3f)); // orange
                    var t3 = Sample(sx + stepX / 3f, sy + stepY / 3f);       // yellow
                    var t5 = Sample(sx - stepX / 3f, sy - stepY / 3f);       // cyan
                    var t6 = Sample(sx - stepX * (2f / 3f), sy - stepY * (2f / 3f)); // blue
                    var t7 = Sample(sx - stepX, sy - stepY);                 // violet

                    var specRed = (t1.Red + t2.Red + t3.Red) / 3.5f + t7.Red / 7f;
                    var specGreen = t2.Green / 7f + (t3.Green + middle.Green + t5.Green) / 3.5f;
                    var specBlue = (t5.Blue + t6.Blue + t7.Blue) / 3f;

                    channelRed = LerpFloat(channelRed, specRed, spectrumStrength);
                    channelGreen = LerpFloat(channelGreen, specGreen, spectrumStrength);
                    channelBlue = LerpFloat(channelBlue, specBlue, spectrumStrength);
                }

                // Translucency transition: the curved meniscus edge has higher crystal clarity,
                // smoothly transitioning into the soft frosted coating in the interior.
                // When opacity is 1.0, it remains 100% solid pure color.
                var clarityRamp = (opacity >= 0.99) ? 0f : (1f - SmoothStep(0f, Math.Min(lensWidth * 1.5f, Math.Min(width, height) * 0.40f), depth));
                var localTint = tint * (1f - 0.28f * clarityRamp);

                // Vibrancy + adaptive frost. 柔光玻璃 diffuses a little more, but only a
                // little: the material is "soft light", not a milky frost panel, so the
                // backdrop must stay readable through it.
                var luma = 0.2126f * channelRed + 0.7152f * channelGreen + 0.0722f * channelBlue;
                var adapt = FrostMix(luma);
                if (soft) adapt = MathF.Min(adapt * 1.15f, 0.11f);
                var r = Channel(channelRed, coating.Red);
                var g = Channel(channelGreen, coating.Green);
                var b = Channel(channelBlue, coating.Blue);

                // --- HyperOS 4 / Modern iOS Intelligent Colored Edge Highlight & Organic Aura ---
                // Dual-scale organic aura:
                // 1. Concentrated chromatic glaze on the meniscus bezel (depth in [0, auraWidth])
                var u1 = auraWidth > 0f ? Math.Clamp(depth * invAura, 0f, 1f) : 1f;
                var bezelAura = (auraWidth > 0f && depth < auraWidth) ? 0.5f * (1f + MathF.Cos(MathF.PI * u1)) : 0f;

                // 2. Wide, soft ambient diffusion that gracefully sweeps into the outer border band.
                //    柔光玻璃 scales this interior share by 染色扩散: at 0 the dye stays a rim band
                //    and nothing bleeds into the content (the pristine, "clean" setting).
                var innerSpread = Math.Min(Math.Min(width, height) * 0.45f, auraWidth * adaptive.InnerMult);
                var u2 = (innerSpread > 0f && depth < innerSpread) ? Math.Clamp(depth / innerSpread, 0f, 1f) : 1f;
                var ambientDiffusion = (innerSpread > 0f && depth < innerSpread) ? 0.5f * (1f + MathF.Cos(MathF.PI * u2)) : 0f;

                // Seamless blend: bezel crest + interior ambient diffusion (soft: bounded by 染色扩散).
                var ambientWeight = soft ? 0.35f * dyeSpread : 0.35f;
                var softAura = (1f - ambientWeight) * bezelAura + ambientWeight * ambientDiffusion;

                float hlR = 255f, hlG = 255f, hlB = 255f;
                float rimGlow = 0f;
                if (edgeTint > 0.001f && softAura > 0.001f)
                {
                    // The colour source: 柔光玻璃 takes the bloomed field, everything else the
                    // pixel itself (point-wise dye, the historic look).
                    var dyeSource = aura?.Sample(x + 0.5f, y + 0.5f) ?? middle;

                    // Physical color detection:
                    // 1. Absolute chroma: max(R,G,B) - min(R,G,B) in [0, 255].
                    //    JPEG 4:2:0 chroma quantization & dark sensor noise routinely cause 2..12 delta,
                    //    which relative HSL saturation falsely amplifies to 100% rainbow noise.
                    var maxC = Math.Max(dyeSource.Red, Math.Max(dyeSource.Green, dyeSource.Blue));
                    var minC = Math.Min(dyeSource.Red, Math.Min(dyeSource.Green, dyeSource.Blue));
                    var chroma = (float)(maxC - minC);
                    var chromaWeight = SmoothStep(soft ? 9f : 14f, soft ? 24f : 32f, chroma);

                    // 2. Physical luminance gating:
                    //    Near-black backgrounds (luma < 10) lack physical radiance to cast a luminous colored aura.
                    var dyeLuma = 0.2126f * dyeSource.Red + 0.7152f * dyeSource.Green + 0.0722f * dyeSource.Blue;
                    var lumaGate = SmoothStep(8f, 28f, dyeLuma);
                    var colorWeight = chromaWeight * lumaGate;

                    if (colorWeight > 0.001f)
                    {
                        dyeSource.ToHsl(out var h, out var s, out var l);
                        var glowS = Math.Clamp(s * 2.2f + 25f * colorWeight, 0f, 100f);
                        var glowL = Math.Clamp(l * 0.15f + 48f, 46f, 60f);
                        var pureGlow = SKColor.FromHsl(h, glowS, glowL);

                        var glowR = (1f - colorWeight) * 255f + colorWeight * pureGlow.Red;
                        var glowG = (1f - colorWeight) * 255f + colorWeight * pureGlow.Green;
                        var glowB = (1f - colorWeight) * 255f + colorWeight * pureGlow.Blue;

                        // 1. Soft chromatic glaze on the outer glass (only glazes when there is
                        //    genuine color). 柔光玻璃 glazes noticeably stronger: the colour wash
                        //    is that material's signature, not a detail on top of a lens.
                        var glazeMix = edgeTint * softAura * (soft ? 0.62f : 0.35f) * colorWeight;
                        r += (glowR - r) * glazeMix;
                        g += (glowG - g) * glazeMix;
                        b += (glowB - b) * glazeMix;

                        // 2. Dynamic colored highlight tint (tapering from saturated color at rim to pure white inside)
                        var hlMix = (soft ? 1.15f : 1f) * MathF.Pow(edgeTint, 0.70f) * softAura * colorWeight;
                        hlMix = Math.Clamp(hlMix, 0f, 1f);
                        hlR = (1f - hlMix) * 255f + hlMix * pureGlow.Red;
                        hlG = (1f - hlMix) * 255f + hlMix * pureGlow.Green;
                        hlB = (1f - hlMix) * 255f + hlMix * pureGlow.Blue;
                    }

                    rimGlow = edgeTint * softAura * softAura * (soft ? 0.5f : 0.35f);
                }

                // 柔光玻璃: the signature soft halo. A broad, mostly omnidirectional bloom
                // that wraps the rim and rolls off into the interior — the "soft light"
                // that replaces the crisp hairline of 液态玻璃. It is applied on top of the
                // highlight (step 5 below) as light rather than as paint.
                var softHalo = 0f;
                if (glowStrength > 0.001f)
                {
                    // The halo hugs the rim: about 1.6 lens bands (with a floor, so it survives
                    // EdgeWidth = 0), and never more than ~a fifth of the short side. Wider than
                    // that and a small card is simply flooded with white instead of glowing at
                    // its edge.
                    var haloWidth = Math.Min(
                        Math.Max(lensWidth * 1.6f, Math.Min(width, height) * 0.08f),
                        Math.Min(width, height) * 0.22f);
                    var halo = (haloWidth > 0f && depth < haloWidth)
                        ? MathF.Pow(1f - depth / haloWidth, 2.4f)
                        : 0f;
                    var wrap = 0.62f + 0.38f * MathF.Max(0f, bevel.Nx * lx + bevel.Ny * ly);
                    softHalo = glowStrength * halo * wrap * 0.55f;
                }

                // --- Modern iOS Luminous Glass Highlights (Zero Dark Shadows) ---
                // 1. Omnidirectional crisp glass rim stroke with strong directional light boost.
                //    柔光玻璃 widens the stroke into a diffuse halo and softens the
                //    directional bias, so light wraps around the edge instead of
                //    drawing a line on it.
                var cosL = bevel.Nx * lx + bevel.Ny * ly; // 1 when facing light, -1 when opposite
                var rimEdge = soft
                    ? MathF.Pow(1f - SmoothStep(0f, rimWidth, depth), 2.0f)
                    : 1f - SmoothStep(0f, rimLineWidth, depth);
                var directional = MathF.Max(0f, cosL);
                var rimLight = soft
                    ? rimEdge * (adaptive.RimBaseLight * 0.55f + adaptive.RimDirLight * 0.55f * MathF.Pow(directional, 0.60f)) * 0.75f
                    : rimEdge * (adaptive.RimBaseLight + adaptive.RimDirLight * MathF.Pow(directional, 0.85f));

                // 2. Meniscus curved reflection and continuous surface specular spread
                var meniscusLight = 0f;
                var spreadWidth = Math.Min(Math.Min(width, height) * 0.45f, lensWidth * adaptive.SpreadMult);
                if (depth < spreadWidth)
                {
                    var t = lensWidth > 0f ? Math.Clamp(depth * invLens, 0f, 1f) : 0f;
                    var tilt = (depth < lensWidth) ? MathF.Pow(1f - t, 2.0f) : 0f;
                    var nx = bevel.Nx * tilt * 0.82f;
                    var ny = bevel.Ny * tilt * 0.82f;
                    var nz = MathF.Sqrt(Math.Max(0.01f, 1f - nx * nx - ny * ny));

                    // Light reflection. 柔光玻璃 replaces the dual-lobe glint (exp 28/8,
                    // a water-bead point highlight) with a single wide sheen (exp 3/2):
                    // the surface looks satin rather than wet.
                    var ndotl = Math.Max(0f, nx * l3x + ny * l3y + nz * l3z);
                    var specGlint = PowInt(ndotl, soft ? 3 : 28);
                    var specGlow = PowInt(ndotl, soft ? 2 : 8);
                    var fresnel = PowInt(1f - nz, 3) * (soft ? 0.16f : 0.35f);

                    // Bezel glint on the curved edge. The soft recipe's wide lobes are
                    // also much weaker, otherwise a broad specular sheen would veil the
                    // whole card instead of lighting its edge.
                    var bevelLight = ((0.70f * specGlint + 0.30f * specGlow) * MathF.Max(0f, cosL) * (soft ? 0.38f : 0.90f)
                                      + fresnel * (soft ? 0.18f : 0.25f)) * (1f - t) * (1f - t);

                    // Soft inner specular sheen rolling off into the interior
                    var innerT = depth / spreadWidth;
                    var innerRoll = 0.5f * (1f + MathF.Cos(MathF.PI * innerT));
                    var innerSheen = PowInt(ndotl, soft ? 4 : 6) * MathF.Max(0f, cosL) * (soft ? 0.04f : 0.08f) * innerRoll;

                    meniscusLight = bevelLight + innerSheen;
                }

                // 3. Combine highlights (Notice: ZERO depthShadow! Modern iOS is airy, clean, and shadowless)
                var totalLight = highlightFactor * (rimLight * adaptive.RimLightScale + meniscusLight * 0.70f + ambientLuster) + rimGlow;
                var lightMix = Math.Clamp(totalLight, 0f, 1f);

                // 4. Screen / Luminous Optical Blending with smart colored highlight
                r += (hlR - r) * lightMix;
                g += (hlG - g) * lightMix;
                b += (hlB - b) * lightMix;

                // 5. 柔光玻璃: the halo is light, not paint — a mostly neutral wash that
                //    only takes a hint of the rim colour (柔光 = soft light, not a neon edge).
                if (softHalo > 0.001f)
                {
                    var cast = 0.30f * edgeTint;
                    r += (LerpFloat(255f, hlR, cast) - r) * softHalo;
                    g += (LerpFloat(255f, hlG, cast) - g) * softHalo;
                    b += (LerpFloat(255f, hlB, cast) - b) * softHalo;
                }

                pixels[y * width + x] = new SKColor(Clamp(r), Clamp(g), Clamp(b));

                float Channel(float value, byte coat)
                {
                    var c = luma + (value - luma) * Saturation;
                    c += (255f - c) * adapt;
                    return c * (1 - localTint) + coat * localTint;
                }
            }
        });

        using var refracted = new SKBitmap(width, height);
        refracted.Pixels = pixels;
        using var shader = refracted.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp);
        using var output = SKSurface.Create(new SKImageInfo(width, height));
        output.Canvas.Clear(SKColors.Transparent);
        using var glassPaint = new SKPaint { Shader = shader, IsAntialias = true };
        output.Canvas.DrawRoundRect(new SKRect(0, 0, width, height), radius, radius, glassPaint);
        using var result = output.Snapshot();
        var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul));
        if (!result.ReadPixels(bitmap.Info, bitmap.GetPixels(), bitmap.RowBytes, 0, 0))
        {
            bitmap.Dispose();
            return null;
        }
        bitmap.SetImmutable();
        return bitmap;

        SKColor Sample(float x, float y)
        {
            x = Math.Clamp(x, 0, info.Width - 1);
            y = Math.Clamp(y, 0, info.Height - 1);
            var ix = (int)x; var iy = (int)y;
            var x1 = Math.Min(ix + 1, info.Width - 1); var y1 = Math.Min(iy + 1, info.Height - 1);
            var a = sourcePixels[iy * info.Width + ix]; var b = sourcePixels[iy * info.Width + x1];
            var c = sourcePixels[y1 * info.Width + ix]; var d = sourcePixels[y1 * info.Width + x1];
            var fx = x - ix; var fy = y - iy;
            return new SKColor(Lerp(a.Red, b.Red, c.Red, d.Red), Lerp(a.Green, b.Green, c.Green, d.Green), Lerp(a.Blue, b.Blue, c.Blue, d.Blue));
            byte Lerp(byte p, byte q, byte r, byte s) => (byte)Math.Clamp(MathF.Round((p + (q - p) * fx) * (1 - fy) + (r + (s - r) * fx) * fy), 0f, 255f);
        }
    }

    /// <summary>
    /// 柔光玻璃's edge dye is a <b>bloom</b>, not a per-pixel tint. Dyeing each rim pixel with the
    /// colour underneath it cuts the colour off exactly where the backdrop's patch ends (a green
    /// scrap next to a red field produced a hard green/red seam along the rim). This field turns
    /// the backdrop into a coarse colour map in which every cell is allowed to spread over its
    /// neighbours — the most colourful cell wins locally — and then blurs it, so the render loop
    /// samples a colour that has bled out of its patch like a light source. A bilinear lookup of
    /// a ~30×24 grid: negligible next to the eight-tap sampling the loop already does.
    /// <para>Public so the frameless clock's glyph renderer (separate assembly) can dye its
    /// numerals from the same bloomed field.</para>
    /// </summary>
    public sealed class AuraField
    {
        private readonly float[] cells;
        private readonly int columns;
        private readonly int rows;
        private readonly float step;
        private readonly float margin;

        private AuraField(float[] cells, int columns, int rows, float step, float margin)
        {
            this.cells = cells;
            this.columns = columns;
            this.rows = rows;
            this.step = step;
            this.margin = margin;
        }

        /// <summary>
        /// Builds the field from the already-blurred backdrop. The grid is anchored to the card
        /// itself (plus one cell of bleed), never to the padded surface: the padding depends on
        /// the blur and the lens, so anchoring there would shift the grid — and with it the dye
        /// colour — whenever those change, even in ways that cannot affect the dye at all.
        /// </summary>
        /// <param name="source">Blurred backdrop pixels (padded surface).</param>
        /// <param name="pad">Padding of that surface, in pixels.</param>
        /// <param name="cardWidth">Card width in render pixels.</param>
        /// <param name="cardHeight">Card height in render pixels.</param>
        public static AuraField Build(SKColor[] source, int sourceWidth, int sourceHeight, int pad,
            int cardWidth, int cardHeight)
        {
            var step = StepFor(cardWidth, cardHeight);
            var margin = step;
            var originX = pad - margin;
            var originY = pad - margin;
            var columns = ColumnCount(cardWidth, margin, step);
            var rows = ColumnCount(cardHeight, margin, step);

            var raw = new float[columns * rows * 3];
            for (var row = 0; row < rows; row++)
            {
                var y0 = (int)(originY + row * step);
                var y1 = (int)(originY + (row + 1) * step);
                for (var column = 0; column < columns; column++)
                {
                    var x0 = (int)(originX + column * step);
                    var x1 = (int)(originX + (column + 1) * step);
                    float red = 0, green = 0, blue = 0;
                    var count = 0;
                    for (var y = Math.Max(0, y0); y < Math.Min(sourceHeight, y1); y++)
                    {
                        var rowOffset = y * sourceWidth;
                        for (var x = Math.Max(0, x0); x < Math.Min(sourceWidth, x1); x++)
                        {
                            var color = source[rowOffset + x];
                            red += color.Red;
                            green += color.Green;
                            blue += color.Blue;
                            count++;
                        }
                    }
                    if (count == 0) count = 1;
                    var i = (row * columns + column) * 3;
                    raw[i] = (float)(red / count);
                    raw[i + 1] = (float)(green / count);
                    raw[i + 2] = (float)(blue / count);
                }
            }

            return new AuraField(SpreadAndSmooth(raw, columns, rows), columns, rows, step, margin);
        }

        /// <summary>
        /// Same field, built by sampling the backdrop instead of averaging it. The GPU path starts
        /// from an already-blurred, downscaled backdrop, so a cell's average is its centre value
        /// and the exhaustive per-pixel pass would be the only remaining O(area) work on the CPU.
        /// <paramref name="sampleCardPixel"/> maps card-local render pixels to a backdrop colour.
        /// </summary>
        public static AuraField BuildFromSampler(Func<float, float, SKColor> sampleCardPixel,
            int cardWidth, int cardHeight)
        {
            var step = StepFor(cardWidth, cardHeight);
            var margin = step;
            var columns = ColumnCount(cardWidth, margin, step);
            var rows = ColumnCount(cardHeight, margin, step);
            var quarter = step * 0.25f;

            var raw = new float[columns * rows * 3];
            for (var row = 0; row < rows; row++)
            for (var column = 0; column < columns; column++)
            {
                var cx = -margin + (column + 0.5f) * step;
                var cy = -margin + (row + 0.5f) * step;
                float red = 0, green = 0, blue = 0;
                for (var sy = -1; sy <= 1; sy += 2)
                for (var sx = -1; sx <= 1; sx += 2)
                {
                    var color = sampleCardPixel(cx + sx * quarter, cy + sy * quarter);
                    red += color.Red;
                    green += color.Green;
                    blue += color.Blue;
                }
                var i = (row * columns + column) * 3;
                raw[i] = red / 4f;
                raw[i + 1] = green / 4f;
                raw[i + 2] = blue / 4f;
            }

            return new AuraField(SpreadAndSmooth(raw, columns, rows), columns, rows, step, margin);
        }

        private static float StepFor(int cardWidth, int cardHeight) =>
            Math.Clamp(Math.Min(cardWidth, cardHeight) / 22f, 4f, 18f);

        private static int ColumnCount(int cardSide, float margin, float step) =>
            Math.Max(1, (int)MathF.Ceiling((cardSide + margin * 2f) / step));

        /// <summary>
        /// Dye spread then smoothing — shared by both builders so the CPU and GPU paths cannot
        /// drift apart.
        /// <para>
        /// Spread: two dilate passes let a colourful cell reach two cells out, so a small patch
        /// behaves like a light source rather than a sticker. The winner is the cell with the most
        /// colour × light (a saturated patch beats a grey neighbour, and a bright saturated one
        /// beats a dark saturated one), and it is taken at 70% so the patch keeps a centre instead
        /// of flattening into a plateau. Smooth: a 3×3 box blur removes the grid's cell edges,
        /// which is what turns the spread into a soft 晕染 gradient instead of a mosaic.
        /// </para>
        /// </summary>
        private static float[] SpreadAndSmooth(float[] raw, int columns, int rows)
        {
            var spread = new float[raw.Length];
            for (var pass = 0; pass < 2; pass++)
            {
                Array.Copy(raw, spread, raw.Length);
                for (var row = 0; row < rows; row++)
                for (var column = 0; column < columns; column++)
                {
                    var i = (row * columns + column) * 3;
                    var best = i;
                    var bestScore = Score(raw, i);
                    for (var dy = -1; dy <= 1; dy++)
                    for (var dx = -1; dx <= 1; dx++)
                    {
                        var r = row + dy;
                        var c = column + dx;
                        if (r < 0 || c < 0 || r >= rows || c >= columns) continue;
                        var candidate = (r * columns + c) * 3;
                        var score = Score(raw, candidate);
                        if (score > bestScore)
                        {
                            bestScore = score;
                            best = candidate;
                        }
                    }
                    if (best != i)
                    {
                        spread[i] = raw[i] * 0.30f + raw[best] * 0.70f;
                        spread[i + 1] = raw[i + 1] * 0.30f + raw[best + 1] * 0.70f;
                        spread[i + 2] = raw[i + 2] * 0.30f + raw[best + 2] * 0.70f;
                    }
                }
                Array.Copy(spread, raw, raw.Length);
            }

            var blurred = new float[raw.Length];
            for (var row = 0; row < rows; row++)
            for (var column = 0; column < columns; column++)
            {
                float red = 0, green = 0, blue = 0;
                var count = 0;
                for (var dy = -1; dy <= 1; dy++)
                for (var dx = -1; dx <= 1; dx++)
                {
                    var r = row + dy;
                    var c = column + dx;
                    if (r < 0 || c < 0 || r >= rows || c >= columns) continue;
                    var i = (r * columns + c) * 3;
                    red += raw[i];
                    green += raw[i + 1];
                    blue += raw[i + 2];
                    count++;
                }
                var target = (row * columns + column) * 3;
                blurred[target] = red / count;
                blurred[target + 1] = green / count;
                blurred[target + 2] = blue / count;
            }

            return blurred;
        }

        /// <summary>Grid dimensions, for the GPU path's texture addressing.</summary>
        public int Columns => columns;

        /// <summary>Grid rows.</summary>
        public int Rows => rows;

        /// <summary>Cell size in card-local render pixels.</summary>
        public float Step => step;

        /// <summary>Bleed margin in card-local render pixels.</summary>
        public float Margin => margin;

        /// <summary>
        /// The grid as a texture for the GPU path: one texel per cell, so a bilinear lookup of a
        /// normalised coordinate reproduces <see cref="Sample"/> exactly.
        /// </summary>
        public SKBitmap ToBitmap()
        {
            var bitmap = new SKBitmap(new SKImageInfo(columns, rows, SKColorType.Bgra8888, SKAlphaType.Opaque));
            for (var row = 0; row < rows; row++)
            for (var column = 0; column < columns; column++)
            {
                var i = (row * columns + column) * 3;
                bitmap.SetPixel(column, row, new SKColor(Clamp(cells[i]), Clamp(cells[i + 1]), Clamp(cells[i + 2])));
            }
            bitmap.SetImmutable();
            return bitmap;
        }

        /// <summary>Colourfulness × brightness — how much of a light source this cell is.</summary>
        private static float Score(float[] data, int index)
        {
            var red = data[index];
            var green = data[index + 1];
            var blue = data[index + 2];
            var max = MathF.Max(red, MathF.Max(green, blue));
            var min = MathF.Min(red, MathF.Min(green, blue));
            var luma = 0.2126f * red + 0.7152f * green + 0.0722f * blue;
            return (max - min) * luma / 255f;
        }

        /// <summary>Bilinear lookup; coordinates are card-local render pixels (0,0 = card corner).</summary>
        public SKColor Sample(float cardX, float cardY)
        {
            var gx = Math.Clamp((cardX + margin) / step - 0.5f, 0f, columns - 1);
            var gy = Math.Clamp((cardY + margin) / step - 0.5f, 0f, rows - 1);
            var x0 = (int)gx;
            var y0 = (int)gy;
            var x1 = Math.Min(x0 + 1, columns - 1);
            var y1 = Math.Min(y0 + 1, rows - 1);
            var fx = gx - x0;
            var fy = gy - y0;

            var i00 = (y0 * columns + x0) * 3;
            var i10 = (y0 * columns + x1) * 3;
            var i01 = (y1 * columns + x0) * 3;
            var i11 = (y1 * columns + x1) * 3;

            var red = Mix(cells[i00], cells[i10], cells[i01], cells[i11], fx, fy);
            var green = Mix(cells[i00 + 1], cells[i10 + 1], cells[i01 + 1], cells[i11 + 1], fx, fy);
            var blue = Mix(cells[i00 + 2], cells[i10 + 2], cells[i01 + 2], cells[i11 + 2], fx, fy);
            return new SKColor(Clamp(red), Clamp(green), Clamp(blue));

            static float Mix(float a, float b, float c, float d, float fx, float fy) =>
                (a + (b - a) * fx) * (1 - fy) + (c + (d - c) * fx) * fy;
        }
    }

    /// <summary>
    /// Lens displacement (px) at inward depth from the border.
    /// Confined strictly to the bezel meniscus [0, lensWidth].
    /// The profile starts at 0 at the outer border (no boundary tearing),
    /// reaches its peak in the outer third of the bezel (optical meniscus refraction),
    /// and tapers smoothly to 0 at lensWidth with C1 continuity.
    /// For depth >= lensWidth (the entire central area), displacement is IDENTICALLY 0.
    /// </summary>
    public static float Displacement(float depth, float lensWidth, float lensShift, float reflectWidth = 0f, float guardWidth = 0f)
    {
        if (depth <= 0f || depth >= lensWidth || lensWidth <= 0f) return 0f;
        var t = depth / lensWidth;
        var shape = MathF.Sin(MathF.PI * t) * MathF.Pow(1f - t, 1.4f) / 0.45f;
        return lensShift * Math.Max(0f, shape);
    }

    /// <summary>Adaptive frost mix (0-1) for a backdrop luminance 0-255.</summary>
    public static float FrostMix(float luma) =>
        Math.Clamp(FrostFloor + (1f - luma / 255f) * FrostLift, 0f, FrostCap);

    private static byte Clamp(float value) => (byte)Math.Clamp(MathF.Round(value), 0f, 255f);

    private static float LerpFloat(float from, float to, float amount) => from + (to - from) * amount;

    private static float SmoothStep(float edge0, float edge1, float value)
    {
        if (edge1 <= edge0) return value >= edge1 ? 1f : 0f;
        var t = Math.Clamp((value - edge0) / (edge1 - edge0), 0f, 1f);
        return t * t * (3f - 2f * t);
    }

    private static float PowInt(float value, int exponent)
    {
        var result = 1f;
        var factor = value;
        for (var e = exponent; e > 0; e >>= 1)
        {
            if ((e & 1) != 0) result *= factor;
            factor *= factor;
        }
        return result;
    }

    /// <summary>
    /// Smooth, continuous 2D distance and normal field for the rounded rectangle.
    /// Purely radial in corner quadrants, purely orthogonal on straight edges,
    /// seamless C1 continuity at tangent points, with NO medial axis crease.
    /// </summary>
    public readonly struct BevelField
    {
        public readonly float HalfWidth, HalfHeight, InnerWidth, InnerHeight, Radius;

        public BevelField(int width, int height, float radius)
        {
            HalfWidth = width / 2f;
            HalfHeight = height / 2f;
            Radius = Math.Clamp(radius, 0f, Math.Min(HalfWidth, HalfHeight));
            InnerWidth = HalfWidth - Radius;
            InnerHeight = HalfHeight - Radius;
        }

        public Bevel Evaluate(float x, float y)
        {
            var vx = x - HalfWidth;
            var vy = y - HalfHeight;
            var sx = vx >= 0 ? 1f : -1f;
            var sy = vy >= 0 ? 1f : -1f;
            var ax = MathF.Abs(vx);
            var ay = MathF.Abs(vy);
            var qx = ax - InnerWidth;
            var qy = ay - InnerHeight;

            // Region 1: In the outer corner quadrant
            if (qx > 0f && qy > 0f)
            {
                var len = MathF.Sqrt(qx * qx + qy * qy);
                if (len > 1e-5f)
                {
                    return new Bevel(qx / len * sx, qy / len * sy, Radius - len);
                }
                return new Bevel(0.7071f * sx, 0.7071f * sy, Radius);
            }

            // Region 2: Horizontal straight edge (top or bottom)
            if (qx <= 0f && qy > 0f)
            {
                return new Bevel(0f, sy, HalfHeight - ay);
            }

            // Region 3: Vertical straight edge (left or right)
            if (qx > 0f && qy <= 0f)
            {
                return new Bevel(sx, 0f, HalfWidth - ax);
            }

            // Region 4: Interior region (ax <= InnerWidth, ay <= InnerHeight)
            var distV = HalfWidth - ax;
            var distH = HalfHeight - ay;
            var depth = MathF.Min(distV, distH);

            var u = -qx;
            var v = -qy;
            var angle = MathF.Atan2(u + 1e-4f, v + 1e-4f);
            var nx = MathF.Cos(angle) * sx;
            var ny = MathF.Sin(angle) * sy;

            return new Bevel(nx, ny, depth);
        }
    }

    /// <summary>Outward unit normal and inward depth at one point of the surface.</summary>
    public readonly struct Bevel
    {
        public readonly float Nx, Ny, Depth;

        public Bevel(float nx, float ny, float depth)
        {
            Nx = nx; Ny = ny; Depth = depth;
        }
    }

    public static void DrawWallpaper(SKCanvas canvas, SKBitmap image, SKPaint paint,
        Frame frame, WallpaperSnapshot wallpaper)
    {
        // Manual wallpaper alignment (DIPs → render px): the user calibrates the
        // sampled position against the real desktop when the display layer's own
        // layout cannot be trusted (taskbar replacements, wallpaper engines).
        // Read through the active material's pipeline settings so both glass materials
        // keep their own alignment.
        var glass = frame.Theme.EffectiveGlass;
        var offX = (float)(glass.WallpaperOffsetX * frame.Scale);
        var offY = (float)(glass.WallpaperOffsetY * frame.Scale);

        // Live desktop capture: the bitmap IS the virtual desktop at physical
        // pixels (captured on Progman), so placement is identity — no style math,
        // no registry — the sampled crop is exactly what is displayed behind the
        // widget even when the display layout was changed by taskbar replacements
        // or wallpaper engines.
        if (wallpaper.LiveCapture)
        {
            var rect = SKRect.Create(-frame.DesktopX - offX, -frame.DesktopY - offY,
                image.Width * frame.PixelScale, image.Height * frame.PixelScale);
            canvas.DrawBitmap(image, rect, paint);
            return;
        }

        var style = wallpaper.Style;
        var tile = wallpaper.Tile;
        // Desktop and screen coordinates are already expressed in render pixels.
        // Preserve the global origin for negative monitor positions and margins.
        var x = frame.ScreenX - frame.DesktopX;
        var y = frame.ScreenY - frame.DesktopY;
        var w = frame.ScreenWidth;
        var h = frame.ScreenHeight;
        if (style == "22") // Span across the virtual desktop.
        {
            x = -frame.DesktopX;
            y = -frame.DesktopY;
            w = frame.DesktopWidth;
            h = frame.DesktopHeight;
        }
        if (tile)
        {
            var matrix = SKMatrix.CreateScale(frame.PixelScale, frame.PixelScale);
            matrix.TransX = x - offX;
            matrix.TransY = y - offY;
            using var tiled = image.ToShader(SKShaderTileMode.Repeat, SKShaderTileMode.Repeat,
                matrix);
            using var tiledPaint = new SKPaint { Shader = tiled, ImageFilter = paint.ImageFilter };
            canvas.DrawPaint(tiledPaint);
            return;
        }
        float drawWidth, drawHeight;
        if (style == "2") { drawWidth = w; drawHeight = h; } // Stretch
        else if (style == "0") { drawWidth = image.Width * frame.PixelScale; drawHeight = image.Height * frame.PixelScale; } // Center
        else
        {
            var ratio = style == "6" ? Math.Min(w / image.Width, h / image.Height) : Math.Max(w / image.Width, h / image.Height);
            drawWidth = image.Width * ratio;
            drawHeight = image.Height * ratio;
        }
        canvas.DrawBitmap(image, SKRect.Create(x + (w - drawWidth) / 2 - offX, y + (h - drawHeight) / 2 - offY, drawWidth, drawHeight), paint);
    }
}

/// <summary>
/// A reference-counted desktop capture.
/// <para>
/// The capture is the single largest allocation in the process — a full virtual desktop at physical
/// resolution, tens of megabytes with several monitors — and live sampling replaces it many times a
/// second. It is therefore released the moment its last user lets go rather than after a fixed
/// grace period: at the default 10 fps a three-second grace kept roughly thirty captures alive at
/// once (hundreds of megabytes), which is where the liquid-glass theme's memory went.
/// </para>
/// </summary>
public sealed class WallpaperLease : IDisposable
{
    private int references = 1;
    private readonly SKBitmap? bitmap;

    public WallpaperLease(SKBitmap? bitmap) => this.bitmap = bitmap;

    public SKBitmap? Bitmap => bitmap;

    public void AddRef() => Interlocked.Increment(ref references);

    public void Dispose()
    {
        if (Interlocked.Decrement(ref references) > 0) return;
        try { bitmap?.Dispose(); } catch { }
    }
}

public record WallpaperSnapshot(
    byte[]? ImageBytes,
    SKColor Background,
    string Style = "10",
    bool Tile = false,
    bool LiveCapture = false) : IDisposable
{
    private byte[]? _imageBytes = ImageBytes;

    /// <summary>
    /// The captured pixels, reference counted.
    /// <para>
    /// Deliberately an <c>init</c> property and not a positional parameter: <c>with</c> then copies
    /// the <i>reference to the lease</i>, which keeps every copy sharing one count. A raw bitmap in
    /// the positional list would instead be copied by value, giving the copy its own ownership of
    /// the same pixels — and then two disposals would free them twice.
    /// </para>
    /// </summary>
    internal WallpaperLease? Lease { get; init; }

    public SKBitmap? CachedBitmap => Lease?.Bitmap;

    /// <summary>A snapshot owning a fresh lease around <paramref name="bitmap"/>.</summary>
    public static WallpaperSnapshot FromBitmap(byte[]? bytes, SKColor background, SKBitmap? bitmap,
        string style = "10", bool tile = false, bool live = false) =>
        new(bytes, background, style, tile, live)
        {
            Lease = bitmap == null ? null : new WallpaperLease(bitmap)
        };

    public byte[]? ImageBytes
    {
        get
        {
            if (_imageBytes != null) return _imageBytes;
            if (CachedBitmap != null)
            {
                try
                {
                    using var img = SKImage.FromBitmap(CachedBitmap);
                    using var data = img.Encode(SKEncodedImageFormat.Png, 90);
                    _imageBytes = data.ToArray();
                }
                catch { }
            }
            return _imageBytes;
        }
        init => _imageBytes = value;
    }

    /// <summary>
    /// A copy that only overrides the wallpaper's placement (used by the settings preview). The copy
    /// shares the pixels and takes its <b>own</b> reference, so its owner must dispose it.
    /// </summary>
    public WallpaperSnapshot WithPlacement(string style, bool tile)
    {
        Lease?.AddRef();
        return this with { Style = style, Tile = tile };
    }

    /// <summary>Take one more reference; the caller becomes responsible for one <see cref="Dispose"/>.</summary>
    public void AddRef() => Lease?.AddRef();

    /// <summary>Release one reference, freeing the capture once the last one goes.</summary>
    public void Dispose() => Lease?.Dispose();

    public string Describe() =>
        $"WallpaperSnapshot(bytes={_imageBytes?.Length ?? 0}, hasBitmap={CachedBitmap != null}, bg=#{Background.Red:X2}{Background.Green:X2}{Background.Blue:X2}, style={Style}, tile={Tile}, live={LiveCapture})";
}
