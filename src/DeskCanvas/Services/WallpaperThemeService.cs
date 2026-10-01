using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Microsoft.Win32;

namespace DeskCanvas.Services;

/// <summary>
/// Chooses the light/dark flag from the desktop wallpaper (the “自动” mode):
/// averages the wallpaper luma and calls it dark below the midpoint. The
/// effective image is resolved with a fallback chain — Windows' transcoded
/// wallpaper (re-encoded for slideshows / spotlight too), then the plain
/// wallpaper path, then the desktop solid color — and re-checked on a slow
/// timer, because Windows broadcasts wallpaper changes only as window
/// messages this code has no hook into.
/// </summary>
public class WallpaperThemeService : IDisposable
{
    /// <summary>Average luma (0-255) below which the wallpaper counts as dark.</summary>
    private const double DarkLumaThreshold = 128;

    private readonly DispatcherTimer timer;
    private string? resolvedSourceKey;
    private bool isDark;
    private bool resolved;
    private bool busy;

    /// <summary>Raised on the UI thread when the wallpaper-derived dark flag changes.</summary>
    public event Action<bool>? DarkFlagChanged;

    public WallpaperThemeService()
    {
        timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
        timer.Tick += OnTick;
        timer.Start();
    }

    /// <summary>
    /// True when the current desktop wallpaper is dark. Resolves synchronously
    /// when nothing is cached yet.
    /// </summary>
    public bool IsWallpaperDark()
    {
        if (!resolved)
        {
            // The registry solid-color fallback in GetSource is Windows-only.
            var source = OperatingSystem.IsWindows() ? GetSource() : (Path: (string?) null, SolidColor: (string?) null);
            resolvedSourceKey = SourceKey(source);
            isDark = ComputeDark(source);
            resolved = true;
        }
        return isDark;
    }
    /// <summary>
    /// Forces an immediate re-check of the wallpaper brightness.
    /// </summary>
    public void RequestCheck() => OnTick(this, EventArgs.Empty);

    private async void OnTick(object? sender, EventArgs e)
    {
        if (busy) return;
        busy = true;
        try
        {
            // The registry solid-color fallback in GetSource is Windows-only.
            var source = OperatingSystem.IsWindows() ? GetSource() : (Path: (string?) null, SolidColor: (string?) null);
            var key = SourceKey(source);
            if (resolved && key == resolvedSourceKey) return;

            // Decoding a 4K wallpaper is too heavy for the UI thread.
            var dark = await Task.Run(() => ComputeDark(source));

            resolvedSourceKey = key;
            resolved = true;
            if (dark == isDark) return;
            isDark = dark;
            DarkFlagChanged?.Invoke(isDark);
        }
        catch
        {
            // Unreadable wallpaper (locked file, broken link): keep the last known mode.
        }
        finally
        {
            busy = false;
        }
    }

    /// <summary>
    /// The image to measure. TranscodedWallpaper is the system's own re-encode
    /// of whatever is currently displayed, so it wins over the raw path
    /// (which can be stale for slideshows and empty for spotlight).
    /// </summary>
    // Registry-based fallback: only reached when the wallpaper image is missing, and the
    // callers run behind the service's Windows availability check.
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static (string? Path, string? SolidColor) GetSource()
    {
        var transcoded = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            @"Microsoft\Windows\Themes\TranscodedWallpaper");
        if (File.Exists(transcoded)) return (transcoded, null);

        var path = InteropService.GetWallpaperPath();
        if (!string.IsNullOrWhiteSpace(path) && File.Exists(path)) return (path, null);

        // No image at all: the desktop may be a solid color.
        var solid = Registry.CurrentUser
            .OpenSubKey(@"Control Panel\Colors")?
            .GetValue("Background") as string;
        return (null, solid);
    }

    private static string SourceKey((string? Path, string? SolidColor) source) =>
        source.Path != null
            ? $"{source.Path}|{new FileInfo(source.Path).LastWriteTimeUtc:O}"
            : $"color:{source.SolidColor}";

    private static bool ComputeDark((string? Path, string? SolidColor) source) =>
        source.Path != null
            ? ImageIsDark(source.Path)
            : source.SolidColor != null && SolidColorIsDark(source.SolidColor);

    /// <summary>
    /// Decodes the image scaled down to 64px wide (off the UI thread — a 4K
    /// wallpaper must not be fully rasterized for an average) and averages the
    /// ITU-R BT.601 luma of every pixel.
    /// </summary>
    private static bool ImageIsDark(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var bitmap = Bitmap.DecodeToWidth(stream, 64);

        var width = bitmap.PixelSize.Width;
        var height = bitmap.PixelSize.Height;
        if (width <= 0 || height <= 0) return false;

        // CopyPixels always writes a Bgra8888 buffer.
        var stride = width * 4;
        var buffer = new byte[stride * height];

        var handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
        try
        {
            bitmap.CopyPixels(new PixelRect(0, 0, width, height), handle.AddrOfPinnedObject(), buffer.Length, stride);
        }
        finally
        {
            handle.Free();
        }

        long sum = 0;
        for (var i = 0; i < buffer.Length; i += 4)
        {
            // Bgra8888: B at +0, G at +1, R at +2.
            sum += (299 * buffer[i + 2] + 587 * buffer[i + 1] + 114 * buffer[i]) / 1000;
        }

        return (double)sum / buffer.Length * 4 < DarkLumaThreshold;
    }

    /// <summary>Parses the "r g b" desktop solid color and measures its luma.</summary>
    private static bool SolidColorIsDark(string value)
    {
        var parts = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3) return false;
        if (!int.TryParse(parts[0], out var r)
            || !int.TryParse(parts[1], out var g)
            || !int.TryParse(parts[2], out var b)) return false;
        return (299 * r + 587 * g + 114 * b) / 1000 < DarkLumaThreshold;
    }

    public void Dispose() => timer.Stop();
}
