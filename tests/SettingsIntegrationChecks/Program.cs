using System.Globalization;
using System.Text.Json;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.TextInput;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DeskCanvas.Core.Interfaces;
using DeskCanvas.Core.Models.Settings;
using DeskCanvas.Core.Services;
using DeskCanvas.Locales;
using DeskCanvas.Services;
using DeskCanvas.ViewModels;
using DeskCanvas.Views.Controls;
using DeskCanvas.Views.Pages;

namespace SettingsIntegrationChecks;

internal static class Program
{
    private static int failures;
    private static int checks;
    private static void Check(bool condition, string name)
    {
        checks++;
        Console.WriteLine($"[{(condition ? "PASS" : "FAIL")}] {name}");
        if (!condition) failures++;
    }

    public static int Main()
    {
        // Match UiFoundationChecks: load real application styles but suppress host startup.
        Directory.CreateDirectory(Path.Combine(AppContext.BaseDirectory, "Widgets"));
        AppBuilder.Configure<ProbeApp>()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .UseSkia().WithInterFont().SetupWithoutStarting();
        var directory = Path.Combine(Path.GetTempPath(), "DeskCanvas.SettingsIntegrationChecks", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "appsettings.json");
        try
        {
            File.WriteAllText(path, JsonSerializer.Serialize(InitialSettings()));
            var provider = new FileSettings(path);
            var initial = provider.Get();
            using var wallpaper = new WallpaperThemeService();
            var themes = new ThemeService(provider, wallpaper);
            themes.Apply(initial);
            var locale = new LocaleService(provider);
            locale.SetCulture("en");
            var publicationCount = 0;
            provider.DataChanged += (_, _, _) => publicationCount++;

            var page = new Appearance(provider);
            var vm = (AppearanceViewModel)page.DataContext!;
            var host = new Window { Content = new ScrollViewer { Content = page }, Width = 900, Height = 700 };
            host.Show();
            Pump();
            var editor = page.FindControl<LabeledSlider>("OpacityEditor")!;
            var slider = editor.FindControl<Slider>("SliderPart")!;
            var numeric = editor.FindControl<NumericUpDown>("NumericPart")!;
            Check(provider.SaveAttempts == 0 && publicationCount == 0, "opening real settings page never saves");

            slider.Value = 0.35;
            slider.Value = 0.45;
            Pump();
            Check(vm.OpacityLevel == 0.45 && provider.Get().Theme.OpacityLevel == initial.Theme.OpacityLevel
                && provider.SaveAttempts == 0, "real slider stages many changes without writing JSON");
            slider.RaiseEvent(new VectorEventArgs { RoutedEvent = Thumb.DragCompletedEvent });
            Pump();
            Check(provider.SaveAttempts == 1 && publicationCount == 1 && Disk(path).Theme.OpacityLevel == 0.45,
                "real drag completion saves once and atomically publishes committed opacity");
            slider.RaiseEvent(new VectorEventArgs { RoutedEvent = Thumb.DragCompletedEvent });
            numeric.RaiseEvent(new RoutedEventArgs(InputElement.LostFocusEvent));
            Check(provider.SaveAttempts == 1, "duplicate completion and numeric focus loss do not save twice");
            Check(!Directory.EnumerateFiles(directory, "*.tmp").Any(), "atomic settings writer cleans sibling temporary files");

            numeric.Value = 0.60m;
            Check(provider.SaveAttempts == 1, "numeric opacity edit stages before Enter");
            numeric.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
            Check(provider.SaveAttempts == 2 && Disk(path).Theme.OpacityLevel == 0.6,
                "numeric Enter uses the same once-per-input commit path");

            var beforeFailure = File.ReadAllBytes(path);
            var beforeResources = Application.Current!.Resources["BackgroundOpacity"];
            var beforePublications = publicationCount;
            slider.Value = 0.75;
            // Lock the actual destination: File.Replace fails after its sibling temp is flushed.
            using (var lockedDestination = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                slider.RaiseEvent(new VectorEventArgs { RoutedEvent = Thumb.DragCompletedEvent });
                Check(vm.HasSettingsError && vm.SettingsError == Locale.Settings_Appearance_SaveFailed && vm.OpacityLevel == 0.75,
                    "durable replace failure displays localized error and retains opacity draft");
                Check(publicationCount == beforePublications && provider.Get().Theme.OpacityLevel == 0.6
                    && Equals(Application.Current.Resources["BackgroundOpacity"], beforeResources),
                    "failed durable save publishes neither settings snapshot nor theme resources");
                Check(File.ReadAllBytes(path).SequenceEqual(beforeFailure) && !Directory.EnumerateFiles(directory, "*.tmp").Any(),
                    "failed atomic replace preserves previous file and removes staged temp");
                var culture = CultureInfo.CurrentUICulture.Name;
                try { provider.Save(provider.Get() with { Region = new Region("zh-Hans") }); }
                catch (IOException) { }
                Check(CultureInfo.CurrentUICulture.Name == culture, "failed save does not publish language changes");
            }
            vm.RetrySettingsSave();
            Check(!vm.HasSettingsError && Disk(path).Theme.OpacityLevel == 0.75,
                "retry commits retained draft after persistence recovers");

            var otherEditor = new AppearanceViewModel(provider);
            vm.OpacityLevel = 0.8;
            otherEditor.AccentColorHex = "#FF336699";
            var attemptsBeforeConflict = provider.SaveAttempts;
            Check(!vm.CommitOpacityLevel() && provider.SaveAttempts == attemptsBeforeConflict
                && vm.SettingsError == Locale.Settings_Appearance_SaveConflict && vm.OpacityLevel == 0.8,
                "second editor change conflicts without saving or losing first editor draft");
            Check(Disk(path).Theme.AccentColor == "#FF336699" && Disk(path).Theme.OpacityLevel == 0.75,
                "conflicting opacity draft never overwrites another editor's committed field");
            vm.ReloadSettings();
            Check(!vm.HasSettingsError && vm.OpacityLevel == 0.75, "explicit reload discards stale draft and clears conflict");

            vm.OpacityLevel = 0.85;
            vm.Monochrome = !provider.Get().Theme.Monochrome;
            Check(vm.OpacityLevel == 0.85 && !vm.CommitOpacityLevel(),
                "another control in same page cannot silently replace staged opacity");
            vm.ReloadSettings();
            vm.Font = "Arial";
            Check(Disk(path).Theme.FontFamily == "Arial"
                && Disk(path).GetThemeForSurface(provider.Get().Theme.EffectiveSurface).FontFamily == "Arial",
                "font routes through common theme persistence and per-surface snapshot");
            using (var lockedDestination = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                vm.Font = "Segoe UI";
                Check(vm.Font == "Segoe UI" && vm.PendingTheme?.FontFamily == "Segoe UI" && vm.HasSettingsError
                    && provider.Get().Theme.FontFamily == "Arial", "failed font save retains a retryable full theme draft");
            }
            vm.RetrySettingsSave();
            Check(!vm.HasSettingsError && Disk(path).Theme.FontFamily == "Segoe UI", "font draft retry uses common durable commit path");

            CheckOpticsSliders(page, provider, path);
            CheckTextComposition(page, provider);
            CheckWindowScopes(provider.Get().Theme);
            host.Close();
            Console.WriteLine($"SettingsIntegrationChecks: {checks - failures}/{checks} passed");
            Console.WriteLine("UNVERIFIED: physical IME, physical multi-monitor transitions, GPU/performance, old package compatibility.");
            return failures == 0 ? 0 : 1;
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static void CheckTextComposition(Appearance page, FileSettings provider)
    {
        var vm = (AppearanceViewModel)page.DataContext!;
        var search = page.GetLogicalDescendants().OfType<TextBox>()
            .First(t => t.Watermark == Locale.Settings_Appearance_FontSearchWatermark);
        search.Focus();
        Pump();
        var request = new TextInputMethodClientRequestedEventArgs { RoutedEvent = InputElement.TextInputMethodClientRequestedEvent };
        search.RaiseEvent(request);
        var client = request.Client;
        Check(client != null, "real font search exposes Avalonia text composition client");
        var saves = provider.SaveAttempts;
        if (client == null) return;
        client.SetPreeditText("zhong");
        Pump();
        Check(string.IsNullOrEmpty(vm.FontSearchText) && provider.SaveAttempts == saves,
            "IME preedit does not alter bound search text or persist settings");
        client.SetPreeditText("");
        search.RaiseEvent(new TextInputEventArgs { RoutedEvent = InputElement.TextInputEvent, Text = "中文" });
        Pump();
        Check(vm.FontSearchText == "中文" && provider.SaveAttempts == saves,
            "committed CJK text reaches real binding without accidental font save");
        search.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Back });
        Pump();
        Check(vm.FontSearchText == "中" && provider.SaveAttempts == saves,
            "CJK edit after composition preserves caret and remains a search draft");
    }

