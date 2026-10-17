using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Themes.Fluent;
using DeskCanvas.Core;
using DeskCanvas.Core.Interfaces;
using DeskCanvas.Core.Models;
using DeskCanvas.Core.Models.Settings;
using DeskCanvas.Core.Services;
using DeskCanvas.Services;
using Reminders.Models;
using Reminders.Services;
using Timetable.Models;
using Timetable.Services;

namespace ProfileStorageChecks;

internal static class Program
{
    private static int passed;
    private static readonly List<string> failures = [];

    private static void Check(bool condition, string message)
    {
        Console.WriteLine($"[{(condition ? "PASS" : "FAIL")}] {message}");
        if (condition) passed++;
        else failures.Add(message);
    }

    [STAThread]
    public static int Main()
    {
        Console.WriteLine("=== ProfileStorageChecks — P12 数据配置方案隔离与操作契约 ===");
        Console.WriteLine();

        var sandbox = Path.Combine(Path.GetTempPath(), "DeskCanvas-ProfileStorageChecks-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(sandbox);

        Environment.SetEnvironmentVariable("DESKCANVAS_DATA_FOLDER", sandbox);
        Environment.SetEnvironmentVariable("DESCANVAS_DATA_FOLDER", sandbox);

        try
        {
            AppBuilder.Configure<Application>()
                .UseHeadless(new AvaloniaHeadlessPlatformOptions())
                .SetupWithoutStarting();

            if (Application.Current != null && Application.Current.Styles.Count == 0)
            {
                Application.Current.Styles.Add(new FluentTheme());
            }

            WidgetBundle.ExtractIfNeeded();

            Console.WriteLine("1. 基础路径与默认方案决议");
            Check(Const.DataFolder == sandbox, $"DataFolder correctly resolved to sandbox ({Const.DataFolder})");
            Check(Const.ActiveProfile == Const.DefaultProfileName, $"Initial ActiveProfile is '{Const.DefaultProfileName}'");
            Check(Const.ProfilesFolder == Path.Combine(sandbox, "Profiles"), $"ProfilesFolder is under DataFolder ({Const.ProfilesFolder})");
            Check(Const.ActiveProfileFolder == Path.Combine(Const.ProfilesFolder, "Default"), $"ActiveProfileFolder is under Profiles/Default ({Const.ActiveProfileFolder})");
            Check(Const.AppSettingsFile == Path.Combine(Const.ActiveProfileFolder, "appSettings.json"), "AppSettingsFile is profile-relative");
            Check(Const.LayoutFile == Path.Combine(Const.ActiveProfileFolder, "layout.json"), "LayoutFile is profile-relative");
            Check(Const.RemindersFile == Path.Combine(Const.ActiveProfileFolder, "reminders.json"), "RemindersFile is profile-relative");
            Check(Const.TimetableFile == Path.Combine(Const.ActiveProfileFolder, "timetable.json"), "TimetableFile is profile-relative");

            Console.WriteLine();
            Console.WriteLine("2. 组件业务数据隔离验证 (Reminders & Timetable)");

            // In Default profile: add a reminder item and timetable semester
            var defaultReminder = new ReminderModel(false, "Default Task Alpha");
            RemindersStore.Save(new RemindersListModel("Default Tasks", [defaultReminder]));

            var defaultTimetable = new TimetableModel
            {
                Name = "2026秋季学期-Default",
                TotalWeeks = 18,
                Courses = []
            };
            TimetableStore.Save(defaultTimetable);

            var defaultRemFile = Path.Combine(Const.ProfilesFolder, "Default", "reminders.json");
            var defaultTimeFile = Path.Combine(Const.ProfilesFolder, "Default", "timetable.json");
            Check(File.Exists(defaultRemFile), "Default profile reminders.json created on disk");
            Check(File.Exists(defaultTimeFile), "Default profile timetable.json created on disk");

            // Switch to new profile "Work"
            Const.SetActiveProfile("Work");
            Check(Const.ActiveProfile == "Work", "ActiveProfile switched to 'Work'");
            Check(Const.ActiveProfileFolder == Path.Combine(Const.ProfilesFolder, "Work"), "ActiveProfileFolder updated to Work");
            Check(File.Exists(Const.ActiveProfilePointerPath), "activeProfile.json pointer file created");
            var pointerJson = File.ReadAllText(Const.ActiveProfilePointerPath);
            Check(pointerJson.Contains("\"Work\""), "activeProfile.json pointer contains 'Work'");

            // RemindersStore should now see clean/default data for Work profile, NOT Default Task Alpha
            var workReminders = RemindersStore.Get();
            Check(workReminders.Reminders.All(r => r.Title != "Default Task Alpha"), "Work profile does not see Default profile reminders (isolated!)");

            // Add Work-specific reminder
            var workReminder = new ReminderModel(false, "Work Meeting Beta");
            RemindersStore.Save(new RemindersListModel("Work Tasks", [workReminder]));

            var workRemFile = Path.Combine(Const.ProfilesFolder, "Work", "reminders.json");
            Check(File.Exists(workRemFile), "Work profile reminders.json created on disk");

            // Switch back to "Default" profile
            Const.SetActiveProfile("Default");
            Check(Const.ActiveProfile == "Default", "Switched back to 'Default'");
            var reloadedDefaultReminders = RemindersStore.Get();
            Check(reloadedDefaultReminders.Reminders.Any(r => r.Title == "Default Task Alpha"), "Default profile still contains 'Default Task Alpha'");
            Check(reloadedDefaultReminders.Reminders.All(r => r.Title != "Work Meeting Beta"), "Default profile does not contain 'Work Meeting Beta' (cross-profile isolation confirmed!)");
            var reloadedDefaultTimetable = TimetableStore.Get();
            Check(reloadedDefaultTimetable.Name == "2026秋季学期-Default", "Default timetable name preserved");

            Console.WriteLine();
            Console.WriteLine("3. ProfileService 方案管理操作契约 (增删改查与目录物理生命周期)");
            var appSettingsProvider = new AppSettingsProvider();
            var layoutProvider = new LayoutProvider();
            var profileService = new ProfileService(
                appSettingsProvider,
                layoutProvider,
                new DummyThemeService(),
                new DummyLocaleService(),
                new DummyWidgetFactory(),
                new DummyDisplayMonitorService());

            var profileList = profileService.GetProfiles();
            Check(profileList.Contains("Default"), "GetProfiles contains 'Default'");
            Check(profileList.Contains("Work"), "GetProfiles contains 'Work'");

            // Create new profile "Study"
            var created = profileService.CreateProfile("Study", copyCurrent: false);
            Check(created, "CreateProfile 'Study' succeeded");
            var studyDir = Path.Combine(Const.ProfilesFolder, "Study");
            Check(Directory.Exists(studyDir), "Study profile directory created on disk");

            // Duplicate profile "Study" to "StudyCopy"
            var duplicated = profileService.DuplicateProfile("Study", "StudyCopy");
            Check(duplicated, "DuplicateProfile 'Study' -> 'StudyCopy' succeeded");
            var copyDir = Path.Combine(Const.ProfilesFolder, "StudyCopy");
            Check(Directory.Exists(copyDir), "StudyCopy directory created on disk");

            // Rename profile "StudyCopy" to "StudyRenamed"
            var renamed = profileService.RenameProfile("StudyCopy", "StudyRenamed");
            Check(renamed, "RenameProfile 'StudyCopy' -> 'StudyRenamed' succeeded");
            var renamedDir = Path.Combine(Const.ProfilesFolder, "StudyRenamed");
            Check(!Directory.Exists(copyDir), "Old StudyCopy directory removed");
            Check(Directory.Exists(renamedDir), "New StudyRenamed directory exists");

            // Delete profile "StudyRenamed"
            var deleted = profileService.DeleteProfile("StudyRenamed");
            Check(deleted, "DeleteProfile 'StudyRenamed' succeeded");
            Check(!Directory.Exists(renamedDir), "StudyRenamed directory deleted without residue on disk");

            // Delete active profile must fail
            var deleteActive = profileService.DeleteProfile(profileService.GetActiveProfile());
            Check(!deleteActive, "Deleting currently active profile is blocked");

            Console.WriteLine();
            Console.WriteLine("4. 环境变量兼容性验证 (优先 DESKCANVAS_DATA_FOLDER, 回退 DESCANVAS_DATA_FOLDER)");
            Check(Const.DataFolderSource == DataFolderSource.EnvironmentVariable, "DataFolderSource correctly identified as EnvironmentVariable");
        }
        finally
        {
            Environment.SetEnvironmentVariable("DESKCANVAS_DATA_FOLDER", null);
            Environment.SetEnvironmentVariable("DESCANVAS_DATA_FOLDER", null);
            try { Directory.Delete(sandbox, recursive: true); } catch { }
        }

        Console.WriteLine();
        if (failures.Count == 0)
        {
            Console.WriteLine($"ProfileStorageChecks ALL PASS ({passed} checks)");
            return 0;
        }

        Console.WriteLine($"ProfileStorageChecks FAILED: {failures.Count} check(s) failed.");
        foreach (var f in failures) Console.WriteLine($"  - {f}");
        return 1;
    }

    private sealed class DummyThemeService : IThemeService
    {
        public void Apply(Theme theme) { }
        public void Apply(AppSettings settings) { }
    }

    private sealed class DummyLocaleService : ILocaleService
    {
        public void SetCulture(string language) { }
    }

    private sealed class DummyWidgetFactory : WidgetFactory
    {
        public DummyWidgetFactory() : base(null!, null!, null!, null!) { }
        public override void CreateFromLayout() { }
        public override void CloseAll() { }
    }

    private sealed class DummyDisplayMonitorService : DisplayMonitorService
    {
        public DummyDisplayMonitorService() : base(null!) { }
        public override void Refresh() { }
    }
}
