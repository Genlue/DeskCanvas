// GlassShot: renders every widget view (all [WidgetInfo] registrations) inside the REAL host
// Widget window pipeline with the LiquidGlass surface style, and saves one PNG per view to
// dist/glass-shots/.
//
// Run: dotnet run --project tests/_glassshot -c Release   (optionally: -- <outputDir>)
//
// Probe, not a gate: like ShortcutsRender/_uiprobe this is a human-review harness. The rig
// mirrors tests/SessionHookChecks (real WidgetFactory + fake screen + headless Win32 HWNDs
// via UseHeadlessDrawing=false + UseSkia — real rendering, real capture).
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using DeskCanvas.Contracts;
using DeskCanvas.Core.Interfaces;
using DeskCanvas.Core.Models;
using DeskCanvas.Core.Models.Attributes;
using DeskCanvas.Core.Models.Settings;
using DeskCanvas.Core.Services;
using DeskCanvas.Runtime;
using DeskCanvas.Services;
using DeskCanvas.Views;
using Microsoft.Extensions.DependencyInjection;

var outDir = args.Length > 0 && args[0] != "seed" ? args[0] : Path.Combine("dist", "glass-shots");
const int SRCCOPY = 0x00CC0020;

// The headless loop uses a throwaway data folder; the seed mode uses the FIXED batch
// folder the real exe will be launched against (they must agree, or the exe sees no layout).
var sandbox = args.Length > 0 && args[0] == "seed"
    ? Path.Combine(Path.GetTempPath(), "DeskCanvas-GlassShot-batch")
    : Path.Combine(Path.GetTempPath(), "DeskCanvas-GlassShot-" + Guid.NewGuid().ToString("N")[..8]);
Environment.SetEnvironmentVariable("DESCANVAS_DATA_FOLDER", sandbox);

// ---- seed mode: write a legacy-format layout.json with every view on a desktop grid,
// for the REAL exe to migrate and render with the REAL glass pipeline (DWM + wallpaper).
if (args.Length > 0 && args[0] == "seed")
{
    var batch = args.Length > 1 ? int.Parse(args[1]) : 0;
    var layoutFile = (string?)typeof(DeskCanvas.Core.Const).GetField("LayoutFile")!.GetValue(null)!;
    var dataDir = Path.GetDirectoryName(layoutFile)!;
    Directory.CreateDirectory(dataDir);
    var flat = new List<(string Name, string View)>();
    foreach (var name in AllWidgetNames())
    {
        Assembly assembly;
        try { assembly = Assembly.Load(name); }
        catch { continue; }
        foreach (var info in assembly.GetCustomAttributes<WidgetInfoAttribute>())
            flat.Add((name, info.ViewType.Name));
    }
    // 1280x800-friendly batch grid: 3 columns x 4 rows.
    var slice = flat.Skip(batch * 12).Take(12).ToList();
    var entries = new List<WidgetLayout>();
    for (var i = 0; i < slice.Count; i++)
    {
        var x = 12 + i % 3 * 420;
        var y = 12 + i / 3 * 195;
        entries.Add(new WidgetLayout(slice[i].Name, slice[i].View, x, y, 400, 175, null));
        File.AppendAllText(Path.Combine(dataDir, "manifest.csv"),
            $"{slice[i].Name}_{slice[i].View},{x},{y},400,175{Environment.NewLine}");
    }
    var legacy = new ScreenLayout(ScreensLayout.LegacyPrimaryId, null, null, null, null, null, entries);
    new LayoutProvider().Save(new ScreensLayout([legacy]));
    Console.WriteLine($"seeded batch {batch}: {entries.Count} widget entries into {dataDir}");
    return 0;
}

string[] AllWidgetNames() =>
[
    "Batteries", "Calendar", "Clock", "ControlCenter", "Countdown", "Dice", "FixedWidgets",
    "Folders", "Habits", "Hitokoto", "Map", "Monitor", "Music", "Notes", "Picture",
    "Pomodoro", "Progress", "Reminders", "Search", "Shortcuts", "StackWidgets", "Storage",
    "Timetable", "Tools", "Weather"
];

