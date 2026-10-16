using System.Diagnostics;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Timetable.Models;
using Timetable.Services;

namespace TimetableMemoryProbe;

/// <summary>
/// Memory probe: quantifies what one Timetable widget instance actually costs, compared with
/// an equally sized plain control, across construction, first render, repeated rebuilds and
/// repeated repaints. Reports managed heap, GC committed bytes and process working set so the
/// "one widget = +90 MB" observation can be attributed to managed allocations, native (Skia/
/// font) caches, or something outside the control entirely.
/// </summary>
internal static class Program
{
    private static readonly List<Control> KeepAlive = [];

    [STAThread]
    private static void Main(string[] args)
    {
        // Isolation: the harness must never touch the live %LocalAppData%\DeskCanvas store.
        Environment.SetEnvironmentVariable("DESCANVAS_DATA_FOLDER",
            Path.Combine(Path.GetTempPath(), "DeskCanvas-MemProbe-Data"));

        AppBuilder.Configure<Application>()
            .UsePlatformDetect()
            .SetupWithoutStarting();

        var app = Application.Current!;
        app.Styles.Add(new FluentTheme());
        app.RequestedThemeVariant = ThemeVariant.Dark;

        var model = LoadSampleModel();
        TimetableStore.ResetForTesting(model);

        var scenario = args.Length > 0 ? args[0] : "full";

        // A/B harness for the fractional-font-size fix: native glyph caches do not survive
        // process exit, so "before" and "after" must run in separate processes.
        //   churn-raw        30 arbitrary fractional CJK sizes (the old behaviour)
        //   churn-quantized  the same range quantised to 0.5 px steps (the fix)
        //   full             the widget scenarios
        if (scenario == "churn-raw" || scenario == "churn-quantized")
        {
            Console.WriteLine($"=== Glyph churn ({scenario}) ===");
            RenderOffscreen(new TextBlock { Text = "warm" }, 380, 380, "warm");
            var churnBaseline = Snapshot("baseline");
            GlyphChurn(churnBaseline, quantize: scenario == "churn-quantized");
            Snapshot("final");
            return;
        }

        Console.WriteLine($"=== Baseline (process warm, theme loaded) ===");
        RenderOffscreen(new TextBlock { Text = "warm" }, 380, 380, "warm");
        var baseline = Snapshot("baseline");

        Console.WriteLine();
        Console.WriteLine("=== A. Plain card control (Border + 6 TextBlocks), 380x380, rendered 2x ===");
        RenderPlainCardRepeatedly(baseline);

        Console.WriteLine();
        Console.WriteLine("=== A2. Plain card, same 10-resize-bucket stress (platform cost control) ===");
        PlainCardResizeStress(baseline);

        Console.WriteLine();
        Console.WriteLine("=== A3. Rebuild decomposition: what leaks across 10 rebuilds at fixed size? ===");
        RebuildStress(baseline, variant: "borders-only");
        RebuildStress(baseline, variant: "english-text");
        RebuildStress(baseline, variant: "chinese-text");
        RebuildStress(baseline, variant: "chinese-plus-tooltip");
        RebuildStress(baseline, variant: "chinese-same-ref");

        Console.WriteLine();
        Console.WriteLine("=== B. Timetable 4x4, first build + render ===");
        RenderTimetableRepeatedly(model, baseline);

        Console.WriteLine();
        Console.WriteLine("=== C. Glyph-size churn (30 fractional font sizes, Chinese text) ===");
        GlyphChurn(baseline, quantize: false);

        Console.WriteLine();
        Console.WriteLine("=== D. Live state ticks (60 x UpdateLiveState-equivalent repaints) ===");
        LiveTickChurn(model, baseline);

        Console.WriteLine();
        Console.WriteLine("=== Final summary ===");
        // Give the compositor batch and finalizer queue time to flush the retired visuals,
        // then measure once more: a falling number means the churn is release-lag, not a leak.
        Thread.Sleep(3000);
        GC.Collect(2, GCCollectionMode.Forced, true, true);
        GC.WaitForPendingFinalizers();
        Thread.Sleep(1000);
        Snapshot("final after settle");
    }

