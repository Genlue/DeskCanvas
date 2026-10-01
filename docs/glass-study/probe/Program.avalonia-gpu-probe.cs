using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using Avalonia.Threading;
using SkiaSharp;
using probe;

internal static class Log
{
    private static readonly string File = Path.Combine(AppContext.BaseDirectory, "probe-log.txt");
    public static void W(string message)
    {
        try { System.IO.File.AppendAllText(File, message + Environment.NewLine); } catch { }
        Console.WriteLine(message);
    }
    public static void Reset() { try { System.IO.File.Delete(File); } catch { } }
}

internal sealed class GlassProbeControl : Control
{
    public override void Render(DrawingContext context) => context.Custom(new GlassDrawOp(new Rect(Bounds.Size)));
}

internal sealed class GlassDrawOp : ICustomDrawOperation
{
    private static bool drawn;

    public GlassDrawOp(Rect bounds) => Bounds = bounds;
    public Rect Bounds { get; }
    public void Dispose() { }
    public bool Equals(ICustomDrawOperation other) => false;
    public bool HitTest(Point p) => false;

    private const int Size = 420;
    private const float Radius = 32f;

    public void Render(ImmediateDrawingContext context)
    {
        var feature = context.TryGetFeature<ISkiaSharpApiLeaseFeature>();
        if (feature is null)
        {
            Log.W("ISkiaSharpApiLeaseFeature: NULL (no Skia lease)");
            return;
        }
        using var lease = feature.Lease();
        if (drawn) return;
        drawn = true;

        var gpu = lease.GrContext is not null;
        Log.W($"lease ok | GRContext={(gpu ? "present -> GPU" : "NULL -> CPU raster")} | " +
              $"SkiaSharp={typeof(SKCanvas).Assembly.GetName().Version} | Avalonia={typeof(Application).Assembly.GetName().Version}");

        try
        {
            using var backdrop = Backdrop.Build();
            using var backdropImage = SKImage.FromBitmap(backdrop);

            var effect = SKRuntimeEffect.Create(Shader.Sksl, out var errors);
            Log.W("SKSL COMPILE (AGSL port): " + (effect is null ? "FAILED " + errors : "OK"));
            if (effect is null) return;

            // Same 420x420 crop as the DeskCanvas reference render.
            using var content = SKShader.CreateImage(backdropImage, SKShaderTileMode.Clamp, SKShaderTileMode.Clamp,
                SKMatrix.CreateTranslation(-Backdrop.CardX, -Backdrop.CardY));

            SKShader Build(float refractionHeight, float refractionAmount, float dispersion, float blur,
                float contrast, float whitePoint, float chroma, float tintAlpha)
            {
                var uniforms = new SKRuntimeEffectUniforms(effect);
                uniforms["size"] = new[] { (float)Size, (float)Size };
                uniforms["offset"] = new[] { 0f, 0f };
                uniforms["cornerRadii"] = new[] { Radius, Radius, Radius, Radius };
                uniforms["refractionHeight"] = refractionHeight;
                uniforms["refractionAmount"] = refractionAmount;
                uniforms["depthEffect"] = 0.3f;
                uniforms["chromaticAberration"] = dispersion;
                uniforms["contrast"] = contrast;
                uniforms["whitePoint"] = whitePoint;
                uniforms["chromaMultiplier"] = chroma;
                uniforms["tintColor"] = new[] { 1f, 1f, 1f };
                uniforms["tintAlpha"] = tintAlpha;
                var children = new SKRuntimeEffectChildren(effect);
                children["content"] = content;
                return effect.ToShader(false, uniforms, children);
            }

            SKBitmap RenderToBitmap(SKShader shader, float blur, bool encode)
            {
                var info = new SKImageInfo(Size, Size);
                using var surface = gpu ? SKSurface.Create(lease.GrContext, false, info) : SKSurface.Create(info);
                surface.Canvas.Clear(SKColors.Transparent);
                using (var paint = new SKPaint { Shader = shader, IsAntialias = true })
                {
                    if (blur > 0.01f) paint.ImageFilter = SKImageFilter.CreateBlur(blur, blur, SKShaderTileMode.Clamp);
                    surface.Canvas.DrawRoundRect(new SKRect(0, 0, Size, Size), Radius, Radius, paint);
                }
                surface.Canvas.Flush();
                if (encode)
                {
                    using var snapshot = surface.Snapshot();
                    using var data = snapshot.Encode(SKEncodedImageFormat.Png, 100);
                    return SKBitmap.Decode(data.ToArray());
                }
                var result = new SKBitmap(info);
                surface.ReadPixels(info, result.GetPixels(), info.RowBytes, 0, 0);
                return result;
            }

            double TimeMs(Func<SKBitmap> action, int iterations)
            {
                action().Dispose();
                var best = double.MaxValue;
                for (var i = 0; i < iterations; i++)
                {
                    var watch = Stopwatch.StartNew();
                    action().Dispose();
                    watch.Stop();
                    best = Math.Min(best, watch.Elapsed.TotalMilliseconds);
                }
                return best;
            }

            // --- the live window canvas (what the user sees) ---
            using (var liveShader = Build(40f, -140f, 0.5f, 0f, 0f, 0f, 1f, 0f))
            {
                Log.W("drawing on the lease canvas (" + (gpu ? "GPU" : "CPU") + ") ...");
                lease.SkCanvas.Save();
                using (var paint = new SKPaint { Shader = liveShader, IsAntialias = true })
                    lease.SkCanvas.DrawRoundRect(new SKRect(20, 20, 400, 400), Radius, Radius, paint);
                lease.SkCanvas.Restore();
                Log.W("DRAW ON LEASE CANVAS: OK");
            }

            // --- Android library defaults, encoded exactly like a cached material ---
            using (var shader = Build(40f, -140f, 0.5f, 0f, 0f, 0f, 1f, 0f))
            using (var bmp = RenderToBitmap(shader, 0f, false))
                Backdrop.Save(bmp, Path.Combine(AppContext.BaseDirectory, "gpu-glass.png"));

            // --- tuned variant (blur + slight tint + stronger chroma, like iOS) ---
            using (var shader = Build(48f, -120f, 0.5f, 9f, 0.05f, 0.06f, 1.15f, 0.06f))
            using (var bmp = RenderToBitmap(shader, 9f, false))
                Backdrop.Save(bmp, Path.Combine(AppContext.BaseDirectory, "gpu-glass-tuned.png"));

            // --- timings ---
            using var defaultShader = Build(40f, -140f, 0.5f, 0f, 0f, 0f, 1f, 0f);
            using var tunedShader = Build(48f, -120f, 0.5f, 9f, 0.05f, 0.06f, 1.15f, 0.06f);
            Log.W($"TIMING Android AGSL->SkSL on {(gpu ? "GPU (ANGLE/D3D11)" : "CPU raster")}, best of N:");
            Log.W($"  420x420 shader draw only        : {TimeMs(() => RenderToBitmap(defaultShader, 0f, false), 30):F2} ms");
            Log.W($"  420x420 draw + PNG encode       : {TimeMs(() => RenderToBitmap(defaultShader, 0f, true), 15):F2} ms");
            Log.W($"  420x420 tuned (blur 9 + tint)   : {TimeMs(() => RenderToBitmap(tunedShader, 9f, false), 30):F2} ms");
        }
        catch (Exception ex)
        {
            Log.W("EXCEPTION: " + ex);
        }
    }
}

