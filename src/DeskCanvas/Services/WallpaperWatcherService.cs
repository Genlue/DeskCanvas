using System;
using System.IO;
using Avalonia.Threading;
using Microsoft.Win32;
using DeskCanvas.Views.Controls;

namespace DeskCanvas.Services;

/// <summary>
/// Listens to wallpaper change events via:
/// 1. Win32 SystemEvents.UserPreferenceChanged (category == Desktop / Color / General)
/// 2. Win32 SystemEvents.DisplaySettingsChanged (monitor resolution or topology change)
/// 3. FileSystemWatcher on %APPDATA%\Microsoft\Windows\Themes (TranscodedWallpaper change)
/// Upon change, invalidates the wallpaper capture cache and notifies all active LiquidGlass surfaces.
/// </summary>
public class WallpaperWatcherService : IDisposable
{
    private readonly WallpaperThemeService? wallpaperThemeService;
    private readonly DispatcherTimer debounceTimer;
    private readonly DispatcherTimer followUpTimer;
    private FileSystemWatcher? fileWatcher;
    private bool disposed;

    public event Action? WallpaperChanged;

    public WallpaperWatcherService(WallpaperThemeService? wallpaperThemeService = null)
    {
        this.wallpaperThemeService = wallpaperThemeService;

        debounceTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(200)
        };
        debounceTimer.Tick += OnDebounceTick;

        followUpTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(650)
        };
        followUpTimer.Tick += OnFollowUpTick;

        if (OperatingSystem.IsWindows())
        {
            try
            {
                SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
                SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;

                InitFileWatcher();
            }
            catch
            {
                // Fallback gracefully if system events cannot be hooked
            }
        }
    }

    private void InitFileWatcher()
    {
        try
        {
            var themesDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                @"Microsoft\Windows\Themes");

            if (Directory.Exists(themesDir))
            {
                fileWatcher = new FileSystemWatcher(themesDir)
                {
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.CreationTime,
                    IncludeSubdirectories = true,
                    EnableRaisingEvents = true
                };

                fileWatcher.Changed += OnFileChanged;
                fileWatcher.Created += OnFileChanged;
                fileWatcher.Renamed += OnFileChanged;
            }
        }
        catch
        {
            // Non-critical if FileSystemWatcher fails
        }
    }

    // Raised only by SystemEvents.UserPreferenceChanged, which is subscribed inside the
    // OperatingSystem.IsWindows() guard in the constructor.
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category is UserPreferenceCategory.Desktop 
            or UserPreferenceCategory.Color 
            or UserPreferenceCategory.General 
            or UserPreferenceCategory.VisualStyle)
        {
            TriggerWallpaperChanged();
        }
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        TriggerWallpaperChanged();
    }

    private void OnFileChanged(object? sender, FileSystemEventArgs e)
    {
        var name = Path.GetFileName(e.FullPath);
        if (name.StartsWith("Transcoded", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith(".bmp", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("slideshow.ini", StringComparison.OrdinalIgnoreCase))
        {
            TriggerWallpaperChanged();
        }
    }

    public void TriggerWallpaperChanged()
    {
        if (disposed) return;

        Dispatcher.UIThread.Post(() =>
        {
            if (disposed) return;
            debounceTimer.Stop();
            debounceTimer.Start();
        });
    }

    private void OnDebounceTick(object? sender, EventArgs e)
    {
        debounceTimer.Stop();
        if (disposed) return;

        // Fullscreen suspension: the desktop capture is released while covered, and Invalidate +
        // RefreshAll would immediately re-capture it (and re-render every glass card) for pixels
        // nobody can see. Re-arm instead — the change is picked up as soon as the desktop returns.
        if (LiquidGlassWallpaper.IsSuspended)
        {
            debounceTimer.Start();
            return;
        }

        // 1. Invalidate desktop capture and file caches
        LiquidGlassWallpaper.Invalidate();

        // 2. Refresh all active liquid glass widgets
        LiquidGlassSurface.RefreshAll();

        // 3. Update theme brightness check if auto dark/light is active
        wallpaperThemeService?.RequestCheck();

        // 4. Raise event for any external listeners
        WallpaperChanged?.Invoke();

        // 5. Schedule a follow-up refresh to capture the 100% settled wallpaper
        // after Windows desktop cross-fade transition completes
        followUpTimer.Stop();
        followUpTimer.Start();
    }

    private void OnFollowUpTick(object? sender, EventArgs e)
    {
        followUpTimer.Stop();
        if (disposed) return;

        // Same suspension rule as OnDebounceTick: re-arm, refresh after the desktop returns.
        if (LiquidGlassWallpaper.IsSuspended)
        {
            followUpTimer.Start();
            return;
        }

        LiquidGlassWallpaper.Invalidate();
        LiquidGlassSurface.RefreshAll();
        wallpaperThemeService?.RequestCheck();
        WallpaperChanged?.Invoke();
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;

        debounceTimer.Stop();
        followUpTimer.Stop();

        if (OperatingSystem.IsWindows())
        {
            try
            {
                SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
                SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
            }
            catch { }
        }

        if (fileWatcher != null)
        {
            try
            {
                fileWatcher.EnableRaisingEvents = false;
                fileWatcher.Dispose();
            }
            catch { }
            fileWatcher = null;
        }

        GC.SuppressFinalize(this);
    }
}
