using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using SkiaSharp;

namespace Picture.Services;

public sealed class PictureFrame : IDisposable
{
    public Bitmap Bitmap { get; }
    public int DurationMs { get; }

    public PictureFrame(Bitmap bitmap, int durationMs)
    {
        Bitmap = bitmap;
        DurationMs = Math.Max(20, durationMs);
    }

    public void Dispose()
    {
        Bitmap.Dispose();
    }
}

public sealed class DecodedPicture : IDisposable
{
    public bool IsAnimated => Frames.Count > 1;
    public IReadOnlyList<PictureFrame> Frames { get; }
    public Bitmap PrimaryBitmap => Frames.Count > 0 ? Frames[0].Bitmap : null!;

    /// <summary>
    /// Disposal flag. Disposing twice must be a no-op (frame bitmaps cannot be disposed
    /// twice), and the flag is also what lets the view model know a picture is dead without
    /// keeping every disposed picture alive in a set.
    /// </summary>
    public bool IsDisposed { get; private set; }

    public DecodedPicture(IReadOnlyList<PictureFrame> frames)
    {
        Frames = frames ?? [];
    }

    public void Dispose()
    {
        if (IsDisposed) return;
        IsDisposed = true;

        foreach (var frame in Frames)
        {
            frame.Dispose();
        }
    }
}

public static class PictureImageLoader
{
    public const int DefaultMaxDimension = 1024;
    public const int DefaultMaxAnimatedDimension = 480;

    /// <summary>
    /// Total pixel budget for one animated image (BGRA8 → 4 bytes per pixel). Every frame of an
    /// animation is kept — no frame is ever dropped — so a very long GIF is bounded by scaling
    /// its canvas down instead of by throwing frames away. 16 MP ≈ 64 MB of decoded pixels.
    /// </summary>
    private const double AnimatedPixelBudget = 16.0 * 1024 * 1024;

    public static DecodedPicture? Load(string path, int maxDimension = DefaultMaxDimension)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return null;

        // One reusable composite/decode buffer for the whole image. Without it every frame of
        // an animated image would allocate its own full-size buffer.
        using var scratch = new ScratchBitmapPool();

