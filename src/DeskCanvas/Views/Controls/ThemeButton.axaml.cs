using System;
using System.ComponentModel;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using DeskCanvas.Core.Interfaces;
using DeskCanvas.Core.Models.Settings;
using DeskCanvas.Locales;
using DeskCanvas.Services;

namespace DeskCanvas.Views.Controls;

public partial class ThemeButton : UserControl, INotifyPropertyChanged
{
    public new event PropertyChangedEventHandler? PropertyChanged;
    private static readonly Bitmap wallpaper = GetWallpaperPreview();
    public Theme AppTheme { get; }
    public Bitmap Wallpaper => wallpaper;
    public bool DimWallpaper => AppTheme.DarkMode == true;
    public bool IsRenderedGlass => AppTheme.UsesRenderedGlass;
    public bool IsColorful => AppTheme.IsColorful;
    public bool IsFrosted => AppTheme.UsesNativeBlur;
    public bool IsSelected => appSettingsProvider.Get().Theme.EffectiveSurface == AppTheme.EffectiveSurface;
    public IBrush SelectionBrush => IsSelected ? Brushes.DodgerBlue : Brushes.Transparent;
    /// <summary>
    /// Material handed to the preview <see cref="LiquidGlassSurface"/>: the preset's own
    /// surface and — unless this button is the active one — the preset's own optics, so the preview
    /// shows what choosing the preset will actually look like rather than the current settings.
    /// </summary>
    public Theme GlassMaterial
    {
        get
        {
            var current = appSettingsProvider.Get().Theme;
            return current with
            {
                Surface = AppTheme.UsesRenderedGlass ? AppTheme.EffectiveSurface : SurfaceStyle.LiquidGlass,
                OpacityLevel = IsSelected ? current.OpacityLevel : AppTheme.OpacityLevel,
                LiquidGlass = IsSelected ? current.EffectiveLiquidGlass : AppTheme.EffectiveLiquidGlass
            };
        }
    }
    public Brush WidgetBackground
    {
        get
        {
            var theme = appSettingsProvider.Get().Theme;
            var dark = IsDark();
            if (IsColorful)
            {
                var color = dark ? Color.Parse("#1C1C1E") : Color.Parse("#FFFFFF");
                return new SolidColorBrush(color, 1.0);
            }
            // Both surfaces use the user's custom dark/light background colors
            // (defaults: dark #2E2E2E, light #FFFFFF).
            var bg = dark
                ? ParseColor(theme.EffectiveSolidBackgroundDark, "#2E2E2E")
                : ParseColor(theme.EffectiveSolidBackgroundLight, "#FFFFFF");
            return new SolidColorBrush(bg, AppTheme.OpacityLevel);
        }
    }

    /// <summary>
    /// Whether the preset preview represents the dark mode: the template's own
    /// choice, or — when it follows the system — the currently resolved variant.
    /// </summary>
    private bool IsDark() =>
        AppTheme.DarkMode ?? Application.Current!.ActualThemeVariant == ThemeVariant.Dark;
    public CornerRadius WidgetCornerRadius => new(WidgetRadius);
    public double WidgetRadius => AppTheme.UseNativeFrame ? 2 : 10;
    public BoxShadows WidgetShadow => AppTheme.UseNativeFrame 
        ? new(BoxShadow.Parse("0 0 10 0 #40000000"))
        : new();
    public FontFamily WidgetFontFamily => AppTheme.FontFamily == "Inter"
        ? new FontFamily("avares://Avalonia.Fonts.Inter#Inter")
        : new FontFamily(AppTheme.FontFamily);

    /// <summary>The preset name shown below the preview (毛玻璃 / 纯色 / 液态玻璃 / 新液态玻璃 / 多彩).</summary>
    public string ThemeName => ViewModels.SurfaceNames.Of(AppTheme.EffectiveSurface);

    private static Color ParseColor(string hex, string fallbackHex) =>
        Color.TryParse(hex, out var color) ? color : Color.Parse(fallbackHex);

    /// <summary>
    /// Optional 1 px edge for presets drawn with a native frame; every other preview shows the
    /// shared 高光 rim instead (see <see cref="PreviewShowsHighlight"/>). The old 描边 ring no
    /// longer exists, so the preview carries no live colour/width any more.
    /// </summary>
    public IBrush? PreviewBorderBrush =>
        AppTheme.UseNativeFrame ? new SolidColorBrush(Color.Parse("#60808080")) : null;

    public Thickness PreviewBorderThickness => AppTheme.UseNativeFrame ? new Thickness(1) : new Thickness(0);

    /// <summary>
    /// 高光 preview: 毛玻璃 / 全彩 presets draw the same rim the host overlays on their cards
    /// (液态玻璃's preview is the rendered glass surface, which lights its own rim).
    /// </summary>
    public bool PreviewShowsHighlight => !IsRenderedGlass && !AppTheme.UseNativeFrame;

    /// <summary>Rim strength of the live theme, shared with the liquid-glass shader.</summary>
    public double PreviewHighlightStrength => appSettingsProvider.Get().Theme.EffectiveLiquidGlass.Highlight;

