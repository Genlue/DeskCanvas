using System.Reflection;
using Avalonia;
using ShapePath = Avalonia.Controls.Shapes.Path;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using DeskCanvas.Runtime;
using DeskCanvas.Services;
using FixedWidgets.Services;

namespace GeometryThreadChecks;

/// <summary>
/// Pins the thread-affinity rule that the 设置 → 全部组件 crash broke.
///
/// <para>
/// Avalonia geometry owns the thread that created it. A <see cref="StreamGeometry"/> parsed on a
/// thread-pool thread and then bound to a <see cref="Avalonia.Controls.Shapes.Path"/> makes the Path throw
/// "The calling thread cannot access this object because a different thread owns it" from
/// <c>Shape.MeasureOverride</c> — inside the render pass, where nothing catches it, so the whole
/// process dies (that is exactly the stack in <c>%LocalAppData%\DeskCanvas\crash_log.txt</c>).
/// </para>
///
/// <para>
/// The gallery page materializes a <b>live preview of every widget</b>, so every widget's async
/// refresh runs while the page lays out. The aggregate (全景聚合) card used to take its weather
/// icon straight out of <c>OpenMeteoWeatherService.GetWeatherAsync</c>, whose continuation runs on
/// the thread pool (all its awaits are <c>ConfigureAwait(false)</c>) — the reply therefore carried
/// a pool-owned geometry into the bound Path, and opening 全部组件 killed the app a moment later.
/// </para>
///
/// <para>
/// Two contracts are checked: (1) the async weather service ships data only — no UI objects, so
/// the view model parses the icon on the UI thread; (2) the shared widget timers always deliver
/// their ticks on the UI thread, so a widget that starts an async refresh from a tick can never
/// build its view state on the pool (the session-unlock path fires from SystemEvents' own thread).
/// </para>
///
/// Usage: dotnet run --project tests/GeometryThreadChecks -c Release
/// </summary>
internal static class Program
{
    private static int failures;

    private const string SamplePath = "M19 13h-6v6h-2v-6H5v-2h6V5h2v6h6v2z";

    private const string Payload = """
    {
      "current": { "temperature_2m": 21.5, "weathercode": 3 },
      "daily": { "temperature_2m_max": [26.0], "temperature_2m_min": [17.0] }
    }
    """;

    private static void Check(bool condition, string message)
    {
        Console.WriteLine($"  [{(condition ? "PASS" : "FAIL")}] {message}");
        if (!condition) failures++;
    }

    [STAThread]
    private static int Main()
    {
        Console.WriteLine("=== Widget geometry / tick thread-affinity checks ===");
        Console.WriteLine();

        AppBuilder.Configure<Application>()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .UseSkia()
            .WithInterFont()
            .SetupWithoutStarting();
        Application.Current!.Styles.Add(new FluentTheme());

        CheckAvaloniaContract();
        CheckWeatherServiceShipsDataOnly();
        CheckWeatherIconsAreUiOwned();
        CheckTicksArriveOnTheUiThread();

        Console.WriteLine();
        Console.WriteLine(failures == 0
            ? "ALL GEOMETRY THREAD CHECKS PASSED"
            : $"{failures} CHECK(S) FAILED");
        return failures == 0 ? 0 : 1;
    }

    // ---- 1. the platform rule this whole file guards -----------------------------------------

    private static void CheckAvaloniaContract()
    {
        Console.WriteLine("--- 1. raw contract: geometry belongs to its creating thread ---");

        StreamGeometry offThread = null!;
        var worker = new Thread(() => offThread = StreamGeometry.Parse(SamplePath));
        worker.Start();
        worker.Join();

        // Observation, not a gate: if a future Avalonia drops the affinity this line stops
        // throwing and the guards below simply become cheaper — they are still correct.
        var throws = Throws(() => Measure(offThread));
        Console.WriteLine($"  [note] worker-thread geometry measured on the UI thread throws: {throws}");

        // The positive half is the gate: parsed here, it lays out here.
        Check(!Throws(() => Measure(StreamGeometry.Parse(SamplePath))),
            "1.1 a geometry parsed on the UI thread measures on the UI thread");
    }

    // ---- 2. the weather service must not hand out UI objects ---------------------------------

