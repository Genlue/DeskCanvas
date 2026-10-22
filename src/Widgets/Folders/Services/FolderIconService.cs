using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;

namespace Folders.Services;

/// <summary>
/// Extracts the associated system icon of a file/folder/shortcut as an Avalonia bitmap.
/// Uses IShellItemImageFactory first (same source as Explorer), then falls back to the shell image list.
/// </summary>
public static class FolderIconService
{
    /// <summary>
    /// Icon cache bounds. Every entry is a decoded 256 px bitmap (~256 KB of unmanaged pixels),
    /// so this is really a memory budget, enforced on both axes: 64 entries ≈ 16 MiB, which still
    /// covers a full desktop folder plus the big-folder popup without letting the cache grow with
    /// the whole disk — and the byte budget (measured from the real pixels) holds even when a
    /// custom icon source returns something larger than the nominal 256 px.
    /// </summary>
    private const int MaxCacheSize = 64;

    /// <summary>Byte budget of the icon cache; eviction returns to <see cref="TargetCacheBytesAfterEviction"/>.</summary>
    private const long MaxCacheBytes = 16L * 1024 * 1024;
    private const long TargetCacheBytesAfterEviction = 12L * 1024 * 1024;

    private static readonly Dictionary<string, (Bitmap? Icon, DateTime Stamp, long AccessOrder)> iconCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object cacheLock = new();
    private static long accessCounter = 0;

    /// <summary>Pixel bytes currently held by <see cref="iconCache"/>; guarded by <see cref="cacheLock"/>.</summary>
    private static long cacheBytes;

    private static long IconBytes(Bitmap? icon) =>
        icon == null ? 0 : (long)icon.PixelSize.Width * icon.PixelSize.Height * 4;

