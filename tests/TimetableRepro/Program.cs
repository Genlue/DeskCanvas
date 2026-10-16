using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Timetable.Models;
using Timetable.Services;

namespace TimetableRepro;

/// <summary>
/// Reproduction harness: renders the Timetable widget view (2x2 tier) and the settings
/// page at various sizes to PNG so the reported truncation defects can be inspected
/// and the fix verified pixel-by-pixel.
/// </summary>
internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        // Isolation FIRST, before anything touches Const: the harness seeds the unified store
        // with the sample model; without the redirect the seeded store writes would land in
        // the packaged app's live %LocalAppData%\DeskCanvas folder.
        Environment.SetEnvironmentVariable("DESCANVAS_DATA_FOLDER",
            Path.Combine(Path.GetTempPath(), "DeskCanvas-Repro-Data"));

        var outDir = Path.GetFullPath(args.Length > 0 ? args[0] : "dist/timetable-repro");
        Directory.CreateDirectory(outDir);

        AppBuilder.Configure<Application>()
            .UsePlatformDetect()
            .SetupWithoutStarting();

        var app = Application.Current!;
        app.Styles.Add(new FluentTheme());
        app.RequestedThemeVariant = ThemeVariant.Dark;

        var model = LoadSampleModel();
        // The widget and settings views read the unified store; seed it so the renders
        // keep showing the sample data.
        TimetableStore.ResetForTesting(model);

        Console.WriteLine("=== Timetable 2x2 (today list) renders ===");
        foreach (var side in new[] { 152.0, 180.0, 200.0, 240.0, 260.0 })
            RenderWidget(outDir, model, side, side);

        Console.WriteLine();
        Console.WriteLine("=== Timetable 4x2 & 4x4 week grid renders ===");
        RenderWidget(outDir, model, 360, 180, "widget-4x2");
        RenderWidget(outDir, model, 360, 360, "widget-4x4");
        RenderWidget(outDir, model, 380, 190, "widget-4x2");
        RenderWidget(outDir, model, 380, 380, "widget-4x4");

        Console.WriteLine("=== Settings page renders (content widths) ===");
        foreach (var width in new[] { 340.0, 380.0, 420.0, 480.0, 560.0 })
            RenderSettings(outDir, model, width);

        Console.WriteLine();
        Console.WriteLine("=== TimePicker width probe ===");
        RenderTimePickerWidths(outDir);
        ProbeNaturalSizes();

        Console.WriteLine();
        Console.WriteLine("=== Hint text probe ===");
        ProbeHintReplica(outDir);

        Console.WriteLine();
        Console.WriteLine($"Output: {outDir}");
    }

    /// <summary>
    /// Is the settings "periods hint" text really broken, or is it a RenderTargetBitmap
    /// artifact? Render replicas of the exact TextBlock configurations and report sizes.
    /// </summary>
    private static void ProbeHintReplica(string outDir)
    {
        var text = "每节课的上下课时间——用于当前课程高亮与倒计时；导入的 JSON 中包含时会自动填入。点击行可展开编辑。";

        var panel = new StackPanel { Spacing = 14, Margin = new Thickness(16), Width = 420 };

        var caption = new TextBlock
        {
            Text = text,
            FontSize = 12,
            Opacity = 0.65,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(4, 0, 4, 0),
        };
        panel.Children.Add(caption);

        var plain = new TextBlock { Text = text, FontSize = 12, TextWrapping = TextWrapping.Wrap };
        panel.Children.Add(plain);

        var shorter = new TextBlock
        {
            Text = "每节课的上下课时间，点击行可展开编辑。",
            FontSize = 12,
            Opacity = 0.65,
            TextWrapping = TextWrapping.Wrap,
        };
        panel.Children.Add(shorter);

        var host = new Border { Background = new SolidColorBrush(Color.FromRgb(43, 43, 48)), Child = panel };
        var window = ShowOffscreen(host, 460, 300);
        try
        {
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();

            Console.WriteLine($"  caption: bounds={caption.Bounds} desired={caption.DesiredSize} fontSize={caption.FontSize}");
            Console.WriteLine($"  plain:   bounds={plain.Bounds} desired={plain.DesiredSize}");
            Console.WriteLine($"  shorter: bounds={shorter.Bounds} desired={shorter.DesiredSize}");

            Save(outDir, host, 460, 300, "probe-hint.png");
        }
        finally
        {
            window.Close();
        }

        // Variant B: the same caption inside a vertical ScrollViewer whose content
        // overflows (exactly how the settings page hosts these captions).
        var page = new StackPanel { Spacing = 4, Margin = new Thickness(4, 2, 8, 10) };
        var caption2 = new TextBlock
        {
            Text = text,
            FontSize = 12,
            Opacity = 0.65,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(4, 0, 4, 0),
        };
        page.Children.Add(new TextBlock { Text = "节次时间", FontSize = 13, FontWeight = FontWeight.SemiBold, Margin = new Thickness(2, 10, 0, 4) });
        page.Children.Add(caption2);
        page.Children.Add(new Border { Height = 700 }); // force the vertical scrollbar

        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            Content = page,
        };
        var host2 = new Border { Background = new SolidColorBrush(Color.FromRgb(43, 43, 48)), Child = scroll };
        var window2 = ShowOffscreen(host2, 460, 300);
        try
        {
            Dispatcher.UIThread.RunJobs();
            window2.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            window2.UpdateLayout();
            Dispatcher.UIThread.RunJobs();

            Console.WriteLine($"  caption2 (in ScrollViewer): bounds={caption2.Bounds} desired={caption2.DesiredSize} translate={caption2.TranslatePoint(new Point(0, 0), scroll)}");
            Save(outDir, host2, 460, 300, "probe-hint-scroll.png");
        }
        finally
        {
            window2.Close();
        }
    }

    private static TimetableModel LoadSampleModel()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "sample-timetable.json");
        // Fall back to the repo copy while running from the test output folder.
        if (!File.Exists(path))
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            for (var i = 0; i < 8 && dir != null; i++, dir = dir.Parent)
            {
                var candidate = Path.Combine(dir.FullName, "sample-timetable.json");
                if (File.Exists(candidate)) { path = candidate; break; }
            }
        }

        var result = TimetableImporter.ImportFileAsync(path).GetAwaiter().GetResult();
        var model = result.Model ?? throw new InvalidOperationException("sample import failed");
        Console.WriteLine($"Model: {model.CourseCount} courses, {model.SlotCount} slots, {model.Periods?.Count} periods");
        return model.Normalize();
    }

    /// <summary>Today (Tuesday) with realistic long names/rooms/teachers — the case from the report.</summary>
    private static TimetableModel BuildLongTextModel()
    {
        var now = DateTime.Now;
        var today = now.DayOfWeek == DayOfWeek.Sunday ? 7 : (int)now.DayOfWeek;
        var model = new TimetableModel(
            1, "长文本课表", null, 20, 0,
            [
                new TimetablePeriod("第1节", "08:00", "08:45"),
                new TimetablePeriod("第2节", "08:55", "09:40"),
                new TimetablePeriod("第3节", "10:00", "10:45"),
                new TimetablePeriod("第4节", "10:55", "11:40"),
            ],
            [
                new TimetableCourse("高级语言程序设计（Python）", "张伟明", "计算机实验楼 404", null,
                    [new TimetableEntry(today, [1, 2], "1-16")]),
                new TimetableCourse("移动应用开发实战", "李娜", "软件学院机房 A-302", null,
                    [new TimetableEntry(today, [3, 4], "1-16")]),
            ]);
        return model.Normalize();
    }

    // ---------- Widget (2x2 today list) ----------

    private static void RenderWidget(string outDir, TimetableModel model, double width, double height,
        string prefix = "widget-2x2")
    {
        var view = new Timetable.Views.Timetable(model);

        // Real host geometry: card Border (CornerRadius 16) + UserControl margin 12
        // (App.axaml .widget-content-host :is(UserControl) rule).
        var card = new Border
        {
            CornerRadius = new CornerRadius(16),
            Background = new SolidColorBrush(Color.FromRgb(43, 43, 48)),
            ClipToBounds = true,
            Padding = new Thickness(12),
            Child = view,
        };

        var host = new Border { Width = width, Height = height, Child = card };

        var window = ShowOffscreen(host, width, height, hostScrollPolicy: true);
        try
        {
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();

            DumpOverflow(view, width, height);
            Save(outDir, host, width, height, $"{prefix}-{(int)width}x{(int)height}.png");
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// The widget host's scrollbar policy, scoped to a widget WINDOW exactly like the real
    /// Widget.axaml styles: "Border:not(:pointerover) ScrollViewer" → Hidden/Hidden;
    /// hover → Auto/Hidden. Both leave HORIZONTAL at Hidden, i.e. card content is measured
    /// with an UNCONSTRAINED width — the host rule that turned long lesson text into
    /// over-wide colour blocks clipped at the card edge.
    /// </summary>
    private static Style HostScrollPolicy()
    {
        var style = new Style(x => x.OfType<ScrollViewer>());
        style.Setters.Add(new Setter(ScrollViewer.VerticalScrollBarVisibilityProperty,
            ScrollBarVisibility.Hidden));
        style.Setters.Add(new Setter(ScrollViewer.HorizontalScrollBarVisibilityProperty,
            ScrollBarVisibility.Hidden));
        return style;
    }

    /// <summary>Shows a real (off-screen) window so the layout pipeline runs for real.</summary>
    private static Window ShowOffscreen(Control content, double width, double height, bool hostScrollPolicy = false)
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
        if (hostScrollPolicy) window.Styles.Add(HostScrollPolicy());
        // RenderTargetBitmap + ClearType subpixel AA drops the body of some texts to bare
        // colour fringes in this off-screen harness (an artifact: the real app's GPU
        // compositor renders them normally — user screenshots show clean text). Grayscale
        // AA keeps the harness faithful to the LAYOUT, which is what it verifies.
        RenderOptions.SetTextRenderingMode(window, TextRenderingMode.Antialias);
        window.Show();
        return window;
    }

    // ---------- TimePicker width probe ----------

    private static void RenderTimePickerWidths(string outDir)
    {
        var widths = new double[] { 140, 150, 158, 160, 170 };
        var panel = new StackPanel { Spacing = 6, Margin = new Thickness(12) };
        foreach (var w in widths)
        {
            var row = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 8 };
            row.Children.Add(new TextBlock
            {
                Text = $"{(int)w}:",
                Width = 36,
                FontSize = 12,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            });
            var picker = new TimePicker
            {
                ClockIdentifier = "24HourClock",
                MinuteIncrement = 5,
                FontSize = 12,
                Width = w,
                SelectedTime = new TimeSpan(8, 0, 0),
            };
            row.Children.Add(picker);

            var picker2 = new TimePicker
            {
                ClockIdentifier = "24HourClock",
                MinuteIncrement = 5,
                FontSize = 12,
                Width = w,
                SelectedTime = new TimeSpan(9, 40, 0),
            };
            row.Children.Add(picker2);
            panel.Children.Add(row);
        }

        // A bare DatePicker probe too (the settings page pins Width=150 today).
        foreach (var w in new double[] { 210, 240, 260, 280 })
        {
            var row = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 8 };
            row.Children.Add(new TextBlock
            {
                Text = $"D{(int)w}:",
                Width = 40,
                FontSize = 12,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            });
            row.Children.Add(new DatePicker { Width = w, FontSize = 12, SelectedDate = DateTimeOffset.Now });
            panel.Children.Add(row);
        }

        var host = new Border { Background = new SolidColorBrush(Color.FromRgb(43, 43, 48)), Child = panel };
        var window = ShowOffscreen(host, 460, 700);
        try
        {
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            Save(outDir, host, 460, 620, "probe-timepicker-widths.png");
        }
        finally
        {
            window.Close();
        }
    }


    // ---------- Natural size probe ----------

    private static void ProbeNaturalSizes()
    {
        static Size Natural(Control c)
        {
            c.Measure(Size.Infinity);
            return c.DesiredSize;
        }

        var tp = new TimePicker
        {
            ClockIdentifier = "24HourClock",
            MinuteIncrement = 5,
            FontSize = 12,
            SelectedTime = new TimeSpan(8, 0, 0),
        };
        Console.WriteLine($"  TimePicker natural (FontSize 12): {Natural(tp).Width:F1} x {Natural(tp).Height:F1}");

        var tpMin = new TimePicker
        {
            ClockIdentifier = "24HourClock",
            MinuteIncrement = 5,
            FontSize = 12,
            MinWidth = 160,
            SelectedTime = new TimeSpan(8, 0, 0),
        };
        Console.WriteLine($"  TimePicker MinWidth=160 → {Natural(tpMin).Width:F1}");

        var tpMin150 = new TimePicker
        {
            ClockIdentifier = "24HourClock",
            MinuteIncrement = 5,
            FontSize = 12,
            MinWidth = 150,
            SelectedTime = new TimeSpan(8, 0, 0),
        };
        Console.WriteLine($"  TimePicker MinWidth=150 → {Natural(tpMin150).Width:F1}");

        var dp = new DatePicker { FontSize = 12, SelectedDate = DateTimeOffset.Now };
        Console.WriteLine($"  DatePicker natural (FontSize 12): {Natural(dp).Width:F1} x {Natural(dp).Height:F1}");

        var dpMin = new DatePicker { FontSize = 12, MinWidth = 220, SelectedDate = DateTimeOffset.Now };
        Console.WriteLine($"  DatePicker MinWidth=220 → {Natural(dpMin).Width:F1}");

        var label = new TextBlock { Text = "第 1 节", FontSize = 12 };
        Console.WriteLine($"  TextBlock \"第 1 节\" (12): {Natural(label).Width:F1}");

        var label2 = new TextBlock { Text = "开始", FontSize = 12 };
        Console.WriteLine($"  TextBlock \"开始\" (12): {Natural(label2).Width:F1}");

        // Real layout pass: an UNSET TimePicker shows "hour"/"min" placeholders (exactly the
        // face in the user's screenshot) whose natural width may exceed the numeric face.
        var emptyPicker = new TimePicker { ClockIdentifier = "24HourClock", MinuteIncrement = 5, FontSize = 12 };
        var valuePicker = new TimePicker
        {
            ClockIdentifier = "24HourClock", MinuteIncrement = 5, FontSize = 12,
            SelectedTime = new TimeSpan(8, 0, 0),
        };
        var probePanel = new StackPanel { Spacing = 8, Margin = new Thickness(10) };
        probePanel.Children.Add(emptyPicker);
        probePanel.Children.Add(valuePicker);
        var probeWindow = ShowOffscreen(probePanel, 400, 200);
        try
        {
            Dispatcher.UIThread.RunJobs();
            probeWindow.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            probeWindow.UpdateLayout();
            Dispatcher.UIThread.RunJobs();

            Console.WriteLine($"  UNSET TimePicker natural width: {emptyPicker.DesiredSize.Width:F1} (bounds {emptyPicker.Bounds.Width:F1})");
            Console.WriteLine($"  SET   TimePicker natural width: {valuePicker.DesiredSize.Width:F1} (bounds {valuePicker.Bounds.Width:F1})");
            foreach (var tb in emptyPicker.GetVisualDescendants().OfType<TextBlock>())
                Console.WriteLine($"    unset inner text \"{tb.Text}\" w={tb.Bounds.Width:F1} x={tb.Bounds.X:F1}");
        }
        finally
        {
            probeWindow.Close();
        }

        // TextPicker inside a Grid cell with a real window — what does the settings row need?
        Console.WriteLine($"  Window-scaling-agnostic check complete.");
    }


    // ---------- Settings page ----------

    private static void RenderSettings(string outDir, TimetableModel model, double width)
    {
        var stub = new StubLayoutProvider(new DeskCanvas.Core.Models.WidgetLayout(
            "Timetable", "Timetable", 0, 0, 200, 200, System.Text.Json.JsonSerializer.SerializeToElement(model)));
        var settings = new Timetable.Views.Settings.TimetableSettings(stub);

        // The page sits inside the settings host's ScrollViewer; emulate it.
        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            Content = settings,
        };

        var window = ShowOffscreen(scroll, width, 900);
        try
        {
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();

            DumpPeriodRowOverflow(settings, width);
            DumpControlOverflow(settings, width);
            DumpWideControls(settings, width);

            if (Math.Abs(width - 480) < 0.1) DyeCaptions(settings);

            Save(outDir, scroll, width, 900, $"settings-{(int)width}.png");

            // Second pass: unfold the first two period rows to inspect the editor layout,
            // scrolled down to the periods section.
            ExpandPeriodRows(settings, 2);
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();

            scroll.Offset = new Vector(0, 480);
            Dispatcher.UIThread.RunJobs();

            Save(outDir, scroll, width, 900, $"settings-expanded-{(int)width}.png");
        }
        finally
        {
            window.Close();
        }
    }

    private static void ExpandPeriodRows(object settings, int count)
    {
        var type = settings.GetType();
        var handles = type.GetField("periodRowHandles",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(settings)!;
        var setter = type.GetMethod("SetPeriodRowExpanded",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        foreach (var handle in ((System.Collections.IEnumerable)handles).Cast<object>().Take(count))
            setter.Invoke(settings, [handle, true]);
    }

    /// <summary>Walk and report every layoutable whose arranged box pokes out of the page width.</summary>
    private static void DumpControlOverflow(Control root, double pageWidth)
    {
        var offenders = new List<string>();
        void Walk(Visual visual)
        {
            foreach (var child in visual.GetVisualChildren())
            {
                if (child is not Control c) continue;
                var b = c.Bounds;
                if (b.Width > 0.5 && (b.Right > pageWidth + 0.5 || b.X < -0.5))
                {
                    var label = c is TextBlock tb ? $"\"{(tb.Text ?? "")[..Math.Min(24, tb.Text?.Length ?? 0)]}\"" :
                        c is Button btn ? $"content={btn.Content}" : "";
                    offenders.Add($"  {c.GetType().Name} {label} x={b.X:F1} w={b.Width:F1} right={b.Right:F1}");
                }
                if (child is Border or Grid or ScrollViewer or StackPanel or Panel)
                    Walk(child);
            }
        }
        Walk(root);
        if (offenders.Count == 0)
            Console.WriteLine($"  [overflow @{pageWidth:F0}] none");
        else
            Console.WriteLine($"  [overflow @{pageWidth:F0}] {offenders.Count} control(s):\n" + string.Join("\n", offenders.Take(30)));
    }

    /// <summary>Per-period rows: does each row's inner grid exceed the visible row width?</summary>
    private static void DumpPeriodRowOverflow(Control root, double pageWidth)
    {
        var panel = root.FindControl<StackPanel>("PeriodsPanel");
        if (panel == null) return;

        foreach (var (row, i) in panel.Children.OfType<Border>().Select((r, i) => (r, i)))
        {
            var childW = row.Child?.Bounds.Width ?? 0;
            var inner = row.Child?.DesiredSize.Width ?? 0;
            if (inner > row.Bounds.Width + 0.5 || childW > row.Bounds.Width + 0.5)
                Console.WriteLine($"  [period row {i}] row w={row.Bounds.Width:F1} child desired={inner:F1} arranged={childW:F1} (OVERFLOW)");
        }
    }

    /// <summary>Print the caption TextBlocks' real position/foreground (fragmentation forensics).</summary>
    private static void DyeCaptions(Control settings)
    {
        foreach (var tb in settings.GetVisualDescendants().OfType<TextBlock>())
        {
            var text = tb.Text ?? "";
            if (!text.Contains("每节课") && !text.StartsWith("已导入") && !text.Contains("显示周末")) continue;

            var pos = tb.TranslatePoint(new Point(0, 0), settings);
            var fg = tb.Foreground is ISolidColorBrush b ? b.Color.ToString() : tb.Foreground?.ToString() ?? "(null)";
            Console.WriteLine($"  [caption] \"{text[..Math.Min(14, text.Length)]}\" bounds={tb.Bounds} pagePos={pos} fg={fg} size={tb.FontSize} opacity={tb.Opacity}");
        }
    }

    /// <summary>The wide editors (pickers/steppers) and their host rows — where do they actually sit?</summary>
    private static void DumpWideControls(Control root, double pageWidth)
    {
        foreach (var control in root.GetVisualDescendants().OfType<Control>())
        {
            if (control is not (NumericUpDown or DatePicker or TimePicker)) continue;
            var b = control.Bounds;
            var flag = b.Right > pageWidth + 0.5 ? "  <== OVERFLOWS PAGE" : "";
            Console.WriteLine($"  [{control.GetType().Name}] x={b.X:F0} right={b.Right:F0} w={b.Width:F0} desired={control.DesiredSize.Width:F0}{flag}");
        }

        // The page's outer StackPanel: what width did it get arranged to?
        var outer = root.GetVisualDescendants().OfType<StackPanel>()
            .FirstOrDefault(p => p.Children.OfType<Control>().Any(c => c is DeskCanvas.Views.Controls.Setting));
        if (outer != null)
            Console.WriteLine($"  [page StackPanel] w={outer.Bounds.Width:F0} desired={outer.DesiredSize.Width:F0}");
    }

    private static void DumpOverflow(Control root, double width, double height)
    {
        var offenders = new List<string>();
        void Walk(Visual visual)
        {
            foreach (var child in visual.GetVisualChildren())
            {
                if (child is not Control c) continue;
                var b = c.Bounds;
                if (b.Width > 0.5 && (b.Right > width + 0.5 || b.Bottom > height + 0.5 || b.X < -0.5 || b.Y < -0.5))
                    offenders.Add($"  {c.GetType().Name} x={b.X:F1} y={b.Y:F1} w={b.Width:F1} h={b.Height:F1} right={b.Right:F1}");
                if (child is Border or Grid or ScrollViewer or StackPanel or Panel) Walk(child);
            }
        }
        Walk(root);
        Console.WriteLine(offenders.Count == 0
            ? $"  [{width:F0}x{height:F0}] no control overflows the card box"
            : $"  [{width:F0}x{height:F0}] {offenders.Count} overflow(s):\n" + string.Join("\n", offenders.Take(20)));
    }

    private static void Save(string outDir, Control root, double width, double height, string name)
    {
        var pixelW = (int)Math.Ceiling(width * 2);
        var pixelH = (int)Math.Ceiling(height * 2);
        var rtb = new RenderTargetBitmap(new PixelSize(pixelW, pixelH), new Vector(192, 192));
        rtb.Render(root);
        var file = Path.Combine(outDir, name);
        rtb.Save(file);
        Console.WriteLine($"  -> {file}");
    }

    private sealed class StubLayoutProvider(DeskCanvas.Core.Models.WidgetLayout initial)
        : DeskCanvas.Core.Interfaces.IWidgetLayoutProvider
    {
        public DeskCanvas.Core.Models.WidgetLayout Current { get; private set; } = initial;
        public string ScreenId { get; set; } = "Repro";

        public DeskCanvas.Core.Models.WidgetLayout Get() => Current;
        public void Save(DeskCanvas.Core.Models.WidgetLayout data) => Current = data;
        public void Remove() { }

        public event DeskCanvas.Core.Interfaces.DataChangedEvent<DeskCanvas.Core.Models.WidgetLayout>? DataChanging
        { add { } remove { } }
        public event DeskCanvas.Core.Interfaces.DataChangedEvent<DeskCanvas.Core.Models.WidgetLayout>? DataChanged
        { add { } remove { } }
    }
}
