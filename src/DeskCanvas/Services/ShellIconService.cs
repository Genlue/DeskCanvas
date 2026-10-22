using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using DeskCanvas.Core;
using SkiaSharp;

namespace DeskCanvas.Services;

/// <summary>
/// Host-level shell metadata: a shortcut's display name, its target, and its icon as an Avalonia
/// bitmap — with an in-memory and an on-disk cache.
/// <para>
/// This is the shared extraction seam the launcher consumes. The Folders plugin still carries its
/// own richer extractor (folder/jumbo/`.ico` special cases) and should be migrated onto this
/// service next — until then the <b>icon extraction itself</b> exists twice and that is tracked
/// as structural debt, not as an accepted pattern (开发规范 §1.2).
/// </para>
/// <para>
/// <b>Alpha correctness:</b> <c>IShellItemImageFactory</c> hands back a 32-bit premultiplied
/// HBITMAP. <c>System.Drawing.Image.FromHbitmap</c> drops the alpha channel and paints every
/// transparent pixel black — that is why this reads the DIB bits directly and encodes PNG from
/// the raw BGRA buffer instead.
/// </para>
/// </summary>
public sealed class ShellIconService
{
    /// <summary>
    /// Icons are always extracted at this edge length, whatever the grid draws them at.
    /// <para>
    /// The shell hands out the icon size you ask for, and asking for the grid's 64 px produced a
    /// visibly soft tile on high-DPI screens. 256 is the largest size Windows stores for the vast
    /// majority of applications, so the view downscales from the best source that exists.
    /// </para>
    /// </summary>
    public const int PreferredIconPixels = 256;

    /// <summary>
    /// Edge length a preview thumbnail is extracted at.
    /// <para>
    /// The Spotlight preview pane draws its hero image ~240 DIP wide, so 512 px covers a 2× DPI
    /// screen with room to spare. Office and PDF handlers cap themselves well below this; the
    /// number exists so a 16 MP photo is not decoded at full size just to fill a 240 DIP box.
    /// </para>
    /// </summary>
    public const int PreferredThumbnailPixels = 512;

    /// <summary>Cache rows beyond this count are dropped oldest-first (icons are cheap to rebuild).</summary>
    private const int MemoryCacheLimit = 512;

    /// <summary>
    /// Byte budget for the in-memory bitmaps. A 256×256 BGRA icon is ~256 KiB, so the count limit
    /// alone would allow ~128 MiB — this keeps the real cost bounded and lets the disk cache carry
    /// the rest (a re-read is a local PNG decode, not a shell round-trip).
    /// </summary>
    private const long MemoryBudgetBytes = 32L * 1024 * 1024;

    private readonly ConcurrentDictionary<string, Bitmap> memory = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<string> memoryOrder = new();
    private long memoryBytes;

    private readonly string diskCacheFolder;

    /// <summary>Thumbnail files kept on disk before the oldest are pruned.</summary>
    private const int ThumbnailCacheLimit = 320;

    /// <summary>How many writes may pass between two thumbnail prunes (see <see cref="PruneThumbnailCache"/>).</summary>
    private const int ThumbnailPruneInterval = 16;

    private int thumbnailWrites;

    public ShellIconService()
    {
        diskCacheFolder = Path.Combine(Const.DataFolder, "cache", "icons");
    }

    /// <summary>The number of bitmaps currently held in memory (diagnostics / checks).</summary>
    public int MemoryCount => memory.Count;

    /// <summary>
    /// Load a shortcut's icon at <paramref name="pixels"/> edge length.
    /// <para>
    /// The returned bitmap is created on a worker thread and handed to the caller; callers must
    /// apply it on the UI thread (Avalonia bitmaps are immutable once built, so this is safe).
    /// Returns <c>null</c> when the shell cannot produce an icon — callers draw a placeholder.
    /// </para>
    /// </summary>
    public Task<Bitmap?> GetIconAsync(string path, int pixels, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(path)) return Task.FromResult<Bitmap?>(null);

        // The view scales the bitmap down; asking the shell for a small size only loses detail.
        var size = Math.Max(pixels, PreferredIconPixels);
        var cachePath = DiskCachePath(path, size, IconFolder);
        if (memory.TryGetValue(cachePath, out var cached)) return Task.FromResult<Bitmap?>(cached);

        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();