    // ---------- Scenarios ----------

    private static void RenderPlainCardRepeatedly(Mem baseline)
    {
        var card = BuildPlainCard();
        KeepAlive.Add(card);
        var window = OpenOffscreen(card, 380, 380);
        try
        {
            RunLayoutJobs(window);
            RenderToBitmap(card, 380, 380);
            RenderToBitmap(card, 380, 380); // second frame on the same live tree
            Snapshot("plain card, 2 frames", baseline);
        }
        finally
        {
            window.Close();
        }
    }

    private static readonly string SameRefTitle = "数据结构与算法分析（第3讲）";
    private static readonly string SameRefSub = "实验楼3室 · 张老师";

    /// <summary>
    /// Simulates the week grid's rebuild loop (Children.Clear + fresh control tree + measure)
    /// at a FIXED window size, with the content varied to bisect which piece retains native
    /// memory: layout alone, shaped text (en/zh), or the per-block tooltips. The
    /// chinese-same-ref variant assigns the identical string instances every round — the
    /// property system must then treat the rebind as a no-op — isolating "text changed"
    /// from "control re-added".
    /// </summary>
    private static void RebuildStress(Mem baseline, string variant)
    {
        var grid = new Grid();
        var card = HostAsCard(grid);
        var window = OpenOffscreen(card, 380, 380);
        try
        {
            for (var round = 0; round < 10; round++)
            {
                grid.Children.Clear();
                grid.RowDefinitions.Clear();
                for (var i = 0; i < 8; i++)
                    grid.RowDefinitions.Add(new RowDefinition { Height = new global::Avalonia.Controls.GridLength(28) });

                for (var i = 0; i < 40; i++)
                {
                    var sameRef = variant == "chinese-same-ref";
                    var border = new Border
                    {
                        Background = ScheduleBlocksBrush(i),
                        CornerRadius = new CornerRadius(6),
                        Margin = new Thickness(1),
                        Child = variant == "borders-only" ? null : new StackPanel
                        {
                            Children =
                            {
                                new TextBlock
                                {
                                    Text = variant switch
                                    {
                                        "english-text" => $"Algebra and Linear Systems {i % 7}",
                                        "chinese-text" or "chinese-plus-tooltip" => $"数据结构与算法分析（第{i % 7}讲）",
                                        "chinese-same-ref" => SameRefTitle,
                                        _ => "",
                                    },
                                    FontSize = 12,
                                    TextTrimming = TextTrimming.CharacterEllipsis,
                                },
                                new TextBlock
                                {
                                    Text = variant switch
                                    {
                                        "english-text" => $"Room {i} · Teacher {i}",
                                        "chinese-text" or "chinese-plus-tooltip" => $"实验楼{i % 9}室 · 张老师",
                                        "chinese-same-ref" => SameRefSub,
                                        _ => "",
                                    },
                                    FontSize = 10,
                                    Opacity = 0.88,
                                },
                            },
                        },
                    };
                    if (variant == "chinese-plus-tooltip")
                        ToolTip.SetTip(border, $"高等数学｜星期一 · 第 {i % 8 + 1} 节 · 1-16 周\n实验楼 404");
                    global::Avalonia.Controls.Grid.SetRow(border, i % 8);
                    grid.Children.Add(border);
                }

                RunLayoutJobs(window);
            }

            Snapshot($"10 rebuilds × 40 cells ({variant})", baseline);
        }
        finally
        {
            window.Close();
        }
    }

    private static IBrush ScheduleBlocksBrush(int i) => new Avalonia.Media.Immutable.ImmutableSolidColorBrush(
        Color.FromRgb((byte)(0x5B + i * 7 % 120), (byte)(0x8F + i * 13 % 100), (byte)(0xF9 - i * 5 % 100)));