    public static void ClearCache()
    {
        lock (cacheLock)
        {
            // Dropping the entries without disposing them left every bitmap to the finalizer:
            // a 256 px icon is ~256 KB of unmanaged pixels, so a folder refresh could park
            // tens of megabytes outside the GC's reach until it got around to running them.
            foreach (var entry in iconCache.Values)
            {
                entry.Icon?.Dispose();
            }

            iconCache.Clear();
            cacheBytes = 0;
        }
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string szTypeName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAP
    {
        public int bmType;
        public int bmWidth;
        public int bmHeight;
        public int bmWidthBytes;
        public ushort bmPlanes;
        public ushort bmBitsPixel;
        public IntPtr bmBits;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ICONINFO
    {
        public bool fIcon;
        public int xHotspot;
        public int yHotspot;
        public IntPtr hbmMask;
        public IntPtr hbmColor;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth;
        public int biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFO
    {
        public BITMAPINFOHEADER bmiHeader;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(string pszPath, uint dwFileAttributes, ref SHFILEINFO psfi, uint cbFileInfo, uint uFlags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetIconInfo(IntPtr hIcon, out ICONINFO piconinfo);

    [DllImport("gdi32.dll")]
    private static extern IntPtr GetObject(IntPtr h, int c, out BITMAP b);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr h);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern int GetDIBits(IntPtr hdc, IntPtr hbm, uint start, uint cLines, IntPtr lpvBits, ref BITMAPINFO lpbmi, uint usage);

    [DllImport("shell32.dll", EntryPoint = "SHGetImageList", CharSet = CharSet.Unicode)]
    private static extern int SHGetImageList(int iImageList, ref Guid riid, out IntPtr ppv);

    [DllImport("comctl32.dll", SetLastError = true)]
    private static extern IntPtr ImageList_GetIcon(IntPtr himl, int i, uint flags);

    private const int SHIL_LARGE = 0;       // 32x32
    private const int SHIL_SMALL = 1;       // 16x16
    private const int SHIL_EXTRALARGE = 2;  // 48x48
    private const int SHIL_JUMBO = 4;       // 256x256
    private const uint ILD_TRANSPARENT = 0x00000001;

    /// <summary>
    /// Get the icon of <paramref name="path"/> as a 32-bit BGRA bitmap.
    /// Uses IShellItemImageFactory first (same source as Explorer), then falls back to SHGetFileInfo.
    /// </summary>
    public static Bitmap? GetIcon(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var stamp = File.GetLastWriteTimeUtc(path);
        lock (cacheLock)
        {
            if (iconCache.TryGetValue(path, out var cached) && cached.Stamp == stamp)
            {
                iconCache[path] = (cached.Icon, cached.Stamp, ++accessCounter);
                return cached.Icon;
            }
        }

        Bitmap? icon = null;
        if (path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
        {
            icon = GetIconFromShortcut(path);
        }
        else if (path.EndsWith(".url", StringComparison.OrdinalIgnoreCase))
        {
            icon = GetIconFromUrlFile(path);
        }
        else if (path.EndsWith(".ico", StringComparison.OrdinalIgnoreCase) && File.Exists(path))
        {
            icon = GetIconFromIcoFile(path);
        }
        else if (Directory.Exists(path))
        {
            icon = GetIconFromDesktopIni(path);
        }

        icon ??= GetIconFromShellItemImageFactory(path)
            ?? GetIconFromPrivateExtract(path)
            ?? GetIconFromSystemImageList(path, SHIL_JUMBO)
            ?? GetIconFromSystemImageList(path, SHIL_EXTRALARGE)
            ?? GetIconFromShGetFileInfo(path);

        lock (cacheLock)
        {
            // Both limits feed the same access-order eviction: the count ceiling keeps the batch
            // behaviour (roughly 30 entries leave at once, so inserts do not each trigger a scan),
            // and the byte budget keeps dropping until the cache is back under its target.
            if (iconCache.Count >= MaxCacheSize || cacheBytes + IconBytes(icon) > MaxCacheBytes)
            {
                // Evicting by access order is deliberately a *cache* eviction only: the bitmap
                // that is handed out here is assigned to item view models and drawn by the
                // folder view, so it may still be on screen. Disposing an evicted entry could
                // free the surface of a bitmap that is being rendered; dropping the reference
                // instead lets the GC (and the bitmap's own finalizer) reclaim it once the
                // visual tree has let go of it.
                var ordered = new List<KeyValuePair<string, (Bitmap? Icon, DateTime Stamp, long AccessOrder)>>(iconCache);
                ordered.Sort((a, b) => a.Value.AccessOrder.CompareTo(b.Value.AccessOrder));
                foreach (var kv in ordered)
                {
                    if (iconCache.Count < MaxCacheSize - 30 && cacheBytes + IconBytes(icon) <= TargetCacheBytesAfterEviction)
                        break;

                    iconCache.Remove(kv.Key);
                    cacheBytes -= IconBytes(kv.Value.Icon);
                }
            }

            // A same-path refresh (stale stamp, low-quality retry) replaces its own entry; the
            // displaced bitmap is only dereferenced, exactly like an eviction (it may be showing).
            if (iconCache.TryGetValue(path, out var displaced))
            {
                cacheBytes -= IconBytes(displaced.Icon);
            }

            // A small result means every 256-capable source failed (often the
            // transient E_PENDING of a not-yet-loaded icon handler): cache it
            // with a stale stamp so the next rebuild retries instead of
            // freezing the blurry 32px variant forever.
            var lowQuality = icon != null && icon.PixelSize.Width < 128;
            iconCache[path] = (icon, lowQuality ? DateTime.MinValue : stamp, ++accessCounter);
            cacheBytes += IconBytes(icon);
        }
        return icon;
    }

    [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszFile, int cch, IntPtr pfd, uint fFlags);
        void GetIDList(out IntPtr ppidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszName, int cch);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszDir, int cch);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszArgs, int cch);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
        void GetHotkey(out short pwHotkey);
        void SetHotkey(short wHotkey);
        void GetShowCmd(out int piShowCmd);
        void SetShowCmd(int iShowCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszIconPath, int cch, out int piIcon);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);
        void Resolve(IntPtr hwnd, uint fFlags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }

    [ComImport, Guid("0000010B-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPersistFile
    {
        void GetClassID(out Guid pClassID);
        [PreserveSig] int IsDirty();
        void Load([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, uint dwMode);
        void Save([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, [MarshalAs(UnmanagedType.Bool)] bool fRemember);
        void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string pszFileName);
        void GetCurFile([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder ppszFileName);
    }

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLinkCoClass { }

    /// <summary>
    /// Extract the custom icon assigned to a shortcut (.lnk), rather than tracing back (dereferencing)
    /// to the target file's default icon. Only falls back to the target file's icon if the shortcut has no custom icon.
    /// </summary>
    private static Bitmap? GetIconFromShortcut(string lnkPath)
    {
        try
        {
            var link = (IShellLinkW)new ShellLinkCoClass();
            ((IPersistFile)link).Load(lnkPath, 0);

            // 1. Check if the shortcut has a custom icon location explicitly specified
            var sb = new System.Text.StringBuilder(1024);
            link.GetIconLocation(sb, 1024, out int iconIndex);
            var rawIcon = sb.ToString().Trim('"', ' ');
            var iconPath = Environment.ExpandEnvironmentVariables(rawIcon);

            if (!string.IsNullOrEmpty(iconPath) && File.Exists(iconPath))
            {
                if (iconPath.EndsWith(".ico", StringComparison.OrdinalIgnoreCase))
                {
                    var icoBmp = GetIconFromIcoFile(iconPath);
                    if (icoBmp != null) return icoBmp;
                }

                var peBmp = GetIconFromPrivateExtract(iconPath, iconIndex);
                if (peBmp != null) return peBmp;

                var shBmp = GetIconFromShellItemImageFactory(iconPath);
                if (shBmp != null) return shBmp;
            }

            // 2. If no custom icon was set on the shortcut, resolve target and get its icon
            var sbTarget = new System.Text.StringBuilder(1024);
            link.GetPath(sbTarget, 1024, IntPtr.Zero, 0);
            var target = Environment.ExpandEnvironmentVariables(sbTarget.ToString().Trim('"', ' '));
            if (!string.IsNullOrEmpty(target) && (File.Exists(target) || Directory.Exists(target)))
            {
                var targetIcon = GetIcon(target);
                if (targetIcon != null) return targetIcon;
            }

            // 3. Fallback: try IShellItemImageFactory directly on the shortcut (.lnk) itself
            var lnkIcon = GetIconFromShellItemImageFactory(lnkPath);
            if (lnkIcon != null) return lnkIcon;

            // 4. Fallback: System Image List on the shortcut
            var sysIcon = GetIconFromSystemImageList(lnkPath, SHIL_JUMBO);
            if (sysIcon != null) return sysIcon;
        }
        catch
        {
            // fallback
        }
        return null;
    }

    /// <summary>
    /// Decode custom icon for an Internet Shortcut (.url).
    /// </summary>
    private static Bitmap? GetIconFromUrlFile(string urlPath)
    {
        try
        {
            if (!File.Exists(urlPath)) return null;
            var lines = File.ReadAllLines(urlPath);
            string? iconFile = null;
            int iconIndex = 0;
            foreach (var line in lines)
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith("IconFile=", StringComparison.OrdinalIgnoreCase))
                    iconFile = trimmed.Substring("IconFile=".Length).Trim('"', ' ');
                else if (trimmed.StartsWith("IconIndex=", StringComparison.OrdinalIgnoreCase))
                    int.TryParse(trimmed.Substring("IconIndex=".Length).Trim(), out iconIndex);
            }

            if (!string.IsNullOrEmpty(iconFile))
            {
                iconFile = Environment.ExpandEnvironmentVariables(iconFile);
                if (File.Exists(iconFile))
                {
                    if (iconFile.EndsWith(".ico", StringComparison.OrdinalIgnoreCase))
                    {
                        var ico = GetIconFromIcoFile(iconFile);
                        if (ico != null) return ico;
                    }
                    var pe = GetIconFromPrivateExtract(iconFile, iconIndex);
                    if (pe != null) return pe;
                    var sh = GetIconFromShellItemImageFactory(iconFile);
                    if (sh != null) return sh;
                }
            }
        }
        catch { }
        return null;
    }

    /// <summary>
    /// Decode custom folder icon from desktop.ini (e.g. customized folder icons in Windows Explorer).
    /// </summary>
    private static Bitmap? GetIconFromDesktopIni(string folderPath)
    {
        try
        {
            var iniPath = Path.Combine(folderPath, "desktop.ini");
            if (!File.Exists(iniPath)) return null;

            var lines = File.ReadAllLines(iniPath);
            string? iconFile = null;
            int iconIndex = 0;

            foreach (var line in lines)
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith("IconResource=", StringComparison.OrdinalIgnoreCase))
                {
                    var val = trimmed.Substring("IconResource=".Length).Trim('"', ' ');
                    var parts = val.Split(',');
                    iconFile = parts[0].Trim('"', ' ');
                    if (parts.Length > 1 && int.TryParse(parts[1].Trim(), out int idx))
                        iconIndex = idx;
                    break;
                }
                if (trimmed.StartsWith("IconFile=", StringComparison.OrdinalIgnoreCase))
                {
                    iconFile = trimmed.Substring("IconFile=".Length).Trim('"', ' ');
                }
                if (trimmed.StartsWith("IconIndex=", StringComparison.OrdinalIgnoreCase))
                {
                    int.TryParse(trimmed.Substring("IconIndex=".Length).Trim(), out iconIndex);
                }
            }

            if (!string.IsNullOrEmpty(iconFile))
            {
                iconFile = Environment.ExpandEnvironmentVariables(iconFile);
                if (File.Exists(iconFile))
                {
                    if (iconFile.EndsWith(".ico", StringComparison.OrdinalIgnoreCase))
                    {
                        var ico = GetIconFromIcoFile(iconFile);
                        if (ico != null) return ico;
                    }
                    var pe = GetIconFromPrivateExtract(iconFile, iconIndex);
                    if (pe != null) return pe;
                    var sh = GetIconFromShellItemImageFactory(iconFile);
                    if (sh != null) return sh;
                }
            }
        }
        catch { }
        return null;
    }

    /// <summary>
    /// Parse an .ico file and decode its largest frame. Supports PNG-compressed
    /// frames and classic 32/24/8bpp DIB frames (with AND mask fallback).
    /// </summary>
    private static Bitmap? GetIconFromIcoFile(string path)
    {
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length < 6) return null;
        var count = BitConverter.ToUInt16(bytes, 4);
        if (count == 0 || count > 200) return null;

        var best = -1;
        var bestScore = 0;
        for (var i = 0; i < count; i++)
        {
            var off = 6 + i * 16;
            if (off + 16 > bytes.Length) break;
            var w = bytes[off] == 0 ? 256 : bytes[off];
            var h = bytes[off + 1] == 0 ? 256 : bytes[off + 1];
            var bpp = BitConverter.ToUInt16(bytes, off + 6);
            var score = Math.Min(w, h) * 1000 + bpp;
            if (score > bestScore) { bestScore = score; best = i; }
        }
        if (best < 0) return null;

        var entryOff = 6 + best * 16;
        var dataLen = BitConverter.ToInt32(bytes, entryOff + 8);
        var dataOff = BitConverter.ToInt32(bytes, entryOff + 12);
        if (dataLen <= 0 || dataOff < 0 || dataOff + dataLen > bytes.Length) return null;

        var data = new byte[dataLen];
        Array.Copy(bytes, dataOff, data, 0, dataLen);

        if (data.Length > 8 && data[0] == 0x89 && data[1] == 0x50 && data[2] == 0x4E && data[3] == 0x47)
        {
            using var ms = new MemoryStream(data);
            return new Bitmap(ms);
        }
        return GetIconFromIcoBmpFrame(data);
    }

    private static Bitmap? GetIconFromIcoBmpFrame(byte[] data)
    {
        if (data.Length < 40) return null;
        var width = BitConverter.ToInt32(data, 4);
        var biHeight = BitConverter.ToInt32(data, 8);
        var bitCount = BitConverter.ToUInt16(data, 14);
        if (width <= 0 || width > 512) return null;

        var topDown = biHeight < 0;
        var height = topDown ? Math.Abs(biHeight) : Math.Abs(biHeight) / 2;
        if (height <= 0 || height > 512) return null;

        var paletteOffset = 40;
        var colors = 0;
        var bytesPerPixel = bitCount switch { 32 => 4, 24 => 3, 8 => 1, _ => 0 };
        if (bytesPerPixel == 0) return null;
        if (bitCount == 8)
        {
            colors = BitConverter.ToInt32(data, 32);
            if (colors == 0) colors = 256;
            paletteOffset = 40 + colors * 4;
        }
        if (paletteOffset + width * height * bytesPerPixel > data.Length) return null;

        var pixels = new byte[width * height * 4];
        var palette = new byte[colors * 4];

        // apply AND mask only when the frame has no usable alpha
        var useMask = bitCount != 32;
        var maskStride = ((width + 31) / 32) * 4;
        var maskOffset = paletteOffset + width * height * bytesPerPixel;
        var mask = (maskOffset + maskStride * height <= data.Length) ? data : null;

        for (var y = 0; y < height; y++)
        {
            var srcRow = topDown ? y : height - 1 - y;
            var dstRow = y;
            for (var x = 0; x < width; x++)
            {
                var src = paletteOffset + (srcRow * width + x) * bytesPerPixel;
                var dst = (dstRow * width + x) * 4;
                switch (bitCount)
                {
                    case 32:
                        pixels[dst] = data[src];
                        pixels[dst + 1] = data[src + 1];
                        pixels[dst + 2] = data[src + 2];
                        pixels[dst + 3] = data[src + 3];
                        break;
                    case 24:
                        pixels[dst] = data[src];
                        pixels[dst + 1] = data[src + 1];
                        pixels[dst + 2] = data[src + 2];
                        pixels[dst + 3] = 255;
                        break;
                    case 8:
                        Array.Copy(data, paletteOffset, palette, 0, palette.Length);
                        var idx = data[src] * 4;
                        pixels[dst] = palette[idx];
                        pixels[dst + 1] = palette[idx + 1];
                        pixels[dst + 2] = palette[idx + 2];
                        pixels[dst + 3] = 255;
                        break;
                }
            }
        }

        if (mask != null)
        {
            if (bitCount == 32 && HasAlpha(pixels)) useMask = false;
            if (useMask)
            {
                for (var y = 0; y < height; y++)
                {
                    var srcRow = topDown ? y : height - 1 - y;
                    for (var x = 0; x < width; x++)
                    {
                        var bitIndex = srcRow * maskStride + x / 8;
                        if (bitIndex >= mask.Length) continue;
                        var isMasked = (mask[bitIndex] & (0x80 >> (x % 8))) != 0;
                        pixels[(y * width + x) * 4 + 3] = isMasked ? (byte)0 : (byte)255;
                    }
                }
            }
        }

        return PixelsToWriteableBitmap(pixels, width, height);
    }

    private static bool HasAlpha(byte[] pixels)
    {
        for (var i = 3; i < pixels.Length; i += 4)
            if (pixels[i] != 0) return true;
        return false;
    }

    private static Bitmap PixelsToWriteableBitmap(byte[] pixels, int width, int height)
    {
        var writeable = new WriteableBitmap(
            new PixelSize(width, height),
            new Vector(96, 96),
            PixelFormat.Bgra8888,
            AlphaFormat.Premul);

        using (var framebuffer = writeable.Lock())
        {
            unsafe
            {
                var dst = new Span<byte>((void*)framebuffer.Address, pixels.Length);
                for (var i = 0; i < pixels.Length; i += 4)
                {
                    var b = pixels[i];
                    var g = pixels[i + 1];
                    var r = pixels[i + 2];
                    var a = pixels[i + 3];
                    if (a == 255)
                    {
                        dst[i] = b;
                        dst[i + 1] = g;
                        dst[i + 2] = r;
                        dst[i + 3] = 255;
                    }
                    else if (a != 0)
                    {
                        dst[i] = (byte)(b * a / 255);
                        dst[i + 1] = (byte)(g * a / 255);
                        dst[i + 2] = (byte)(r * a / 255);
                        dst[i + 3] = a;
                    }
                }
            }
        }
        return writeable;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE
    {
        public int cx;
        public int cy;
    }

    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig] int GetImage([In] SIZE size, [In] uint flags, out IntPtr phbm);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHCreateItemFromParsingName(string pszPath, IntPtr pbc, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory ppv);

    private const uint SIIGBF_RESIZETOFIT = 0x00000000;
    private const uint SIIGBF_BIGGERSIZEOK = 0x00000001;
    private const uint SIIGBF_MEMORYONLY = 0x00000002;
    private const uint SIIGBF_ICONONLY = 0x00000004;
    private const uint SIIGBF_THUMBNAILONLY = 0x00000008;
    private const uint SIIGBF_INCACHEONLY = 0x00000010;
    private const uint SIIGBF_CROPTOSQUARE = 0x00000020;
    private const uint SIIGBF_WIDESTEP = 0x00000040;
    private const uint SIIGBF_ICONBACKGROUND = 0x00000080;
    private const uint SIIGBF_SCALEUP = 0x00000100;

    private static Bitmap? GetIconFromShellItemImageFactory(string path)
    {
        try
        {
            var iid = new Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b");
            if (SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out var factory) != 0 || factory == null)
                return null;

            var size = new SIZE { cx = 256, cy = 256 };
            uint[] flagAttempts = [
                SIIGBF_ICONONLY | SIIGBF_BIGGERSIZEOK | SIIGBF_SCALEUP,
                SIIGBF_ICONONLY | SIIGBF_BIGGERSIZEOK,
                SIIGBF_ICONONLY,
                SIIGBF_BIGGERSIZEOK | SIIGBF_SCALEUP,
                SIIGBF_RESIZETOFIT
            ];

            foreach (var flags in flagAttempts)
            {
                var hbm = IntPtr.Zero;
                try
                {
                    var hr = factory.GetImage(size, flags, out hbm);
                    if (hr == unchecked((int)0x8000000A)) // E_PENDING: icon not ready
                        return null;
                    if (hr == 0 && hbm != IntPtr.Zero)
                    {
                        var bmp = HBitmapToBitmap(hbm, path);
                        if (bmp != null) return bmp;
                    }
                }
                finally
                {
                    if (hbm != IntPtr.Zero) DeleteObject(hbm);
                }
            }
        }
        catch
        {
            return null;
        }
        return null;
    }

