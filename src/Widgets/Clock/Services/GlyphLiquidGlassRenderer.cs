using System;
using System.Threading.Tasks;
using SkiaSharp;
using uWidgets.Core.Models.Settings;
using uWidgets.Services;

namespace Clock.Services;

/// <summary>
/// Optical Liquid Glass renderer applied directly inside numeral glyph contours.
/// Calculates an Euclidean Distance Field (EDT) over the glyph mask to evaluate
/// per-pixel distance-to-stroke-boundary and outward surface normals, creating
/// real optical lens refraction, chromatic dispersion, 3D specular highlights,
/// and iOS glass rim lines along the individual number strokes.
/// </summary>
public static class GlyphLiquidGlassRenderer
{
    private const float LensDips = 10.0f;
    private const float RimLineDips = 1.2f;
    private const float Saturation = 1.22f;
    private const float HighlightReference = 65.0f;

    /// <summary>7-tap separable smoothing kernel (radius 3) for the normal field.</summary>
    private static readonly float[] Weights = [1f, 3f, 6f, 8f, 6f, 3f, 1f];

    public static byte[] Render(LiquidGlassRenderer.Frame frame, WallpaperSnapshot wallpaper, byte[] glyphMask, double? refractionWidth = null)
    {
        var optics = frame.Theme.EffectiveLiquidGlass;
        var scale = frame.Scale;
        var width = frame.Width;
        var height = frame.Height;
        var sigma = (float)optics.Blur * scale / 8f;

        // Distance field + result buffers come from the pool (see Scratch): one frame needs ~29
        // bytes per pixel in eight arrays, and the clock draws a frame every minute/second.
        var scratch = RentScratch(width * height, Math.Max(width, height));
        try
        {
            return RenderCore(frame, wallpaper, glyphMask, refractionWidth, scratch, optics, scale, width, height, sigma);
        }
        finally
        {
            ReturnScratch(scratch);
        }
    }

    /// <summary>
    /// The sub-pixel depth field of a glyph mask, in pixels: for every pixel inside the glyph, the
    /// distance from its centre to the nearest point of the glyph outline (negative outside the
    /// outline). Exposed so the field itself can be asserted numerically against an analytic shape
    /// in <c>tests/ClockGlassChecks</c> — the rim line, the lens band and the normals are all read
    /// from it, so its accuracy *is* the edge quality.
    /// </summary>
    public static float[] ComputeDepthField(byte[] mask, int width, int height)
    {
        var scratch = RentScratch(width * height, Math.Max(width, height));
        try
        {
            ComputeDistanceField(mask, width, height, scratch, out _, out _);
            var field = new float[width * height];
            Array.Copy(scratch.Dist, field, field.Length);
            return field;
        }
        finally
        {
            ReturnScratch(scratch);
        }
    }