    private static void CheckOpticsSliders(Appearance page, FileSettings provider, string path)
    {
        var vm = (AppearanceViewModel)page.DataContext!;
        var cases = new (string Label, Func<double> Preview, Func<AppSettings, double> Saved)[]
        {
            (Locale.Settings_Appearance_Glass_Blur, () => vm.GlassBlur, s => s.Theme.EffectiveLiquidGlass.Blur),
            (Locale.Settings_Appearance_Glass_HighlightAngle, () => vm.GlassHighlightAngle, s => s.Theme.EffectiveLiquidGlass.HighlightAngle),
            (Locale.Settings_Appearance_TextShadow_Strength, () => vm.TextShadowStrength, s => s.Theme.TextShadowStrength)
        };
        foreach (var (label, preview, saved) in cases)
        {
            var editor = page.GetLogicalDescendants().OfType<LabeledSlider>()
                .Single(control => AutomationProperties.GetName(control) == label);
            var slider = editor.FindControl<Slider>("SliderPart")!;
            var saves = provider.SaveAttempts;
            var previous = saved(provider.Get());
            slider.Value = 30;
            slider.Value = 40;
            Pump();
            Check(preview() == 40 && provider.SaveAttempts == saves && saved(Disk(path)) == previous,
                $"{label}: multiple real slider changes preview draft without saving");
            slider.RaiseEvent(new VectorEventArgs { RoutedEvent = Thumb.DragCompletedEvent });
            Pump();
            Check(provider.SaveAttempts == saves + 1 && saved(Disk(path)) == 40,
                $"{label}: completion persists once through shared slider session");
            slider.RaiseEvent(new VectorEventArgs { RoutedEvent = Thumb.DragCompletedEvent });
            Check(provider.SaveAttempts == saves + 1, $"{label}: duplicate completion stays silent");
        }

        vm.GlassBlur = 45;
        vm.AccentColorHex = "#FFCC5522";
        var beforeConflict = provider.SaveAttempts;
        Check(vm.GlassBlur == 45 && !vm.CommitThemeSliders() && provider.SaveAttempts == beforeConflict
            && vm.SettingsError == Locale.Settings_Appearance_SaveConflict,
            "optics draft survives another control save and reports conflict on completion");
        vm.ReloadSettings();
        Check(vm.GlassBlur == 40 && !vm.HasSettingsError, "explicit reload resets optics preview after conflict");
    }

