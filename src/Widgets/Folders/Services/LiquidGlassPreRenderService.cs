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
using uWidgets.Views;

namespace Folders.Services;

/// <summary>
/// Pre-renders the Liquid Glass background bitmap for BigFolderPopupWindow ahead of time,
/// so when the user clicks to open the expanded folder view, the glass background is
/// immediately available without any latency or delay.
/// Also responds dynamically to position, size, and wallpaper changes.
/// <para>
/// Mirrors the host's PopupLiquidGlassService animation-frame strip: intermediate zoom frames
/// (one per covered scale, anchored on the opening widget) are pre-rendered through the
/// reflection bridge, cached in memory and on disk, and played back by the panel window on
/// open/close. Wallpaper switches dispose both caches immediately.
/// </para>
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

    // ---- open/close animation frame strip ----
    private sealed class FrameStrip
    {
        public readonly List<PanelAnimationFrame> Frames = new();
    }

    private static readonly Dictionary<string, FrameStrip> frameStrips = new(StringComparer.Ordinal);
    private static readonly HashSet<string> diskStripLoads = new(StringComparer.Ordinal);
    private const int MaxCachedStrips = 2;

    internal const double FrameScaleFrom = 0.60;
    internal const double FrameScaleMax = 0.97;
    internal const int FrameCount = 12;

    /// <summary>Signalled on the UI thread whenever a frame strip finished rendering or was loaded from disk.</summary>
    public static event Action? FrameStripCompleted;

    /// <summary>Same disk cache area as the host's popup glass frames; the "bf_" prefix keeps the two services' files apart.</summary>
    public static string FrameCacheDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "uWidgets", "Cache", "PopupGlassFrames");

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

            foreach (var strip in frameStrips.Values)
            {
                foreach (var frame in strip.Frames)
                {
                    try { frame.Bitmap.Dispose(); } catch { }
                }
            }
            frameStrips.Clear();
            diskStripLoads.Clear();
        }
        PurgeFrameCacheDirectory();
    }

    private static void PurgeFrameCacheDirectory()
    {
        Task.Run(() =>
        {
            try
            {
                if (Directory.Exists(FrameCacheDirectory))
                    Directory.Delete(FrameCacheDirectory, true);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Popup glass frame cache purge failed: {ex.Message}");
            }
        });
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

                    // With the final bitmap queued, pre-render the zoom's animation frames so a
                    // later open plays real glass for every scale.
                    if (!token.IsCancellationRequested && screenCenter.HasValue)
                        RenderAnimationFrames(screenCenter.Value, logicalWidth, logicalHeight, cornerRadius, theme, isDark, opticsKey, token);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Background LiquidGlass pre-render error: {ex}");
                }
            }, token);
        }
    }

    // ---- animation frame strip: render, cache, query ----

    private sealed record FramePlan(string Identity, double PhysWidth, double PhysHeight, double AnchorX, double AnchorY,
        double TargetX, double TargetY, int RenderW, int RenderH, double Scale,
        int Left, int Top, int DesktopWidth, int DesktopHeight, int ScreenX, int ScreenY, int ScreenWidth, int ScreenHeight)
    {
        public FramePlan(string identity, double physWidth, double physHeight, double anchorX, double anchorY,
            (double targetX, double targetY, int renderW, int renderH, double scale, Screen targetScreen, IReadOnlyList<Screen> allScreens, int left, int top, int desktopWidth, int desktopHeight) p)
            : this(identity, physWidth, physHeight, anchorX, anchorY,
                p.targetX, p.targetY, p.renderW, p.renderH, p.scale,
                p.left, p.top, p.desktopWidth, p.desktopHeight,
                p.targetScreen.Bounds.X - p.left, p.targetScreen.Bounds.Y - p.top,
                p.targetScreen.Bounds.Width, p.targetScreen.Bounds.Height) { }
    }

    private static FramePlan PlanFrames(Point spawn, double logicalWidth, double logicalHeight, double cornerRadius, string opticsKey, bool isDark)
    {
        var p = ComputePlacement(spawn, logicalWidth, logicalHeight, null, null);
        double physWidth = logicalWidth * p.scale;
        double physHeight = logicalHeight * p.scale;
        var anchorX = Math.Clamp((spawn.X - p.targetX) / Math.Max(1, physWidth), -0.5, 1.5);
        var anchorY = Math.Clamp((spawn.Y - p.targetY) / Math.Max(1, physHeight), -0.5, 1.5);
        var identity = $"bf_{(int)p.targetX}_{(int)p.targetY}_{p.renderW}_{p.renderH}_{(int)p.scale}_{(isDark ? 1 : 0)}" +
                       $"_{opticsKey}_{Math.Round(cornerRadius)}_{Math.Round(anchorX * 100)}_{Math.Round(anchorY * 100)}";
        return new FramePlan(identity, physWidth, physHeight, anchorX, anchorY, p);
    }

    private static void RenderAnimationFrames(
        Point spawn, double logicalWidth, double logicalHeight, double cornerRadius,
        Theme theme, bool isDark, string opticsKey, CancellationToken token)
    {
        var plan = PlanFrames(spawn, logicalWidth, logicalHeight, cornerRadius, opticsKey, isDark);
        if (plan.PhysWidth < 8 || plan.PhysHeight < 8) return;

        var frames = new List<(double Scale, byte[] Png)>(FrameCount);
        try
        {
            var step = (FrameScaleMax - FrameScaleFrom) / (FrameCount + 1);
            for (var i = 0; i < FrameCount; i++)
            {
                if (token.IsCancellationRequested) return;
                var t = FrameScaleFrom + step * (i + 1);
                var w = Math.Max(1, (int)Math.Round(plan.PhysWidth * t));
                var h = Math.Max(1, (int)Math.Round(plan.PhysHeight * t));
                var rectX = plan.TargetX + plan.AnchorX * (1 - t) * plan.PhysWidth;
                var rectY = plan.TargetY + plan.AnchorY * (1 - t) * plan.PhysHeight;

                // Same desktop/screen metrics as the final render, shifted to the covered rect:
                // the glass must sample the wallpaper exactly where that rect sits. The radius
                // is the scaled DIP radius (LiquidGlassRenderer applies frame.Scale itself).
                var bytes = LiquidGlassBridge.Render(
                    w, h, (float)plan.Scale, (float)(cornerRadius * t),
                    (float)(rectX - plan.Left), (float)(rectY - plan.Top),
                    (float)plan.DesktopWidth, (float)plan.DesktopHeight,
                    (float)plan.ScreenX, (float)plan.ScreenY,
                    (float)plan.ScreenWidth, (float)plan.ScreenHeight,
                    theme, isDark, settingsSurface: false, pixelScale: 1.0f);
                if (bytes == null || bytes.Length == 0) return;
                frames.Add((t, bytes));
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Popup glass animation frame render error: {ex}");
            return;
        }
        if (token.IsCancellationRequested) return;

        var directory = FrameCacheDirectory;
        var identity = plan.Identity;
        Task.Run(() =>
        {
            try
            {
                Directory.CreateDirectory(directory);
                for (var i = 0; i < frames.Count; i++)
                    File.WriteAllBytes(Path.Combine(directory, $"{identity}_{i}.png"), frames[i].Png);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Popup glass frame cache write failed: {ex.Message}");
            }
        });

        StoreStrip(LocationKey(plan.TargetX, plan.TargetY, plan.RenderW, plan.RenderH, cornerRadius), frames, token);
    }

    private static void StoreStrip(string locationKey, List<(double Scale, byte[] Png)> frames, CancellationToken token)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (token.IsCancellationRequested) return;
            try
            {
                var strip = new FrameStrip();
                foreach (var (scale, png) in frames)
                    strip.Frames.Add(new PanelAnimationFrame(scale, new Bitmap(new MemoryStream(png))));

                lock (renderLock)
                {
                    if (frameStrips.TryGetValue(locationKey, out var old))
                    {
                        foreach (var frame in old.Frames)
                        {
                            try { frame.Bitmap.Dispose(); } catch { }
                        }
                    }
                    frameStrips[locationKey] = strip;
                    while (frameStrips.Count > MaxCachedStrips)
                    {
                        var firstKey = frameStrips.Keys.First();
                        foreach (var frame in frameStrips[firstKey].Frames)
                        {
                            try { frame.Bitmap.Dispose(); } catch { }
                        }
                        frameStrips.Remove(firstKey);
                    }
                }

                FrameStripCompleted?.Invoke();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to store popup glass animation frames: {ex}");
            }
        });
    }

    /// <summary>
    /// The cached animation frames for this placement, ascending by scale. True on a memory hit;
    /// on a miss this kicks off a disk load whose completion fires <see cref="FrameStripCompleted"/>.
    /// </summary>
    public static bool TryGetGlassFrames(
        Point? screenCenter, double logicalWidth, double logicalHeight, double cornerRadius,
        Screen? targetScreen, IReadOnlyList<Screen>? allScreens, out IReadOnlyList<PanelAnimationFrame>? frames)
    {
        frames = null;
        targetScreen ??= allScreens?.FirstOrDefault();
        if (targetScreen == null || logicalWidth < 8 || logicalHeight < 8) return false;

        var p = ComputePlacement(screenCenter, logicalWidth, logicalHeight, targetScreen, allScreens);
        var locationKey = LocationKey(p.targetX, p.targetY, p.renderW, p.renderH, cornerRadius);

        lock (renderLock)
        {
            if (frameStrips.TryGetValue(locationKey, out var strip))
            {
                frames = strip.Frames;
                return true;
            }
            if (!diskStripLoads.Add(locationKey)) return false;
        }

        LoadStripFromDisk(p, cornerRadius, locationKey);
        return false;
    }

    private static void LoadStripFromDisk(
        (double targetX, double targetY, int renderW, int renderH, double scale, Screen targetScreen, IReadOnlyList<Screen> allScreens, int left, int top, int desktopWidth, int desktopHeight) p,
        double cornerRadius, string locationKey)
    {
        Task.Run(() =>
        {
            List<(double Scale, byte[] Png)>? frames = null;
            try
            {
                var directory = FrameCacheDirectory;
                if (!Directory.Exists(directory)) return;

                var prefix = $"bf_{(int)p.targetX}_{(int)p.targetY}_{p.renderW}_{p.renderH}_{(int)p.scale}_";
                var suffix = $"_{Math.Round(cornerRadius)}_";
                var groups = new Dictionary<string, List<(int Index, string Path, DateTime Written)>>(StringComparer.Ordinal);
                foreach (var path in Directory.GetFiles(directory, prefix + "*" + suffix + "*.png"))
                {
                    var name = Path.GetFileNameWithoutExtension(path);
                    var separator = name.LastIndexOf('_');
                    if (separator < 0 || !int.TryParse(name[(separator + 1)..], out var index)) continue;
                    var group = name[..separator];
                    if (!groups.TryGetValue(group, out var list)) groups[group] = list = new List<(int, string, DateTime)>();
                    list.Add((index, path, File.GetLastWriteTimeUtc(path)));
                }

                var best = groups.Values
                    .Where(g => g.Count >= 6)
                    .OrderByDescending(g => g.Count)
                    .ThenByDescending(g => g.Max(f => f.Written))
                    .FirstOrDefault();
                if (best == null) return;

                var step = (FrameScaleMax - FrameScaleFrom) / (FrameCount + 1);
                frames = best
                    .OrderBy(f => f.Index)
                    .Select(f => (FrameScaleFrom + step * (f.Index + 1), File.ReadAllBytes(f.Path)))
                    .ToList();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Popup glass frame cache read failed: {ex.Message}");
                return;
            }

            if (frames.Count > 0)
                StoreStrip(locationKey, frames, CancellationToken.None);
        });
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
