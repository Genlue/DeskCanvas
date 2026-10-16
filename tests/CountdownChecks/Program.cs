using System;
using System.IO;
using System.Reflection;
using Avalonia;
using Countdown.Models;
using Countdown.Services;
using Countdown.ViewModels;
using Countdown.Views;
using DeskCanvas.Core.Interfaces;
using DeskCanvas.Core.Models.Attributes;
using DeskCanvas.Views;

namespace CountdownChecks;

internal static class Program
{
    private static int passed;
    private static int failed;

    private static void Check(bool condition, string name)
    {
        if (condition)
        {
            passed++;
            Console.WriteLine($"  [ok]   {name}");
        }
        else
        {
            failed++;
            Console.WriteLine($"  [FAIL] {name}");
        }
    }

    [STAThread]
    private static int Main()
    {
        Environment.SetEnvironmentVariable("DESCANVAS_DATA_FOLDER",
            Path.Combine(Path.GetTempPath(), "DeskCanvas-Countdown-Checks-" + Guid.NewGuid().ToString("N")));

        AppBuilder.Configure<Application>().UsePlatformDetect().SetupWithoutStarting();

        Console.WriteLine("=== Countdown Widget Checks ===");

        TestAssemblyMetadata();
        TestLunarCalendar();
        TestCalculationEngine();
        TestDataStore();
        TestViewContracts();
        TestPerformanceAndLifecycleOptimizations();

        Console.WriteLine();
        Console.WriteLine($"Result: {passed} passed, {failed} failed.");
        return failed == 0 ? 0 : 1;
    }

    private static void TestAssemblyMetadata()
    {
        Console.WriteLine("\n--- Assembly Metadata ---");
        var asm = typeof(CountdownView).Assembly;

        var widgetInfo = asm.GetCustomAttribute<WidgetInfoAttribute>();
        Check(widgetInfo != null, "Assembly has WidgetInfoAttribute");
        Check(widgetInfo?.ViewType == typeof(CountdownView), "WidgetInfo ViewType is CountdownView");
        Check(widgetInfo?.ModelType == typeof(CountdownModel), "WidgetInfo ModelType is CountdownModel");
        Check(widgetInfo?.EditModelViewType == typeof(Countdown.Views.Settings.CountdownSettings), "WidgetInfo EditModelViewType is CountdownSettings");

        var localeAttr = asm.GetCustomAttribute<LocaleAttribute>();
        Check(localeAttr != null, "Assembly has LocaleAttribute");
        Check(localeAttr?.DisplayName == "Countdown", "Locale DisplayName is 'Countdown'");
    }

    private static void TestLunarCalendar()
    {
        Console.WriteLine("\n--- Lunar Calendar Helper ---");

        // Spring Festival 2026: 2026-02-17
        var springFestival2026 = LunarCalendarHelper.LunarToSolar(2026, 1, 1, false);
        Check(springFestival2026.HasValue, "Lunar 2026-01-01 converts to solar date");
        Check(springFestival2026?.Year == 2026 && springFestival2026?.Month == 2 && springFestival2026?.Day == 17,
            $"Lunar 2026-01-01 is 2026-02-17 (got {springFestival2026:yyyy-MM-dd})");

        // Solar to Lunar
        LunarCalendarHelper.SolarToLunar(new DateTime(2026, 2, 17), out int y, out int m, out int d, out bool isLeap);
        Check(y == 2026 && m == 1 && d == 1 && !isLeap, $"Solar 2026-02-17 is Lunar {y}-{m}-{d}");

        // Format Lunar month day
        string formatted = LunarCalendarHelper.FormatLunarMonthDay(1, 1, false);
        Check(formatted == "正月初一", $"Format Lunar 1-1 is 正月初一 (got {formatted})");

        string midAutumn = LunarCalendarHelper.FormatLunarMonthDay(8, 15, false);
        Check(midAutumn == "八月十五", $"Format Mid-Autumn is 八月十五 (got {midAutumn})");
    }

