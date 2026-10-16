using System.Diagnostics;
using Avalonia;
using Avalonia.Threading;
using Picture.Models;
using Picture.ViewModels;
using SkiaSharp;

namespace PictureMemoryChecks;

/// <summary>
/// Pins the picture widget's bitmap-ownership contract under the two patterns an added photo
/// folder actually produces:
/// <list type="number">
/// <item><b>A slideshow stepping through many files</b> — decoded pictures must stay bounded
/// (a couple of entries, not one per step / one per file);</item>
/// <item><b>Rapid switching whose fades overlap</b> (fast clicks, short intervals) — every
/// decoded picture that loses its slot must still be disposed deterministically. A picture that
/// is only dropped "to the GC" leaves its full-size pixel buffer resident until a finalizer
/// happens to run, which is exactly the "adding a folder keeps raising memory" report.</item>
/// </list>
/// It also pins that applying a <b>large item list</b> (adding a folder with hundreds of photos)
/// decodes only the picture on screen — never the whole list — and that suspension releases
/// every decoded picture (live count returns to zero).
/// </summary>
internal class Program
{
    private static int failures;

    /// <summary>Decoded pictures allowed to outlive their last owner (current + fading + cache).</summary>
    private const int LiveBudget = 4;

    [STAThread]
    static int Main()
    {
        AppBuilder.Configure<Application>().UsePlatformDetect().SetupWithoutStarting();

        var cts = new CancellationTokenSource();
        Dispatcher.UIThread.Post(async void () =>
        {
            try
            {
                await RunAsync();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"UNHANDLED EXCEPTION: {ex}");
                failures++;
            }
            finally
            {
                cts.Cancel();
            }
        });
        Dispatcher.UIThread.MainLoop(cts.Token);

        Console.WriteLine();
        Console.WriteLine(failures == 0
            ? "ALL PICTURE MEMORY CHECKS PASSED"
            : $"{failures} CHECK(S) FAILED");
        return failures == 0 ? 0 : 1;
    }

    private static void Check(bool condition, string label)
    {
        if (condition)
        {
            Console.WriteLine($"  [PASS] {label}");
        }
        else
        {
            Console.WriteLine($"  [FAIL] {label}");
            failures++;
        }
    }

    private static async Task RunAsync()
    {
        var dir = Path.Combine(Path.GetTempPath(), "picture-memory-checks-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);

        try
        {
            const int photoCount = 14;
            var paths = new List<string>(photoCount);
            for (var i = 0; i < photoCount; i++)
            {
                var path = Path.Combine(dir, $"photo{i:D3}.jpg");
                WriteJpeg(path, 1600, 1200, (byte)(30 + i * 10), 90, 200);
                paths.Add(path);
            }

            Console.WriteLine($"generated {photoCount} photos in {dir}");
            Console.WriteLine();

            var model = new PictureModel(
                Items: paths.Select(p => new PictureItem { Path = p, Name = Path.GetFileNameWithoutExtension(p) }).ToList(),
                Interval: SlideshowInterval.Manual,   // the check advances the slideshow itself
                Order: PlayOrder.Sequential);

            var vm = new PictureViewModel(model);

            // ---------------------------------------------------------------
            // 1. Sequential walk through the folder: bounded steady state
            // ---------------------------------------------------------------
            Console.WriteLine("--- sequential walk (the fade settles between steps) ---");
            for (var i = 0; i < photoCount - 1; i++)
            {
                vm.NextPicture();
                await Task.Delay(1000);  // longer than one full fade (~400-650 ms)
            }
            await Task.Delay(1200);      // dispose delay (250 ms) + margin

            Console.WriteLine($"  created={vm.PicturesCreated} disposed={vm.PicturesDisposed} live={vm.LivePictureCount}");
            Check(vm.PicturesCreated >= photoCount - 1, "the walk decoded every new picture (sanity)");
            Check(vm.LivePictureCount <= LiveBudget,
                $"after a full walk, at most {LiveBudget} pictures stay alive (was {vm.LivePictureCount})");

            // ---------------------------------------------------------------
            // 2. Rapid switching: overlapping fades must not strand pictures
            // ---------------------------------------------------------------
            Console.WriteLine("--- rapid switching (fades overlap) ---");
            for (var i = 0; i < 40; i++)
            {
                vm.NextPicture();
                await Task.Delay(120);   // well under the fade duration
            }
            await Task.Delay(1500);

            Console.WriteLine($"  created={vm.PicturesCreated} disposed={vm.PicturesDisposed} live={vm.LivePictureCount}");
            Check(vm.LivePictureCount <= LiveBudget,
                $"overlapping fades leave at most {LiveBudget} pictures alive (was {vm.LivePictureCount})");

            // ---------------------------------------------------------------
            // 3. Adding a folder: a large item list decodes only what is shown
            // ---------------------------------------------------------------
            Console.WriteLine("--- folder with 200 items (18 real + 182 unresolvable) ---");
            var folderItems = new List<PictureItem>();
            foreach (var p in paths)
            {
                folderItems.Add(new PictureItem { Path = p, Name = Path.GetFileNameWithoutExtension(p) });
            }
            for (var i = 0; i < 182; i++)
            {
                folderItems.Add(new PictureItem { Path = Path.Combine(dir, $"missing{i:D3}.jpg"), Name = $"missing{i:D3}" });
            }

            var createdBefore = vm.PicturesCreated;
            vm.ApplyModel(model with { Items = folderItems, CurrentIndex = 0 });
            await Task.Delay(600);

            var decodedForFolder = vm.PicturesCreated - createdBefore;
            Console.WriteLine($"  decoded while applying the folder: {decodedForFolder} (created={vm.PicturesCreated}, live={vm.LivePictureCount})");
            Check(decodedForFolder <= 2,
                $"applying a 200-item folder decodes at most 2 pictures (decoded {decodedForFolder})");
            Check(vm.LivePictureCount <= LiveBudget,
                $"a 200-item folder holds at most {LiveBudget} decoded pictures (live {vm.LivePictureCount})");

            // ---------------------------------------------------------------
            // 4. Suspension releases everything
            // ---------------------------------------------------------------
            Console.WriteLine("--- suspend releases every decoded picture ---");
            vm.Suspend();
            Check(vm.LivePictureCount == 0,
                $"suspension disposes every decoded picture (live {vm.LivePictureCount})");
            Check(!vm.IsSlideshowRunning && !vm.IsAnimationRunning,
                "suspension stops the slideshow and animation timers");

            vm.Dispose();
            Check(vm.LivePictureCount == 0, "disposal disposes every decoded picture");
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    private static void WriteJpeg(string path, int width, int height, byte r, byte g, byte b)
    {
        using var bitmap = new SKBitmap(new SKImageInfo(width, height));
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(new SKColor(r, g, b));
            using var paint = new SKPaint { Color = new SKColor((byte)(255 - r), (byte)(255 - g), 40), IsAntialias = true };
            canvas.DrawCircle(width / 2f, height / 2f, Math.Min(width, height) / 3f, paint);
        }
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, 85);
        using var stream = File.Create(path);
        data.SaveTo(stream);
    }
}
