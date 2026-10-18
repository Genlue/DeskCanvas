using System.Text.Json;
using Avalonia;
using Avalonia.Threading;
using DeskCanvas.Core;
using DeskCanvas.Services;
using Hitokoto.Models;
using Hitokoto.Services;

namespace HitokotoMemoryChecks;

/// <summary>
/// Pins the 一言 (Hitokoto) widget's memory contracts:
/// <list type="number">
/// <item><b>The store is index-backed and bounded</b> — a hand-grown <c>hitokoto.json</c> is
/// trimmed to the favorites/history caps on load, likes and bookmarks are answered without
/// walking the lists, and a burst of likes/bookmarks rewrites the file once (debounced) instead
/// of once per action.</item>
/// <item><b>The desktop capture is handed back when idle</b> — with live sampling off, the
/// full-desktop capture (the single largest allocation in the process) is released once no render
/// has demanded a frame within the grace period, and the next demand re-captures on demand.</item>
/// </list>
/// The store checks run against an isolated portable data folder next to this check's own
/// executable, so the real widget data is never touched.
/// </summary>
internal class Program
{
    private static int failures;

    [STAThread]
    static int Main()
    {
        // Portable data folder: Const.DataFolder resolves to the exe folder when a Widgets
        // directory sits next to it. Must exist before Const is first touched.
        Directory.CreateDirectory(Path.Combine(Path.GetDirectoryName(Environment.ProcessPath)!, "Widgets"));

        // Shorten the idle-release grace for the check. The value is read when
        // LiquidGlassWallpaper loads, so set it before the type is first touched.
        Environment.SetEnvironmentVariable("DESKCANVAS_GLASS_IDLE_RELEASE_MS", "1200");

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
            ? "ALL HITOKOTO MEMORY CHECKS PASSED"
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

    private static HitokotoItem Item(int id, string uuid) =>
        new(Id: id, Uuid: uuid, Hitokoto: $"quote-{uuid}", Type: "a", From: "checks", FromWho: "checks");

    private static async Task RunAsync()
    {
        Console.WriteLine($"data folder: {Const.DataFolder}");
        // The check-harness sandbox folder is created lazily, but the store file below is written
        // first: create it here so the run does not depend on another component having done it.
        Directory.CreateDirectory(Const.DataFolder);
        await CheckStoreAsync();
        await CheckIdleReleaseAsync();
    }

    // ---- store: caps, indexes, debounced writes -------------------------------------------------

    private static async Task CheckStoreAsync()
    {
        Console.WriteLine();
        Console.WriteLine("store: caps / indexes / debounced writes");

        var dataFile = Path.Combine(Const.DataFolder, "hitokoto.json");

        // A legal but over-grown file: favorites and history beyond both caps, plus liked uuids.
        var favorites = Enumerable.Range(0, HitokotoStore.MaxFavoriteCount + 100)
            .Select(i => Item(i, $"fav-{i}")).ToList();
        var history = Enumerable.Range(0, HitokotoStore.MaxHistoryCount + 10)
            .Select(i => Item(100_000 + i, $"hist-{i}")).ToList();
        var liked = Enumerable.Range(0, 5).Select(i => $"liked-{i}").ToList();
        File.WriteAllText(dataFile, JsonSerializer.Serialize(new
        {
            Current = Item(1, "current-1"),
            Favorites = favorites,
            History = history,
            LikedUuids = liked
        }, new JsonSerializerOptions { WriteIndented = true }));

        var store = HitokotoStore.Instance;

        Check(store.GetFavorites().Count == HitokotoStore.MaxFavoriteCount,
            $"favorites are capped at {HitokotoStore.MaxFavoriteCount} on load (got {store.GetFavorites().Count})");
        Check(store.GetHistory().Count == HitokotoStore.MaxHistoryCount,
            $"history is capped at {HitokotoStore.MaxHistoryCount} on load (got {store.GetHistory().Count})");
        Check(store.IsFavorite("fav-0"), "the newest favorite survives the trim");
        Check(!store.IsFavorite($"fav-{HitokotoStore.MaxFavoriteCount + 99}"), "the oldest favorite is dropped by the trim");
        Check(store.IsLiked("liked-2"), "a liked uuid from the file is answered as liked");
        Check(!store.IsLiked("never-liked"), "an unknown uuid is not liked");

        // A new bookmark holds the cap and lands on top.
        Check(store.ToggleFavorite(Item(50_000, "added-1")), "toggling adds the bookmark");
        Check(store.IsFavorite("added-1"), "the new bookmark is answered from the index");
        Check(store.GetFavorites().Count == HitokotoStore.MaxFavoriteCount, "the cap still holds after adding");
        Check(store.GetFavorites()[0].Uuid == "added-1", "the newest bookmark sits at the top");

        // Debounce: the rewrite waits out the window instead of firing per action.
        await Task.Delay(50);
        Check(!File.ReadAllText(dataFile).Contains("added-1", StringComparison.Ordinal),
            "the file is not rewritten immediately (writes are debounced)");
        Check(await WaitForAsync(() => File.ReadAllText(dataFile).Contains("added-1", StringComparison.Ordinal),
                TimeSpan.FromSeconds(5)),
            "the debounced write lands after the window");

        // Likes: idempotent, and persisted by the same debounced write.
        var likedItem = Item(60_000, "liked-new");
        var marked = store.MarkLiked(likedItem);
        Check(marked.IsLiked && marked.Likes == likedItem.Likes + 1, "marking a like bumps the count once");
        Check(store.IsLiked("liked-new"), "the new like is answered from the index");
        Check(store.MarkLiked(marked).Likes == marked.Likes, "marking the same like again does not bump the count");
        Check(await WaitForAsync(() => File.ReadAllText(dataFile).Contains("liked-new", StringComparison.Ordinal),
                TimeSpan.FromSeconds(5)),
            "likes are persisted by the debounced write");

        // Recording quotes keeps the history window bounded, newest first.
        for (var i = 0; i < HitokotoStore.MaxHistoryCount + 10; i++)
            store.RecordQuote(Item(70_000 + i, $"new-{i}"));
        Check(store.GetHistory().Count == HitokotoStore.MaxHistoryCount, "recording keeps the history capped");
        Check(store.GetHistory()[0].Uuid == $"new-{HitokotoStore.MaxHistoryCount + 9}", "the newest quote leads the history");

        store.ClearHistory();
        Check(store.GetHistory().Count == 0, "clearing history empties it");
        Check(await WaitForAsync(() => !File.ReadAllText(dataFile).Contains("\"hist-", StringComparison.Ordinal),
                TimeSpan.FromSeconds(5)),
            "the cleared history is persisted");
    }

    // ---- glass: idle capture release ------------------------------------------------------------

    private static async Task CheckIdleReleaseAsync()
    {
        Console.WriteLine();
        Console.WriteLine("glass: idle capture release");

        var snapshot = LiquidGlassWallpaper.Get();
        snapshot.Dispose();

        if (!LiquidGlassWallpaper.HasCachedCapture)
        {
            Console.WriteLine("  [SKIP] no desktop capture is available in this session — idle-release assertions skipped");
            return;
        }

        var retained = LiquidGlassWallpaper.RetainedCaptureBytes;
        Check(retained > 0, $"the capture is retained while a render demands it ({retained / (1024.0 * 1024.0):F1} MiB)");

        // The grace is shortened to 1.2 s via DESKCANVAS_GLASS_IDLE_RELEASE_MS; the release itself
        // runs on this dispatcher.
        Check(await WaitForAsync(() => !LiquidGlassWallpaper.HasCachedCapture, TimeSpan.FromSeconds(8)),
            "the desktop capture is handed back after the idle grace");
        Check(LiquidGlassWallpaper.RetainedCaptureBytes == 0, "no capture bytes are retained after the idle release");

        // …and the next render re-captures on demand.
        var again = LiquidGlassWallpaper.Get();
        again.Dispose();
        Check(LiquidGlassWallpaper.HasCachedCapture, "the next render re-captures on demand");

        LiquidGlassWallpaper.Release();
    }

    // ---- helpers --------------------------------------------------------------------------------

    private static async Task<bool> WaitForAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                if (condition()) return true;
            }
            catch
            {
                // A half-written file is just "not yet".
            }
            await Task.Delay(100);
        }
        return false;
    }
}