    private static byte[] RenderCore(LiquidGlassRenderer.Frame frame, WallpaperSnapshot wallpaper, byte[] glyphMask,
        double? refractionWidth, Scratch scratch, LiquidGlassSettings optics, float scale, int width, int height, float sigma)
    {
        // Compute Euclidean Distance Field and Outward Normals for the glyph mask
        ComputeDistanceField(glyphMask, width, height, scratch, out var maxStrokeDepth, out var avgStrokeDepth);
        var distField = scratch.Dist;
        var nxField = scratch.Nx;
        var nyField = scratch.Ny;

        // Adaptive optics scaling for compact 1x1 widgets and 1-grid strips
        var is1x1 = (frame.Columns == 1 && frame.Rows == 1) || (frame.Columns == 0 && Math.Min(width / scale, height / scale) <= 110f && Math.Max(width / scale, height / scale) <= 115f);
        var is1Strip = !is1x1 && ((frame.Columns == 1 || frame.Rows == 1) || (frame.Columns == 0 && Math.Min(width / scale, height / scale) <= 130f));

        var glyphEdgeScale = is1x1 ? 0.60f : (is1Strip ? 0.80f : 1.0f);
        var glyphShiftScale = is1x1 ? 0.70f : (is1Strip ? 0.85f : 1.0f);
        var glyphRimScale = is1x1 ? 0.80f : (is1Strip ? 0.90f : 1.0f);

        // Representative stroke half-thickness (radius) derived directly from glyph distance field:
        var estimatedRadius = Math.Max(avgStrokeDepth * 1.85f, maxStrokeDepth * 0.82f);
        var strokeRadius = Math.Clamp(estimatedRadius, 2.0f * scale, Math.Min(width, height) * 0.25f);

        // 柔光玻璃: the numerals follow the soft recipe — a wide, shallow lens, a diffused rim
        // instead of the crisp hairline, a single wide sheen instead of the water-bead glint,
        // and the edge dye reads a *bloomed* colour field so a coloured patch spreads along the
        // strokes like a light source instead of stopping where the patch ends.
        var soft = frame.Theme.IsSoftGlow;
        var glowStrength = soft ? (float)Math.Clamp(optics.Glow, 0, 100) / 100f : 0f;
        var spectrumStrength = soft ? (float)Math.Clamp(optics.Spectrum, 0, 100) / 100f : 0f;

        // Refraction width calculation — see ResolveLens.
        var lens = ResolveLens(optics, scale, strokeRadius, glyphEdgeScale, glyphShiftScale, refractionWidth, soft);
        var lensWidth = lens.LensWidth;
        var lensShift = lens.LensShift;
        var dispStrength = lens.Dispersion;

        // Numeral strokes are thin: a blur wider than the lens ring erases the very detail
        // the lens bends, which flattens the refraction into a frosted wash. Keep the
        // sampling blur subordinate to the lens width (the card renderer keeps the full
        // user blur — a card is large enough for it not to matter there).
        if (lensWidth > 0.001f)
        {
            sigma = Math.Min(sigma, Math.Max(0.75f * scale, lensWidth * 0.55f));
        }

        var dyeWidth = Math.Max(Math.Clamp(strokeRadius * 0.32f, 2.5f * scale, 12f * scale), lensWidth);
        var rimLineWidth = Math.Clamp(Math.Max(lensWidth, strokeRadius * 0.15f) * 0.22f * glyphRimScale, 0.70f * scale, 2.4f * scale);

        var pad = (int)Math.Ceiling(Math.Max(sigma * 3f, Math.Max(lensWidth + 8f, lensShift * 1.3f + 16f)));
        var info = new SKImageInfo(width + 2 * pad, height + 2 * pad);
        using var backdrop = SKSurface.Create(info);
        var canvas = backdrop.Canvas;
        canvas.Clear(wallpaper.Background);

        // Sample the wallpaper from the decoded bitmap the snapshot already owns. Going
        // through WallpaperSnapshot.ImageBytes would PNG-encode (and then re-decode) the
        // whole virtual desktop on every single frame: ~40 MB of transient bitmaps plus a
        // multi-megabyte compressed copy per tick, and it silently drops the live desktop
        // capture (which has no ImageBytes of its own, only CachedBitmap).
        SKBitmap? image = wallpaper.CachedBitmap;
        var ownsBitmap = false;
        if (image == null && wallpaper.ImageBytes != null)
        {
            try
            {
                image = SKBitmap.Decode(wallpaper.ImageBytes);
                ownsBitmap = true;
            }
            catch
            {
                image = null;
            }
        }

        try
        {
            if (image != null)
            {
                using var filter = sigma > 0 ? SKImageFilter.CreateBlur(sigma, sigma, SKShaderTileMode.Clamp) : null;
                using var paint = new SKPaint { IsAntialias = true, FilterQuality = SKFilterQuality.High, ImageFilter = filter };
                canvas.Save();
                canvas.Translate(pad, pad);
                LiquidGlassRenderer.DrawWallpaper(canvas, image, paint, frame, wallpaper);
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

        var angle = optics.LightAngle * Math.PI / 180.0;
        var lx = (float)Math.Cos(angle);
        var ly = (float)Math.Sin(angle);
        var l3x = lx * 0.65f;
        var l3y = ly * 0.65f;
        var l3z = 0.76f;

        using var source = SKBitmap.FromImage(background);
        var sourcePixels = source.Pixels;
        var sourceW = source.Width;
        var sourceH = source.Height;

        // 柔光玻璃: the dye reads a bloomed colour field (see LiquidGlassRenderer.AuraField), so a
        // coloured patch spreads along the strokes instead of dyeing the pixels right above it.
        var aura = soft ? LiquidGlassRenderer.AuraField.Build(sourcePixels, sourceW, sourceH, pad, width, height) : null;

        var pixels = scratch.Pixels;
        var parallel = new ParallelOptions { MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount / 2, 2, 8) };

        Parallel.For(0, height, parallel, y =>
        {
            var yCoord = y + 0.5f;
            var ambientLuster = MathF.Max(0f, 1f - yCoord / height) * 0.03f;
            var rowOffset = y * width;

            for (var x = 0; x < width; x++)
            {
                var idx = rowOffset + x;
                var maskVal = glyphMask[idx];
                if (maskVal < 10)
                {
                    pixels[idx] = SKColors.Empty;
                    continue;
                }

                var depth = distField[idx];
                var nx = nxField[idx];
                var ny = nyField[idx];

                // Lens displacement
                var shift = Displacement(depth, lensWidth, lensShift);
                var sx = x + pad - nx * shift;
                var sy = y + pad - ny * shift;

                // Chromatic dispersion
                var rimT = (lensWidth > 0f && depth < lensWidth) ? Math.Clamp(depth / lensWidth, 0f, 1f) : 1f;
                var rimFalloff = (1f - rimT) * (1f - rimT);
                var dispShape = MathF.Sin(MathF.PI * rimT) * rimFalloff / 0.35f;
                var dispScale = (shift * 0.07f + 0.95f * scale) * dispShape;
                var split = dispStrength * dispScale;

                var middle = SamplePixel(sourcePixels, sourceW, sourceH, sx, sy);
                var red = middle;
                var blue = middle;
                if (split > 0.002f)
                {
                    red = SamplePixel(sourcePixels, sourceW, sourceH, sx + nx * split, sy + ny * split);
                    blue = SamplePixel(sourcePixels, sourceW, sourceH, sx - nx * split, sy - ny * split);
                }

                float channelRed = red.Red, channelGreen = middle.Green, channelBlue = blue.Blue;

                // 柔光玻璃: the same seven-tap spectrum the card material uses (red → violet),
                // blended in over the three-tap split by the Spectrum slider.
                if (spectrumStrength > 0.01f && split > 0.002f)
                {
                    var stepX = nx * split;
                    var stepY = ny * split;
                    var t1 = SamplePixel(sourcePixels, sourceW, sourceH, sx + stepX, sy + stepY);
                    var t2 = SamplePixel(sourcePixels, sourceW, sourceH, sx + stepX * (2f / 3f), sy + stepY * (2f / 3f));
                    var t3 = SamplePixel(sourcePixels, sourceW, sourceH, sx + stepX / 3f, sy + stepY / 3f);
                    var t5 = SamplePixel(sourcePixels, sourceW, sourceH, sx - stepX / 3f, sy - stepY / 3f);
                    var t6 = SamplePixel(sourcePixels, sourceW, sourceH, sx - stepX * (2f / 3f), sy - stepY * (2f / 3f));
                    var t7 = SamplePixel(sourcePixels, sourceW, sourceH, sx - stepX, sy - stepY);

                    var specRed = (t1.Red + t2.Red + t3.Red) / 3.5f + t7.Red / 7f;
                    var specGreen = t2.Green / 7f + (t3.Green + middle.Green + t5.Green) / 3.5f;
                    var specBlue = (t5.Blue + t6.Blue + t7.Blue) / 3f;

                    channelRed = Mix(channelRed, specRed, spectrumStrength);
                    channelGreen = Mix(channelGreen, specGreen, spectrumStrength);
                    channelBlue = Mix(channelBlue, specBlue, spectrumStrength);
                }

                var clarityRamp = (opacity >= 0.99) ? 0f : (1f - SmoothStep(0f, lensWidth * 1.5f, depth));
                var localTint = tint * (1f - 0.28f * clarityRamp);

                var luma = 0.2126f * channelRed + 0.7152f * channelGreen + 0.0722f * channelBlue;
                var adapt = FrostMix(luma);
                if (soft) adapt = MathF.Min(adapt * 1.15f, 0.11f);
                var r = Channel(channelRed, coating.Red, luma, adapt, localTint);
                var g = Channel(channelGreen, coating.Green, luma, adapt, localTint);
                var b = Channel(channelBlue, coating.Blue, luma, adapt, localTint);

                // --- Intelligent Edge Dyeing Algorithm for Numeral Meniscus ---
                var u1 = dyeWidth > 0.001f ? Math.Clamp(depth / dyeWidth, 0f, 1f) : 1f;
                var bezelAura = (depth < dyeWidth && dyeWidth > 0.001f) ? 0.5f * (1f + MathF.Cos(MathF.PI * u1)) : 0f;

                float hlR = 255f, hlG = 255f, hlB = 255f;
                float dyeR = 255f, dyeG = 255f, dyeB = 255f;
                float dyeWeight = 0f;

                if (edgeTint > 0.001f && bezelAura > 0.001f)
                {
                    // 柔光玻璃 dyes from the bloomed field, everything else from the pixel itself.
                    var dyeSource = aura?.Sample(sx - pad, sy - pad) ?? middle;

                    // Absolute chroma & physical luminance gating
                    var maxC = Math.Max(dyeSource.Red, Math.Max(dyeSource.Green, dyeSource.Blue));
                    var minC = Math.Min(dyeSource.Red, Math.Min(dyeSource.Green, dyeSource.Blue));
                    var chroma = (float)(maxC - minC);
                    var chromaWeight = SmoothStep(soft ? 8f : 10f, soft ? 22f : 26f, chroma);
                    var dyeLuma = 0.2126f * dyeSource.Red + 0.7152f * dyeSource.Green + 0.0722f * dyeSource.Blue;
                    var lumaGate = SmoothStep(8f, 26f, dyeLuma);
                    var colorWeight = chromaWeight * lumaGate;

                    if (colorWeight > 0.001f)
                    {
                        dyeSource.ToHsl(out var h, out var s, out var l);
                        var glowS = Math.Clamp(s * 2.5f + 30f * colorWeight, 30f, 100f);
                        var glowL = Math.Clamp(l * 0.20f + 48f, 44f, 62f);
                        var pureGlow = SKColor.FromHsl(h, glowS, glowL);
                        dyeR = pureGlow.Red;
                        dyeG = pureGlow.Green;
                        dyeB = pureGlow.Blue;
                        dyeWeight = colorWeight;
                    }
                    else if (!string.IsNullOrEmpty(frame.Theme.AccentColor) && SKColor.TryParse(frame.Theme.AccentColor, out var accent))
                    {
                        dyeR = accent.Red;
                        dyeG = accent.Green;
                        dyeB = accent.Blue;
                        dyeWeight = 0.85f;
                    }

                    if (dyeWeight > 0.001f)
                    {
                        // 1. Vibrant chromatic glaze on the outer meniscus edge
                        var glazeStrength = Math.Clamp(edgeTint * 1.5f, 0f, 1f);
                        var glazeMix = glazeStrength * bezelAura * 0.85f * dyeWeight;
                        r += (dyeR - r) * glazeMix;
                        g += (dyeG - g) * glazeMix;
                        b += (dyeB - b) * glazeMix;

                        // 2. Specular highlight is strongly tinted with the saturated dye color
                        var hlTint = Math.Clamp(MathF.Pow(edgeTint, 0.55f) * 1.35f * dyeWeight, 0f, 1f);
                        hlR = (1f - hlTint) * 255f + hlTint * dyeR;
                        hlG = (1f - hlTint) * 255f + hlTint * dyeG;
                        hlB = (1f - hlTint) * 255f + hlTint * dyeB;
                    }
                }

                // Meniscus highlight & crisp glass rim line. 柔光玻璃 widens the stroke into a
                // diffuse halo and softens the directional bias, so light wraps around the
                // numeral edge instead of drawing a line on it.
                var cosL = nx * lx + ny * ly;
                var directional = MathF.Max(0f, cosL);
                var rimEdge = soft
                    ? MathF.Pow(1f - SmoothStep(0f, rimLineWidth * 6f, depth), 2.0f)
                    : 1f - SmoothStep(0f, rimLineWidth, depth);

                // Directional specular glint: only the light-facing edge catches direct specular shine!
                var rimLight = soft
                    ? rimEdge * (0.20f + 0.55f * MathF.Pow(directional, 0.70f)) * 0.80f
                    : rimEdge * (0.12f + 0.88f * MathF.Pow(directional, 1.2f));

                // Direct rim dye: guarantees the outer stroke perimeter is visibly dyed, not white!
                if (dyeWeight > 0.001f && edgeTint > 0.001f)
                {
                    var rimDyeFactor = rimEdge * Math.Clamp(edgeTint * 1.4f, 0f, 1f) * dyeWeight * 0.75f;
                    r += (dyeR - r) * rimDyeFactor;
                    g += (dyeG - g) * rimDyeFactor;
                    b += (dyeB - b) * rimDyeFactor;
                }

                var meniscusLight = 0f;
                var spreadWidth = lensWidth * 1.6f;
                if (depth < spreadWidth && lensWidth > 0.001f)
                {
                    var t = Math.Clamp(depth / lensWidth, 0f, 1f);
                    var tilt = (depth < lensWidth) ? MathF.Pow(1f - t, 2.0f) : 0f;
                    var tnx = nx * tilt * 0.82f;
                    var tny = ny * tilt * 0.82f;
                    var tnz = MathF.Sqrt(Math.Max(0.01f, 1f - tnx * tnx - tny * tny));

                    var ndotl = Math.Max(0f, tnx * l3x + tny * l3y + tnz * l3z);
                    var specGlint = MathF.Pow(ndotl, soft ? 3f : 28f);
                    var specGlow = MathF.Pow(ndotl, soft ? 2f : 8f);
                    var fresnel = MathF.Pow(1f - tnz, 3) * (soft ? 0.16f : 0.35f);

                    var bevelLight = ((0.70f * specGlint + 0.30f * specGlow) * MathF.Max(0f, cosL) * (soft ? 0.38f : 0.90f)
                                      + fresnel * (soft ? 0.18f : 0.25f)) * (1f - t) * (1f - t);
                    var innerT = depth / spreadWidth;
                    var innerRoll = 0.5f * (1f + MathF.Cos(MathF.PI * innerT));
                    var innerSheen = MathF.Pow(ndotl, soft ? 4f : 6f) * MathF.Max(0f, cosL) * (soft ? 0.04f : 0.08f) * innerRoll;

                    meniscusLight = bevelLight + innerSheen;
                }

                // 柔光玻璃: the diffuse halo along the strokes — the numerals read as softly lit
                // rather than outlined, and it survives EdgeWidth = 0 (no lens at all).
                var softHalo = 0f;
                if (glowStrength > 0.001f)
                {
                    var haloWidth = Math.Min(
                        Math.Max(lensWidth * 1.6f, Math.Min(width, height) * 0.05f),
                        Math.Min(width, height) * 0.18f);
                    var halo = (haloWidth > 0f && depth < haloWidth)
                        ? MathF.Pow(1f - depth / haloWidth, 2.4f)
                        : 0f;
                    var wrap = 0.62f + 0.38f * MathF.Max(0f, cosL);
                    softHalo = glowStrength * halo * wrap * 0.55f;
                }

                var totalLight = highlightFactor * (rimLight * (soft ? 1.25f : 1.10f) + meniscusLight * 0.65f + ambientLuster);
                // Cap total light mix at 0.78 so specular glint never totally blinds out the saturated dye color underneath
                var lightMix = Math.Clamp(totalLight, 0f, 0.78f);

                r += (hlR - r) * lightMix;
                g += (hlG - g) * lightMix;
                b += (hlB - b) * lightMix;

                // The halo is light, not paint: a mostly neutral wash with a hint of the dye colour.
                if (softHalo > 0.001f)
                {
                    var cast = 0.30f * edgeTint;
                    r += (255f + (hlR - 255f) * cast - r) * softHalo;
                    g += (255f + (hlG - 255f) * cast - g) * softHalo;
                    b += (255f + (hlB - 255f) * cast - b) * softHalo;
                }

                // Anti-aliased alpha at character boundaries
                var alpha = maskVal;
                pixels[idx] = new SKColor(ClampByte(r), ClampByte(g), ClampByte(b), alpha);
            }
        });

        using var resultSurface = SKSurface.Create(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul));
        using var resultBitmap = new SKBitmap();
        var handle = System.Runtime.InteropServices.GCHandle.Alloc(pixels, System.Runtime.InteropServices.GCHandleType.Pinned);
        try
        {
            resultBitmap.InstallPixels(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul),
                handle.AddrOfPinnedObject());
            resultSurface.Canvas.DrawBitmap(resultBitmap, 0, 0);
            using var imageSnapshot = resultSurface.Snapshot();
            using var encoded = imageSnapshot.Encode(SKEncodedImageFormat.Png, 95);
            return encoded.ToArray();
        }
        finally
        {
            handle.Free();
        }
    }

    /// <summary>
    /// Resolves the glyph lens geometry for one frame.
    ///
    /// The meniscus is the whole point of the material: without it the numerals are only a
    /// blurred, tinted fill, so they read as 毛玻璃 (frosted) even when the global theme is
    /// 液态玻璃. The per-widget setting is therefore an <b>optional manual override</b>:
    /// <list type="bullet">
    /// <item><c>&gt; 0</c>: explicit lens width in DIPs, for a user who tuned the numerals
    /// separately from the rest of the desktop.</item>
    /// <item><c>null</c> or <c>&lt;= 0</c> (the default): the lens is derived from the global
    /// liquid glass optics (<see cref="LiquidGlassSettings.EdgeWidth"/> for the ring width and
    /// <see cref="LiquidGlassSettings.Refraction"/> for the bending strength), which is what
    /// "跟随全局主题" has to reproduce.</item>
    /// </list>
    /// Exposed (rather than inlined) so the optics can be asserted numerically in
    /// <c>tests/ClockThemeChecks</c>.
    /// </summary>
    /// <param name="optics">Effective global liquid glass parameters.</param>
    /// <param name="scale">Render scaling (physical px per DIP).</param>
    /// <param name="strokeRadius">Half-thickness of the numeral strokes, in render px.</param>
    /// <param name="glyphEdgeScale">Adaptive edge scale for compact layouts.</param>
    /// <param name="glyphShiftScale">Adaptive displacement scale for compact layouts.</param>
    /// <param name="refractionWidth">
    /// Explicit lens width in DIPs that overrides the adaptive lens. Production callers pass
    /// <c>null</c> (the clock has no widget-level 边缘折射宽度 override any more) — the parameter
    /// is kept so the optics checks can sweep lens widths directly.
    /// </param>
    public static (float LensWidth, float LensShift, float Dispersion) ResolveLens(
        LiquidGlassSettings optics,
        float scale,
        float strokeRadius,
        float glyphEdgeScale,
        float glyphShiftScale,
        double? refractionWidth,
        bool soft = false)
    {
        var manualWidth = refractionWidth.HasValue
            ? (float)Math.Max(0.0, refractionWidth.Value)
            : 0f;

        if (manualWidth > 0.001f)
        {
            var width = SoftClamp(manualWidth * scale * glyphEdgeScale, 0.5f * scale, strokeRadius * 0.46f);
            var shift = SoftClamp((float)(optics.Refraction / 100.0) * width * 1.85f * glyphShiftScale, 0.5f * scale, width * 2.5f);
            return (width, shift, (float)(optics.Dispersion / 100.0));
        }

        // EdgeWidth 0 = the lens ring is switched off globally: the numerals keep their
        // diffusion, dye and rim light, but nothing is bent.
        if (optics.EdgeWidth <= 0.0) return (0f, 0f, (float)(optics.Dispersion / 100.0));

        var edgeFrac = (float)Math.Clamp((optics.EdgeWidth / 24.0) * 0.38f * glyphEdgeScale, 0.18f, 0.46f);
        var adaptiveWidth = SoftClamp(strokeRadius * edgeFrac, 1.2f * scale, strokeRadius * 0.46f);
        var adaptiveShift = SoftClamp((float)(optics.Refraction / 100.0) * adaptiveWidth * 1.85f * glyphShiftScale, 0.8f * scale, adaptiveWidth * 2.5f);

        if (soft)
        {
            // 柔光玻璃: the same "wide and shallow" the card material uses — 1.55x the band at
            // less than half the displacement.
            adaptiveWidth = SoftClamp(adaptiveWidth * 1.55f, 1.2f * scale, strokeRadius * 0.46f);
            adaptiveShift *= 0.45f;
        }

        return (adaptiveWidth, adaptiveShift, (float)(optics.Dispersion / 100.0));
    }

    /// <summary>
    /// Clamp that tolerates an inverted range. A degenerate glyph mask (hairline strokes, or a
    /// mask with no interior pixels at all) bottoms out <c>strokeRadius</c> at its floor, where
    /// the hard lower bound exceeds <c>strokeRadius * 0.46</c> and <see cref="Math.Clamp(float,float,float)"/>
    /// would throw — which silently aborted the whole background render and left the numerals
    /// with the flat fallback wash.
    /// </summary>
    private static float SoftClamp(float value, float min, float max)
        => Math.Clamp(value, min, Math.Max(min, max));

    /// <summary>
    /// Per-frame compute buffers, reused across frames.
    ///
    /// One frame needs eight arrays of <c>width × height</c> entries — seven <c>float[]</c> for the
    /// distance field and its gradients plus the <c>SKColor[]</c> result — which is ~29 bytes per
    /// pixel (≈8 MB for a 4×2 clock at 2×, all of it on the large object heap). Allocating that
    /// per tick churned gigabytes a day through the LOH and fragmented the heap for no reason: the
    /// clock renders one frame at a time, and every pixel of these arrays is written before it is
    /// read. Buffers are pooled by pixel count and handed back when the frame is done.
    /// </summary>
    private sealed class Scratch
    {
        public int Capacity;
        public int LineCapacity;
        public float[] Dist = [];
        public float[] Nx = [];
        public float[] Ny = [];
        public float[] RawGx = [];
        public float[] RawGy = [];
        public float[] TempGx = [];
        public float[] TempGy = [];
        public SKColor[] Pixels = [];

        /// <summary>Transform workspace: the seeds, the squared-distance result and the 1-D helpers.</summary>
        public float[] Seed = [];
        public float[] Squared = [];
        public float[] Line = [];
        public float[] LineOut = [];
        public int[] Envelope = [];
        public float[] Boundaries = [];

        /// <summary>Grow the buffers to hold <paramref name="size"/> pixels and
        /// <paramref name="lineLength"/>-long transform lines.</summary>
        public void EnsureCapacity(int size, int lineLength)
        {
            if (Capacity < size)
            {
                Dist = new float[size];
                Nx = new float[size];
                Ny = new float[size];
                RawGx = new float[size];
                RawGy = new float[size];
                TempGx = new float[size];
                TempGy = new float[size];
                Pixels = new SKColor[size];
                Seed = new float[size];
                Squared = new float[size];
                Capacity = size;
            }

            if (LineCapacity < lineLength)
            {
                Line = new float[lineLength];
                LineOut = new float[lineLength];
                Envelope = new int[lineLength];
                // One extra slot: the envelope stores a boundary per parabola plus the trailing +∞.
                Boundaries = new float[lineLength + 1];
                LineCapacity = lineLength;
            }
        }
    }

    private static readonly object scratchGate = new();
    private static readonly Stack<Scratch> scratchPool = new();

    /// <summary>Take a set of buffers sized for <paramref name="size"/> pixels (never blocks a frame).</summary>
    private static Scratch RentScratch(int size, int lineLength)
    {
        Scratch scratch;
        lock (scratchGate)
        {
            scratch = scratchPool.Count > 0 ? scratchPool.Pop() : new Scratch();
        }

        scratch.EnsureCapacity(size, lineLength);
        return scratch;
    }

    /// <summary>Hand the buffers back. Only a couple are kept: more would just be resident memory.</summary>
    private static void ReturnScratch(Scratch scratch)
    {
        lock (scratchGate)
        {
            if (scratchPool.Count < 2) scratchPool.Push(scratch);
        }
    }

    /// <summary>
    /// Builds the sub-pixel depth field plus the smoothed outward normal field the whole material
    /// is read from. Two properties make it accurate enough for a 1.5 px rim line, and both are
    /// needed — the field is sampled for the lens displacement, the rim line, the edge dye and the
    /// directional specular, so any wobble in it shows up directly on the numerals:
    /// <list type="number">
    /// <item><b>The distance transform is exact.</b> The previous 3×3 chamfer approximation
    /// quantised every distance to 1 or √2, and — the part that actually hurt — the quantisation
    /// depended on the *local edge direction*: the first ring inside the glyph measured ~1 px along
    /// an axis-aligned edge and ~1.414 px along a 45° one. On a slanted contour that alternation
    /// repeats pixel by pixel, and with a rim line barely 1.5 px wide it modulated the rim's
    /// brightness by several times from one pixel to the next. That is the "毛刺 / 断断续续"
    /// (burred, dashed) edge: continuous along the top of a stroke, broken into dots along its
    /// slanted sides. Replacing the sweep with the exact transform is what removes it — measured
    /// against an analytic disc, the near-field outline error drops from 0.40 px to 0.15 px
    /// (see <c>tests/ClockGlassChecks</c>).</item>
    /// <item><b>The anti-aliased coverage is used instead of a hard 50% threshold.</b> A font
    /// rasteriser's coverage *is* the sub-pixel position of the outline within a boundary pixel
    /// (c ≈ t + 0.5 for an edge at signed distance t from the pixel centre), so an anti-aliased
    /// pixel now reports where the outline runs through it (0.03 px mean error) instead of being
    /// pinned to the pixel grid.</item>
    /// </list>
    /// Pixels a whole pixel or more inside the glyph keep the classic bitmap-seeded ±0.5 px
    /// quantisation (there is no sub-pixel information beyond the boundary pixel itself), but it is
    /// a smooth radial staircase rather than the direction-dependent alternation described above.
    /// </summary>
    private static void ComputeDistanceField(byte[] mask, int width, int height, Scratch scratch,
        out float maxStrokeDepth, out float avgStrokeDepth)
    {
        const float INF = 1e20f;
        var size = width * height;
        var dist = scratch.Dist;
        var nx = scratch.Nx;
        var ny = scratch.Ny;

        // Reused buffers carry the previous frame's field: the normals and the raw gradients are
        // only written where the mask is set, so anything stale would leak into this frame.
        Array.Clear(nx, 0, size);
        Array.Clear(ny, 0, size);
        Array.Clear(scratch.RawGx, 0, size);
        Array.Clear(scratch.RawGy, 0, size);
        Array.Clear(scratch.TempGx, 0, size);
        Array.Clear(scratch.TempGy, 0, size);

        // Seeds: background pixels (coverage below the 50% level) are the sources of the transform,
        // glyph pixels are infinite. What comes back is the squared distance to the nearest
        // background pixel *centre*, which the coverage term below turns into a distance to the
        // outline itself.
        var seed = scratch.Seed;
        for (var i = 0; i < size; i++)
        {
            seed[i] = mask[i] < 128 ? 0f : INF;
        }

        DistanceTransformExact(seed, scratch.Squared, width, height, scratch);

        var squared = scratch.Squared;
        for (var i = 0; i < size; i++)
        {
            var coverage = mask[i] * (1f / 255f);
            // Straight-edge identity along the inward normal: the outline sits 1.5 px beyond the
            // centre of the nearest background pixel, and the pixel's own coverage says where in
            // its last half pixel it crosses — exact for an anti-aliased boundary pixel, and the
            // classic ±0.5 px grid estimate further inside. The max() keeps pixels whose outline
            // passes *outside* them on the correct (negative) side.
            var depth = MathF.Sqrt(squared[i]) + coverage - 1.5f;
            var local = coverage - 0.5f;
            dist[i] = local > depth ? local : depth;
        }

        // Calculate stroke depth statistics. Pixels the transform could not reach (a mask with no
        // background at all) carry a sentinel far larger than any real stroke, and must not drag
        // the stroke-radius estimate with them.
        var depthCap = Math.Min(width, height);
        var maxD = 0f;
        var sumD = 0.0;
        var count = 0;
        for (var i = 0; i < size; i++)
        {
            var d = dist[i];
            if (d > 0f && d < depthCap)
            {
                if (d > maxD) maxD = d;
                sumD += d;
                count++;
            }
        }
        maxStrokeDepth = (count > 0) ? maxD : 2f;
        avgStrokeDepth = (count > 0) ? (float)(sumD / count) : 1f;

        // Raw distance gradient
        var rawGx = scratch.RawGx;
        var rawGy = scratch.RawGy;
        for (var y = 1; y < height - 1; y++)
        {
            var row = y * width;
            for (var x = 1; x < width - 1; x++)
            {
                var idx = row + x;
                if (mask[idx] > 10)
                {
                    rawGx[idx] = dist[idx + 1] - dist[idx - 1];
                    rawGy[idx] = dist[idx + width] - dist[idx - width];
                }
            }
        }

        // Smooth continuous normal field: 7-tap separable filter over glyph mask
        // Eliminates corner medial-axis creases and triangular seams
        var tempGx = scratch.TempGx;
        var tempGy = scratch.TempGy;
        var weights = Weights;

        for (var y = 0; y < height; y++)
        {
            var row = y * width;
            for (var x = 0; x < width; x++)
            {
                var idx = row + x;
                if (mask[idx] <= 10) continue;
                float sx = 0f, sy = 0f, sw = 0f;
                for (int k = -3; k <= 3; k++)
                {
                    var px = Math.Clamp(x + k, 0, width - 1);
                    var pidx = row + px;
                    if (mask[pidx] > 10)
                    {
                        var w = weights[k + 3];
                        sx += rawGx[pidx] * w;
                        sy += rawGy[pidx] * w;
                        sw += w;
                    }
                }
                if (sw > 0f)
                {
                    tempGx[idx] = sx / sw;
                    tempGy[idx] = sy / sw;
                }
            }
        }

        for (var y = 0; y < height; y++)
        {
            var row = y * width;
            for (var x = 0; x < width; x++)
            {
                var idx = row + x;
                if (mask[idx] <= 10) continue;
                float sx = 0f, sy = 0f, sw = 0f;
                for (int k = -3; k <= 3; k++)
                {
                    var py = Math.Clamp(y + k, 0, height - 1);
                    var pidx = py * width + x;
                    if (mask[pidx] > 10)
                    {
                        var w = weights[k + 3];
                        sx += tempGx[pidx] * w;
                        sy += tempGy[pidx] * w;
                        sw += w;
                    }
                }
                if (sw > 0f)
                {
                    var gx = sx / sw;
                    var gy = sy / sw;
                    var len = MathF.Sqrt(gx * gx + gy * gy);
                    if (len > 0.001f)
                    {
                        nx[idx] = -gx / len;
                        ny[idx] = -gy / len;
                    }
                }
            }
        }
    }

    /// <summary>
    /// Exact squared Euclidean distance transform of a separable function
    /// (Felzenszwalb &amp; Huttenlocher, "Distance Transforms of Sampled Functions", 2012):
    /// <c>D(q) = min_p f(p) + (q − p)²</c>, computed as two independent 1-D passes over the rows
    /// and then the columns. Linear time and — unlike the chamfer sweep it replaces — free of the
    /// direction-dependent error that made the glyph contours ragged.
    /// </summary>
    private static void DistanceTransformExact(float[] seed, float[] result, int width, int height, Scratch scratch)
    {
        var line = scratch.Line;
        var lineOut = scratch.LineOut;

        for (var y = 0; y < height; y++)
        {
            var row = y * width;
            for (var x = 0; x < width; x++) line[x] = seed[row + x];
            DistanceTransformLine(line, lineOut, width, scratch);
            for (var x = 0; x < width; x++) result[row + x] = lineOut[x];
        }

        for (var x = 0; x < width; x++)
        {
            for (var y = 0; y < height; y++) line[y] = result[y * width + x];
            DistanceTransformLine(line, lineOut, height, scratch);
            for (var y = 0; y < height; y++) result[y * width + x] = lineOut[y];
        }
    }

    /// <summary>
    /// One dimension of the transform: the lower envelope of the parabolas
    /// <c>f(p) + (q − p)²</c>, sampled at every index of a line of length <paramref name="n"/>.
    /// The envelope is built as its parabola boundaries are found, then walked once — hence O(n).
    /// </summary>
    private static void DistanceTransformLine(float[] f, float[] d, int n, Scratch scratch)
    {
        var v = scratch.Envelope;
        var z = scratch.Boundaries;

        var k = 0;
        v[0] = 0;
        z[0] = float.NegativeInfinity;
        z[1] = float.PositiveInfinity;

        for (var q = 1; q < n; q++)
        {
            var s = ParabolaIntersection(f, v[k], q);
            while (k > 0 && s <= z[k])
            {
                k--;
                s = ParabolaIntersection(f, v[k], q);
            }
            k++;
            v[k] = q;
            z[k] = s;
            z[k + 1] = float.PositiveInfinity;
        }

        k = 0;
        for (var q = 0; q < n; q++)
        {
            while (z[k + 1] < q) k++;
            var delta = (float)(q - v[k]);
            d[q] = delta * delta + f[v[k]];
        }
    }

    /// <summary>Where the parabolas centred at <paramref name="p"/> and <paramref name="q"/> cross.</summary>
    private static float ParabolaIntersection(float[] f, int p, int q) =>
        ((f[q] + q * (float)q) - (f[p] + p * (float)p)) / (2f * q - 2f * p);

    private static float Displacement(float depth, float lensWidth, float lensShift)
    {
        if (depth <= 0f || depth >= lensWidth || lensWidth <= 0.001f || lensShift <= 0.001f) return 0f;
        var t = depth / lensWidth;
        var shape = MathF.Sin(MathF.PI * t) * MathF.Pow(1f - t, 1.4f) / 0.45f;
        return lensShift * Math.Max(0f, shape);
    }

    private static float Channel(float val, byte coat, float luma, float adapt, float localTint)
    {
        var c = luma + (val - luma) * Saturation;
        c += (255f - c) * adapt;
        return c * (1f - localTint) + coat * localTint;
    }

    /// <summary>Linear interpolation between channel values (spectrum blending, halo casting).</summary>
    private static float Mix(float from, float to, float amount) => from + (to - from) * amount;

    private static float FrostMix(float luma)
    {
        var normalized = luma / 255f;
        var deficit = Math.Clamp(1f - normalized, 0f, 1f);
        return Math.Min(0.09f, 0.015f + 0.07f * deficit * deficit);
    }

    private static float SmoothStep(float e0, float e1, float x)
    {
        var t = Math.Clamp((x - e0) / (e1 - e0), 0f, 1f);
        return t * t * (3f - 2f * t);
    }

    private static byte ClampByte(float val) => (byte)Math.Clamp((int)Math.Round(val), 0, 255);

    private static SKColor SamplePixel(SKColor[] pixels, int w, int h, float x, float y)
    {
        var ix = Math.Clamp((int)MathF.Floor(x), 0, w - 1);
        var iy = Math.Clamp((int)MathF.Floor(y), 0, h - 1);
        var x1 = Math.Min(ix + 1, w - 1);
        var y1 = Math.Min(iy + 1, h - 1);
        var a = pixels[iy * w + ix];
        var b = pixels[iy * w + x1];
        var c = pixels[y1 * w + ix];
        var d = pixels[y1 * w + x1];
        var fx = x - ix;
        var fy = y - iy;
        return new SKColor(
            Lerp(a.Red, b.Red, c.Red, d.Red),
            Lerp(a.Green, b.Green, c.Green, d.Green),
            Lerp(a.Blue, b.Blue, c.Blue, d.Blue));

        byte Lerp(byte p, byte q, byte r, byte s) =>
            (byte)Math.Clamp((int)MathF.Round((p + (q - p) * fx) * (1f - fy) + (r + (s - r) * fx) * fy), 0, 255);
    }
}
