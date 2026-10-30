using System;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Map.Models;
using Map.Services;

namespace Map.Controls;

/// <summary>
/// Native high-performance map canvas control.
/// Renders slippy map tiles, center marker pin, scale bar, and handles panning / zooming gestures.
/// </summary>
public class MapCanvas : Control
{
    public static readonly StyledProperty<double> LatitudeProperty =
        AvaloniaProperty.Register<MapCanvas, double>(nameof(Latitude), 39.9042);

    public static readonly StyledProperty<double> LongitudeProperty =
        AvaloniaProperty.Register<MapCanvas, double>(nameof(Longitude), 116.4074);

    public static readonly StyledProperty<int> ZoomProperty =
        AvaloniaProperty.Register<MapCanvas, int>(nameof(Zoom), 14);

    public static readonly StyledProperty<MapProvider> ProviderProperty =
        AvaloniaProperty.Register<MapCanvas, MapProvider>(nameof(Provider), MapProvider.Amap);

    public static readonly StyledProperty<bool> ShowPinProperty =
        AvaloniaProperty.Register<MapCanvas, bool>(nameof(ShowPin), true);

    public static readonly StyledProperty<bool> ShowScaleBarProperty =
        AvaloniaProperty.Register<MapCanvas, bool>(nameof(ShowScaleBar), true);

    public static readonly StyledProperty<bool> AllowMapDragProperty =
        AvaloniaProperty.Register<MapCanvas, bool>(nameof(AllowMapDrag), true);

    public static readonly StyledProperty<string?> BaiduApiKeyProperty =
        AvaloniaProperty.Register<MapCanvas, string?>(nameof(BaiduApiKey));

    public static readonly StyledProperty<CornerRadius> CornerRadiusProperty =
        AvaloniaProperty.Register<MapCanvas, CornerRadius>(nameof(CornerRadius), new CornerRadius(0));

    public static readonly StyledProperty<bool> IsDarkModeProperty =
        AvaloniaProperty.Register<MapCanvas, bool>(nameof(IsDarkMode), false);

    public double Latitude
    {
        get => GetValue(LatitudeProperty);
        set => SetValue(LatitudeProperty, value);
    }

    public double Longitude
    {
        get => GetValue(LongitudeProperty);
        set => SetValue(LongitudeProperty, value);
    }

    public int Zoom
    {
        get => GetValue(ZoomProperty);
        set => SetValue(ZoomProperty, value);
    }

    public MapProvider Provider
    {
        get => GetValue(ProviderProperty);
        set => SetValue(ProviderProperty, value);
    }

    public bool IsDarkMode
    {
        get => GetValue(IsDarkModeProperty);
        set => SetValue(IsDarkModeProperty, value);
    }

    public bool ShowPin
    {
        get => GetValue(ShowPinProperty);
        set => SetValue(ShowPinProperty, value);
    }

    public bool ShowScaleBar
    {
        get => GetValue(ShowScaleBarProperty);
        set => SetValue(ShowScaleBarProperty, value);
    }

    public bool AllowMapDrag
    {
        get => GetValue(AllowMapDragProperty);
        set => SetValue(AllowMapDragProperty, value);
    }

    public string? BaiduApiKey
    {
        get => GetValue(BaiduApiKeyProperty);
        set => SetValue(BaiduApiKeyProperty, value);
    }

    public CornerRadius CornerRadius
    {
        get => GetValue(CornerRadiusProperty);
        set => SetValue(CornerRadiusProperty, value);
    }

    private MapTileService? tileService;
    private bool isDragging;
    private Point dragStartPoint;
    private double dragStartWorldX;
    private double dragStartWorldY;

    public event Action<double, double>? CenterCoordinatesChanged;
    public event Action<int>? ZoomChanged;

    public void AttachTileService(MapTileService service)
    {
        if (tileService != null)
            tileService.TileLoaded -= OnTileLoaded;

        tileService = service;
        tileService.TileLoaded += OnTileLoaded;
    }

    private void OnTileLoaded()
    {
        InvalidateVisual();
    }

