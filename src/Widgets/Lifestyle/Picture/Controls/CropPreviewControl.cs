using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Picture.Models;

namespace Picture.Controls;

public class CropPreviewControl : Control
{
    public static readonly StyledProperty<Bitmap?> PreviewBitmapProperty =
        AvaloniaProperty.Register<CropPreviewControl, Bitmap?>(nameof(PreviewBitmap));

    public static readonly StyledProperty<double> CropXProperty =
        AvaloniaProperty.Register<CropPreviewControl, double>(nameof(CropX), 0.5);

    public static readonly StyledProperty<double> CropYProperty =
        AvaloniaProperty.Register<CropPreviewControl, double>(nameof(CropY), 0.5);

    public static readonly StyledProperty<double> ZoomProperty =
        AvaloniaProperty.Register<CropPreviewControl, double>(nameof(Zoom), 1.0);

    public static readonly StyledProperty<PictureFitMode> FitModeProperty =
        AvaloniaProperty.Register<CropPreviewControl, PictureFitMode>(nameof(FitMode), PictureFitMode.CustomCrop);

    public static readonly StyledProperty<double> WidgetAspectProperty =
        AvaloniaProperty.Register<CropPreviewControl, double>(nameof(WidgetAspect), 1.0);

    public Bitmap? PreviewBitmap
    {
        get => GetValue(PreviewBitmapProperty);
        set => SetValue(PreviewBitmapProperty, value);
    }

    public double CropX
    {
        get => GetValue(CropXProperty);
        set => SetValue(CropXProperty, value);
    }

    public double CropY
    {
        get => GetValue(CropYProperty);
        set => SetValue(CropYProperty, value);
    }

    public double Zoom
    {
        get => GetValue(ZoomProperty);
        set => SetValue(ZoomProperty, value);
    }

    public PictureFitMode FitMode
    {
        get => GetValue(FitModeProperty);
        set => SetValue(FitModeProperty, value);
    }

    public double WidgetAspect
    {
        get => GetValue(WidgetAspectProperty);
        set => SetValue(WidgetAspectProperty, value);
    }

    /// <summary>
    /// Invoked when CropX, CropY, or Zoom is adjusted directly via mouse dragging or mouse wheel scrolling.
    /// </summary>
    public event Action<double, double, double>? CropChanged;

    private bool _isDragging;
    private Point _dragStartPoint;
    private double _startCropX;
    private double _startCropY;
    private Rect _viewfinderRect;

    static CropPreviewControl()
    {
        AffectsRender<CropPreviewControl>(
            PreviewBitmapProperty,
            CropXProperty,
            CropYProperty,
            ZoomProperty,
            FitModeProperty,
            WidgetAspectProperty);
    }