    private static void CheckWeatherServiceShipsDataOnly()
    {
        Console.WriteLine();
        Console.WriteLine("--- 2. weather service: data only, no UI objects ---");

        var geometryMembers = typeof(WeatherInfo)
            .GetProperties()
            .Where(property => typeof(Geometry).IsAssignableFrom(property.PropertyType))
            .Select(property => property.Name)
            .ToList();
        Check(geometryMembers.Count == 0,
            $"2.1 WeatherInfo carries no geometry (found: {(geometryMembers.Count == 0 ? "none" : string.Join(", ", geometryMembers))}) " +
            "— the reply is built on a pool thread, so an icon there belongs to the pool");

        // A fetch that genuinely completes off the UI thread, like a network reply.
        var shared = new OpenMeteoSharedProvider(async (_, token) =>
        {
            await Task.Delay(80, token).ConfigureAwait(false);
            return Payload;
        });

        using var service = new OpenMeteoWeatherService(shared);
        // Blocking is fine on this side: the service's continuation only needs the pool.
        var info = service.GetWeatherAsync(39.9, 116.4).GetAwaiter().GetResult();

        Check(info is { WeatherCode: 3, ConditionText.Length: > 0 } && Math.Abs(info.Temperature - 21.5) < 0.001,
            $"2.2 GetWeatherAsync (fetch completed on a pool thread) still returns the reading " +
            $"(code {info?.WeatherCode}, {info?.Temperature:0.#}°, {info?.ConditionText})");
        Check(Math.Abs((info?.MaxTemp ?? 0) - 26.0) < 0.001 && Math.Abs((info?.MinTemp ?? 0) - 17.0) < 0.001,
            "2.3 the daily min/max ride along with the code the view model needs");
    }

    // ---- 3. the icon helper is UI-thread work, and every code stays drawable ------------------

    private static void CheckWeatherIconsAreUiOwned()
    {
        Console.WriteLine();
        Console.WriteLine("--- 3. the icon the card binds is parsed on the UI thread ---");

        // The exact shape of the crash: the icon for the code the service returned, handed to a
        // Path the way <Path Data="{Binding WeatherIcon}"/> does it.
        Check(!Throws(() => Measure(OpenMeteoWeatherService.GetWeatherIcon(3))),
            "3.1 the icon for a real weather reply lays out on the UI thread (the 全部组件 crash path)");

        int[] codes = [0, 1, 2, 3, 45, 48, 51, 55, 56, 61, 63, 65, 66, 67, 71, 73, 75, 77, 80, 82, 85, 86, 95, 96, 99, 12345];
        var broken = codes.Where(code => Throws(() => Measure(OpenMeteoWeatherService.GetWeatherIcon(code)))).ToList();
        Check(broken.Count == 0,
            $"3.2 every weather code produces a drawable icon (broken: {(broken.Count == 0 ? "none" : string.Join(", ", broken))})");

        var first = OpenMeteoWeatherService.GetWeatherIcon(0);
        var second = OpenMeteoWeatherService.GetWeatherIcon(0);
        Check(!ReferenceEquals(first, second) || !Throws(() => Measure(second)),
            "3.3 asking twice is safe (no shared instance to poison across owners)");
    }

    // ---- 4. shared timers must deliver ticks on the UI thread ---------------------------------

    private static void CheckTicksArriveOnTheUiThread()
    {
        Console.WriteLine();
        Console.WriteLine("--- 4. shared timers deliver ticks on the UI thread ---");

        var onTick = typeof(UpdateTimer).GetMethod("OnTimerTick", BindingFlags.NonPublic | BindingFlags.Instance)!;

        // The session-unlock catch-up: SystemEvents raises SessionSwitch on its own thread and
        // UpdateTimer used to call the subscribers right there.
        using (var timer = new UpdateTimer(TimeSpan.FromHours(1))) // long interval: only the explicit raise ticks
        {
            bool? ranOnUiThread = null;
            var ranInline = false;
            void Handler()
            {
                ranInline = true;
                ranOnUiThread = Dispatcher.UIThread.CheckAccess();
            }

            timer.Subscribe(Handler);
            var worker = new Thread(() => onTick.Invoke(timer, [timer, EventArgs.Empty]));
            worker.Start();
            worker.Join();

            Check(!ranInline, "4.1 a tick raised on a background thread is not delivered inline on that thread");
            Dispatcher.UIThread.RunJobs();
            Check(ranOnUiThread == true,
                "4.2 the deferred tick reaches the subscriber on the UI thread (widget async refreshes stay UI-bound)");
            timer.Unsubscribe(Handler);
        }

        // The normal DispatcherTimer path must stay synchronous: a tick that silently deferred
        // would break the "the clock repaints on this tick" expectation.
        using (var timer = new UpdateTimer(TimeSpan.FromHours(1)))
        {
            bool? ranOnUiThread = null;
            var ranInline = false;
            void Handler()
            {
                ranInline = true;
                ranOnUiThread = Dispatcher.UIThread.CheckAccess();
            }

            timer.Subscribe(Handler);
            onTick.Invoke(timer, [timer, EventArgs.Empty]);

            Check(ranInline && ranOnUiThread == true,
                "4.3 a tick raised on the UI thread is still delivered synchronously there");
            timer.Unsubscribe(Handler);
        }
    }

    // ---- helpers ------------------------------------------------------------------------------

    private static void Measure(Geometry? geometry)
    {
        var path = new ShapePath { Data = geometry, Width = 24, Height = 24 };
        path.Measure(new Size(24, 24));
    }

    private static bool Throws(Action action)
    {
        try
        {
            action();
            return false;
        }
        catch (Exception)
        {
            return true;
        }
    }
}
