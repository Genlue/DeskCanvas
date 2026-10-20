using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Avalonia;
using Countdown.FestivalPreset;
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
        TestFestivalPresets();
        TestCustomPresets();
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

    private static void TestFestivalPresets()
    {
        Console.WriteLine("\n--- Festival Preset Catalog & Resolver ---");

        var catalog = FestivalPresetCatalog.All;
        Check(catalog.Count >= 24, $"Catalog has all required presets (got {catalog.Count})");
        Check(catalog.Select(p => p.Id).Distinct().Count() == catalog.Count, "Preset ids are unique");
        Check(catalog.All(p => !string.IsNullOrWhiteSpace(p.Title) && p.Title != p.TitleKey),
            "Every preset resolves a localized title (resx keys exist)");

        // 农历日期由 .NET ChineseLunisolarCalendar 推算（与组件使用的数据源一致）：
        // 2026 年正月初一 = 02-17，元宵 03-03，龙抬头 03-20，端午 06-19，七夕 08-19，
        // 中元 08-27，中秋 09-25，重阳 10-18；腊八（腊月初八）落在 2027-01-15。
        var lunarToday = new DateTime(2026, 2, 17);
        CheckFestival("festival.spring-festival", lunarToday, new DateTime(2026, 2, 17), "Spring Festival 2026 (today included)");
        CheckFestival("festival.lantern", lunarToday, new DateTime(2026, 3, 3), "Lantern Festival 2026");
        CheckFestival("festival.longtaitou", lunarToday, new DateTime(2026, 3, 20), "Longtaitou 2026");
        CheckFestival("festival.dragon-boat", lunarToday, new DateTime(2026, 6, 19), "Dragon Boat 2026");
        CheckFestival("festival.qixi", lunarToday, new DateTime(2026, 8, 19), "Qixi 2026");
        CheckFestival("festival.ghost", lunarToday, new DateTime(2026, 8, 27), "Ghost Festival 2026");
        CheckFestival("festival.mid-autumn", lunarToday, new DateTime(2026, 9, 25), "Mid-Autumn 2026");
        CheckFestival("festival.double-ninth", lunarToday, new DateTime(2026, 10, 18), "Double Ninth 2026");
        CheckFestival("festival.laba", lunarToday, new DateTime(2027, 1, 15), "Laba (12th month) 2026");

        // 除夕：2023 年腊月有 30 天（2024-02-09），2024/2026 年腊月只有 29 天（01-28 / 02-05）。
        CheckFestival("festival.new-years-eve", new DateTime(2023, 6, 1), new DateTime(2024, 2, 9), "New Year's Eve, 30-day 12th month");
        CheckFestival("festival.new-years-eve", new DateTime(2025, 1, 1), new DateTime(2025, 1, 28), "New Year's Eve, 29-day 12th month");
        CheckFestival("festival.new-years-eve", new DateTime(2025, 1, 28), new DateTime(2025, 1, 28), "New Year's Eve includes today");
        CheckFestival("festival.new-years-eve", new DateTime(2026, 10, 18), new DateTime(2027, 2, 5), "New Year's Eve rolls to next year");

        // 浮动节日：5 月第 2 个周日 / 6 月第 3 个周日 / 11 月第 4 个周四。
        CheckFestival("festival.mothers-day", new DateTime(2024, 1, 1), new DateTime(2024, 5, 12), "Mother's Day 2024");
        CheckFestival("festival.mothers-day", new DateTime(2025, 1, 1), new DateTime(2025, 5, 11), "Mother's Day 2025");
        CheckFestival("festival.mothers-day", new DateTime(2026, 1, 1), new DateTime(2026, 5, 10), "Mother's Day 2026");
        CheckFestival("festival.mothers-day", new DateTime(2026, 5, 11), new DateTime(2027, 5, 9), "Mother's Day rolls to 2027");

        CheckFestival("festival.fathers-day", new DateTime(2024, 1, 1), new DateTime(2024, 6, 16), "Father's Day 2024");
        CheckFestival("festival.fathers-day", new DateTime(2025, 1, 1), new DateTime(2025, 6, 15), "Father's Day 2025");
        CheckFestival("festival.fathers-day", new DateTime(2026, 1, 1), new DateTime(2026, 6, 21), "Father's Day 2026");

        CheckFestival("festival.thanksgiving", new DateTime(2024, 1, 1), new DateTime(2024, 11, 28), "Thanksgiving 2024");
        CheckFestival("festival.thanksgiving", new DateTime(2025, 1, 1), new DateTime(2025, 11, 27), "Thanksgiving 2025");
        CheckFestival("festival.thanksgiving", new DateTime(2026, 1, 1), new DateTime(2026, 11, 26), "Thanksgiving 2026");

        // 清明是近似规则：2024/2025-04-04，2026-04-05。
        CheckFestival("festival.qingming", new DateTime(2024, 1, 1), new DateTime(2024, 4, 4), "Qingming 2024");
        CheckFestival("festival.qingming", new DateTime(2025, 1, 1), new DateTime(2025, 4, 4), "Qingming 2025");
        CheckFestival("festival.qingming", new DateTime(2026, 1, 1), new DateTime(2026, 4, 5), "Qingming 2026");
        CheckFestival("festival.qingming", new DateTime(2026, 4, 6), new DateTime(2027, 4, 5), "Qingming rolls to 2027");

        // 公历固定日期：今年已过顺延到明年；当天算“今天”。
        CheckFestival("festival.new-year", new DateTime(2026, 10, 18), new DateTime(2027, 1, 1), "New Year's Day rolls to next year");
        CheckFestival("festival.christmas", new DateTime(2026, 12, 25), new DateTime(2026, 12, 25), "Christmas includes today");
        CheckFestival("festival.christmas", new DateTime(2026, 12, 26), new DateTime(2027, 12, 25), "Christmas rolls to next year");
    }

    private static void CheckFestival(string id, DateTime today, DateTime expected, string name)
    {
        var preset = FestivalPresetCatalog.Find(id);
        if (preset == null)
        {
            Check(false, $"{name}: preset '{id}' exists");
            return;
        }

        var actual = FestivalPresetResolver.ResolveNextOccurrence(preset, today);
        Check(actual == expected.Date, $"{name}: expected {expected:yyyy-MM-dd}, got {actual:yyyy-MM-dd}");
    }

    private static void TestCustomPresets()
    {
        Console.WriteLine("\n--- Custom Presets (My Presets) ---");

        string id = "check-preset-" + Guid.NewGuid().ToString("N");
        var preset = new CustomPreset(
            Id: id,
            Title: "My Payday",
            TargetDate: new DateTime(2026, 3, 7),
            Repeat: RepeatCycle.Monthly,
            Category: "Life",
            ColorTag: "#0A84FF");

        CustomPresetStore.Add(preset);
        var loaded = CustomPresetStore.Get().FirstOrDefault(p => p.Id == id);
        Check(loaded != null, "Custom preset persisted and reloaded");
        if (loaded == null) return;

        Check(loaded.Title == "My Payday" && loaded.Repeat == RepeatCycle.Monthly && loaded.ColorTag == "#0A84FF",
            "Custom preset fields round-trip");

        var next = CountdownCalculationService.ResolveEffectiveTargetDate(
            loaded.ToCountdownItem(), new DateTime(2026, 10, 18));
        Check(next == new DateTime(2026, 11, 7), $"Custom monthly preset resolves next payday (got {next:yyyy-MM-dd})");

        Check(CustomPresetStore.Delete(id), "Custom preset deleted");
        Check(!CustomPresetStore.Get().Any(p => p.Id == id), "Deleted preset is gone after reload");
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