    public CropPreviewControl()
    {
        ClipToBounds = true;
        Cursor = new Cursor(StandardCursorType.SizeAll);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (FitMode != PictureFitMode.CustomCrop || PreviewBitmap == null) return;

        var pos = e.GetPosition(this);
        if (_viewfinderRect.Contains(pos))
        {
            _isDragging = true;
            _dragStartPoint = pos;
            _startCropX = Math.Clamp(CropX, 0.0, 1.0);
            _startCropY = Math.Clamp(CropY, 0.0, 1.0);
            e.Pointer.Capture(this);
            e.Handled = true;
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (!_isDragging || PreviewBitmap == null) return;

        var currentPos = e.GetPosition(this);
        double deltaX = currentPos.X - _dragStartPoint.X;
        double deltaY = currentPos.Y - _dragStartPoint.Y;

        var imgSize = PreviewBitmap.Size;
        if (imgSize.Width <= 0 || imgSize.Height <= 0) return; // disposed / not yet realised

        double aspect = WidgetAspect > 0.05 ? WidgetAspect : 1.0;
        double imgAspect = imgSize.Width / imgSize.Height;
        double effectiveZoom = Math.Clamp(Zoom, 1.0, 3.0);

        double baseW, baseH;
        if (aspect > imgAspect)
        {
            baseW = imgSize.Width;
            baseH = imgSize.Width / aspect;
        }
        else
        {
            baseH = imgSize.Height;
            baseW = imgSize.Height * aspect;
        }

        double cropW = baseW / effectiveZoom;
        double cropH = baseH / effectiveZoom;
        if (cropW > imgSize.Width) cropW = imgSize.Width;
        if (cropH > imgSize.Height) cropH = imgSize.Height;

        double maxOffsetX = Math.Max(0, imgSize.Width - cropW);
        double maxOffsetY = Math.Max(0, imgSize.Height - cropH);

        double newNormX = _startCropX;
        double newNormY = _startCropY;

        if (maxOffsetX > 0.001 && _viewfinderRect.Width > 0.001)
        {
            // Moving mouse to the right reveals left area, so cropX decreases
            double dCropImgX = -deltaX * (cropW / _viewfinderRect.Width);
            newNormX = _startCropX + (dCropImgX / maxOffsetX);
        }

        if (maxOffsetY > 0.001 && _viewfinderRect.Height > 0.001)
        {
            // Moving mouse down reveals top area, so cropY decreases
            double dCropImgY = -deltaY * (cropH / _viewfinderRect.Height);
            newNormY = _startCropY + (dCropImgY / maxOffsetY);
        }

        CropX = Math.Clamp(newNormX, 0.0, 1.0);
        CropY = Math.Clamp(newNormY, 0.0, 1.0);

        CropChanged?.Invoke(CropX, CropY, Zoom);
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_isDragging)
        {
            _isDragging = false;
            e.Pointer.Capture(null);
            e.Handled = true;
        }
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        _isDragging = false;
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (FitMode != PictureFitMode.CustomCrop || PreviewBitmap == null) return;

        double step = e.Delta.Y > 0 ? 0.05 : -0.05;
        double newZoom = Math.Clamp(Math.Round((Zoom + step) * 100) / 100.0, 1.0, 3.0);
        if (Math.Abs(newZoom - Zoom) > 0.001)
        {
            Zoom = newZoom;
            CropChanged?.Invoke(CropX, CropY, Zoom);
            e.Handled = true;
        }
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        var bounds = Bounds;
        if (bounds.Width <= 0 || bounds.Height <= 0) return;

        // 1. Dark container background
        context.DrawRectangle(new SolidColorBrush(Color.FromArgb(40, 0, 0, 0)), null, new Rect(0, 0, bounds.Width, bounds.Height), 8, 8);

        var bitmap = PreviewBitmap;
        if (bitmap == null)
        {
            var text = new FormattedText(
                Locales.Locale.Picture_SelectImageHint,
                System.Globalization.CultureInfo.CurrentUICulture,
                FlowDirection.LeftToRight,
                new Typeface(FontFamily.Default, FontStyle.Normal, FontWeight.Normal),
                12,
                new SolidColorBrush(Color.FromArgb(120, 255, 255, 255)));

            context.DrawText(text, new Point((bounds.Width - text.Width) / 2.0, (bounds.Height - text.Height) / 2.0));
            return;
        }

        var imgSize = bitmap.Size;
        if (imgSize.Width <= 0 || imgSize.Height <= 0) return; // disposed / not yet realised

        // 2. Compute viewfinder rect inside bounds preserving WidgetAspect
        double padding = 8.0;
        double maxW = Math.Max(10, bounds.Width - padding * 2);
        double maxH = Math.Max(10, bounds.Height - padding * 2);
        double aspect = WidgetAspect > 0.05 ? WidgetAspect : 1.0;

        double viewW, viewH;
        if (maxW / maxH > aspect)
        {
            viewH = maxH;
            viewW = viewH * aspect;
        }
        else
        {
            viewW = maxW;
            viewH = viewW / aspect;
        }

        double viewX = (bounds.Width - viewW) / 2.0;
        double viewY = (bounds.Height - viewH) / 2.0;
        _viewfinderRect = new Rect(viewX, viewY, viewW, viewH);

        // 3. Draw viewfinder content clipped to rounded rect
        using (context.PushClip(new RoundedRect(_viewfinderRect, new CornerRadius(6))))
        {
            // A. Checkerboard background for transparent PNG
            DrawCheckerboard(context, _viewfinderRect, 10);

            // B. Target aspect is EXACTLY the widget's aspect ratio!
            double targetAspect = aspect;
            double imgAspect = imgSize.Width / imgSize.Height;

            switch (FitMode)
            {
                case PictureFitMode.Fit:
                {
                    Rect bgSrc;
                    if (targetAspect > imgAspect)
                    {
                        double srcH = imgSize.Width / targetAspect;
                        double srcY = Math.Max(0, (imgSize.Height - srcH) / 2.0);
                        bgSrc = new Rect(0, srcY, imgSize.Width, Math.Min(srcH, imgSize.Height));
                    }
                    else
                    {
                        double srcW = imgSize.Height * targetAspect;
                        double srcX = Math.Max(0, (imgSize.Width - srcW) / 2.0);
                        bgSrc = new Rect(srcX, 0, Math.Min(srcW, imgSize.Width), imgSize.Height);
                    }

                    using (context.PushOpacity(0.2))
                    {
                        context.DrawImage(bitmap, bgSrc, _viewfinderRect);
                    }

                    double scale = Math.Min(_viewfinderRect.Width / imgSize.Width, _viewfinderRect.Height / imgSize.Height);
                    double w = imgSize.Width * scale;
                    double h = imgSize.Height * scale;
                    double x = _viewfinderRect.X + (_viewfinderRect.Width - w) / 2.0;
                    double y = _viewfinderRect.Y + (_viewfinderRect.Height - h) / 2.0;
                    context.DrawImage(bitmap, new Rect(0, 0, imgSize.Width, imgSize.Height), new Rect(x, y, w, h));
                    break;
                }
                case PictureFitMode.Fill:
                {
                    Rect srcRect;
                    if (targetAspect > imgAspect)
                    {
                        double srcH = imgSize.Width / targetAspect;
                        double srcY = Math.Max(0, (imgSize.Height - srcH) / 2.0);
                        srcRect = new Rect(0, srcY, imgSize.Width, Math.Min(srcH, imgSize.Height));
                    }
                    else
                    {
                        double srcW = imgSize.Height * targetAspect;
                        double srcX = Math.Max(0, (imgSize.Width - srcW) / 2.0);
                        srcRect = new Rect(srcX, 0, Math.Min(srcW, imgSize.Width), imgSize.Height);
                    }
                    context.DrawImage(bitmap, srcRect, _viewfinderRect);
                    break;
                }
                case PictureFitMode.CustomCrop:
                default:
                {
                    double effectiveZoom = Math.Clamp(Zoom, 1.0, 3.0);
                    double baseW, baseH;
                    if (targetAspect > imgAspect)
                    {
                        baseW = imgSize.Width;
                        baseH = imgSize.Width / targetAspect;
                    }
                    else
                    {
                        baseH = imgSize.Height;
                        baseW = imgSize.Height * targetAspect;
                    }

                    double cropW = baseW / effectiveZoom;
                    double cropH = baseH / effectiveZoom;
                    if (cropW > imgSize.Width) cropW = imgSize.Width;
                    if (cropH > imgSize.Height) cropH = imgSize.Height;

                    double maxOffsetX = Math.Max(0, imgSize.Width - cropW);
                    double maxOffsetY = Math.Max(0, imgSize.Height - cropH);

                    double normX = Math.Clamp(CropX, 0.0, 1.0);
                    double normY = Math.Clamp(CropY, 0.0, 1.0);

                    double cropX = normX * maxOffsetX;
                    double cropY = normY * maxOffsetY;

                    var srcRect = new Rect(cropX, cropY, cropW, cropH);
                    context.DrawImage(bitmap, srcRect, _viewfinderRect);
                    break;
                }
            }

            // C. Rule of thirds grid (subtle)
            if (FitMode == PictureFitMode.CustomCrop)
            {
                var gridPen = new Pen(new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)), 1);
                double vx = _viewfinderRect.X;
                double vy = _viewfinderRect.Y;
                double vw = _viewfinderRect.Width;
                double vh = _viewfinderRect.Height;

                context.DrawLine(gridPen, new Point(vx + vw / 3.0, vy), new Point(vx + vw / 3.0, vy + vh));
                context.DrawLine(gridPen, new Point(vx + vw * 2.0 / 3.0, vy), new Point(vx + vw * 2.0 / 3.0, vy + vh));
                context.DrawLine(gridPen, new Point(vx, vy + vh / 3.0), new Point(vx + vw, vy + vh / 3.0));
                context.DrawLine(gridPen, new Point(vx, vy + vh * 2.0 / 3.0), new Point(vx + vw, vy + vh * 2.0 / 3.0));
            }
        }

        // 4. Viewfinder highlight border
        var borderPen = new Pen(new SolidColorBrush(Color.FromArgb(160, 255, 255, 255)), 1.5);
        context.DrawRectangle(null, borderPen, _viewfinderRect, 6, 6);
    }

    private static void DrawCheckerboard(DrawingContext context, Rect rect, double cellSize = 10)
    {
        var darkBrush = new SolidColorBrush(Color.FromArgb(18, 255, 255, 255));
        var lightBrush = new SolidColorBrush(Color.FromArgb(32, 255, 255, 255));

        context.DrawRectangle(darkBrush, null, rect);

        int cols = (int)Math.Ceiling(rect.Width / cellSize);
        int rows = (int)Math.Ceiling(rect.Height / cellSize);

        for (int r = 0; r < rows; r++)
        {
            for (int c = 0; c < cols; c++)
            {
                if ((r + c) % 2 == 0)
                {
                    double x = rect.X + c * cellSize;
                    double y = rect.Y + r * cellSize;
                    double w = Math.Min(cellSize, rect.Right - x);
                    double h = Math.Min(cellSize, rect.Bottom - y);
                    if (w > 0 && h > 0)
                    {
                        context.DrawRectangle(lightBrush, null, new Rect(x, y, w, h));
                    }
                }
            }
        }
    }
}
