using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Picture.Models;

namespace Picture.Controls;

public class PictureCanvas : Control
{
    public static readonly StyledProperty<Bitmap?> CurrentBitmapProperty =
        AvaloniaProperty.Register<PictureCanvas, Bitmap?>(nameof(CurrentBitmap));

    public static readonly StyledProperty<Bitmap?> PreviousBitmapProperty =
        AvaloniaProperty.Register<PictureCanvas, Bitmap?>(nameof(PreviousBitmap));

    public static readonly StyledProperty<double> TransitionProgressProperty =
        AvaloniaProperty.Register<PictureCanvas, double>(nameof(TransitionProgress), 1.0);

    public static readonly StyledProperty<double> CropXProperty =
        AvaloniaProperty.Register<PictureCanvas, double>(nameof(CropX), 0.5);

    public static readonly StyledProperty<double> CropYProperty =
        AvaloniaProperty.Register<PictureCanvas, double>(nameof(CropY), 0.5);

    public static readonly StyledProperty<double> ZoomProperty =
        AvaloniaProperty.Register<PictureCanvas, double>(nameof(Zoom), 1.0);

    public static readonly StyledProperty<PictureFitMode> FitModeProperty =
        AvaloniaProperty.Register<PictureCanvas, PictureFitMode>(nameof(FitMode), PictureFitMode.CustomCrop);

    public static readonly StyledProperty<CornerRadius> CornerRadiusProperty =
        AvaloniaProperty.Register<PictureCanvas, CornerRadius>(nameof(CornerRadius), new CornerRadius(0));

    public Bitmap? CurrentBitmap
    {
        get => GetValue(CurrentBitmapProperty);
        set => SetValue(CurrentBitmapProperty, value);
    }

    public Bitmap? PreviousBitmap
    {
        get => GetValue(PreviousBitmapProperty);
        set => SetValue(PreviousBitmapProperty, value);
    }

    public double TransitionProgress
    {
        get => GetValue(TransitionProgressProperty);
        set => SetValue(TransitionProgressProperty, value);
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

    public CornerRadius CornerRadius
    {
        get => GetValue(CornerRadiusProperty);
        set => SetValue(CornerRadiusProperty, value);
    }

    static PictureCanvas()
    {
        AffectsRender<PictureCanvas>(
            CurrentBitmapProperty,
            PreviousBitmapProperty,
            TransitionProgressProperty,
            CropXProperty,
            CropYProperty,
            ZoomProperty,
            FitModeProperty,
            CornerRadiusProperty);
    }

    public PictureCanvas()
    {
        ClipToBounds = true;
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        var bounds = Bounds;
        if (bounds.Width <= 0 || bounds.Height <= 0) return;

        bool hasRadius = CornerRadius.TopLeft > 0 || CornerRadius.TopRight > 0
                         || CornerRadius.BottomLeft > 0 || CornerRadius.BottomRight > 0;

        IDisposable? clipDisposable = hasRadius
            ? context.PushClip(new RoundedRect(new Rect(0, 0, bounds.Width, bounds.Height), CornerRadius))
            : null;

        try
        {
            var progress = Math.Clamp(TransitionProgress, 0.0, 1.0);

            if (PreviousBitmap != null && progress < 1.0)
            {
                using (context.PushOpacity(1.0 - progress))
                {
                    DrawImage(context, PreviousBitmap, bounds);
                }
            }

            if (CurrentBitmap != null)
            {
                using (context.PushOpacity(progress))
                {
                    DrawImage(context, CurrentBitmap, bounds);
                }
            }
        }
        finally
        {
            clipDisposable?.Dispose();
        }
    }

    private void DrawImage(DrawingContext context, Bitmap bitmap, Rect destBounds)
    {
        if (bitmap == null || destBounds.Width <= 0 || destBounds.Height <= 0) return;

        // A disposed (or not yet realised) Bitmap reports an empty Size. Drawing it would
        // corrupt the render pass, which Avalonia cannot recover from, so bail out first.
        // The owning view model guarantees slots are cleared before disposal; this is the
        // last line of defence against a stale reference reaching the compositor.
        var imgSize = bitmap.Size;
        if (imgSize.Width <= 0 || imgSize.Height <= 0) return;
        var imgWidth = imgSize.Width;
        var imgHeight = imgSize.Height;

        double targetAspect = destBounds.Width / destBounds.Height;
        double imgAspect = imgWidth / imgHeight;

        switch (FitMode)
        {
            case PictureFitMode.Fit:
            {
                // 1. Draw a dimmed ambient fill in the background so the widget card is never empty
                Rect bgSrc;
                if (targetAspect > imgAspect)
                {
                    double srcH = imgWidth / targetAspect;
                    double srcY = Math.Max(0, (imgHeight - srcH) / 2.0);
                    bgSrc = new Rect(0, srcY, imgWidth, Math.Min(srcH, imgHeight));
                }
                else
                {
                    double srcW = imgHeight * targetAspect;
                    double srcX = Math.Max(0, (imgWidth - srcW) / 2.0);
                    bgSrc = new Rect(srcX, 0, Math.Min(srcW, imgWidth), imgHeight);
                }

                using (context.PushOpacity(0.2))
                {
                    context.DrawImage(bitmap, bgSrc, destBounds);
                }

                // 2. Draw the centered sharp image
                double scale = Math.Min(destBounds.Width / imgWidth, destBounds.Height / imgHeight);
                double w = imgWidth * scale;
                double h = imgHeight * scale;
                double x = (destBounds.Width - w) / 2.0;
                double y = (destBounds.Height - h) / 2.0;
                context.DrawImage(bitmap, new Rect(0, 0, imgWidth, imgHeight), new Rect(x, y, w, h));
                break;
            }

            case PictureFitMode.Fill:
            {
                // Center UniformToFill
                Rect srcRect;
                if (targetAspect > imgAspect)
                {
                    double srcH = imgWidth / targetAspect;
                    double srcY = Math.Max(0, (imgHeight - srcH) / 2.0);
                    srcRect = new Rect(0, srcY, imgWidth, Math.Min(srcH, imgHeight));
                }
                else
                {
                    double srcW = imgHeight * targetAspect;
                    double srcX = Math.Max(0, (imgWidth - srcW) / 2.0);
                    srcRect = new Rect(srcX, 0, Math.Min(srcW, imgWidth), imgHeight);
                }
                context.DrawImage(bitmap, srcRect, destBounds);
                break;
            }

            case PictureFitMode.CustomCrop:
            default:
            {
                double effectiveZoom = Math.Clamp(Zoom, 1.0, 3.0);

                double baseW, baseH;
                if (targetAspect > imgAspect)
                {
                    baseW = imgWidth;
                    baseH = imgWidth / targetAspect;
                }
                else
                {
                    baseH = imgHeight;
                    baseW = imgHeight * targetAspect;
                }

                double cropW = baseW / effectiveZoom;
                double cropH = baseH / effectiveZoom;

                if (cropW > imgWidth) cropW = imgWidth;
                if (cropH > imgHeight) cropH = imgHeight;

                double maxOffsetX = Math.Max(0, imgWidth - cropW);
                double maxOffsetY = Math.Max(0, imgHeight - cropH);

                double normX = Math.Clamp(CropX, 0.0, 1.0);
                double normY = Math.Clamp(CropY, 0.0, 1.0);

                double cropX = normX * maxOffsetX;
                double cropY = normY * maxOffsetY;

                var srcRect = new Rect(cropX, cropY, cropW, cropH);
                context.DrawImage(bitmap, srcRect, destBounds);
                break;
            }
        }
    }
}
