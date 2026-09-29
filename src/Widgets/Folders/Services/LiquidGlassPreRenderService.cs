using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using uWidgets.Core.Models.Settings;

namespace Folders.Services;

/// <summary>
/// Pre-renders the Liquid Glass background bitmap for BigFolderPopupWindow ahead of time,
/// so when the user clicks to open the expanded folder view, the glass background is
/// immediately available without any latency or delay.
/// Also responds dynamically to position, size, and wallpaper changes.
/// </summary>
public static class LiquidGlassPreRenderService
{
    private static readonly object renderLock = new();
    private static CancellationTokenSource? currentCts;
    private static string? lastRenderKey;
    private static long wallpaperRevision = 0;
    private static bool subscribedWallpaper = false;

    private static readonly Dictionary<string, Bitmap> locationCache = new(StringComparer.Ordinal);
    private const int MaxCachedLocations = 4;

    public static Bitmap? CachedPopupBitmap { get; private set; }

    public static event Action? PreRenderCompleted;

    static LiquidGlassPreRenderService()
    {
        EnsureWallpaperSubscribed();
    }

    public static void EnsureWallpaperSubscribed()
    {
        if (subscribedWallpaper) return;
        subscribedWallpaper = true;
        LiquidGlassBridge.SubscribeWallpaperInvalidated(OnWallpaperChanged);
    }

    private static void OnWallpaperChanged()
    {
        InvalidateWallpaper();
    }

    public static void InvalidateWallpaper()
    {
        Interlocked.Increment(ref wallpaperRevision);
        lock (renderLock)
        {
            lastRenderKey = null;
            foreach (var bmp in locationCache.Values)
            {
                bmp.Dispose();
            }
            locationCache.Clear();
            CachedPopupBitmap?.Dispose();
            CachedPopupBitmap = null;
        }
    }

    public static (double targetX, double targetY, int renderW, int renderH, double scale, Screen targetScreen, IReadOnlyList<Screen> allScreens, int left, int top, int desktopWidth, int desktopHeight) 
        ComputePlacement(Point? screenCenter, double logicalWidth, double logicalHeight, Screen? targetScreen, IReadOnlyList<Screen>? allScreens)
    {
        targetScreen ??= allScreens?.FirstOrDefault();
        double scale = targetScreen?.Scaling > 0 ? targetScreen.Scaling : 1.0;
        double physWidth = logicalWidth * scale;
        double physHeight = logicalHeight * scale;

        double targetX;
        double targetY;

        if (targetScreen != null)
        {
            if (screenCenter.HasValue)
            {
                targetX = screenCenter.Value.X - physWidth / 2.0;
                targetY = screenCenter.Value.Y - physHeight / 2.0;
            }
            else
            {
                targetX = targetScreen.WorkingArea.X + (targetScreen.WorkingArea.Width - physWidth) / 2.0;
                targetY = targetScreen.WorkingArea.Y + (targetScreen.WorkingArea.Height - physHeight) / 2.0;
            }

            var work = targetScreen.WorkingArea;
            double margin = 16 * scale;
            targetX = Math.Clamp(targetX, work.X + margin, work.X + Math.Max(0, work.Width - physWidth - margin));
            targetY = Math.Clamp(targetY, work.Y + margin, work.Y + Math.Max(0, work.Height - physHeight - margin));
        }
        else
        {
            targetX = 0;
            targetY = 0;
        }

        int renderW = Math.Max(1, (int)Math.Round(physWidth));
        int renderH = Math.Max(1, (int)Math.Round(physHeight));

        var screensList = (allScreens != null && allScreens.Count > 0) ? allScreens : (targetScreen != null ? [targetScreen] : Array.Empty<Screen>());
        var left = screensList.Count > 0 ? screensList.Min(s => s.Bounds.X) : 0;
        var top = screensList.Count > 0 ? screensList.Min(s => s.Bounds.Y) : 0;
        var desktopWidth = screensList.Count > 0 ? screensList.Max(s => s.Bounds.Right) - left : 1920;
        var desktopHeight = screensList.Count > 0 ? screensList.Max(s => s.Bounds.Bottom) - top : 1080;

        return (targetX, targetY, renderW, renderH, scale, targetScreen!, screensList, left, top, desktopWidth, desktopHeight);
    }