    static MapCanvas()
    {
        AffectsRender<MapCanvas>(LatitudeProperty, LongitudeProperty, ZoomProperty, ProviderProperty,
            IsDarkModeProperty, ShowPinProperty, ShowScaleBarProperty, CornerRadiusProperty);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        var bounds = Bounds;
        if (bounds.Width <= 0 || bounds.Height <= 0 || tileService == null) return;

        bool hasRadius = CornerRadius.TopLeft > 0 || CornerRadius.TopRight > 0
                         || CornerRadius.BottomLeft > 0 || CornerRadius.BottomRight > 0;

        if (hasRadius)
        {
            using (context.PushClip(new RoundedRect(new Rect(0, 0, bounds.Width, bounds.Height), CornerRadius)))
            {
                RenderMap(context, bounds);
            }
        }
        else
        {
            RenderMap(context, bounds);
        }
    }

    private void RenderMap(DrawingContext context, Rect bounds)
    {
        var zoom = Math.Clamp(Zoom, MapModel.MinZoom, MapModel.MaxZoom);
        var (centerWorldX, centerWorldY) = MapProjection.LatLonToWorldPixels(Latitude, Longitude, zoom);

        var halfW = bounds.Width / 2.0;
        var halfH = bounds.Height / 2.0;

        var minWorldX = centerWorldX - halfW;
        var maxWorldX = centerWorldX + halfW;
        var minWorldY = centerWorldY - halfH;
        var maxWorldY = centerWorldY + halfH;

        var minTileX = (int)Math.Floor(minWorldX / 256.0);
        var maxTileX = (int)Math.Floor(maxWorldX / 256.0);
        var minTileY = (int)Math.Floor(minWorldY / 256.0);
        var maxTileY = (int)Math.Floor(maxWorldY / 256.0);

        var maxTile = 1 << zoom;
        // Guard against runaway drag or absurd bounding boxes
        if (maxTileX - minTileX > 16)
        {
            var midX = (int)Math.Floor(centerWorldX / 256.0);
            minTileX = midX - 8;
            maxTileX = midX + 8;
        }
        if (maxTileY - minTileY > 16)
        {
            var midY = (int)Math.Floor(centerWorldY / 256.0);
            minTileY = midY - 8;
            maxTileY = midY + 8;
        }

        // Dark mode adaptation: iPad light automatically uses iPad dark (CartoDB Dark Matter)
        var effectiveProvider = (IsDarkMode && Provider == MapProvider.AppleLight) ? MapProvider.AppleDark : Provider;
        var isDarkMap = IsDarkMode || effectiveProvider is MapProvider.AppleDark or MapProvider.AmapSatellite or MapProvider.GoogleSatellite or MapProvider.GoogleHybrid;

        var placeholderBrush = isDarkMap ? new SolidColorBrush(Color.Parse("#121214")) : new SolidColorBrush(Color.Parse("#F2F2F5"));
        var gridLinePen = new Pen(new SolidColorBrush(isDarkMap ? Color.Parse("#1E1E22") : Color.Parse("#E5E5EA")), 1);

        // Night-mode dimming tint for bright road map tiles when in Dark Mode
        var shouldDimLightRoadTiles = IsDarkMode && effectiveProvider is MapProvider.Amap or MapProvider.Google or MapProvider.OpenStreetMap or MapProvider.Baidu;
        var darkTintBrush = shouldDimLightRoadTiles ? new SolidColorBrush(Color.FromArgb(90, 10, 12, 18)) : null;

        // Draw visible tiles
        for (var tx = minTileX; tx <= maxTileX; tx++)
        {
            for (var ty = minTileY; ty <= maxTileY; ty++)
            {
                if (ty < 0 || ty >= maxTile) continue;

                var tileScreenX = tx * 256.0 - minWorldX;
                var tileScreenY = ty * 256.0 - minWorldY;
                var destRect = new Rect(tileScreenX, tileScreenY, 256, 256);

                Bitmap? tileBitmap = null;
                try
                {
                    tileBitmap = tileService?.GetTile(effectiveProvider, zoom, tx, ty, BaiduApiKey);
                }
                catch
                {
                    // Ignore tile lookup error
                }

                if (tileBitmap != null)
                {
                    try
                    {
                        context.DrawImage(tileBitmap, new Rect(0, 0, 256, 256), destRect);
                        if (darkTintBrush != null)
                        {
                            context.FillRectangle(darkTintBrush, destRect);
                        }
                    }
                    catch
                    {
                        // Fallback if bitmap handle is invalid or disposed
                        context.FillRectangle(placeholderBrush, destRect);
                    }
                }
                else
                {
                    // Placeholder tile
                    context.FillRectangle(placeholderBrush, destRect);
                    context.DrawRectangle(gridLinePen, destRect);
                }
            }
        }

        // Draw Apple-style Center Pin
        if (ShowPin)
        {
            DrawIpadPin(context, halfW, halfH);
        }

        // Draw Apple Maps Scale Bar if enabled and wide enough
        if (ShowScaleBar && bounds.Width >= 160 && bounds.Height >= 120)
        {
            DrawScaleBar(context, bounds, zoom, isDarkMap);
        }
    }

