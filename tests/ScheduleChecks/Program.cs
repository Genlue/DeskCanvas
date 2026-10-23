using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Themes.Fluent;
using Calendar.Models;
using Calendar.Services;
using Calendar.ViewModels;
using Calendar.Views;
using DeskCanvas.Core;

namespace ScheduleChecks;

public class Program
{
    private static int checks = 0;
    private static int fails = 0;

    private static void Check(bool condition, string name)
    {
        checks++;
        if (condition)
        {
            Console.WriteLine($"[PASS] {name}");
        }
        else
        {
            fails++;
            Console.WriteLine($"[FAIL] {name}");
        }
    }

    public static async Task Main(string[] args)
    {
        Console.WriteLine("=== macOS Schedule Widget Integration Checks ===");

        // Setup headless Avalonia platform
        AppBuilder.Configure<ProbeApp>()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .UseSkia()
            .SetupWithoutStarting();

        Application.Current!.Styles.Add(new FluentTheme());

        // 1. ScheduleStore checks
        Console.WriteLine("\n--- 1. ScheduleStore Checks ---");
        var store = ScheduleStore.Get();
        Check(store != null, "1.1 ScheduleStore.Get() loads model");
        Check(store?.Events != null, "1.2 ScheduleStore events list is initialized");

        var testEvent = new ScheduleEventModel(
            "test_evt_123",
            "测试敏捷站会",
            DateTime.Today.AddHours(11),
            DateTime.Today.AddHours(11).AddMinutes(30),
            false,
            "#007AFF",
            "工作",
            "2号会议室"
        );

        var modelChangedFired = false;
        ScheduleStore.ModelChanged += (m, s) =>
        {
            modelChangedFired = true;
        };

        ScheduleStore.AddOrUpdateEvent(testEvent);
        Check(modelChangedFired, "1.3 ScheduleStore.ModelChanged fired on add");

        var updatedStore = ScheduleStore.Get();
        Check(updatedStore.Events!.Any(e => e.Id == "test_evt_123"), "1.4 Added event exists in store");

        ScheduleStore.ToggleComplete("test_evt_123");
        var completedEvent = ScheduleStore.Get().Events!.FirstOrDefault(e => e.Id == "test_evt_123");
        Check(completedEvent != null && completedEvent.IsCompleted, "1.5 ToggleComplete marks event completed");

        ScheduleStore.DeleteEvent("test_evt_123");
        Check(!ScheduleStore.Get().Events!.Any(e => e.Id == "test_evt_123"), "1.6 Deleted event removed from store");

        // Add temporary events for ViewModel and Spotlight testing
        var sampleEvent1 = new ScheduleEventModel(
            "sample_evt_review",
            "方案评审与技术论证",
            DateTime.Today.AddHours(14),
            DateTime.Today.AddHours(15).AddMinutes(30),
            false,
            "#007AFF",
            "工作",
            "腾讯会议 892-124"
        );
        var sampleEvent2 = new ScheduleEventModel(
            "sample_evt_sync",
            "跨端同步与交互对齐",
            DateTime.Today.AddHours(16),
            DateTime.Today.AddHours(17),
            false,
            "#34C759",
            "生活",
            null
        );
        ScheduleStore.AddOrUpdateEvent(sampleEvent1);
        ScheduleStore.AddOrUpdateEvent(sampleEvent2);

        // 2. ScheduleViewModel checks
        Console.WriteLine("\n--- 2. ScheduleViewModel Checks ---");
        var vm = new ScheduleViewModel(ScheduleStore.Get());
        Check(!string.IsNullOrEmpty(vm.DayOfWeek), "2.1 ViewModel DayOfWeek populated");
        Check(!string.IsNullOrEmpty(vm.Day), "2.2 ViewModel Day number populated");
        Check(!string.IsNullOrEmpty(vm.Month), "2.3 ViewModel Month populated");
        Check(vm.UpcomingEvents.Count == 2, "2.4 ViewModel UpcomingEvents contains 2 events");
        Check(vm.SmallEvents.Count == 2, "2.5 ViewModel SmallEvents contains 2 events for 2x2 display");
        Check(!string.IsNullOrEmpty(vm.SmallEvents[0].SmallSummaryText), "2.6 SmallSummaryText generated correctly");
        Check(vm.Days.Count == 7, "2.7 ViewModel mini month week days count is 7");

        // 3. Spotlight Provider checks
        Console.WriteLine("\n--- 3. Spotlight Provider Checks ---");
        var spotlightProvider = new ScheduleSpotlightProvider();
        Check(spotlightProvider.Id == "calendar.schedule", "3.1 ProviderId is calendar.schedule");

        var emptyHits = await spotlightProvider.QueryAsync("", 5, CancellationToken.None);
        Check(emptyHits.Count == 0, "3.2 Empty query returns 0 hits");

        var hitsByTitle = await spotlightProvider.QueryAsync("评审", 5, CancellationToken.None);
        Check(hitsByTitle.Count > 0, "3.3 Query '评审' matches schedule events");
        Check(hitsByTitle.Any(h => h.Title.Contains("评审")), "3.4 Matched hit contains search keyword");

        var hitsByLocation = await spotlightProvider.QueryAsync("腾讯", 5, CancellationToken.None);
        Check(hitsByLocation.Count > 0, "3.5 Query '腾讯' matches schedule event location");

        // Clean up temporary events to leave store completely empty as required
        ScheduleStore.DeleteEvent("sample_evt_review");
        ScheduleStore.DeleteEvent("sample_evt_sync");
        Check((ScheduleStore.Get().Events ?? []).Count == 0, "3.6 ScheduleStore remains clean and empty");

        // 4. View construction checks
        Console.WriteLine("\n--- 4. Widget View Construction Checks ---");
        var scheduleView = new Schedule();
        Check(scheduleView != null, "4.1 Schedule UserControl instantiated");
        Check(scheduleView.ColorfulBackgroundResourceKey == "CalendarCardBackground", "4.2 ColorfulBackgroundResourceKey matches CalendarCardBackground");
        Check(scheduleView.Content is Calendar.Views.Controls.ScheduleSmall, "4.3 Default content is ScheduleSmall");

        Console.WriteLine($"\nChecks completed: {checks - fails}/{checks} passed.");
        if (fails > 0)
        {
            Environment.Exit(1);
        }
    }
}

sealed class ProbeApp : Application
{
    public override void OnFrameworkInitializationCompleted() { }
}