    private static Bitmap? HBitmapToBitmap(IntPtr hbm, string path)
    {
        var hdc = IntPtr.Zero;
        try
        {
            if (GetObject(hbm, Marshal.SizeOf<BITMAP>(), out var bitmap) == 0) return null;

            var width = Math.Abs(bitmap.bmWidth);
            var height = Math.Abs(bitmap.bmHeight);
            if (width == 0 || height == 0) return null;

            hdc = CreateCompatibleDC(IntPtr.Zero);
            if (hdc == IntPtr.Zero) return null;

            var stride = width * 4;
            var pixels = new byte[stride * height];
            var ptr = Marshal.AllocHGlobal(pixels.Length);
            try
            {
                var bmi = new BITMAPINFO();
                bmi.bmiHeader.biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>();
                bmi.bmiHeader.biWidth = width;
                bmi.bmiHeader.biHeight = -height;
                bmi.bmiHeader.biPlanes = 1;
                bmi.bmiHeader.biBitCount = 32;

                if (GetDIBits(hdc, hbm, 0, (uint)height, ptr, ref bmi, 0) == 0) return null;
                Marshal.Copy(ptr, pixels, 0, pixels.Length);
            }
            finally
            {
                Marshal.FreeHGlobal(ptr);
            }

            var hasAlpha = false;
            var needsPremul = false;
            for (var i = 0; i < pixels.Length; i += 4)
            {
                var a = pixels[i + 3];
                if (a != 0) hasAlpha = true;
                if (pixels[i] > a || pixels[i + 1] > a || pixels[i + 2] > a)
                {
                    needsPremul = true;
                }
            }

            if (!hasAlpha)
            {
                for (var i = 3; i < pixels.Length; i += 4)
                    pixels[i] = 255;
                needsPremul = false;
            }

            var writeable = new WriteableBitmap(
                new PixelSize(width, height),
                new Vector(96, 96),
                PixelFormat.Bgra8888,
                AlphaFormat.Premul);

            using (var framebuffer = writeable.Lock())
            {
                unsafe
                {
                    var dst = new Span<byte>((void*)framebuffer.Address, pixels.Length);
                    if (!needsPremul)
                    {
                        pixels.CopyTo(dst);
                    }
                    else
                    {
                        for (var i = 0; i < pixels.Length; i += 4)
                        {
                            var b = pixels[i];
                            var g = pixels[i + 1];
                            var r = pixels[i + 2];
                            var a = pixels[i + 3];
                            if (a == 255)
                            {
                                dst[i] = b;
                                dst[i + 1] = g;
                                dst[i + 2] = r;
                                dst[i + 3] = 255;
                            }
                            else if (a == 0)
                            {
                                dst[i] = 0;
                                dst[i + 1] = 0;
                                dst[i + 2] = 0;
                                dst[i + 3] = 0;
                            }
                            else
                            {
                                dst[i] = (byte)(b * a / 255);
                                dst[i + 1] = (byte)(g * a / 255);
                                dst[i + 2] = (byte)(r * a / 255);
                                dst[i + 3] = a;
                            }
                        }
                    }
                }
            }
            return writeable;
        }
        catch
        {
            return null;
        }
        finally
        {
            if (hdc != IntPtr.Zero) DeleteDC(hdc);
        }
    }

