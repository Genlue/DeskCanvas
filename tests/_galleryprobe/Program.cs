using System.Diagnostics;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using ShapePath = Avalonia.Controls.Shapes.Path;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using DeskCanvas.Core;
using DeskCanvas.Core.Interfaces;
using DeskCanvas.Core.Models;
using DeskCanvas.Core.Models.Attributes;
using DeskCanvas.Core.Models.Settings;
using DeskCanvas.Core.Services;
using DeskCanvas.Services;
using DeskCanvas.Views;
using DeskCanvas.Views.Pages;
using Microsoft.Extensions.DependencyInjection;

// GalleryProbe: end-to-end reproduction of the 设置 → 全部组件 crash.
//
// It builds the REAL gallery page (same page, same page-cache-free construction) with every
// built widget assembly, hosts it in a headless window, and keeps pumping layout/render for a
// few seconds so the previews' async refreshes — the aggregate card's weather fetch in
// particular — land while the page is live. That is the moment the defect fired: the weather
// service parsed its icon geometry on a pool thread, the card bound it to a Path, and the next
// layout pass threw "The calling thread cannot access this object because a different thread
// owns it" from Shape.MeasureOverride — inside the render pass, unhandled, process gone.
//
// On the pre-fix code this process dies with that exact exception; on the fixed code it prints
// PASS lines and exits 0.
//
// Run: dotnet run --project tests/_galleryprobe -c Release [-- <seconds>]
// Requires a prior build (it copies src/DeskCanvas/bin/Debug/net10.0/Widgets).

var seconds = args.Length > 0 && int.TryParse(args[0], out var parsed) ? parsed : 14;

var repoRoot = Directory.GetCurrentDirectory();
// The probe must load widgets built against the SAME host configuration it runs with.
var configuration = AppContext.BaseDirectory.Contains($"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}")
    ? "Release"
    : "Debug";
var widgetsSource = new[] { configuration, configuration == "Release" ? "Debug" : "Release" }
    .Select(config => Path.Combine(repoRoot, "src", "DeskCanvas", "bin", config, "net10.0", "Widgets"))
    .FirstOrDefault(Directory.Exists);
if (widgetsSource == null)
{
    Console.WriteLine($"missing widget build output under src/DeskCanvas/bin/{configuration}/net10.0/Widgets (build src/DeskCanvas.sln first)");
    return 2;
}
Console.WriteLine($"widgets from: {widgetsSource}");

// A throwaway data folder keeps the probe away from %LocalAppData%\DeskCanvas. The widget DLLs
// are copied in because Const.WidgetsFolder is derived from the data folder.
var sandbox = Path.Combine(Path.GetTempPath(), "DeskCanvas-GalleryProbe-" + Guid.NewGuid().ToString("N")[..8]);
var widgetsTarget = Path.Combine(sandbox, "Widgets");
Directory.CreateDirectory(widgetsTarget);
foreach (var file in Directory.GetFiles(widgetsSource, "*.dll"))
    File.Copy(file, Path.Combine(widgetsTarget, Path.GetFileName(file)), true);
Environment.SetEnvironmentVariable("DESCANVAS_DATA_FOLDER", sandbox);

var crashes = new List<string>();
AppDomain.CurrentDomain.UnhandledException += (_, e) => crashes.Add("AppDomain: " + e.ExceptionObject);
TaskScheduler.UnobservedTaskException += (_, e) => crashes.Add("Task: " + e.Exception);

AppBuilder.Configure<Application>()
    .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
    .UseSkia()
    .WithInterFont()
    .SetupWithoutStarting();
Application.Current!.Styles.Add(new FluentTheme());

void Flush() => Dispatcher.UIThread.RunJobs();

// The real exe has every host assembly loaded before any widget resolves its own references;
// this probe references them lazily, so bind them into the default load context first. Without
// this the widget ALC resolves its own copy of DeskCanvas.Runtime and the widget ctors fail with
// "Method not found" on SharedDataServices.
_ = typeof(Const).Assembly;
_ = typeof(DeskCanvas.Runtime.OpenMeteoSharedProvider).Assembly;
_ = typeof(DeskCanvas.Contracts.IWidgetSession).Assembly;
_ = typeof(DeskCanvas.UI.Controls.PanelDragBehavior).Assembly;

// The real startup seeds the profile directory and the app settings file; a missing directory
// turns the handled first-run case into a DirectoryNotFoundException from LayoutProvider.Get(),
// and Notes/Stack read Const.AppSettingsFile directly in their constructors.
Directory.CreateDirectory(Path.GetDirectoryName(Const.LayoutFile)!);

var settings = new AppSettingsProvider();
// Seed the profile files the real startup writes; JsonParser.Get() does not fall back to
// defaults on a missing file, and Notes/Stack read Const.AppSettingsFile in their constructors.
if (!File.Exists(Const.AppSettingsFile))
{
    settings.Save(new AppSettings(
        new Theme(false, null, 1, false, false, "Inter"),
        [], new Layout(GridMode.Manual, false, false, false, false), new Dimensions(100, 10, 16),
        new Region("en"), false, null));
}

var layout = new LayoutProvider();
var monitor = new DisplayMonitorService(layout);
typeof(DisplayMonitorService).GetField("refreshing", BindingFlags.NonPublic | BindingFlags.Instance)!
    .SetValue(monitor, true);