    private static void CheckWindowScopes(Theme theme)
    {
        var first = new Window { Content = new Border { Background = Brushes.Transparent } };
        var second = new Window { Content = new Border { Background = Brushes.Transparent } };
        first.Show(); second.Show();
        var global = Application.Current!.Resources["BackgroundOpacity"];
        ThemeResourceHelper.WriteScopeCardResources(first.Resources, theme with { OpacityLevel = 0.2 });
        ThemeResourceHelper.WriteScopeCardResources(second.Resources, theme with { OpacityLevel = 0.9 });
        ThemeService.ApplyAccentRamp(first.Resources, theme with { AccentColor = "#FF112233", Monochrome = false }, false);
        ThemeService.ApplyAccentRamp(second.Resources, theme with { AccentColor = "#FFEECC99", Monochrome = false }, true);
        Check(first.TryFindResource("BackgroundOpacity", ThemeVariant.Light, out var a) && Equals(a, 0.2)
            && second.TryFindResource("BackgroundOpacity", ThemeVariant.Dark, out var b) && Equals(b, 0.9),
            "two real windows resolve independent scoped opacity resources");
        var secondAccent = second.Resources["SystemAccentColor"];
        ThemeService.ApplyAccentRamp(first.Resources, theme with { AccentColor = "#FFFF0000", Monochrome = false }, false);
        Check(Equals(second.Resources["SystemAccentColor"], secondAccent)
            && Equals(Application.Current.Resources["BackgroundOpacity"], global),
            "updating first window scope never leaks into second scope or application opacity");
        first.Close(); second.Close();
    }

    private static void Pump() => Dispatcher.UIThread.RunJobs();
    private static AppSettings Disk(string path) => new JsonParser<AppSettings>(path).Get();
    private static AppSettings InitialSettings() => new(
        new Theme(false, null, 0.4, false, false, "Inter", SurfaceStyle.Acrylic), [],
        new Layout(GridMode.Manual, true, false, true, false), new Dimensions(80, 12, 16),
        new Region("en"), false, null);

    private sealed class FileSettings(string path) : JsonParser<AppSettings>(path), IAppSettingsProvider
    {
        public int SaveAttempts { get; private set; }
        public override void Save(AppSettings value)
        {
            SaveAttempts++;
            base.Save(value);
        }
    }
    private sealed class ProbeApp : DeskCanvas.App
    {
        public override void OnFrameworkInitializationCompleted() { }
    }
}