// ---- list mode: enumerate windows of processes whose name contains the filter.
if (args.Length > 1 && args[0] == "list")
{
    var filter = args[1];
    var pids = new HashSet<uint>();
    foreach (var proc in System.Diagnostics.Process.GetProcessesByName(filter))
        pids.Add((uint)proc.Id);
    Console.WriteLine($"pids: {string.Join(',', pids)}");
    EnumWindows((hWnd, lp) =>
    {
        _ = GetWindowThreadProcessId(hWnd, out var pid);
        if (!pids.Contains(pid)) return true;
        var buf = new System.Text.StringBuilder(128);
        _ = GetClassName(hWnd, buf, 128);
        var titleBuf = new System.Text.StringBuilder(128);
        _ = GetWindowText(hWnd, titleBuf, 128);
        var r = new RECTS { Left = 0, Top = 0, Right = 0, Bottom = 0 };
        GetWindowRect(hWnd, ref r);
        Console.WriteLine($"hwnd=0x{hWnd.ToInt64():X} class={buf} vis={IsWindowVisible(hWnd)} rect=({r.Left},{r.Top})-({r.Right},{r.Bottom}) title='{titleBuf}'");
        return true;
    }, IntPtr.Zero);
    return 0;
}

Directory.CreateDirectory(outDir);

const int SW_MINIMIZE = 6;
const int SW_RESTORE = 9;
const int GWL_EXSTYLE_TASKBAR = -20;

[DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
[DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hWnd);
[DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
[DllImport("user32.dll")] static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder buf, int max);
[DllImport("user32.dll")] static extern bool IsIconic(IntPtr hWnd);
[DllImport("user32.dll")] static extern int GetWindowLong(IntPtr hWnd, int index);
[DllImport("user32.dll")] static extern bool ShowWindow(IntPtr hWnd, int cmd);

// ---- minimizeall/restoreall: clear the desktop so the bottom-band widget furniture is
// visible, and put everything back afterwards. Writes/reads a HWND list file.
if (args.Length > 1 && (args[0] == "minimizeall" || args[0] == "restoreall"))
{
    var restoring = args[0] == "restoreall";
    var self = (uint)Environment.ProcessId;
    var targets = new List<IntPtr>();
    EnumWindows((hWnd, lp) =>
    {
        if (!IsWindowVisible(hWnd)) return true;
        if (restoring) return true;
        var classBuf = new System.Text.StringBuilder(64);
        _ = GetClassName(hWnd, classBuf, 64);
        var cls = classBuf.ToString();
        if (cls is "Progman" or "WorkerW" or "Shell_TrayWnd") return true;
        _ = GetWindowThreadProcessId(hWnd, out var pid);
        if (pid == self) return true;
        if (IsIconic(hWnd)) return true;
        if (GetWindowLong(hWnd, GWL_EXSTYLE_TASKBAR) == 0 && cls == "Shell_SecondaryTrayWnd") return true;
        targets.Add(hWnd);
        return true;
    }, IntPtr.Zero);

    if (restoring)
    {
        foreach (var line in File.ReadAllLines(args[1]))
            if (long.TryParse(line, out var h) && h != 0)
                ShowWindow((IntPtr)h, SW_RESTORE);
        Console.WriteLine($"restored {File.ReadAllLines(args[1]).Length} windows");
        return 0;
    }

    var minimized = 0;
    foreach (var hWnd in targets)
    {
        if (ShowWindow(hWnd, SW_MINIMIZE)) minimized++;
        File.AppendAllText(args[1], $"{hWnd}{Environment.NewLine}");
    }
    Console.WriteLine($"minimized {minimized} windows -> {args[1]}");
    return 0;
}

// ---- shot mode: capture the primary screen via GDI (no Avalonia init) and write a BMP.
if (args.Length > 1 && args[0] == "shot")
{
    var w = GetSystemMetrics(0);
    var h = GetSystemMetrics(1);
    var screenDc = GetDC(IntPtr.Zero);
    var memDc = CreateCompatibleDC(screenDc);
    var bmp = CreateCompatibleBitmap(screenDc, w, h);
    var old = SelectObject(memDc, bmp);
    BitBlt(memDc, 0, 0, w, h, screenDc, 0, 0, SRCCOPY);
    SelectObject(memDc, old);

    // 24bpp top-down DIB
    var bi = new BITMAPINFOHEADER
    {
        biSize = 40, biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 24, biCompression = 0,
    };
    var pixelCount = w * h * 3;
    var pixels = Marshal.AllocHGlobal(pixelCount);
    GetDIBits(memDc, bmp, 0, (uint)h, pixels, ref bi, 0);
    var stride = (w * 3 + 3) & ~3;
    var row = new byte[stride];
    using var fs = new FileStream(args[1], FileMode.Create);
    fs.Write([0x42, 0x4D]);                                  // BM
    var dataSize = 54 + stride * h;
    fs.Write(BitConverter.GetBytes(dataSize));               // file size
    fs.Write([0, 0, 0, 0]);                                  // reserved
    fs.Write(BitConverter.GetBytes(54));                     // pixel offset
    fs.Write(BitConverter.GetBytes(40));                     // header size
    fs.Write(BitConverter.GetBytes(w));
    fs.Write(BitConverter.GetBytes(h));                      // positive = bottom-up rows
    fs.Write(BitConverter.GetBytes((short)1));
    fs.Write(BitConverter.GetBytes((short)24));
    fs.Write(BitConverter.GetBytes(0));                      // compression
    fs.Write(BitConverter.GetBytes(stride * h));             // image size
    fs.Write(BitConverter.GetBytes(2835)); fs.Write(BitConverter.GetBytes(2835));
    fs.Write(BitConverter.GetBytes(0)); fs.Write(BitConverter.GetBytes(0));
    for (var y = h - 1; y >= 0; y--)                          // bottom-up file wants last row first
    {
        Marshal.Copy(new IntPtr(pixels.ToInt64() + y * stride), row, 0, stride);
        fs.Write(row);
    }
    Marshal.FreeHGlobal(pixels);
    DeleteObject(bmp);
    DeleteDC(memDc);
    ReleaseDC(IntPtr.Zero, screenDc);
    Console.WriteLine($"captured {w}x{h} -> {args[1]}");
    return 0;
}

// ---- convert mode: BMP/PNG -> PNG through the Avalonia/Skia encoder.
if (args.Length > 2 && args[0] == "convert")
{
    AppBuilder.Configure<Application>()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
        .UseSkia()
        .SetupWithoutStarting();
    new Bitmap(args[1]).Save(args[2]);
    Console.WriteLine(File.Exists(args[2])
        ? $"converted {args[1]} -> {args[2]} ({new FileInfo(args[2]).Length} bytes)"
        : $"converted FAILED: {args[2]} not on disk");
    return 0;
}

AppBuilder.Configure<ProbeApp>()
    .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
    .UseSkia()
    .SetupWithoutStarting();

void Flush() => Dispatcher.UIThread.RunJobs();

// ---- rig (SessionHookChecks pattern) ------------------------------------------------------

var settings = new AppSettingsProvider();
var layout = new LayoutProvider();
var monitor = new DisplayMonitorService(layout);
typeof(DisplayMonitorService).GetField("refreshing", BindingFlags.NonPublic | BindingFlags.Instance)!
    .SetValue(monitor, true);

var screenHostWindow = new Window { Width = 100, Height = 100, ShowInTaskbar = false };
screenHostWindow.Show();
Flush();
var screen = screenHostWindow.Screens.All.First();
monitor.Attach(screenHostWindow);
// Attach flags a topology change; the settle that clears it runs on the dispatcher — run
// it now (and force-clear defensively) or Create() would return an empty set forever.
Flush();
typeof(DisplayMonitorService).GetProperty("IsTopologyChanging")!
    .SetValue(monitor, false);

// LiquidGlass surface for every shot.
settings.Save(new AppSettings(
    new Theme(false, null, 1, false, false, "Inter", Surface: SurfaceStyle.LiquidGlass),
    [], new Layout(GridMode.Manual, false, false, false, false), new Dimensions(100, 10, 16),
    new Region("en"), false, null, ActiveProfile: AppSettings.DefaultProfileName));

var assemblyProvider = new AssemblyProviderStub();
var widgetFactory = new WidgetFactory(assemblyProvider, layout, monitor, () => null!, settings);
var themeService = new ThemeService(settings, new WallpaperThemeService());
var localeService = new LocaleService(settings);
var profileService = new ProfileService(settings, layout, themeService, localeService, widgetFactory, monitor);

var services = new ServiceCollection()
    .AddSingleton<IAppSettingsProvider>(settings)
    .AddSingleton<ILayoutProvider>(layout)
    .AddSingleton<IAssemblyProvider>(assemblyProvider)
    .AddSingleton<WallpaperThemeService>()
    .AddSingleton<WallpaperWatcherService>()
    .AddSingleton<IThemeService>(themeService)
    .AddSingleton<ILocaleService>(localeService)
    .AddSingleton<IGridService<Widget>, GridService>()
    .AddSingleton(monitor)
    .AddSingleton(profileService)
    .AddSingleton(widgetFactory)
    .AddSingleton<IWidgetFactory<Window, UserControl>>(widgetFactory)
    .AddSingleton<Func<Settings>>(() => null!)
    .BuildServiceProvider();
AssemblyProviderStub.Services = services;

// ---- per-view shots -----------------------------------------------------------------------

var names = new[]
{
    "Batteries", "Calendar", "Clock", "ControlCenter", "Countdown", "Dice", "FixedWidgets",
    "Folders", "Habits", "Hitokoto", "Map", "Monitor", "Music", "Notes", "Picture",
    "Pomodoro", "Progress", "Reminders", "Search", "Shortcuts", "StackWidgets", "Storage",
    "Timetable", "Tools", "Weather"
};

var shot = 0;
var failed = 0;

foreach (var name in names)
{
    Assembly assembly;
    try
    {
        assembly = Assembly.Load(name);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[SKIP] {name}: assembly load failed — {ex.Message}");
        failed++;
        continue;
    }

    var registrations = assembly.GetCustomAttributes<WidgetInfoAttribute>().ToList();
    if (registrations.Count == 0)
    {
        Console.WriteLine($"[SKIP] {name}: no WidgetInfo registrations");
        failed++;
        continue;
    }

    foreach (var info in registrations)
    {
        var viewName = info.ViewType.Name;
        // A comfortable card size from the registration's default grid span (~96 px cells),
        // clamped to something readable on the card.
        var width = Math.Clamp(info.DefaultColumns * 96 + 16, 220, 780);
        var height = Math.Clamp(info.DefaultRows * 96 + 16, 150, 640);

        try
        {
            var instanceId = WidgetInstanceId.New();
            var entry = new WidgetLayout(name, viewName, 40, 40, width, height, null,
                InstanceId: instanceId.Value, Generation: 1);
            var screenConfig = new ScreenLayout(
                "probe-screen", "ProbeScreen|2560x1440", null, null, null, null, [entry]);
            layout.Save(new ScreensLayout([screenConfig]));

            // layout.Save raises DataChanged, which runs the monitor's reconciliation and
            // wipes the injected Attached set — re-inject AFTER the save (the
            // SessionHookChecks/WidgetReleaseChecks ordering).
            var attached = new AttachedScreen(
                screen,
                new ScreenIdentity("\\\\.\\PROBE1", "Probe Screen", screen.Bounds.Width, screen.Bounds.Height,
                    screen.IsPrimary, screen.Scaling, "PROBE", "PROBE0"),
                screenConfig);
            typeof(DisplayMonitorService).GetProperty("Attached")!
                .SetValue(monitor, new List<AttachedScreen> { attached });
            typeof(DisplayMonitorService).GetProperty("IsTopologyChanging")!
                .SetValue(monitor, false);

            WidgetSessionRegistry.ResetForChecks();
            var created = widgetFactory.Create().ToList();
            Flush();

            var widget = LiveWindow(widgetFactory);
            widget.Show();
            Flush();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Flush();

            var file = Path.Combine(outDir, $"{name}_{viewName}.png");
            var frame = widget.CaptureRenderedFrame();
            if (frame != null)
            {
                frame.Save(file);
                shot++;
                Console.WriteLine($"[OK] {name}/{viewName} -> {file}");
            }
            else
            {
                failed++;
                Console.WriteLine($"[FAIL] {name}/{viewName}: no rendered frame");
            }

            widgetFactory.CloseAll();
            Flush();
        }
        catch (Exception ex)
        {
            failed++;
            Console.WriteLine($"[FAIL] {name}/{viewName}: {ex.Message}");
            try { widgetFactory.CloseAll(); Flush(); } catch { }
        }
    }
}

Console.WriteLine($"\nGlassShot done: {shot} saved, {failed} skipped/failed -> {Path.GetFullPath(outDir)}");
return failed == 0 ? 0 : 1;


[DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr hWnd);
[DllImport("user32.dll")] static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
[DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr hdc);
[DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int width, int height);
[DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
[DllImport("gdi32.dll")] static extern bool BitBlt(IntPtr dst, int x, int y, int width, int height, IntPtr src, int sx, int sy, int rop);
[DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);
[DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr hdc);
[DllImport("gdi32.dll")] static extern int GetDIBits(IntPtr hdc, IntPtr hbmp, uint start, uint lines, IntPtr bits, ref BITMAPINFOHEADER bmi, uint usage);
[DllImport("user32.dll")] static extern int GetSystemMetrics(int index);
[DllImport("user32.dll")] static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder buf, int max);
[DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hWnd, ref RECTS r);

static Widget LiveWindow(WidgetFactory factory)
{
    var field = typeof(WidgetFactory).GetField("activeWidgets", BindingFlags.NonPublic | BindingFlags.Instance)!;
    var dict = (System.Collections.IDictionary)field.GetValue(factory)!;
    foreach (var bucket in dict.Values)
        foreach (Widget widget in (System.Collections.IEnumerable)bucket!)
            return widget;
    throw new InvalidOperationException("no live widget");
}

/// <summary>Loads widget assemblies by name (they sit in the probe output via project refs)
/// and activates through the same three-tier fallback semantics as AssemblyProvider.</summary>




sealed class AssemblyProviderStub : IAssemblyProvider
{
    public static IServiceProvider? Services;

    public System.Linq.ILookup<string, AssemblyInfo> GetAssemblyInfos(string directoryPath) =>
        Array.Empty<AssemblyInfo>().ToLookup(info => info.AssemblyName);

    public Assembly LoadAssembly(string assemblyName) => Assembly.Load(assemblyName);

    public void UnloadAssembly(string assemblyName) { }

    public object Activate(Type type, params object[] args)
    {
        // Same three-tier fallback as AssemblyProvider.Activate (DI-aware, reflection,
        // best-effort constructor matching).
        try
        {
            return ActivatorUtilities.CreateInstance(Services!, type, args);
        }
        catch
        {
            try
            {
                return args.Length == 0
                    ? Activator.CreateInstance(type)!
                    : Activator.CreateInstance(type, args)!;
            }
            catch
            {
                foreach (var ctor in type.GetConstructors().OrderByDescending(c => c.GetParameters().Length))
                {
                    var pars = ctor.GetParameters();
                    var values = new object?[pars.Length];
                    var ok = true;
                    for (var i = 0; i < pars.Length; i++)
                    {
                        var pt = pars[i].ParameterType;
                        var fromArgs = args.FirstOrDefault(a => a != null && pt.IsAssignableFrom(a.GetType()));
                        if (fromArgs != null)
                        {
                            values[i] = fromArgs;
                            continue;
                        }
                        var fromDi = Services?.GetService(pt);
                        if (fromDi != null)
                            values[i] = fromDi;
                        else if (pars[i].HasDefaultValue)
                            values[i] = pars[i].DefaultValue;
                        else
                        {
                            ok = false;
                            break;
                        }
                    }
                    if (ok)
                        return ctor.Invoke(values);
                }
                throw;
            }
        }
    }

    public System.Resources.ResourceManager? GetLocaleResourceManager(Assembly assembly) => null;
}

public struct RECTS { public int Left; public int Top; public int Right; public int Bottom; }

/// <summary>Loads the real App.axaml styles (DesignTokens, glass styles, control themes)
/// while skipping the whole DI/startup pipeline.</summary>
sealed class ProbeApp : DeskCanvas.App
{
    public override void OnFrameworkInitializationCompleted() { }
}
[StructLayout(LayoutKind.Sequential)]
struct BITMAPINFOHEADER
{
    public int biSize;
    public int biWidth;
    public int biHeight;
    public short biPlanes;
    public short biBitCount;
    public int biCompression;
    public int biSizeImage;
    public int biXPelsPerMeter;
    public int biYPelsPerMeter;
    public int biClrUsed;
    public int biClrImportant;
}

delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