    /// <summary>
    /// Trims transparent edges from a bitmap so that the icon artwork is tightly centered.
    /// Returns the cropped bitmap, or the original if trimming is not needed or not possible.
    /// </summary>
    public static Bitmap TrimTransparent(Bitmap source)
    {
        WriteableBitmap wb;
        if (source is WriteableBitmap direct)
        {
            wb = direct;
        }
        else
        {
            try
            {
                wb = new WriteableBitmap(source.PixelSize, source.Dpi, PixelFormat.Bgra8888, AlphaFormat.Premul);
                using var fb = wb.Lock();
                source.CopyPixels(fb);
            }
            catch
            {
                return source;
            }
        }

        int w = wb.PixelSize.Width;
        int h = wb.PixelSize.Height;
        if (w <= 0 || h <= 0) return source;

        int minX = w, maxX = -1, minY = h, maxY = -1;
        using (var fb = wb.Lock())
        {
            byte[] row = new byte[fb.RowBytes];
            for (int y = 0; y < h; y++)
            {
                Marshal.Copy(fb.Address + y * fb.RowBytes, row, 0, fb.RowBytes);
                for (int x = 0; x < w; x++)
                {
                    byte a = row[x * 4 + 3];
                    if (a > 10)
                    {
                        if (x < minX) minX = x;
                        if (x > maxX) maxX = x;
                        if (y < minY) minY = y;
                        if (y > maxY) maxY = y;
                    }
                }
            }
        }

        if (maxX < minX || maxY < minY) return source;

        int cropW = maxX - minX + 1;
        int cropH = maxY - minY + 1;

        if (cropW >= w - 3 && cropH >= h - 3) return source;

        try
        {
            var cropped = new WriteableBitmap(
                new PixelSize(cropW, cropH),
                wb.Dpi,
                PixelFormat.Bgra8888,
                AlphaFormat.Premul);

            using (var srcFb = wb.Lock())
            using (var dstFb = cropped.Lock())
            {
                byte[] row = new byte[cropW * 4];
                for (int y = 0; y < cropH; y++)
                {
                    IntPtr srcRowPtr = srcFb.Address + (minY + y) * srcFb.RowBytes + minX * 4;
                    IntPtr dstRowPtr = dstFb.Address + y * dstFb.RowBytes;
                    Marshal.Copy(srcRowPtr, row, 0, row.Length);
                    Marshal.Copy(row, 0, dstRowPtr, row.Length);
                }
            }

            return cropped;
        }
        catch
        {
            return source;
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int PrivateExtractIcons(string lpszFile, int nIconIndex, int cx, int cy,
        IntPtr[] phicon, int[] piconid, int nIcons, uint uFlags);

    /// <summary>
    /// Fallback for when the shell factory fails: PrivateExtractIcons trying largest available
    /// sizes (256, 128, 96, 64, 48, 32) instead of jumping down to a tiny icon.
    /// </summary>
    private static Bitmap? GetIconFromPrivateExtract(string path, int iconIndex = 0)
    {
        var phicon = new IntPtr[1];
        var piconid = new int[1];
        var hIcon = IntPtr.Zero;
        try
        {
            int[] preferredSizes = [256, 128, 96, 64, 48, 32];
            foreach (var size in preferredSizes)
            {
                phicon[0] = IntPtr.Zero;
                if (PrivateExtractIcons(path, iconIndex, size, size, phicon, piconid, 1, 0) > 0 && phicon[0] != IntPtr.Zero)
                {
                    hIcon = phicon[0];
                    break;
                }
            }

            if (hIcon == IntPtr.Zero && iconIndex != 0)
            {
                foreach (var size in preferredSizes)
                {
                    phicon[0] = IntPtr.Zero;
                    if (PrivateExtractIcons(path, 0, size, size, phicon, piconid, 1, 0) > 0 && phicon[0] != IntPtr.Zero)
                    {
                        hIcon = phicon[0];
                        break;
                    }
                }
            }

            if (hIcon == IntPtr.Zero) return null;
            return IconToBitmap(hIcon);
        }
        catch
        {
            return null;
        }
        finally
        {
            if (hIcon != IntPtr.Zero) DestroyIcon(hIcon);
        }
    }

    /// <summary>
    /// Fallback: retrieve high-res icon from Windows System Image List (Jumbo 256x256 or ExtraLarge 48x48).
    /// </summary>
    private static Bitmap? GetIconFromSystemImageList(string path, int imageListType = SHIL_JUMBO)
    {
        var hIcon = IntPtr.Zero;
        var pImageList = IntPtr.Zero;
        try
        {
            var info = new SHFILEINFO();
            var attrs = Directory.Exists(path) ? 0x00000010u : 0x00000080u;
            var flags = 0x00004000u; // SHGFI_SYSICONINDEX
            if (!File.Exists(path) && !Directory.Exists(path))
                flags |= 0x00000010u; // SHGFI_USEFILEATTRIBUTES

            var res = SHGetFileInfo(path, attrs, ref info, (uint)Marshal.SizeOf<SHFILEINFO>(), flags);
            if (res == IntPtr.Zero) return null;

            var iid = new Guid("46EB5926-582E-4017-9FDF-E8998DAA0950"); // IID_IImageList
            if (SHGetImageList(imageListType, ref iid, out pImageList) != 0 || pImageList == IntPtr.Zero)
                return null;

            hIcon = ImageList_GetIcon(pImageList, info.iIcon, ILD_TRANSPARENT);
            if (hIcon == IntPtr.Zero) return null;

            return IconToBitmap(hIcon);
        }
        catch
        {
            return null;
        }
        finally
        {
            if (hIcon != IntPtr.Zero) DestroyIcon(hIcon);
            if (pImageList != IntPtr.Zero) Marshal.Release(pImageList);
        }
    }

    /// <summary>
    /// Last resort: extract the associated icon via SHGetFileInfo(SHGFI_ICON) —
    /// only 32px, used when every 256-capable source failed (e.g. unresolved .lnk).
    /// </summary>
    private static Bitmap? GetIconFromShGetFileInfo(string path)
    {
        const uint SHGFI_ICON = 0x000000100;
        const uint SHGFI_LARGEICON = 0x000000000;
        const uint SHGFI_USEFILEATTRIBUTES = 0x000000010;
        const uint FILE_ATTRIBUTE_DIRECTORY = 0x00000010;

        var hIcon = IntPtr.Zero;
        try
        {
            var info = new SHFILEINFO();
            var attributes = Directory.Exists(path) ? FILE_ATTRIBUTE_DIRECTORY : 0;
            var flags = SHGFI_ICON | SHGFI_LARGEICON;
            if (!(File.Exists(path) || Directory.Exists(path)))
                flags |= SHGFI_USEFILEATTRIBUTES;

            SHGetFileInfo(path, attributes, ref info, (uint)Marshal.SizeOf<SHFILEINFO>(), flags);
            hIcon = info.hIcon;
            if (hIcon == IntPtr.Zero) return null;

            return IconToBitmap(hIcon);
        }
        catch
        {
            return null;
        }
        finally
        {
            if (hIcon != IntPtr.Zero) DestroyIcon(hIcon);
        }
    }

    private static Bitmap? IconToBitmap(IntPtr hIcon)
    {
        var hbmColor = IntPtr.Zero;
        var hbmMask = IntPtr.Zero;
        var hdc = IntPtr.Zero;
        try
        {
            if (!GetIconInfo(hIcon, out var iconInfo)) return null;
            hbmColor = iconInfo.hbmColor;
            hbmMask = iconInfo.hbmMask;
            if (hbmColor == IntPtr.Zero) return null;

            if (GetObject(hbmColor, Marshal.SizeOf<BITMAP>(), out var bitmap) == 0) return null;

            var width = Math.Abs(bitmap.bmWidth);
            var height = Math.Abs(bitmap.bmHeight);
            if (width == 0 || height == 0) return null;

            hdc = CreateCompatibleDC(IntPtr.Zero);
            if (hdc == IntPtr.Zero) return null;

            var stride = width * 4;
            var pixels = new byte[stride * height];
            var ptr = Marshal.AllocHGlobal(pixels.Length);
            try
            {
                var bmi = new BITMAPINFO();
                bmi.bmiHeader.biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>();
                bmi.bmiHeader.biWidth = width;
                bmi.bmiHeader.biHeight = -height;
                bmi.bmiHeader.biPlanes = 1;
                bmi.bmiHeader.biBitCount = 32;

                if (GetDIBits(hdc, hbmColor, 0, (uint)height, ptr, ref bmi, 0) == 0) return null;
                Marshal.Copy(ptr, pixels, 0, pixels.Length);
            }
            finally
            {
                Marshal.FreeHGlobal(ptr);
            }

            ApplyAlphaFromMask(hdc, hbmMask, width, height, pixels);

            var hasAlpha = false;
            var needsPremul = false;
            for (var i = 0; i < pixels.Length; i += 4)
            {
                var a = pixels[i + 3];
                if (a != 0) hasAlpha = true;
                if (pixels[i] > a || pixels[i + 1] > a || pixels[i + 2] > a)
                {
                    needsPremul = true;
                }
            }

            if (!hasAlpha)
            {
                for (var i = 3; i < pixels.Length; i += 4)
                    pixels[i] = 255;
                needsPremul = false;
            }

            var writeable = new WriteableBitmap(
                new PixelSize(width, height),
                new Vector(96, 96),
                PixelFormat.Bgra8888,
                AlphaFormat.Premul);

            using (var framebuffer = writeable.Lock())
            {
                unsafe
                {
                    var dst = new Span<byte>((void*)framebuffer.Address, pixels.Length);
                    if (!needsPremul)
                    {
                        pixels.CopyTo(dst);
                    }
                    else
                    {
                        for (var i = 0; i < pixels.Length; i += 4)
                        {
                            var b = pixels[i];
                            var g = pixels[i + 1];
                            var r = pixels[i + 2];
                            var a = pixels[i + 3];
                            if (a == 255)
                            {
                                dst[i] = b;
                                dst[i + 1] = g;
                                dst[i + 2] = r;
                                dst[i + 3] = 255;
                            }
                            else if (a == 0)
                            {
                                dst[i] = 0;
                                dst[i + 1] = 0;
                                dst[i + 2] = 0;
                                dst[i + 3] = 0;
                            }
                            else
                            {
                                dst[i] = (byte)(b * a / 255);
                                dst[i + 1] = (byte)(g * a / 255);
                                dst[i + 2] = (byte)(r * a / 255);
                                dst[i + 3] = a;
                            }
                        }
                    }
                }
            }
            return writeable;
        }
        catch
        {
            return null;
        }
        finally
        {
            if (hbmColor != IntPtr.Zero) DeleteObject(hbmColor);
            if (hbmMask != IntPtr.Zero) DeleteObject(hbmMask);
            if (hdc != IntPtr.Zero) DeleteDC(hdc);
        }
    }

    /// <summary>
    /// If the color bitmap has no alpha channel, derive it from the 1bpp AND mask.
    /// </summary>
    private static void ApplyAlphaFromMask(IntPtr hdc, IntPtr hbmMask, int width, int height, byte[] pixels)
    {
        var hasAlpha = false;
        for (var i = 3; i < pixels.Length; i += 4)
        {
            if (pixels[i] != 0) { hasAlpha = true; break; }
        }
        if (hasAlpha || hbmMask == IntPtr.Zero) return;

        if (GetObject(hbmMask, Marshal.SizeOf<BITMAP>(), out var maskBmp) == 0) return;

        var maskStride = ((Math.Abs(maskBmp.bmWidth) + 31) / 32) * 4;
        var maskBytes = new byte[maskStride * Math.Abs(maskBmp.bmHeight)];
        var maskPtr = Marshal.AllocHGlobal(maskBytes.Length);
        try
        {
            var bmi = new BITMAPINFO();
            bmi.bmiHeader.biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>();
            bmi.bmiHeader.biWidth = Math.Abs(maskBmp.bmWidth);
            bmi.bmiHeader.biHeight = -Math.Abs(maskBmp.bmHeight);
            bmi.bmiHeader.biPlanes = 1;
            bmi.bmiHeader.biBitCount = 1;

            if (GetDIBits(hdc, hbmMask, 0, (uint)Math.Abs(maskBmp.bmHeight), maskPtr, ref bmi, 0) == 0) return;
            Marshal.Copy(maskPtr, maskBytes, 0, maskBytes.Length);
        }
        finally
        {
            Marshal.FreeHGlobal(maskPtr);
        }

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var bitIndex = y * maskStride + x / 8;
                if (bitIndex >= maskBytes.Length) continue;
                var isMasked = (maskBytes[bitIndex] & (0x80 >> (x % 8))) != 0;
                var offset = y * width * 4 + x * 4;
                pixels[offset + 3] = isMasked ? (byte)0 : (byte)255;
            }
        }
    }
}