var screenHost = new Window { Width = 100, Height = 100, ShowInTaskbar = false };
screenHost.Show();
Flush();
monitor.Attach(screenHost);
Flush();
typeof(DisplayMonitorService).GetProperty("IsTopologyChanging")!.SetValue(monitor, false);

var assemblyProvider = new AssemblyProviderStub();
var widgetFactory = new WidgetFactory(assemblyProvider, layout, monitor, () => null!, settings);

var services = new ServiceCollection()
    .AddSingleton<IAppSettingsProvider>(settings)
    .AddSingleton<ILayoutProvider>(layout)
    .AddSingleton<IAssemblyProvider>(assemblyProvider)
    .AddSingleton<WallpaperThemeService>()
    .AddSingleton<WallpaperWatcherService>()
    .AddSingleton<IThemeService>(new ThemeService(settings, new WallpaperThemeService()))
    .AddSingleton<ILocaleService>(new LocaleService(settings))
    .AddSingleton<IGridService<Widget>, GridService>()
    .AddSingleton(monitor)
    .AddSingleton(widgetFactory)
    .AddSingleton<IWidgetFactory<Window, UserControl>>(widgetFactory)
    .AddSingleton<Func<Settings>>(() => null!)
    .BuildServiceProvider();
AssemblyProviderStub.Services = services;

var infos = assemblyProvider.GetAssemblyInfos(Const.WidgetsFolder).SelectMany(group => group).ToList();

// The gallery swallows a preview that will not build; report those here so a missing card is
// never mistaken for a passing layout.
var failedPreviews = new List<string>();
foreach (var info in infos)
{
    try
    {
        var assembly = assemblyProvider.LoadAssembly(info.AssemblyName);
        foreach (var viewType in assembly.GetCustomAttributes<WidgetInfoAttribute>().Select(a => a.ViewType))
            widgetFactory.CreateControl(viewType);
    }
    catch (Exception ex)
    {
        failedPreviews.Add($"{info.AssemblyName}: {ex.GetType().Name}: {ex.Message}");
    }
}
foreach (var failure in failedPreviews) Console.WriteLine($"preview failed: {failure}");

var gallery = new Gallery(settings, layout, assemblyProvider, infos, widgetFactory, monitor);
var window = new Window { Width = 1200, Height = 900, Content = gallery };
window.Show();
Flush();

var previews = gallery.Widgets;
var aggregate = previews.FirstOrDefault(preview => preview.Subtype.Contains("Aggregate"));
var aggregateVm = aggregate?.Control.DataContext;
object? AggregateProperty(string name) => aggregateVm?.GetType().GetProperty(name)?.GetValue(aggregateVm);

Console.WriteLine($"assemblies: {infos.Count}, previews: {previews.Count}, aggregate preview: {aggregate != null}");
Console.WriteLine($"aggregate weather before pumping: temp={AggregateProperty("TemperatureText")}");

var failures = 0;
void Check(bool condition, string message)
{
    Console.WriteLine($"  [{(condition ? "PASS" : "FAIL")}] {message}");
    if (!condition) failures++;
}

var elapsed = Stopwatch.StartNew();
try
{
    while (elapsed.Elapsed < TimeSpan.FromSeconds(seconds))
    {
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Flush();
        Thread.Sleep(25);
    }
    Check(true, $"{seconds}s of layout/render on the live gallery page, no exception");
}
catch (Exception ex)
{
    Check(false, $"layout/render threw: {ex.GetType().Name}: {ex.Message}");
}

var icon = AggregateProperty("WeatherIcon") as Geometry;
Console.WriteLine($"aggregate weather after pumping: temp={AggregateProperty("TemperatureText")}, icon={icon?.GetType().Name ?? "null"}");

try
{
    new ShapePath { Data = icon, Width = 24, Height = 24 }.Measure(new Size(24, 24));
    Check(icon != null, "the live aggregate card's weather icon measures on the UI thread");
}
catch (Exception ex)
{
    Check(false, $"the live aggregate card's weather icon threw on measure: {ex.GetType().Name}: {ex.Message}");
}

foreach (var crash in crashes) Console.WriteLine($"CRASH RECORDED: {crash}");
Console.WriteLine(failures == 0 && crashes.Count == 0 ? "GALLERY PROBE: NO CRASH" : "GALLERY PROBE: CRASH");
return failures == 0 && crashes.Count == 0 ? 0 : 1;

internal sealed class AssemblyProviderStub : IAssemblyProvider
{
    private readonly Lazy<AssemblyProvider> inner = new(() => new AssemblyProvider(Services));

    public static IServiceProvider Services { get; set; } = null!;

    public ILookup<string, AssemblyInfo> GetAssemblyInfos(string directoryPath) => inner.Value.GetAssemblyInfos(directoryPath);

    public Assembly LoadAssembly(string name) => inner.Value.LoadAssembly(name);

    public void UnloadAssembly(string name) => inner.Value.UnloadAssembly(name);

    public object Activate(Type type, params object[] args) => inner.Value.Activate(type, args);

    public System.Resources.ResourceManager? GetLocaleResourceManager(Assembly assembly) => inner.Value.GetLocaleResourceManager(assembly);
}