    /// <summary>Rim light direction of the live theme.</summary>
    public double PreviewHighlightAngle => appSettingsProvider.Get().Theme.EffectiveLiquidGlass.HighlightAngle;

    public SolidColorBrush? WidgetForeground
    {
        get
        {
            var dark = IsDark();
            if (IsColorful)
                return new SolidColorBrush(dark ? Color.Parse("#FF453A") : Color.Parse("#FF3B30"));
            // Every other mode reads the accent ramp ThemeService writes: 强调色 keeps the
            // picked accent, 背景色 resolves to the inverted background, 黑白 to pure
            // black (light) / white (dark).
            var color = ThemeScopeResolver.ResolveColor(this, dark ? "SystemAccentColorLight2" : "SystemAccentColorDark1", ActualThemeVariant, dark ? Colors.White : Colors.Black);
            return new SolidColorBrush(color);
        }
    }
    
    private readonly IAppSettingsProvider appSettingsProvider;

    public ThemeButton(IAppSettingsProvider appSettingsProvider, Theme appTheme)
    {
        this.appSettingsProvider = appSettingsProvider;
        AppTheme = appTheme;
        DataContext = this;
        InitializeComponent();
        AttachedToVisualTree += (_, _) =>
        {
            appSettingsProvider.DataChanged += OnSettingsChanged;
            RefreshPreview();
        };
        DetachedFromVisualTree += (_, _) => appSettingsProvider.DataChanged -= OnSettingsChanged;
    }

    private void OnSettingsChanged(object sender, AppSettings? oldData, AppSettings newData) => RefreshPreview();

    private void RefreshPreview()
    {
        foreach (var property in new[] { nameof(IsSelected), nameof(SelectionBrush), nameof(GlassMaterial),
            nameof(IsRenderedGlass), nameof(WidgetBackground), nameof(WidgetForeground), nameof(PreviewBorderBrush), nameof(PreviewBorderThickness),
            nameof(PreviewShowsHighlight), nameof(PreviewHighlightStrength), nameof(PreviewHighlightAngle) })
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
    }
    
    public static Bitmap GetWallpaperPreview(int targetHeight = 150)
    {
        // 1. Fast path: Decode directly from the Windows wallpaper file in milliseconds
        try
        {
            var wallpaperPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                @"Microsoft\Windows\Themes\TranscodedWallpaper");
            if (!File.Exists(wallpaperPath))
                wallpaperPath = InteropService.GetWallpaperPath();

            if (File.Exists(wallpaperPath))
            {
                using var fileStream = File.OpenRead(wallpaperPath);
                return Bitmap.DecodeToHeight(fileStream, targetHeight);
            }
        }
        catch { }

        // 2. Fallback: query wallpaper snapshot. The caller owns the reference Get() hands out.
        using var snapshot = LiquidGlassWallpaper.Get();
        try
        {
            if (snapshot.ImageBytes != null)
            {
                using var stream = new MemoryStream(snapshot.ImageBytes);
                return Bitmap.DecodeToHeight(stream, targetHeight);
            }
        }
        catch (Exception) { /* No readable wallpaper: preview the desktop solid color. */ }
        var renderTarget = new RenderTargetBitmap(new PixelSize(targetHeight * 4 / 3, targetHeight), new Vector(96, 96));
        using (var context = renderTarget.CreateDrawingContext(false))
            context.FillRectangle(new SolidColorBrush(Color.FromRgb(snapshot.Background.Red, snapshot.Background.Green, snapshot.Background.Blue)),
                new Rect(0, 0, targetHeight * 4 / 3, targetHeight));
        return renderTarget;
    }

    /// <summary>
    /// Applies only the surface material (glass vs solid). Dark mode, accent color,
    /// monochrome, font and native frame are set by their own controls and preserved,
    /// so choosing a material no longer resets the user's other appearance choices.
    /// Choosing Colorful automatically enables multi-color mode (Monochrome = false).
    /// </summary>
    private void Apply(object? sender, RoutedEventArgs e)
    {
        var settings = appSettingsProvider.Get();
        if (IsSelected) return;

        var currentSurface = settings.Theme.EffectiveSurface;
        // 1. Save current active theme's customized settings to its surface snapshot
        var updatedSettings = settings.WithThemeForSurface(currentSurface, settings.Theme);

        // 2. Load target surface theme from SurfaceThemes (or default preset)
        var targetTheme = updatedSettings.GetThemeForSurface(AppTheme.EffectiveSurface);

        // 3. Keep global preferences synchronized (Font, Frame, DarkMode, AutoTheme)
        targetTheme = targetTheme with
        {
            Surface = AppTheme.EffectiveSurface,
            FontFamily = settings.Theme.FontFamily,
            UseNativeFrame = settings.Theme.UseNativeFrame,
            DarkMode = settings.Theme.DarkMode,
            AutoTheme = settings.Theme.AutoTheme
        };

        // 4. Save and apply
        appSettingsProvider.Save(updatedSettings.WithThemeForSurface(AppTheme.EffectiveSurface, targetTheme) with { Theme = targetTheme });
    }
}
