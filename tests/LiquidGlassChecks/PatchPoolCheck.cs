using System;
using System.Collections.Generic;
using SkiaSharp;
using DeskCanvas.Services;
using DeskCanvas.Services.GlassCapture;

/// <summary>
/// Ownership contract of the capture patch buffer pool.
/// <para>
/// The launcher's live sample publishes tens of frames per second, and each one used to allocate a
/// whole patch (4.7 MB at 1152×1024) of <b>native</b> pixels. SkiaSharp puts that memory outside the
/// GC heap, so the process ballooned to 1.5 GB and only came back when an unrelated managed
/// allocation triggered a collection. The fix is to recycle the buffers, which only works if the
/// ownership chain is exact:
/// </para>
/// <list type="number">
///   <item><description>a snapshot carrying a <i>pooled</i> bitmap must give it back to the pool
///   when its last lease reference dies — not dispose it;</description></item>
///   <item><description>a snapshot carrying an ordinary bitmap must keep disposing it, or every
///   other capture path would start leaking;</description></item>
///   <item><description>the recycled buffer must still be a valid, writable, correctly strided
///   picture of the requested size.</description></item>
/// </list>
/// <para>
/// This is pure ownership bookkeeping — no display, no GPU, no capture — so it runs in the normal
/// check sweep.
/// </para>
/// </summary>
internal static class PatchPoolCheck
{
    public static int Run()
    {
        var failures = 0;

        void Assert(bool condition, string label)
        {
            if (condition) Console.WriteLine($"  PASS: {label}");
            else
            {
                Console.WriteLine($"  FAIL: {label}");
                failures++;
            }
        }

        Console.WriteLine("=== 采集补丁缓冲池 (ownership) ===");

        // ---- 1. a rented buffer is recycled, not reallocated -------------------------------
        var first = PatchBitmapPool.Rent(64, 40);
        Assert(first is { Width: 64, Height: 40 }, "a rented buffer has the requested size");
        Assert(first.GetPixels() != IntPtr.Zero, "a rented buffer owns pixels");
        Assert(first.RowBytes >= 64 * 4, "a rented buffer is at least as wide as 64 BGRA pixels per row");

        PatchBitmapPool.Return(first);
        var again = PatchBitmapPool.Rent(64, 40);
        Assert(ReferenceEquals(first, again), "the pool hands the same buffer back instead of allocating");
        PatchBitmapPool.Return(again);

        // ---- 2. a pooled snapshot returns its buffer instead of disposing it ---------------
        var leased = PatchBitmapPool.Rent(48, 32);
        var pooledSnapshot = WallpaperSnapshot.FromBitmap(null, SKColors.Black, leased,
            live: true, pooled: true);
        pooledSnapshot.Dispose();
        Assert(leased.Handle != IntPtr.Zero, "a pooled snapshot keeps the pixels alive after its lease is released");

        var recycled = PatchBitmapPool.Rent(48, 32);
        Assert(ReferenceEquals(leased, recycled), "the released pooled buffer comes straight back out of the pool");
        PatchBitmapPool.Return(recycled);

        // ---- 3. an ordinary snapshot still owns and frees its bitmap -----------------------
        var ordinary = new SKBitmap(new SKImageInfo(48, 32, SKColorType.Bgra8888, SKAlphaType.Opaque));
        var ordinarySnapshot = WallpaperSnapshot.FromBitmap(null, SKColors.Black, ordinary, live: true);
        ordinarySnapshot.Dispose();
        Assert(ordinary.Handle == IntPtr.Zero, "a non-pooled snapshot still disposes its bitmap");

        // ---- 4. references are counted, so a second holder postpones the return -----------
        var shared = PatchBitmapPool.Rent(32, 24);
        var sharedSnapshot = WallpaperSnapshot.FromBitmap(null, SKColors.Black, shared,
            live: true, pooled: true);
        var second = sharedSnapshot.WithPlacement("10", false);   // takes its own reference
        sharedSnapshot.Dispose();
        var whileHeld = PatchBitmapPool.Rent(32, 24);
        Assert(!ReferenceEquals(shared, whileHeld), "a buffer with a live second reference is not recycled yet");
        PatchBitmapPool.Return(whileHeld);

        second.Dispose();
        var afterRelease = PatchBitmapPool.Rent(32, 24);
        Assert(ReferenceEquals(shared, afterRelease), "the buffer is recycled once the last reference is released");
        PatchBitmapPool.Return(afterRelease);

        // ---- 5. a recycled buffer is writable and reports the new content ------------------
        var reused = PatchBitmapPool.Rent(16, 16);
        reused.SetPixel(3, 5, new SKColor(10, 20, 30));
        Assert(reused.GetPixel(3, 5) == new SKColor(10, 20, 30), "a recycled buffer accepts and returns a pixel write");
        PatchBitmapPool.Return(reused);

        // ---- 6. rent/return in a loop reuses one buffer, however many frames run ----------
        SKBitmap? repeated = null;
        var grew = 0;
        for (var i = 0; i < 10; i++)
        {
            var bitmap = PatchBitmapPool.Rent(24, 24);
            if (repeated == null) repeated = bitmap;
            else if (!ReferenceEquals(repeated, bitmap)) grew++;
            PatchBitmapPool.Return(bitmap);
        }

        Assert(grew == 0, $"ten sequential rent/return rounds keep handing back the same buffer (saw {grew + 1} distinct)");

        // ---- 7. the idle list is capped, not unbounded ------------------------------------
        var batch = new List<SKBitmap>();
        for (var i = 0; i < 6; i++) batch.Add(PatchBitmapPool.Rent(28, 28));
        foreach (var bitmap in batch) PatchBitmapPool.Return(bitmap);

        var nextBatch = new List<SKBitmap>();
        for (var i = 0; i < 6; i++) nextBatch.Add(PatchBitmapPool.Rent(28, 28));
        var reusedCount = 0;
        foreach (var bitmap in nextBatch)
        foreach (var previous in batch)
            if (ReferenceEquals(previous, bitmap)) reusedCount++;

        Assert(reusedCount is > 0 and <= 3,
            $"the pool recycles at most three idle buffers (reused {reusedCount} of six, cap 3)");
        foreach (var bitmap in nextBatch) PatchBitmapPool.Return(bitmap);

        // ---- 8. copying a frame into a rented buffer keeps that buffer's pixels ------------
        // This is the whole premise of CopyLatest: rent a buffer, copy the held crop into it, hand
        // it out. SKBitmap.CopyTo looked like the obvious call and silently broke it — it installs
        // fresh pixel storage on the destination, so a patch-sized native block was still allocated
        // per published frame and the pool was decoration. The pointer assertion is the regression
        // guard: it is the only thing that sees the difference.
        var sourceInfo = new SKImageInfo(40, 30, SKColorType.Bgra8888, SKAlphaType.Opaque);
        using var source = new SKBitmap(sourceInfo);
        source.Erase(new SKColor(200, 40, 10));

        var destination = PatchBitmapPool.Rent(40, 30);
        var destinationPixels = destination.GetPixels();
        var copied = PatchBitmapPool.Copy(source, destination);
        Assert(copied, "copying a frame into a rented buffer succeeds");
        Assert(destination.GetPixels() == destinationPixels,
            "the copy writes into the buffer's own pixels instead of reallocating them");
        Assert(destination.GetPixel(5, 5) == new SKColor(200, 40, 10),
            "the copied buffer actually carries the source pixels");

        // A recycled buffer is rewritten, not reallocated, on the next round: the steady state of a
        // live sampler must be "same pointer, new pixels".
        var secondPixels = destination.GetPixels();
        source.Erase(new SKColor(20, 90, 240));
        PatchBitmapPool.Copy(source, destination);
        Assert(destination.GetPixels() == secondPixels && destination.GetPixel(9, 9) == new SKColor(20, 90, 240),
            "a reused buffer keeps its allocation across frames and takes the new content");
        PatchBitmapPool.Return(destination);

        // A mismatched size is refused rather than corrupting the buffer.
        using var wrongSize = new SKBitmap(new SKImageInfo(20, 10, SKColorType.Bgra8888, SKAlphaType.Opaque));
        var guard = PatchBitmapPool.Rent(40, 30);
        Assert(!PatchBitmapPool.Copy(wrongSize, guard), "a differently sized frame is refused");
        PatchBitmapPool.Return(guard);

        // ---- 9. an over-released lease must not hand one buffer to two snapshots -----------
        // The lease used to release whenever its counter reached zero *or below*, so a second
        // Dispose pushed the same buffer into the free list twice. Two later rents then returned the
        // same pixels to two live snapshots, and each publish overwrote the other's content — which
        // the glass renders as wallpaper sampled from the wrong region.
        var guarded = PatchBitmapPool.Rent(56, 36);
        var guardedSnapshot = WallpaperSnapshot.FromBitmap(null, SKColors.Black, guarded,
            live: true, pooled: true);
        guardedSnapshot.Dispose();
        guardedSnapshot.Dispose();   // second release on the same lease

        var firstRent = PatchBitmapPool.Rent(56, 36);
        var secondRent = PatchBitmapPool.Rent(56, 36);
        Assert(!ReferenceEquals(firstRent, secondRent),
            "a double-released lease never yields the same buffer twice");
        PatchBitmapPool.Return(firstRent);
        PatchBitmapPool.Return(secondRent);

        var duplicate = PatchBitmapPool.Rent(20, 14);
        PatchBitmapPool.Return(duplicate);
        PatchBitmapPool.Return(duplicate);   // duplicate release straight into the pool
        var afterDuplicateA = PatchBitmapPool.Rent(20, 14);
        var afterDuplicateB = PatchBitmapPool.Rent(20, 14);
        Assert(!ReferenceEquals(afterDuplicateA, afterDuplicateB),
            "the pool ignores a duplicate release instead of pooling one buffer twice");
        PatchBitmapPool.Return(afterDuplicateA);
        PatchBitmapPool.Return(afterDuplicateB);

        PatchBitmapPool.Clear();
        Console.WriteLine(failures == 0 ? "patch pool check passed" : $"patch pool check FAILED ({failures})");
        return failures;
    }
}