    public static Bitmap? GetCachedBitmapFor(Point? screenCenter, double logicalWidth, double logicalHeight, double cornerRadius, Screen? targetScreen, IReadOnlyList<Screen>? allScreens)
    {
        var p = ComputePlacement(screenCenter, logicalWidth, logicalHeight, targetScreen, allScreens);
        var locKey = LocationKey(p.targetX, p.targetY, p.renderW, p.renderH, cornerRadius);
        lock (renderLock)
        {
            if (locationCache.TryGetValue(locKey, out var bmp))
            {
                return bmp;
            }
            return CachedPopupBitmap;
        }
    }

    // The corner radius is baked into the cached bitmap's rounded alpha; it must be part of
    // the key so a widget-radius change re-renders instead of surfacing a stale shape.
    private static string LocationKey(double targetX, double targetY, int renderW, int renderH, double cornerRadius)
        => $"{(int)targetX}_{(int)targetY}_{renderW}_{renderH}_{Math.Round(cornerRadius)}";

    public static void RequestPreRender(
        Point? screenCenter,
        double logicalWidth,
        double logicalHeight,
        double cornerRadius,
        Theme? theme,
        bool isDark,
        Screen? targetScreen,
        IReadOnlyList<Screen>? allScreens)
    {
        EnsureWallpaperSubscribed();
        if (theme?.UsesRenderedGlass != true) return;
        if (!LiquidGlassBridge.IsAvailable) return;

        targetScreen ??= allScreens?.FirstOrDefault();
        if (targetScreen == null) return;

        var p = ComputePlacement(screenCenter, logicalWidth, logicalHeight, targetScreen, allScreens);

        // The key covers the surface as well: the two glass materials share the sampling
        // pipeline but not the recipe, so each carries its own optics into the key. Mirrors
        // uWidgets' LiquidGlassDispatch.OpticsKey (Folders has no direct host reference).
        var opticsKey = theme.EffectiveSurface == SurfaceStyle.LiquidGlassV2
            ? $"v2_{theme.EffectiveLiquidGlassV2.Blur}_{theme.EffectiveLiquidGlassV2.Refraction}_{theme.EffectiveLiquidGlassV2.Highlight}_{theme.EffectiveLiquidGlassV2.Vibrancy}_{theme.EffectiveLiquidGlassV2.Dispersion}"
            : $"lg_{theme.EffectiveLiquidGlass.Blur}_{theme.EffectiveLiquidGlass.Refraction}_{theme.EffectiveLiquidGlass.LightAngle}_{theme.EffectiveLiquidGlass.Glow}_{theme.EffectiveLiquidGlass.Spectrum}";
        var key = $"{wallpaperRevision}_{(int)p.targetX}_{(int)p.targetY}_{p.renderW}_{p.renderH}_{p.scale}_{isDark}_{theme.EffectiveSurface}_{opticsKey}_{Math.Round(cornerRadius)}";
        var locKey = LocationKey(p.targetX, p.targetY, p.renderW, p.renderH, cornerRadius);

        lock (renderLock)
        {
            if (key == lastRenderKey && (locationCache.ContainsKey(locKey) || CachedPopupBitmap != null))
            {
                return;
            }

            currentCts?.Cancel();
            currentCts = new CancellationTokenSource();
            var token = currentCts.Token;

            Task.Run(() =>
            {
                try
                {
                    if (token.IsCancellationRequested) return;

                    var bytes = LiquidGlassBridge.Render(
                        p.renderW,
                        p.renderH,
                        (float)p.scale,
                        (float)cornerRadius, // Correct: LiquidGlassRenderer scales by frame.Scale internally
                        (float)(p.targetX - p.left),
                        (float)(p.targetY - p.top),
                        (float)p.desktopWidth,
                        (float)p.desktopHeight,
                        (float)(p.targetScreen.Bounds.X - p.left),
                        (float)(p.targetScreen.Bounds.Y - p.top),
                        (float)p.targetScreen.Bounds.Width,
                        (float)p.targetScreen.Bounds.Height,
                        theme,
                        isDark,
                        settingsSurface: false,
                        pixelScale: 1.0f);

                    if (bytes == null || bytes.Length == 0 || token.IsCancellationRequested) return;

                    Dispatcher.UIThread.Post(() =>
                    {
                        if (token.IsCancellationRequested) return;
                        try
                        {
                            using var stream = new MemoryStream(bytes);
                            var nextBmp = new Bitmap(stream);

                            lock (renderLock)
                            {
                                if (locationCache.TryGetValue(locKey, out var oldBmp))
                                {
                                    oldBmp.Dispose();
                                }
                                else if (locationCache.Count >= MaxCachedLocations)
                                {
                                    var firstKey = locationCache.Keys.First();
                                    locationCache[firstKey].Dispose();
                                    locationCache.Remove(firstKey);
                                }

                                locationCache[locKey] = nextBmp;

                                if (CachedPopupBitmap != null && !locationCache.ContainsValue(CachedPopupBitmap))
                                {
                                    CachedPopupBitmap.Dispose();
                                }
                                CachedPopupBitmap = nextBmp;
                                lastRenderKey = key;
                            }

                            PreRenderCompleted?.Invoke();
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"Failed to create pre-rendered LiquidGlass bitmap: {ex}");
                        }
                    });
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Background LiquidGlass pre-render error: {ex}");
                }
            }, token);
        }
    }

    /// <summary>
    /// Synchronously or fast-async triggers an on-demand render when the popup window opens
    /// if the background pre-cache is not yet available.
    /// </summary>
    public static async Task<Bitmap?> RenderDirectAsync(
        Point? screenCenter,
        double logicalWidth,
        double logicalHeight,
        double cornerRadius,
        Theme? theme,
        bool isDark,
        Screen? targetScreen,
        IReadOnlyList<Screen>? allScreens)
    {
        if (theme?.UsesRenderedGlass != true || !LiquidGlassBridge.IsAvailable) return null;

        targetScreen ??= allScreens?.FirstOrDefault();
        if (targetScreen == null) return null;

        var p = ComputePlacement(screenCenter, logicalWidth, logicalHeight, targetScreen, allScreens);
        var locKey = LocationKey(p.targetX, p.targetY, p.renderW, p.renderH, cornerRadius);

        try
        {
            var bytes = await Task.Run(() =>
            {
                return LiquidGlassBridge.Render(
                    p.renderW,
                    p.renderH,
                    (float)p.scale,
                    (float)cornerRadius,
                    (float)(p.targetX - p.left),
                    (float)(p.targetY - p.top),
                    (float)p.desktopWidth,
                    (float)p.desktopHeight,
                    (float)(p.targetScreen.Bounds.X - p.left),
                    (float)(p.targetScreen.Bounds.Y - p.top),
                    (float)p.targetScreen.Bounds.Width,
                    (float)p.targetScreen.Bounds.Height,
                    theme,
                    isDark,
                    settingsSurface: false,
                    pixelScale: 1.0f);
            });

            if (bytes == null || bytes.Length == 0) return null;

            using var stream = new MemoryStream(bytes);
            var bmp = new Bitmap(stream);

            lock (renderLock)
            {
                if (locationCache.TryGetValue(locKey, out var oldBmp))
                {
                    oldBmp.Dispose();
                }
                locationCache[locKey] = bmp;
                CachedPopupBitmap = bmp;
            }

            return bmp;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"RenderDirectAsync failed: {ex}");
            return null;
        }
    }
}