    private static void DrawIpadPin(DrawingContext context, double centerX, double centerY)
    {
        // Pin tip is anchored precisely at (centerX, centerY)
        // 1. Soft ground shadow
        var shadowBrush = new SolidColorBrush(Color.FromArgb(70, 0, 0, 0));
        context.DrawEllipse(shadowBrush, null, new Point(centerX, centerY + 1.5), 6.5, 2.5);

        // 2. Pin body (Red Apple Maps tear-drop pin)
        const double pinHeight = 26.0;
        const double pinHeadRadius = 8.5;
        var headCenterY = centerY - pinHeight + pinHeadRadius; // centerY - 17.5

        var pinGeometry = new StreamGeometry();
        using (var sgc = pinGeometry.Open())
        {
            // Start at the bottom sharp anchor tip
            sgc.BeginFigure(new Point(centerX, centerY), true);

            // Left curve flowing smoothly up to left side of the circular head
            sgc.CubicBezierTo(
                new Point(centerX - 1.5, centerY - 6.0),
                new Point(centerX - pinHeadRadius, headCenterY + 6.0),
                new Point(centerX - pinHeadRadius, headCenterY));

            // Semicircle arc sweeping over the top of the pin head (9 o'clock to 3 o'clock)
            sgc.ArcTo(
                new Point(centerX + pinHeadRadius, headCenterY),
                new Size(pinHeadRadius, pinHeadRadius),
                0, false, SweepDirection.Clockwise);

            // Right curve flowing smoothly from right side of head back to bottom tip
            sgc.CubicBezierTo(
                new Point(centerX + pinHeadRadius, headCenterY + 6.0),
                new Point(centerX + 1.5, centerY - 6.0),
                new Point(centerX, centerY));

            sgc.EndFigure(true);
        }

        var pinGradient = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0.2, 0.0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(0.8, 1.0, RelativeUnit.Relative),
            GradientStops =
            [
                new GradientStop(Color.Parse("#FF453A"), 0.0),
                new GradientStop(Color.Parse("#E02015"), 0.5),
                new GradientStop(Color.Parse("#B31008"), 1.0)
            ]
        };

        var strokePen = new Pen(new SolidColorBrush(Color.FromArgb(220, 255, 255, 255)), 1.2);
        context.DrawGeometry(pinGradient, strokePen, pinGeometry);