            var bitmap = LoadFromDisk(cachePath) ?? ExtractAndCache(path, size, cachePath);
            if (bitmap == null) return null;

            Remember(cachePath, bitmap);
            return bitmap;
        }, cancellationToken);
    }

    /// <summary>
    /// Load the shell's <b>thumbnail</b> for a file at <paramref name="pixels"/> edge length.
    /// <para>
    /// This is the same image Explorer draws on a folder's tiles: the photo itself for an image,
    /// page 1 for a PDF, slide 1 for a presentation, a frame for a video. The shell routes the
    /// request to whichever thumbnail handler the file type registered, so the launcher inherits
    /// every format the machine can already preview — no per-format decoder of its own.
    /// </para>
    /// <para>
    /// Returns <c>null</c> when no handler exists or it refuses the file (a text file, a broken
    /// archive, a network path that timed out). Callers must fall back to <see cref="GetIconAsync"/>;
    /// asking for a thumbnail is a different question from asking for an icon, and the shell
    /// answers them with different code paths (<c>SIIGBF_THUMBNAILONLY</c> vs <c>ICONONLY</c>).
    /// </para>
    /// </summary>
    public Task<Bitmap?> GetThumbnailAsync(string path, int pixels, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(path)) return Task.FromResult<Bitmap?>(null);

        // A thumbnail is a property of a file. Folders hand back their icon through this API, which
        // the caller already has — and a missing path would just burn a COM round-trip.
        try
        {
            if (!File.Exists(path)) return Task.FromResult<Bitmap?>(null);
        }
        catch
        {
            return Task.FromResult<Bitmap?>(null);
        }

        var size = Math.Max(pixels, PreferredThumbnailPixels);
        var cachePath = DiskCachePath(path, size, ThumbnailFolder);
        if (memory.TryGetValue(cachePath, out var cached)) return Task.FromResult<Bitmap?>(cached);

        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();

            var bitmap = LoadFromDisk(cachePath) ?? ExtractThumbnailAndCache(path, size, cachePath);
            if (bitmap == null) return null;

            Remember(cachePath, bitmap);
            return bitmap;
        }, cancellationToken);
    }

    /// <summary>Drop every cached bitmap (called when the shortcut folder changes wholesale).</summary>
    public void ClearMemoryCache()
    {
        memory.Clear();
        while (memoryOrder.TryDequeue(out _)) { }
        Interlocked.Exchange(ref memoryBytes, 0);
        ReportLedger();
    }

    // ---- shell metadata -----------------------------------------------------------

    /// <summary>
    /// The shell's display name for a shortcut (respects a localized/renamed link), falling back
    /// to the file name without extension. Never throws.
    /// </summary>
    public static string GetDisplayName(string path)
    {
        try
        {
            var info = new SHFILEINFO();
            var res = SHGetFileInfo(path, 0, ref info, (uint)Marshal.SizeOf<SHFILEINFO>(), ShgfiDisplayName);
            if (res != IntPtr.Zero && !string.IsNullOrWhiteSpace(info.szDisplayName))
                return info.szDisplayName;
        }
        catch { }

        try { return Path.GetFileNameWithoutExtension(path); }
        catch { return path; }
    }

    /// <summary>
    /// Resolve a <c>.lnk</c> to its target path (so a result row can show where an entry points).
    /// Returns <c>null</c> for non-shortcuts, unresolved links, and UWP-style links with no path.
    /// </summary>
    public static string? GetShortcutTarget(string path)
    {
        if (!path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase)) return null;

        object? link = null;
        try
        {
            link = new ShellLinkCoClass();
            ((IPersistFile)link).Load(path, 0);
            var sb = new StringBuilder(1024);
            ((IShellLinkW)link).GetPath(sb, sb.Capacity, IntPtr.Zero, 0);
            var target = sb.ToString().Trim();
            return target.Length == 0 ? null : target;
        }
        catch { return null; }
        finally
        {
            if (link != null && Marshal.IsComObject(link)) Marshal.ReleaseComObject(link);
        }
    }

    // ---- internals ----------------------------------------------------------------

    private void Remember(string key, Bitmap bitmap)
    {
        if (!memory.TryAdd(key, bitmap)) return;
        memoryOrder.Enqueue(key);
        Interlocked.Add(ref memoryBytes, BytesOf(bitmap));

        while ((memory.Count > MemoryCacheLimit || Interlocked.Read(ref memoryBytes) > MemoryBudgetBytes)
               && memoryOrder.TryDequeue(out var oldest))
        {
            if (!memory.TryRemove(oldest, out var dropped)) continue;
            Interlocked.Add(ref memoryBytes, -BytesOf(dropped));
        }

        ReportLedger();
    }

    private static long BytesOf(Bitmap bitmap)
        => (long)bitmap.PixelSize.Width * bitmap.PixelSize.Height * 4;

    /// <summary>Absolute counts, per 开发规范 §7.3 — never increments.</summary>
    private void ReportLedger()
    {
        try
        {
            long bytes = 0;
            var count = 0;
            foreach (var pair in memory)
            {
                count++;
                bytes += (long)pair.Value.PixelSize.Width * pair.Value.PixelSize.Height * 4;
            }

            DeskCanvas.Runtime.ResourceLedger.Instance.Report(
                "launcher.icons", DeskCanvas.Contracts.ResourceKind.FileIcon, count, bytes, "UI");
        }
        catch
        {
            // Reporting must never break the producer.
        }
    }

    private static Bitmap? LoadFromDisk(string cachePath)
    {
        try
        {
            if (!File.Exists(cachePath)) return null;
            return new Bitmap(cachePath);
        }
        catch
        {
            // A truncated/corrupt cache file must degrade to re-extraction, not to a crash.
            try { File.Delete(cachePath); } catch { }
            return null;
        }
    }

    private Bitmap? ExtractAndCache(string path, int pixels, string cachePath)
    {
        var png = ExtractPng(path, pixels);
        if (png == null) return null;

        WriteCache(cachePath, png, ThumbnailFolder);
        return DecodePng(png);
    }

    /// <summary>
    /// Extract the shell thumbnail and re-encode it as PNG. No icon fallback on purpose: the
    /// caller already owns the icon, and a thumbnail request that the shell refuses must read as
    /// "no thumbnail", not as "here is the generic icon again".
    /// </summary>
    private Bitmap? ExtractThumbnailAndCache(string path, int pixels, string cachePath)
    {
        var png = ExtractThumbnailPng(path, pixels);
        if (png == null) return null;

        WriteCache(cachePath, png, ThumbnailFolder);
        return DecodePng(png);
    }

    private static Bitmap? DecodePng(byte[] png)
    {
        try
        {
            using var stream = new MemoryStream(png, writable: false);
            return new Bitmap(stream);
        }
        catch { return null; }
    }

    /// <summary>
    /// Persist a cache PNG. The disk cache is an optimisation: a read-only data folder still gets
    /// a working image, so every IO failure here is swallowed.
    /// </summary>
    private void WriteCache(string cachePath, byte[] png, string folder)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
            File.WriteAllBytes(cachePath, png);
        }
        catch
        {
            return;
        }

        if (folder == ThumbnailFolder) PruneThumbnailCache(cachePath);
    }

    /// <summary>
    /// Keep the thumbnail cache bounded.
    /// <para>
    /// Icons are a few KiB each, but a 512 px photo thumbnail is a few hundred — an unbounded
    /// folder would grow by hundreds of megabytes for a user who previews images all day. The
    /// check runs every <see cref="ThumbnailPruneInterval"/> writes instead of on every one, and
    /// deletes oldest-first so the image just written (the hottest one) always survives.
    /// </para>
    /// </summary>
    private void PruneThumbnailCache(string keepPath)
    {
        if (Interlocked.Increment(ref thumbnailWrites) % ThumbnailPruneInterval != 0) return;

        try
        {
            var folder = new DirectoryInfo(Path.Combine(Const.DataFolder, "cache", ThumbnailFolder));
            if (!folder.Exists) return;

            var files = folder.GetFiles("*.png");
            if (files.Length <= ThumbnailCacheLimit) return;

            foreach (var file in files.OrderBy(f => f.LastWriteTimeUtc)
                         .Take(files.Length - ThumbnailCacheLimit)
                         .Where(f => !string.Equals(f.FullName, keepPath, StringComparison.OrdinalIgnoreCase)))
            {
                try { file.Delete(); } catch { }
            }
        }
        catch
        {
            // Pruning is housekeeping; a locked cache file must never break a preview.
        }
    }

    /// <summary>Extract the shell icon and re-encode it as PNG (alpha preserved).</summary>
    private static byte[]? ExtractPng(string path, int pixels)
    {
        var hbitmap = GetShellBitmap(path, pixels, SiigbfIconOnly | SiigbfBiggerSizeOk);
        if (hbitmap != IntPtr.Zero)
        {
            try
            {
                var png = EncodeHBitmapPng(hbitmap);
                if (png != null) return png;
            }
            finally { DeleteObject(hbitmap); }
        }

        return ExtractPrivateIconPng(path) ?? ExtractHIconPng(path);
    }

    /// <summary>Extract the shell thumbnail as PNG (null when the type has no handler).</summary>
    private static byte[]? ExtractThumbnailPng(string path, int pixels)
    {
        var hbitmap = GetShellBitmap(path, pixels, SiigbfThumbnailOnly | SiigbfBiggerSizeOk);
        if (hbitmap == IntPtr.Zero) return null;

        try { return EncodeHBitmapPng(hbitmap); }
        finally { DeleteObject(hbitmap); }
    }

    private static IntPtr GetShellBitmap(string path, int pixels, uint flags)
    {
        object? item = null;
        try
        {
            var iid = typeof(IShellItemImageFactory).GUID;
            var hr = SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out item);
            if (hr != 0 || item is not IShellItemImageFactory factory) return IntPtr.Zero;

            return factory.GetImage(new SIZE { cx = pixels, cy = pixels }, flags);
        }
        catch { return IntPtr.Zero; }
        finally
        {
            if (item != null && Marshal.IsComObject(item)) Marshal.ReleaseComObject(item);
        }
    }

    /// <summary>Read the 32-bit DIB bits out of the HBITMAP and encode them as PNG via Skia.</summary>
    private static byte[]? EncodeHBitmapPng(IntPtr hbitmap)
    {
        var bitmap = new BITMAP();
        if (GetObject(hbitmap, Marshal.SizeOf<BITMAP>(), ref bitmap) == 0) return null;

        int width = bitmap.bmWidth, height = Math.Abs(bitmap.bmHeight);
        if (width <= 0 || height <= 0) return null;

        var info = new BITMAPINFO
        {
            bmiHeader = new BITMAPINFOHEADER
            {
                biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
                biWidth = width,
                biHeight = -height,   // top-down
                biPlanes = 1,
                biBitCount = 32,
                biCompression = 0,    // BI_RGB
            },
        };

        var buffer = new byte[width * height * 4];
        var hdc = CreateCompatibleDC(IntPtr.Zero);
        try
        {
            if (GetDIBits(hdc, hbitmap, 0, (uint)height, buffer, ref info, 0) == 0) return null;
        }
        finally { DeleteDC(hdc); }

        // GetDIBits happily widens a 24-bit bitmap to 32 bpp and leaves every alpha byte at 0,
        // which decodes as a fully transparent image. Icons are 32-bit ARGB, but several shell
        // thumbnail handlers (and every 24-bit HBITMAP) are not — so a source that carries no
        // alpha at all is made opaque instead of invisible.
        if (bitmap.bmBitsPixel < 32 || !HasAlpha(buffer)) MakeOpaque(buffer);

        var imageInfo = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var skBitmap = new SKBitmap(imageInfo);
        var destination = skBitmap.GetPixels();
        if (destination == IntPtr.Zero) return null;

        var rowBytes = width * 4;
        for (var y = 0; y < height; y++)
        {
            Marshal.Copy(buffer, y * rowBytes, destination + y * skBitmap.RowBytes, rowBytes);
        }

        using var image = SKImage.FromBitmap(skBitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data?.ToArray();
    }

    /// <summary>True when any pixel of a BGRA buffer is not fully transparent.</summary>
    private static bool HasAlpha(byte[] bgra)
    {
        for (var i = 3; i < bgra.Length; i += 4)
        {
            if (bgra[i] != 0) return true;
        }

        return false;
    }

    private static void MakeOpaque(byte[] bgra)
    {
        for (var i = 3; i < bgra.Length; i += 4) bgra[i] = 255;
    }

    /// <summary>
    /// Fallback 1: pull the icon straight out of the executable at the preferred size.
    /// <c>SHGetFileInfo</c> below only ever yields a 32 px system-image-list icon, which is the
    /// soft blur the grid used to show when the factory rejected a path.
    /// </summary>
    private static byte[]? ExtractPrivateIconPng(string path)
    {
        var large = new IntPtr[1];
        var ids = new uint[1];
        try
        {
            var count = PrivateExtractIcons(path, 0, PreferredIconPixels, PreferredIconPixels,
                large, ids, 1, 0);
            if (count == 0 || large[0] == IntPtr.Zero) return null;

            using var icon = System.Drawing.Icon.FromHandle(large[0]);
            using var bitmap = icon.ToBitmap();
            using var stream = new MemoryStream();
            bitmap.Save(stream, System.Drawing.Imaging.ImageFormat.Png);
            return stream.ToArray();
        }
        catch { return null; }
        finally
        {
            if (large[0] != IntPtr.Zero) DestroyIcon(large[0]);
        }
    }

    /// <summary>Fallback 2: the classic system image list icon, for paths the factory rejects.</summary>
    private static byte[]? ExtractHIconPng(string path)
    {
        var info = new SHFILEINFO();
        var res = SHGetFileInfo(path, 0, ref info, (uint)Marshal.SizeOf<SHFILEINFO>(),
            ShgfiIcon | ShgfiLargeIcon);
        if (res == IntPtr.Zero || info.hIcon == IntPtr.Zero) return null;

        try
        {
            using var icon = System.Drawing.Icon.FromHandle(info.hIcon);
            using var bitmap = icon.ToBitmap();
            using var stream = new MemoryStream();
            bitmap.Save(stream, System.Drawing.Imaging.ImageFormat.Png);
            return stream.ToArray();
        }
        catch { return null; }
        finally { DestroyIcon(info.hIcon); }
    }

    private static string DiskCachePath(string path, int pixels, string folder)
    {
        var stamp = 0L;
        try { stamp = File.GetLastWriteTimeUtc(path).Ticks; } catch { }

        // The stamp is part of the identity: editing a file must invalidate its thumbnail. The
        // folder prefix keeps an icon and a thumbnail of the same path apart even at equal size.
        var identity = $"{folder}|{path.ToLowerInvariant()}|{pixels}|{stamp}";
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..24];
        return Path.Combine(Const.DataFolder, "cache", folder, $"{hash}.png");
    }

    // ---- interop -----------------------------------------------------------------

    private const string IconFolder = "icons";
    private const string ThumbnailFolder = "thumbs";
    private const uint ShgfiIcon = 0x000000100;
    private const uint ShgfiLargeIcon = 0x000000000;
    private const uint ShgfiDisplayName = 0x000000200;
    private const uint SiigbfBiggerSizeOk = 0x01;
    private const uint SiigbfIconOnly = 0x04;
    private const uint SiigbfThumbnailOnly = 0x08;

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE { public int cx; public int cy; }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAP
    {
        public int bmType, bmWidth, bmHeight, bmWidthBytes;
        public ushort bmPlanes, bmBitsPixel;
        public IntPtr bmBits;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth, biHeight;
        public ushort biPlanes, biBitCount;
        public uint biCompression, biSizeImage;
        public int biXPelsPerMeter, biYPelsPerMeter;
        public uint biClrUsed, biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFO
    {
        public BITMAPINFOHEADER bmiHeader;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 3)] public uint[] bmiColors;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
    }

    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        IntPtr GetImage(SIZE size, uint flags);
    }

    [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cch, IntPtr pfd, uint fFlags);
    }

    [ComImport, Guid("0000010B-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPersistFile
    {
        void GetClassID(out Guid pClassID);
        [PreserveSig] int IsDirty();
        void Load([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, uint dwMode);
    }

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLinkCoClass { }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern int SHCreateItemFromParsingName(
        [MarshalAs(UnmanagedType.LPWStr)] string pszPath, IntPtr pbc, ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out object ppv);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(string pszPath, uint dwFileAttributes,
        ref SHFILEINFO psfi, uint cbFileInfo, uint uFlags);

    [DllImport("gdi32.dll")] private static extern int GetObject(IntPtr h, int c, ref BITMAP pv);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr hdc);
    [DllImport("gdi32.dll")]
    private static extern int GetDIBits(IntPtr hdc, IntPtr hbm, uint start, uint lines,
        byte[] bits, ref BITMAPINFO info, uint usage);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr hObject);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint PrivateExtractIcons(string lpszFile, int nIconIndex, int cxIcon, int cyIcon,
        IntPtr[] phicon, uint[] piconid, uint nIcons, uint flags);
}