    private static void TestCalculationEngine()
    {
        Console.WriteLine("\n--- Countdown Calculation Service ---");
        var baseNow = new DateTime(2026, 10, 16, 12, 0, 0);

        // 1. Future event: 10 days later
        var futureItem = new CountdownItem(
            Id: "1",
            Title: "Test Future",
            TargetDate: baseNow.AddDays(10),
            StartDate: baseNow.AddDays(-10)
        );

        var resultFuture = CountdownCalculationService.Compute(futureItem, baseNow, CountdownGranularity.DaysOnly);
        Check(!resultFuture.IsCountup, "Future event is not countup");
        Check(resultFuture.Days == 10, $"Days remaining is 10 (got {resultFuture.Days})");
        Check(resultFuture.MainDisplayNumber == "10", $"Main display is '10' (got '{resultFuture.MainDisplayNumber}')");
        Check(Math.Abs(resultFuture.Progress - 0.5) < 0.05, $"Progress is ~50% (got {resultFuture.Progress:P0})");

        // 2. Countup mode: past event
        var pastItem = new CountdownItem(
            Id: "2",
            Title: "Anniversary",
            TargetDate: baseNow.AddDays(-100),
            Direction: CountDirection.Countup
        );

        var resultPast = CountdownCalculationService.Compute(pastItem, baseNow, CountdownGranularity.DaysOnly);
        Check(resultPast.IsCountup, "Past anniversary is countup");
        Check(resultPast.Days == 100, $"Days passed is 100 (got {resultPast.Days})");

        // 3. Dynamic 24h granularity: 5 hours remaining
        var nearItem = new CountdownItem(
            Id: "3",
            Title: "Today Meeting",
            TargetDate: baseNow.AddHours(5).AddMinutes(30).AddSeconds(15)
        );

        var resultNear = CountdownCalculationService.Compute(nearItem, baseNow, CountdownGranularity.Auto24Hours);
        Check(resultNear.MainDisplayNumber == "05:30:15", $"Dynamic 24h mode displays HH:mm:ss (got '{resultNear.MainDisplayNumber}')");

        // 4. StrokeDashOffset
        var itemVm = new CountdownItemViewModel(futureItem, CountdownGranularity.DaysOnly, baseNow);
        Check(itemVm.StrokeDashOffset >= 0 && itemVm.StrokeDashOffset <= 43.1,
            $"StrokeDashOffset is within valid ring bounds: {itemVm.StrokeDashOffset:F2}");
    }

    private static void TestDataStore()
    {
        Console.WriteLine("\n--- Data Store Operations ---");

        var events = CountdownStore.Get();
        Check(events != null && events.Count > 0, $"Default events seeded: count={events?.Count}");

        string testId = Guid.NewGuid().ToString("N");
        var newItem = new CountdownItem(testId, "Test Event", DateTime.Now.AddDays(30));
        CountdownStore.AddOrUpdate(newItem);

        var reloaded = CountdownStore.Get();
        Check(reloaded.Any(e => e.Id == testId), "New event added and persisted");

        CountdownStore.TogglePin(testId);
        reloaded = CountdownStore.Get();
        var found = reloaded.FirstOrDefault(e => e.Id == testId);
        Check(found != null && found.IsPinned, "Event pin toggled");

        CountdownStore.Delete(testId);
        reloaded = CountdownStore.Get();
        Check(!reloaded.Any(e => e.Id == testId), "Event deleted successfully");
    }

    private static void TestViewContracts()
    {
        Console.WriteLine("\n--- View & Secondary Panel Contracts ---");

        var view = new CountdownView();
        Check(view is IWidgetSelfRefreshing, "CountdownView implements IWidgetSelfRefreshing");
        Check(view is IWidgetSurfaceHints hints && hints.ColorfulBackgroundResourceKey == "CountdownCardBackground",
            "CountdownView declares CountdownCardBackground for Colorful theme");

        Check(typeof(CountdownPopupWindow).IsSubclassOf(typeof(SecondaryPanelWindow)),
            "CountdownPopupWindow inherits SecondaryPanelWindow");

        var vm = new CountdownViewModel(new CountdownModel(Style: CountdownCardStyle.MinimalNumber));
        Check(vm.IsMinimalNumberStyle && !vm.IsRingProgressStyle && !vm.IsEventListStyle,
            "ViewModel correctly reflects MinimalNumber style");

        vm.UpdateSettings(new CountdownModel(Style: CountdownCardStyle.RingProgress));
        Check(!vm.IsMinimalNumberStyle && vm.IsRingProgressStyle && !vm.IsEventListStyle,
            "ViewModel correctly updates to RingProgress style across layouts");

        vm.UpdateSettings(new CountdownModel(Style: CountdownCardStyle.EventList));
        Check(!vm.IsMinimalNumberStyle && !vm.IsRingProgressStyle && vm.IsEventListStyle,
            "ViewModel correctly updates to EventList style across layouts");
        vm.Dispose();
    }