        // 3. Inner White Dot
        context.DrawEllipse(Brushes.White, null, new Point(centerX, headCenterY), 3.2, 3.2);
    }

    private void DrawScaleBar(DrawingContext context, Rect bounds, int zoom, bool isDarkMap)
    {
        var mPerPx = MapProjection.MetersPerPixel(Latitude, zoom);
        var targetPixels = 70.0;
        var roughMeters = mPerPx * targetPixels;

        // Nice round distance
        var (niceMeters, label) = RoundDistance(roughMeters);
        var actualPixels = niceMeters / mPerPx;
        if (actualPixels < 25 || actualPixels > 160) return;

        var startX = 14.0;
        var endX = startX + actualPixels;
        var bottomY = bounds.Height - 12.0;
        var tickH = 4.0;

        var lineColor = isDarkMap ? Color.FromArgb(220, 255, 255, 255) : Color.FromArgb(200, 20, 20, 20);
        var linePen = new Pen(new SolidColorBrush(lineColor), 1.6);

        // Draw bracket line
        context.DrawLine(linePen, new Point(startX, bottomY - tickH), new Point(startX, bottomY));
        context.DrawLine(linePen, new Point(startX, bottomY), new Point(endX, bottomY));
        context.DrawLine(linePen, new Point(endX, bottomY - tickH), new Point(endX, bottomY));

        // Draw scale label
        var typeface = new Typeface(FontFamily.Default, FontStyle.Normal, FontWeight.SemiBold);
        var formattedText = new FormattedText(
            label,
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            typeface,
            9.0,
            new SolidColorBrush(lineColor)
        );

        context.DrawText(formattedText, new Point(startX + 2, bottomY - 14));
    }

    private static (double Meters, string Label) RoundDistance(double roughMeters)
    {
        double[] niceSteps = [10, 20, 50, 100, 200, 500, 1000, 2000, 5000, 10000, 20000, 50000, 100000, 200000, 500000];
        double best = 1000;
        var minDiff = double.MaxValue;

        foreach (var step in niceSteps)
        {
            var diff = Math.Abs(step - roughMeters);
            if (diff < minDiff)
            {
                minDiff = diff;
                best = step;
            }
        }

        var label = best >= 1000 ? $"{(int)(best / 1000)} km" : $"{(int)best} m";
        return (best, label);
    }

    // ---------- Pointer / Touch / Wheel Interactions ----------

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);

        if (!AllowMapDrag) return;
        var point = e.GetCurrentPoint(this);

        if (point.Properties.IsLeftButtonPressed)
        {
            isDragging = true;
            dragStartPoint = point.Position;
            var (wx, wy) = MapProjection.LatLonToWorldPixels(Latitude, Longitude, Zoom);
            dragStartWorldX = wx;
            dragStartWorldY = wy;
            e.Pointer.Capture(this);
            e.Handled = true;
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);

        if (!isDragging || !AllowMapDrag) return;

        var currentPoint = e.GetPosition(this);
        var dx = currentPoint.X - dragStartPoint.X;
        var dy = currentPoint.Y - dragStartPoint.Y;

        var newWorldX = dragStartWorldX - dx;
        var newWorldY = dragStartWorldY - dy;

        // Prevent invalid or NaN coordinates from corrupting map state
        if (double.IsNaN(newWorldX) || double.IsNaN(newWorldY) ||
            double.IsInfinity(newWorldX) || double.IsInfinity(newWorldY))
        {
            return;
        }

        var (newLat, newLon) = MapProjection.WorldPixelsToLatLon(newWorldX, newWorldY, Zoom);
        if (double.IsNaN(newLat) || double.IsNaN(newLon) ||
            double.IsInfinity(newLat) || double.IsInfinity(newLon))
        {
            return;
        }

        Latitude = Math.Clamp(newLat, -85.0, 85.0);
        Longitude = ((newLon + 180.0) % 360.0 + 360.0) % 360.0 - 180.0;

        InvalidateVisual();
        CenterCoordinatesChanged?.Invoke(Latitude, Longitude);
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);

        if (isDragging)
        {
            isDragging = false;
            e.Pointer.Capture(null);
            e.Handled = true;
        }
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);

        var delta = e.Delta.Y;
        if (Math.Abs(delta) < 0.1) return;

        var newZoom = Math.Clamp(Zoom + (delta > 0 ? 1 : -1), MapModel.MinZoom, MapModel.MaxZoom);
        if (newZoom != Zoom)
        {
            Zoom = newZoom;
            ZoomChanged?.Invoke(Zoom);
            InvalidateVisual();
        }

        e.Handled = true;
    }
}
