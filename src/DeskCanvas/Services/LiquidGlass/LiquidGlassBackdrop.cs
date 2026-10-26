using System;
using SkiaSharp;

namespace DeskCanvas.Services.LiquidGlass;

/// <summary>
/// What a liquid-glass surface samples: a bitmap, where that bitmap sits in the destination's
/// coordinate space, and how its texels relate to destination pixels.
/// <para>
/// This is deliberately the whole of the backdrop contract. It is not "the wallpaper" or "the
/// desktop" or "a capture" — a host can pass a region of the desktop, a photo, a render target
/// readback, a gradient baked to a bitmap, or a two-texel strip it wants stretched. The pass only
/// ever needs <c>texel = (Origin + p) * Scale</c>.
/// </para>
/// <para>
/// <b>Ownership:</b> the pass borrows <see cref="Bitmap"/> for the duration of the call and never
/// disposes it. The caller owns it — a snapshot's lease, a cache entry, a local `using`.
/// </para>
/// </summary>
public readonly record struct LiquidGlassBackdrop
{
    public LiquidGlassBackdrop(SKBitmap bitmap, SKPoint origin, float scale)
    {
        Bitmap = bitmap ?? throw new ArgumentNullException(nameof(bitmap));
        Origin = origin;
        Scale = scale > 0f ? scale : 1f;
    }

    public LiquidGlassBackdrop(SKBitmap bitmap, float originX, float originY, float scale)
        : this(bitmap, new SKPoint(originX, originY), scale)
    {
    }

    /// <summary>The pixels to refract. Borrowed, never disposed by the pass.</summary>
    public SKBitmap Bitmap { get; }

    /// <summary>Position of the destination's local (0,0) inside the bitmap's coordinate space.</summary>
    public SKPoint Origin { get; }

    /// <summary>Backdrop texels per destination pixel (背景清晰度, i.e. the sampling resolution).</summary>
    public float Scale { get; }

    /// <summary>Size of the sampled area in destination pixels.</summary>
    public SKSize SizeInDestinationPixels => new(Bitmap.Width / Scale, Bitmap.Height / Scale);

    /// <summary>Map a destination-space point to a texel coordinate.</summary>
    public SKPoint ToTexel(SKPoint destination) =>
        new((Origin.X + destination.X) * Scale, (Origin.Y + destination.Y) * Scale);
}
