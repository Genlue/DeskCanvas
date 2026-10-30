using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using SkiaSharp;

namespace Tools.Services;

public static class ClipboardNative
{
    public const uint CF_TEXT = 1;
    public const uint CF_BITMAP = 2;
    public const uint CF_DIB = 8;
    public const uint CF_UNICODETEXT = 13;
    public const uint CF_HDROP = 15;
    public const uint CF_DIBV5 = 17;

    private const uint GMEM_MOVEABLE = 0x0002;
    private const uint GMEM_ZEROINIT = 0x0040;
    private const uint GHND = GMEM_MOVEABLE | GMEM_ZEROINIT;

    [DllImport("user32.dll")]
    public static extern uint GetClipboardSequenceNumber();

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool OpenClipboard(IntPtr hWndNewOwner);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool CloseClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool EmptyClipboard();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsClipboardFormatAvailable(uint format);

    [DllImport("user32.dll")]
    public static extern IntPtr GetClipboardData(uint uFormat);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr SetClipboardData(uint uFormat, IntPtr hMem);

    [DllImport("kernel32.dll")]
    public static extern IntPtr GlobalAlloc(uint uFlags, UIntPtr dwBytes);

    [DllImport("kernel32.dll")]
    public static extern IntPtr GlobalLock(IntPtr hMem);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GlobalUnlock(IntPtr hMem);

    [DllImport("kernel32.dll")]
    public static extern UIntPtr GlobalSize(IntPtr hMem);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    public static extern uint DragQueryFileW(IntPtr hDrop, uint iFile, [Out] StringBuilder? lpszFile, uint cch);

    [StructLayout(LayoutKind.Sequential)]
    private struct DROPFILES
    {
        public uint pFiles;
        public int ptX;
        public int ptY;
        [MarshalAs(UnmanagedType.Bool)]
        public bool fNC;
        [MarshalAs(UnmanagedType.Bool)]
        public bool fWide;
    }

    /// <summary>
    /// Try to open clipboard with retries in case another app holds the lock.
    /// </summary>
    public static bool TryOpenClipboard(int retries = 5, int delayMs = 30)
    {
        for (int i = 0; i < retries; i++)
        {
            if (OpenClipboard(IntPtr.Zero)) return true;
            System.Threading.Thread.Sleep(delayMs);
        }
        return false;
    }

    /// <summary>
    /// Read Unicode text from clipboard.
    /// </summary>
    public static string? ReadText()
    {
        if (!IsClipboardFormatAvailable(CF_UNICODETEXT)) return null;
        if (!TryOpenClipboard()) return null;

        try
        {
            var hData = GetClipboardData(CF_UNICODETEXT);
            if (hData == IntPtr.Zero) return null;

            var ptr = GlobalLock(hData);
            if (ptr == IntPtr.Zero) return null;

            try
            {
                return Marshal.PtrToStringUni(ptr);
            }
            finally
            {
                GlobalUnlock(hData);
            }
        }
        catch
        {
            return null;
        }
        finally
        {
            CloseClipboard();
        }
    }

    /// <summary>
    /// Read file list (CF_HDROP) from clipboard.
    /// </summary>
    public static string[]? ReadFiles()
    {
        if (!IsClipboardFormatAvailable(CF_HDROP)) return null;
        if (!TryOpenClipboard()) return null;

        try
        {
            var hDrop = GetClipboardData(CF_HDROP);
            if (hDrop == IntPtr.Zero) return null;

            uint count = DragQueryFileW(hDrop, 0xFFFFFFFF, null, 0);
            if (count == 0) return null;

            var files = new List<string>((int)count);
            var sb = new StringBuilder(1024);

            for (uint i = 0; i < count; i++)
            {
                sb.Clear();
                uint length = DragQueryFileW(hDrop, i, sb, (uint)sb.Capacity);
                if (length > 0)
                {
                    files.Add(sb.ToString());
                }
            }

            return files.Count > 0 ? files.ToArray() : null;
        }
        catch
        {
            return null;
        }
        finally
        {
            CloseClipboard();
        }
    }