        try
        {
            using var stream = File.OpenRead(path);
            using var codec = SKCodec.Create(stream);

            if (codec != null && codec.Info.Width > 0 && codec.Info.Height > 0)
            {
                int frameCount = codec.FrameCount;
                if (frameCount > 1)
                {
                    var animatedFrames = DecodeAnimatedFrames(
                        codec, frameCount, Math.Min(maxDimension, DefaultMaxAnimatedDimension), scratch);
                    if (animatedFrames.Count > 0)
                    {
                        return new DecodedPicture(animatedFrames);
                    }
                }
                else
                {
                    var singleFrame = DecodeSingleFrame(codec, 0, maxDimension, scratch);
                    if (singleFrame != null)
                    {
                        return new DecodedPicture([new PictureFrame(singleFrame, 1000)]);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[PictureImageLoader] Skia codec failed for '{path}': {ex.Message}");
        }

        // Fallback 1: Avalonia native Bitmap decoder with downsampling
        try
        {
            using var fs = File.OpenRead(path);
            var avaloniaBmp = Bitmap.DecodeToWidth(fs, maxDimension, BitmapInterpolationMode.MediumQuality);
            return new DecodedPicture([new PictureFrame(avaloniaBmp, 1000)]);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[PictureImageLoader] Avalonia Bitmap fallback failed for '{path}': {ex.Message}");
        }

        // Fallback 2: Skia SKBitmap decoder with downsampling
        try
        {
            using var skBmp = SKBitmap.Decode(path);
            if (skBmp != null && skBmp.Width > 0 && skBmp.Height > 0)
            {
                SKBitmap bmpToConvert = skBmp;
                SKBitmap? resized = null;
                if (skBmp.Width > maxDimension || skBmp.Height > maxDimension)
                {
                    float scale = Math.Min((float)maxDimension / skBmp.Width, (float)maxDimension / skBmp.Height);
                    int tw = Math.Max(1, (int)Math.Round(skBmp.Width * scale));
                    int th = Math.Max(1, (int)Math.Round(skBmp.Height * scale));
                    var targetInfo = new SKImageInfo(tw, th, SKColorType.Bgra8888, SKAlphaType.Premul);
                    resized = skBmp.Resize(targetInfo, SKFilterQuality.Medium);
                    if (resized != null) bmpToConvert = resized;
                }

                var converted = ConvertSkBitmapToWriteable(bmpToConvert);
                resized?.Dispose();
                if (converted != null)
                {
                    return new DecodedPicture([new PictureFrame(converted, 1000)]);
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[PictureImageLoader] SKBitmap.Decode fallback failed for '{path}': {ex.Message}");
        }

        return null;
    }

    private static List<PictureFrame> DecodeAnimatedFrames(SKCodec codec, int totalFrames, int maxDimension, ScratchBitmapPool scratch)
    {
        var frames = new List<PictureFrame>();
        int origW = codec.Info.Width;
        int origH = codec.Info.Height;
        if (origW <= 0 || origH <= 0) return frames;

        // Never drop frames: keep the whole animation at its native frame rate, and bound the
        // memory a long animation can take by shrinking its canvas instead.
        maxDimension = Math.Min(maxDimension, DimensionForFrameBudget(origW, origH, totalFrames));

        int targetW = origW;
        int targetH = origH;
        if (origW > maxDimension || origH > maxDimension)
        {
            float scale = Math.Min((float)maxDimension / origW, (float)maxDimension / origH);
            targetW = Math.Max(1, (int)Math.Round(origW * scale));
            targetH = Math.Max(1, (int)Math.Round(origH * scale));
        }

        // Composite at the decoder's cheapest native size when it can scale (large GIF/WEBP
        // canvases otherwise cost one full-size buffer per frame while decoding).
        int decodeW = origW;
        int decodeH = origH;
        if (targetW < origW || targetH < origH)
        {
            var scaled = codec.GetScaledDimensions((float)targetW / origW);
            if (scaled.Width > 0 && scaled.Height > 0 && scaled.Width <= origW && scaled.Height <= origH)
            {
                decodeW = scaled.Width;
                decodeH = scaled.Height;
            }
        }

        var info = new SKImageInfo(decodeW, decodeH, SKColorType.Bgra8888, SKAlphaType.Premul);
        var targetInfo = new SKImageInfo(targetW, targetH, SKColorType.Bgra8888, SKAlphaType.Premul);

        int count = totalFrames;

        // Frames that later frames depend on must be retained; everything else is composited
        // through a single scratch buffer instead of allocating per frame.
        var lastDependentIndex = new Dictionary<int, int>();
        for (int i = 0; i < count; i++)
        {
            int req = codec.FrameInfo[i].RequiredFrame;
            if (req >= 0) lastDependentIndex[req] = i;
        }

        var retained = new List<SKBitmap?>();

        try
        {
            for (int i = 0; i < count; i++)
            {
                var frameInfo = codec.FrameInfo[i];
                int duration = frameInfo.Duration;
                if (duration <= 10) duration = 100; // standard GIF fallback for 0/<=10 ms

                int req = frameInfo.RequiredFrame;
                SKBitmap source;

                if (req >= 0 && req < retained.Count && retained[req] != null)
                {
                    // Dependent (delta) frame — the case for virtually every optimized GIF/WEBP:
                    // start from the frame this one depends on and let the codec composite this
                    // frame's dirty rect on top. Rendering the prior frame's pixels verbatim
                    // would freeze the whole animation on its first independent frame.
                    var result = scratch.DecodeFrame(codec, i, info, req, retained[req]!);
                    if (!ProducedPixels(result))
                    {
                        continue;
                    }
                    source = scratch.Bitmap;
                }
                else
                {
                    // Decode into the shared scratch buffer (no per-frame allocation).
                    var result = scratch.DecodeFrame(codec, i, info, -1);
                    if (!ProducedPixels(result))
                    {
                        continue;
                    }
                    source = scratch.Bitmap;
                }

                bool neededLater = lastDependentIndex.TryGetValue(i, out var lastDep) && lastDep > i;
                if (neededLater)
                {
                    // Keep a private copy so the scratch buffer stays reusable. Index-aligned
                    // with the frame number even if earlier frames were skipped, because the
                    // RequiredFrame chain addresses these copies by frame index.
                    while (retained.Count <= i) retained.Add(null);
                    var copy = new SKBitmap(info);
                    source.CopyTo(copy);
                    retained[i] = copy;
                }
                else
                {
                    while (retained.Count <= i) retained.Add(null);
                }

                SKBitmap? resizedBmp = null;
                var bmpToConvert = source;
                if (decodeW != targetW || decodeH != targetH)
                {
                    resizedBmp = source.Resize(targetInfo, SKFilterQuality.Medium);
                    if (resizedBmp != null) bmpToConvert = resizedBmp;
                }

                var wb = ConvertSkBitmapToWriteable(bmpToConvert);
                resizedBmp?.Dispose();

                // One frame per decoded frame, each carrying its own encoded delay, so the
                // animation plays back at exactly the speed it was authored at.
                if (wb != null) frames.Add(new PictureFrame(wb, duration));

                // Free retained frames that nothing depends on any more.
                for (int k = 0; k < retained.Count; k++)
                {
                    if (retained[k] == null) continue;
                    if (!lastDependentIndex.TryGetValue(k, out var last) || last <= i)
                    {
                        retained[k]!.Dispose();
                        retained[k] = null;
                    }
                }
            }
        }
        finally
        {
            foreach (var sk in retained) sk?.Dispose();
        }

        return frames;
    }

    /// <summary>
    /// Largest long-edge size an animation may use while keeping <b>all</b> its frames inside
    /// <see cref="AnimatedPixelBudget"/>. Frame count never changes the playback speed, only the
    /// resolution the frames are decoded at.
    /// </summary>
    private static int DimensionForFrameBudget(int origW, int origH, int frameCount)
    {
        if (frameCount <= 1 || origW <= 0 || origH <= 0) return int.MaxValue;

        double perFramePixelBudget = AnimatedPixelBudget / frameCount;
        double aspect = (double)origW / origH;

        // Per-frame budget is W*H = H*H*aspect → solve for the long edge.
        double longEdge = aspect >= 1.0
            ? Math.Sqrt(perFramePixelBudget * aspect)
            : Math.Sqrt(perFramePixelBudget / aspect);

        // Never shrink an animation below what a widget can actually show.
        return (int)Math.Clamp(longEdge, 128.0, 2048.0);
    }

    /// <summary>
    /// Whether a decode result produced (at least partially) usable pixels. Reasons that mean
    /// "no pixels at all" must be rejected; anything else — a truncated file, for instance —
    /// still yields an image, which is what the widget should show.
    /// </summary>
    private static bool ProducedPixels(SKCodecResult result) => result switch
    {
        SKCodecResult.InvalidParameters => false,
        SKCodecResult.InvalidInput => false,
        SKCodecResult.InternalError => false,
        SKCodecResult.CouldNotRewind => false,
        _ => true
    };

    private static Bitmap? DecodeSingleFrame(SKCodec codec, int frameIndex, int maxDimension, ScratchBitmapPool scratch)
    {
        int origW = codec.Info.Width;
        int origH = codec.Info.Height;
        if (origW <= 0 || origH <= 0) return null;

        int targetW = origW;
        int targetH = origH;
        if (origW > maxDimension || origH > maxDimension)
        {
            float scale = Math.Min((float)maxDimension / origW, (float)maxDimension / origH);
            targetW = Math.Max(1, (int)Math.Round(origW * scale));
            targetH = Math.Max(1, (int)Math.Round(origH * scale));
        }

        // Ask the decoder for the smallest size it can produce natively (JPEG can decode
        // straight to 1/2, 1/4, 1/8 ...), so a 4000x3000 photo never materialises in RAM.
        int decodeW = origW;
        int decodeH = origH;
        if (targetW < origW || targetH < origH)
        {
            var scaled = codec.GetScaledDimensions((float)targetW / origW);
            if (scaled.Width > 0 && scaled.Height > 0 && scaled.Width <= origW && scaled.Height <= origH)
            {
                decodeW = scaled.Width;
                decodeH = scaled.Height;
            }
        }

        var decodeInfo = new SKImageInfo(decodeW, decodeH, SKColorType.Bgra8888, SKAlphaType.Premul);
        var result = scratch.DecodeFrame(codec, frameIndex, decodeInfo, requiredFrame: -1);
        if (!ProducedPixels(result))
            return null;

        if (decodeW == targetW && decodeH == targetH)
        {
            return ConvertSkBitmapToWriteable(scratch.Bitmap);
        }

        var targetInfo = new SKImageInfo(targetW, targetH, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var resized = scratch.Bitmap.Resize(targetInfo, SKFilterQuality.Medium);
        return resized == null ? null : ConvertSkBitmapToWriteable(resized);
    }

    private static WriteableBitmap? ConvertSkBitmapToWriteable(SKBitmap skBitmap)
    {
        if (skBitmap.Width <= 0 || skBitmap.Height <= 0) return null;

        var wb = new WriteableBitmap(
            new PixelSize(skBitmap.Width, skBitmap.Height),
            new Vector(96, 96),
            PixelFormat.Bgra8888,
            AlphaFormat.Premul);

        try
        {
            using var locked = wb.Lock();
            unsafe
            {
                if (skBitmap.ColorType == SKColorType.Bgra8888 && skBitmap.AlphaType == SKAlphaType.Premul)
                {
                    Buffer.MemoryCopy(
                        (void*)skBitmap.GetPixels(),
                        (void*)locked.Address,
                        locked.RowBytes * skBitmap.Height,
                        skBitmap.RowBytes * skBitmap.Height);
                }
                else
                {
                    var targetInfo = new SKImageInfo(skBitmap.Width, skBitmap.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
                    using var converted = new SKBitmap(targetInfo);
                    skBitmap.CopyTo(converted, SKColorType.Bgra8888);
                    Buffer.MemoryCopy(
                        (void*)converted.GetPixels(),
                        (void*)locked.Address,
                        locked.RowBytes * skBitmap.Height,
                        converted.RowBytes * skBitmap.Height);
                }
            }
        }
        catch
        {
            // Never leave a half-initialised bitmap alive: it would keep its pixel buffer
            // allocated but could not be rendered.
            wb.Dispose();
            throw;
        }

        return wb;
    }

    /// <summary>
    /// Reusable decode/composite buffer. Keeps at most one bitmap alive for the duration of a
    /// single decode and grows (never shrinks) to the largest frame it has seen, so decoding an
    /// animated image costs one buffer instead of one per frame.
    /// </summary>
    private sealed class ScratchBitmapPool : IDisposable
    {
        private SKBitmap? bitmap;
        private SKImageInfo info;

        public SKBitmap Bitmap => bitmap!;

        /// <summary>Decodes <paramref name="frameIndex"/> into the shared buffer. When
        /// <paramref name="requiredFrame"/> is -1 the buffer starts transparent; otherwise pass
        /// <paramref name="seed"/> (the already-decoded frame this one depends on) and the codec
        /// composites the new frame's dirty rect over its pixels.</summary>
        public SKCodecResult DecodeFrame(SKCodec codec, int frameIndex, SKImageInfo frameInfo, int requiredFrame, SKBitmap? seed = null)
        {
            if (bitmap == null || bitmap.Width < frameInfo.Width || bitmap.Height < frameInfo.Height)
            {
                bitmap?.Dispose();
                bitmap = new SKBitmap(frameInfo);
                info = frameInfo;
            }

            var target = bitmap;
            if (seed == null)
            {
                // Fresh decode: the buffer (which may be larger than this frame) must not leak
                // stale pixels, so clear the used area first.
                if (target.Width != frameInfo.Width || target.Height != frameInfo.Height)
                {
                    target.Erase(SKColors.Transparent, new SKRectI(0, 0, frameInfo.Width, frameInfo.Height));
                }
                else
                {
                    target.Erase(SKColors.Transparent);
                }
            }
            else
            {
                // Composite on top of the required frame: seed the buffer with its pixels.
                // Never erase first — that would wipe the background the frame draws over.
                seed.CopyTo(target);
            }

            var options = requiredFrame >= 0
                ? new SKCodecOptions(frameIndex, requiredFrame)
                : new SKCodecOptions(frameIndex);
            return codec.GetPixels(frameInfo, target.GetPixels(), options);
        }

        public void Dispose()
        {
            bitmap?.Dispose();
            bitmap = null;
        }
    }
}