internal sealed class ProbeApp : Application
{
    public override void OnFrameworkInitializationCompleted()
    {
        var window = new Window
        {
            Width = 460,
            Height = 460,
            Title = "Avalonia Skia runtime-effect probe",
            Background = Brushes.Magenta,
            SystemDecorations = SystemDecorations.None,
            Topmost = true,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            Content = new GlassProbeControl()
        };
        window.Show();
        DispatcherTimer.RunOnce(() =>
        {
            Log.W("closing window");
            window.Close();
        }, TimeSpan.FromSeconds(4));
        base.OnFrameworkInitializationCompleted();
    }
}

internal static class Entry
{
    [STAThread]
    public static void Main(string[] args)
    {
        Log.Reset();
        var software = args.Contains("software");
        Log.W($"=== Avalonia runtime-effect probe ({(software ? "SOFTWARE" : "GPU/AngleEgl")}) ===");
        try
        {
            BuildAvaloniaApp(software).StartWithClassicDesktopLifetime(args);
            Log.W("app exited normally");
        }
        catch (Exception ex)
        {
            Log.W("FATAL: " + ex);
        }
    }

    private static AppBuilder BuildAvaloniaApp(bool software) =>
        AppBuilder.Configure<ProbeApp>()
            .UsePlatformDetect()
            .With(new Win32PlatformOptions
            {
                RenderingMode = software
                    ? new[] { Win32RenderingMode.Software }
                    : new[] { Win32RenderingMode.AngleEgl }
            })
            .LogToTrace();
}