    /// <summary>
    /// Read image from clipboard as SkiaSharp SKBitmap.
    /// </summary>
    public static SKBitmap? ReadImage()
    {
        if (!IsClipboardFormatAvailable(CF_DIB) && !IsClipboardFormatAvailable(CF_DIBV5)) return null;
        if (!TryOpenClipboard()) return null;

        try
        {
            var format = IsClipboardFormatAvailable(CF_DIB) ? CF_DIB : CF_DIBV5;
            var hMem = GetClipboardData(format);
            if (hMem == IntPtr.Zero) return null;

            var size = (int)GlobalSize(hMem);
            if (size < 40) return null;

            var ptr = GlobalLock(hMem);
            if (ptr == IntPtr.Zero) return null;

            try
            {
                // Read BITMAPINFOHEADER
                int biSize = Marshal.ReadInt32(ptr, 0);
                short biBitCount = Marshal.ReadInt16(ptr, 14);
                int biCompression = Marshal.ReadInt32(ptr, 16);
                int biClrUsed = Marshal.ReadInt32(ptr, 32);

                int colorCount = 0;
                if (biBitCount <= 8)
                {
                    colorCount = biClrUsed == 0 ? (1 << biBitCount) : biClrUsed;
                }
                else if (biBitCount >= 16 && biCompression == 3) // BI_BITFIELDS
                {
                    colorCount = 3;
                }

                int paletteSize = colorCount * 4;
                int offBits = 14 + biSize + paletteSize;
                int totalFileSize = 14 + size;

                // Construct a complete BMP in memory. The header is written with the BinaryWriter and
                // the DIB body is then copied straight into the MemoryStream's own backing buffer, so a
                // full-resolution capture costs one 8–33 MB array instead of two (the second copy used
                // to land on the LOH on every image copy).
                using var ms = new MemoryStream(totalFileSize);
                using (var bw = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
                {
                    // BITMAPFILEHEADER
                    bw.Write((ushort)0x4D42); // 'BM'
                    bw.Write(totalFileSize);
                    bw.Write((ushort)0);
                    bw.Write((ushort)0);
                    bw.Write(offBits);
                }

                // Marshal.Copy cannot resize the stream itself, so the logical length is set up front
                // (the extra bytes stay zero-initialised).
                ms.SetLength(totalFileSize);
                Marshal.Copy(ptr, ms.GetBuffer()!, 14, size);

                ms.Position = 0;
                return SKBitmap.Decode(ms);
            }
            finally
            {
                GlobalUnlock(hMem);
            }
        }
        catch
        {
            return null;
        }
        finally
        {
            CloseClipboard();
        }
    }

    /// <summary>
    /// Set Unicode text to clipboard.
    /// </summary>
    public static bool SetText(string text)
    {
        if (!TryOpenClipboard()) return false;
        try
        {
            EmptyClipboard();
            byte[] bytes = Encoding.Unicode.GetBytes(text + "\0");
            var hMem = GlobalAlloc(GHND, (UIntPtr)bytes.Length);
            if (hMem == IntPtr.Zero) return false;

            var ptr = GlobalLock(hMem);
            if (ptr == IntPtr.Zero) return false;

            Marshal.Copy(bytes, 0, ptr, bytes.Length);
            GlobalUnlock(hMem);

            return SetClipboardData(CF_UNICODETEXT, hMem) != IntPtr.Zero;
        }
        finally
        {
            CloseClipboard();
        }
    }

    /// <summary>
    /// Set files list (CF_HDROP) to clipboard.
    /// </summary>
    public static bool SetFiles(string[] filePaths)
    {
        if (filePaths == null || filePaths.Length == 0) return false;
        if (!TryOpenClipboard()) return false;

        try
        {
            EmptyClipboard();

            // Calculate buffer size
            int dropFilesSize = Marshal.SizeOf<DROPFILES>();
            int totalBytes = dropFilesSize;
            foreach (var path in filePaths)
            {
                totalBytes += (path.Length + 1) * 2;
            }
            totalBytes += 2; // double null terminator

            var hMem = GlobalAlloc(GHND, (UIntPtr)totalBytes);
            if (hMem == IntPtr.Zero) return false;

            var ptr = GlobalLock(hMem);
            if (ptr == IntPtr.Zero) return false;

            try
            {
                var drop = new DROPFILES
                {
                    pFiles = (uint)dropFilesSize,
                    ptX = 0,
                    ptY = 0,
                    fNC = false,
                    fWide = true
                };
                Marshal.StructureToPtr(drop, ptr, false);

                IntPtr pCurrent = ptr + dropFilesSize;
                foreach (var path in filePaths)
                {
                    byte[] b = Encoding.Unicode.GetBytes(path + "\0");
                    Marshal.Copy(b, 0, pCurrent, b.Length);
                    pCurrent += b.Length;
                }
                Marshal.WriteInt16(pCurrent, 0); // trailing null

                return SetClipboardData(CF_HDROP, hMem) != IntPtr.Zero;
            }
            finally
            {
                GlobalUnlock(hMem);
            }
        }
        finally
        {
            CloseClipboard();
        }
    }

    /// <summary>
    /// Set image (as CF_DIB) to clipboard from image file.
    /// </summary>
    public static bool SetImageFromFile(string imagePath)
    {
        if (!File.Exists(imagePath)) return false;
        try
        {
            using var bitmap = SKBitmap.Decode(imagePath);
            if (bitmap == null) return false;
            return SetImage(bitmap);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Set image (as CF_DIB) to clipboard from SKBitmap.
    /// </summary>
    public static bool SetImage(SKBitmap bitmap)
    {
        if (!TryOpenClipboard()) return false;
        try
        {
            EmptyClipboard();

            int width = bitmap.Width;
            int height = bitmap.Height;
            int stride = ((width * 32 + 31) / 32) * 4;
            int imageSize = stride * height;
            int headerSize = 40; // BITMAPINFOHEADER
            int totalSize = headerSize + imageSize;

            var hMem = GlobalAlloc(GHND, (UIntPtr)totalSize);
            if (hMem == IntPtr.Zero) return false;

            var ptr = GlobalLock(hMem);
            if (ptr == IntPtr.Zero) return false;

            try
            {
                // Write BITMAPINFOHEADER
                Marshal.WriteInt32(ptr, 0, headerSize);
                Marshal.WriteInt32(ptr, 4, width);
                Marshal.WriteInt32(ptr, 8, height); // positive = bottom-up DIB
                Marshal.WriteInt16(ptr, 12, 1);     // planes
                Marshal.WriteInt16(ptr, 14, 32);    // bitCount (32-bit BGRA)
                Marshal.WriteInt32(ptr, 16, 0);     // BI_RGB
                Marshal.WriteInt32(ptr, 20, imageSize);
                Marshal.WriteInt32(ptr, 24, 0);
                Marshal.WriteInt32(ptr, 28, 0);
                Marshal.WriteInt32(ptr, 32, 0);
                Marshal.WriteInt32(ptr, 36, 0);

                // Write pixel bytes bottom-up
                IntPtr pPixels = ptr + headerSize;
                byte[] row = new byte[stride];

                for (int y = 0; y < height; y++)
                {
                    int srcY = height - 1 - y; // bottom-up
                    for (int x = 0; x < width; x++)
                    {
                        var color = bitmap.GetPixel(x, srcY);
                        int idx = x * 4;
                        row[idx + 0] = color.Blue;
                        row[idx + 1] = color.Green;
                        row[idx + 2] = color.Red;
                        row[idx + 3] = color.Alpha;
                    }
                    Marshal.Copy(row, 0, pPixels + (y * stride), stride);
                }

                return SetClipboardData(CF_DIB, hMem) != IntPtr.Zero;
            }
            finally
            {
                GlobalUnlock(hMem);
            }
        }
        catch
        {
            return false;
        }
        finally
        {
            CloseClipboard();
        }
    }
}