    private static void TestPerformanceAndLifecycleOptimizations()
    {
        Console.WriteLine("\n--- Performance & Lifecycle Optimizations ---");
        var baseNow = new DateTime(2026, 10, 16, 12, 0, 0);

        var futureItem = new CountdownItem(
            Id: "perf-1",
            Title: "Perf Future",
            TargetDate: baseNow.AddDays(10),
            StartDate: baseNow.AddDays(-10)
        );

        // 1. Result memoization: consecutive ticks with same days return identical instance
        var res1 = CountdownCalculationService.Compute(futureItem, baseNow, CountdownGranularity.DaysOnly);
        var res2 = CountdownCalculationService.Compute(futureItem, baseNow.AddSeconds(1), CountdownGranularity.DaysOnly, res1);
        Check(ReferenceEquals(res1, res2), "Compute reuses identical instance when values haven't changed (0 allocation)");

        // 2. CountdownItemViewModel.Tick skips PropertyChanged on no-op
        var itemVm = new CountdownItemViewModel(futureItem, CountdownGranularity.DaysOnly, baseNow);
        int propChangedCount = 0;
        itemVm.PropertyChanged += (s, e) => propChangedCount++;
        itemVm.Tick(baseNow.AddSeconds(1));
        Check(propChangedCount == 0, $"Tick fires 0 PropertyChanged events on unchanged tick (got {propChangedCount})");

        // 3. PropertyChanged fires when detailed time actually changes
        var nearItem = new CountdownItem(
            Id: "perf-2",
            Title: "Perf Near",
            TargetDate: baseNow.AddHours(5).AddMinutes(30).AddSeconds(15)
        );
        var detailedVm = new CountdownItemViewModel(nearItem, CountdownGranularity.AlwaysDetailed, baseNow);
        int detailedPropChanges = 0;
        detailedVm.PropertyChanged += (s, e) => detailedPropChanges++;
        detailedVm.Tick(baseNow.AddSeconds(1));
        Check(detailedPropChanges > 0, $"Detailed Tick fires PropertyChanged when seconds tick (got {detailedPropChanges})");
        Check(detailedVm.MainDisplayNumber == "05:30:14", $"MainDisplayNumber updated to 05:30:14 (got '{detailedVm.MainDisplayNumber}')");

        // 4. Cached day strings & brush caching
        string day10 = CountdownCalculationService.FormatDayNumber(10);
        string day10Again = CountdownCalculationService.FormatDayNumber(10);
        Check(ReferenceEquals(day10, day10Again), "FormatDayNumber returns cached string reference");

        var brush1 = CountdownItemViewModel.ParseColorBrush("#FF5A5F");
        var brush2 = CountdownItemViewModel.ParseColorBrush("#FF5A5F");
        Check(ReferenceEquals(brush1, brush2), "ParseColorBrush returns cached brush instance");

        // 5. ViewModel lifecycle (doesn't run timer until Start, cleans up on Dispose)
        var vm = new CountdownViewModel(new CountdownModel { Granularity = CountdownGranularity.DaysOnly });
        Check(!vm.IsRunning, "CountdownViewModel timer does not run on construction");

        vm.Start();
        Check(vm.IsRunning, "CountdownViewModel timer is running after Start()");

        vm.Stop();
        Check(!vm.IsRunning, "CountdownViewModel timer stopped after Stop()");

        vm.Dispose();
        Check(vm.IsDisposed && !vm.IsRunning, "CountdownViewModel disposed and timer stopped");

        // 6. Safe dead subscriber pruning in CountdownStore
        var dummyVm = new CountdownViewModel(new CountdownModel());
        dummyVm.Start();
        dummyVm.Dispose(); // Disposed now

        // Trigger store notification - should prune dummyVm cleanly without throwing
        bool notifySucceeded = false;
        try
        {
            CountdownStore.NotifyModelChanged(CountdownStore.Get(), null);
            notifySucceeded = true;
        }
        catch { }
        Check(notifySucceeded, "NotifyModelChanged safely prunes disposed subscribers");
    }
}