    private static void PlainCardResizeStress(Mem baseline)
    {
        var card = BuildPlainCard();
        KeepAlive.Add(card);
        var window = OpenOffscreen(card, 380, 380);
        try
        {
            RunLayoutJobs(window);
            RenderToBitmap(card, 380, 380);
            for (var i = 1; i <= 10; i++)
            {
                window.Width = 380 + i * 5;
                window.Height = 380 + i * 5;
                RunLayoutJobs(window);
            }
            RenderToBitmap(card, 430, 430);
            Snapshot("plain card after identical resize stress", baseline);
        }
        finally
        {
            window.Close();
        }
    }

    private static void RenderTimetableRepeatedly(TimetableModel model, Mem baseline)
    {
        var view = new Timetable.Views.Timetable(model);
        var card = HostAsCard(view);
        KeepAlive.Add(card);

        // Keep the window open across the whole scenario: build → render → resize-bucket
        // rebuilds → repaint frames, then measure once per stage.
        var window = OpenOffscreen(card, 380, 380);
        try
        {
            RunLayoutJobs(window);
            RenderToBitmap(card, 380, 380);
            Snapshot("timetable build+render", baseline);

            // Resize-bucket rebuilds: OnSizeChanged rebuilds every 5 px — simulate a drag.
            for (var i = 1; i <= 10; i++)
            {
                window.Width = 380 + i * 5;
                window.Height = 380 + i * 5;
                RunLayoutJobs(window);
            }
            Snapshot("timetable after 10 resize-bucket rebuilds", baseline);

            // Repaint churn: render the same control 10 more times (frames).
            for (var i = 0; i < 10; i++)
                RenderToBitmap(card, 405 + i, 405);
            Snapshot("timetable after 10 more frames", baseline);
        }
        finally
        {
            window.Close();
        }
    }

    private static void GlyphChurn(Mem baseline, bool quantize)
    {
        // The week grid computes font sizes with Math.Clamp from live geometry, producing
        // arbitrary fractional sizes. Render 30 consecutive sizes of the same Chinese string:
        // raw 0.13 px steps (the old behaviour) vs the fix's 0.5 px quantisation.
        var panel = new StackPanel();
        for (var i = 0; i < 30; i++)
        {
            var size = 9.5f + i * 0.13f;
            panel.Children.Add(new TextBlock
            {
                Text = "数据结构与算法分析 高级语言程序设计（Python）",
                FontSize = quantize ? Quantize(size) : size,
                FontWeight = i % 2 == 0 ? FontWeight.SemiBold : FontWeight.Normal,
            });
        }
        var card = HostAsCard(panel);
        KeepAlive.Add(card);
        RenderOffscreen(card, 380, 380, "glyph-churn");
        Snapshot(quantize ? "30 quantized CJK font sizes" : "30 fractional CJK font sizes", baseline);
    }

    private static void LiveTickChurn(TimetableModel model, Mem baseline)
    {
        // The shared one-minute timer only mutates properties (no rebuild), but each mutation
        // invalidates and repaints. Simulate 60 minutes of ticks on the built widget.
        var view = KeepAlive.OfType<Timetable.Views.Timetable>().FirstOrDefault();
        if (view == null) return;
        for (var i = 0; i < 60; i++)
        {
            view.InvalidateVisual();
            Dispatcher.UIThread.RunJobs();
        }
        Snapshot("60 property-tick repaints", baseline);
    }

    // ---------- Helpers ----------

    private static Border BuildPlainCard()
    {
        var stack = new StackPanel { Spacing = 2 };
        foreach (var text in new[] { "星期一", "高等数学", "08:00–09:40", "星期二", "大学英语", "10:00–11:40" })
            stack.Children.Add(new TextBlock { Text = text, FontSize = 12 });
        return new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(43, 43, 48)),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(4),
            Child = stack,
        };
    }

    private static Border HostAsCard(Control content) => new()
    {
        CornerRadius = new CornerRadius(16),
        Background = new SolidColorBrush(Color.FromRgb(43, 43, 48)),
        ClipToBounds = true,
        Padding = new Thickness(12),
        Child = content,
    };

    private static Window OpenOffscreen(Control content, double width, double height)
    {
        var window = new Window
        {
            SystemDecorations = SystemDecorations.None,
            ShowInTaskbar = false,
            ShowActivated = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Position = new PixelPoint(-32000, -32000),
            Width = width,
            Height = height,
            Background = Brushes.Transparent,
            Content = content,
        };
        window.Show();
        return window;
    }

    private static void RunLayoutJobs(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
    }

    private static void RenderToBitmap(Control content, double width, double height)
    {
        var pixelW = (int)Math.Ceiling(width * 2);
        var pixelH = (int)Math.Ceiling(height * 2);
        using var rtb = new RenderTargetBitmap(new PixelSize(pixelW, pixelH), new Vector(192, 192));
        rtb.Render(content);
    }

    private static void RenderOffscreen(Control content, double width, double height, string name, bool quiet = false)
    {
        var window = OpenOffscreen(content, width, height);
        try
        {
            RunLayoutJobs(window);
            RenderToBitmap(content, width, height);
        }
        finally
        {
            window.Close();
        }

        if (!quiet) Console.WriteLine($"  rendered {name} ({(int)width}x{(int)height})");
    }

    /// <summary>Mirror of Timetable.Services.TextMetrics.Quantize (internal to the widget).</summary>
    private static double Quantize(double size) =>
        Math.Round(size * 2.0, MidpointRounding.AwayFromZero) / 2.0;

    private sealed record Mem(long WorkingSet, long Private, long GcHeap, long Committed, long Fragmented);

    private static Mem Snapshot(string label, Mem? baseline = null)
    {
        GC.Collect(2, GCCollectionMode.Forced, true, true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Forced, true, true);
        GC.WaitForPendingFinalizers();

        var info = GC.GetGCMemoryInfo();
        var mem = new Mem(
            Process.GetCurrentProcess().WorkingSet64,
            Process.GetCurrentProcess().PrivateMemorySize64,
            GC.GetTotalMemory(forceFullCollection: false),
            info.TotalCommittedBytes,
            info.FragmentedBytes);

        static string Mb(long bytes) => $"{bytes / 1024.0 / 1024.0:F1} MB";

        if (baseline == null)
        {
            Console.WriteLine($"  [{label}] workingSet={Mb(mem.WorkingSet)} private={Mb(mem.Private)} " +
                              $"gcHeap={Mb(mem.GcHeap)} gcCommitted={Mb(mem.Committed)} fragmented={Mb(mem.Fragmented)}");
        }
        else
        {
            Console.WriteLine($"  [{label}] ΔworkingSet={Mb(mem.WorkingSet - baseline.WorkingSet)} " +
                              $"Δprivate={Mb(mem.Private - baseline.Private)} ΔgcHeap={Mb(mem.GcHeap - baseline.GcHeap)} " +
                              $"ΔgcCommitted={Mb(mem.Committed - baseline.Committed)}");
        }

        return mem;
    }

    private static TimetableModel LoadSampleModel()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "sample-timetable.json");
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory);
             dir != null && !File.Exists(path);
             dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "sample-timetable.json");
            if (File.Exists(candidate)) path = candidate;
        }

        var result = TimetableImporter.ImportFileAsync(path).GetAwaiter().GetResult();
        var model = result.Model ?? throw new InvalidOperationException("sample import failed");
        Console.WriteLine($"Model: {model.CourseCount} courses, {model.SlotCount} slots, {model.Periods?.Count} periods");
        return model.Normalize();
    }
}
