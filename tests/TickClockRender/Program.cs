// Headless render probe for the Tick Clock card.
// Usage:
//   TickClockRender [width] [height] [out.png] [cornerRadius] [frozenTime] [fontFamily]
//   TickClockRender sweep
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using DeskCanvas.Core.Models.Attributes;
using SkiaSharp;
using System.Reflection;
using TickClock.Locales;
using TickClock.Models;
using TickClock.Views;
using TickClock.Views.Controls;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        // 参考图放大工具：TickClockRender crop <src> <outDir>
        if (args.Length >= 3 && args[0] == "crop") return Crop(args[1], args[2]);
        if (args.Length >= 2 && args[0] == "probe") return Probe(args[1]);
        if (args.Length >= 2 && args[0] == "probecorner") return ProbeCorners(args[1]);
        if (args.Length >= 4 && args[0] == "compare") return Compare(args[1], args[2], args[3]);
        if (args.Length >= 2 && args[0] == "inkrows") return InkRows(args[1]);

        AppBuilder.Configure<Application>()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .UseSkia()
            .SetupWithoutStarting();

        Application.Current!.Styles.Add(new Avalonia.Themes.Fluent.FluentTheme());
        Application.Current.RequestedThemeVariant = ThemeVariant.Light;

        if (!CheckManifest()) return 1;

        if (args.Length > 0 && args[0] == "sweep") return Sweep();

        double width = Arg(args, 0, 430);
        double height = Arg(args, 1, 430);
        string outPath = args.Length > 2 ? args[2] : Path.Combine(AppContext.BaseDirectory, "tickclock.png");
        double radius = Arg(args, 3, 46);
        string? frozen = args.Length > 4 ? args[4] : null;

        var (window, view) = Show(width, height, radius);

        if (frozen != null)
        {
            var parts = frozen.Split('|');
            if (parts.Length > 0) view.FindControl<TimeInkText>("TimeLabel")!.Text = parts[0];
            if (parts.Length > 1) view.FindControl<TextBlock>("WeekdayLabel")!.Text = parts[1];
            // "auto" = 保留 ViewModel 的真实格式化结果（用来检查日期与点的间距）
            if (parts.Length > 2 && parts[2] != "auto") view.FindControl<TextBlock>("DateLabel")!.Text = parts[2];
        }

        // 第 8 个参数：模拟宿主卡片圆角，验证刻度环自适应（不传则保持 NaN 回退）。
        if (args.Length > 7 && double.TryParse(args[7], out var cardCorner))
            view.FindControl<DialTicksControl>("DialTicks")!.CardCornerRadius = cardCorner;

        if (args.Length > 5 && !string.IsNullOrWhiteSpace(args[5]))
        {
            var family = new FontFamily(args[5]);
            view.FindControl<TimeInkText>("TimeLabel")!.FontFamily = family;
            view.FindControl<TextBlock>("DateLabel")!.FontFamily = family;
        }

        if (args.Length > 6 && !string.IsNullOrWhiteSpace(args[6]) && Enum.TryParse<FontStretch>(args[6], true, out var stretch))
        {
            view.FindControl<TimeInkText>("TimeLabel")!.FontWeight = FontWeight.Bold;
            view.FindControl<TextBlock>("DateLabel")!.FontStretch = stretch;
        }

        // 预览有色主题（正式运行时全彩=朱红、单色=主题前景、其余=强调色），
        // 测试宿主没有 App.Services，用环境变量把星期钉成参考图的红。
        if (Environment.GetEnvironmentVariable("TICKCLOCK_RED") == "1")
        {
            view.FindControl<TextBlock>("WeekdayLabel")!.Foreground =
                new SolidColorBrush(Color.Parse("#E93A31"));
        }

        Repaint(window);

        window.CaptureRenderedFrame()?.Save(outPath);

        var weekday = view.FindControl<TextBlock>("WeekdayLabel")!;
        var time = view.FindControl<TimeInkText>("TimeLabel")!;
        var date = view.FindControl<TextBlock>("DateLabel")!;
        var timeBox = view.FindControl<Control>("TimeLabel")!;
        Console.WriteLine($"{width}x{height} card={radius} view={view.Bounds.Width:F0}x{view.Bounds.Height:F0}");
        Console.WriteLine($"  weekday fs={weekday.FontSize:F1} h={weekday.Bounds.Height:F1} text='{weekday.Text}'");
        Console.WriteLine($"  time    box={timeBox.Bounds.Width:F1}x{timeBox.Bounds.Height:F1} label={time.Bounds.Width:F1}x{time.Bounds.Height:F1} text='{time.Text}'");
        Console.WriteLine($"  date    fs={date.FontSize:F1} h={date.Bounds.Height:F1} text='{date.Text}'");
        Console.WriteLine($"  png={outPath}");
        return 0;
    }

    /// <summary>
    /// 宿主是靠反射读 [WidgetInfo]/[Locale] 才能把 DLL 挂进组件库的：这里断言这两个清单
    /// 都只有一份、标题/副标题/组件名三个资源键都能在 Locale 类上解析出非空值，
    /// 分类与默认跨度也符合产品定位（时钟与日历 / 2x2）。
    /// </summary>
    private static bool CheckManifest()
    {
        var assembly = typeof(TickClockView).Assembly;
        var infos = assembly.GetCustomAttributes<WidgetInfoAttribute>().ToArray();
        var locales = assembly.GetCustomAttributes<LocaleAttribute>().ToArray();
        var problems = new List<string>();

        if (infos.Length != 1) problems.Add($"expected 1 WidgetInfo, found {infos.Length}");
        if (locales.Length != 1) problems.Add($"expected 1 Locale, found {locales.Length}");

        if (problems.Count == 0)
        {
            var info = infos[0];
            var locale = locales[0];
            if (info.ViewType != typeof(TickClockView)) problems.Add("ViewType mismatch");
            if (info.ModelType != typeof(TickClockModel)) problems.Add("ModelType mismatch");
            if (info.EditModelViewType is null) problems.Add("no settings page registered");
            if (info.Category != "WidgetCategory_ClockCalendar") problems.Add($"category={info.Category}");
            if (info.DefaultColumns != 2 || info.DefaultRows != 2) problems.Add($"default span {info.DefaultColumns}x{info.DefaultRows}");
            if (info.PresetSpanList is not "2x2,4x4") problems.Add($"preset spans={info.PresetSpanList}");

            string? Resolve(string? key) => key is null
                ? null
                : locale.LocaleType.GetProperty(key)?.GetValue(null) as string;

            foreach (var (name, key) in new[]
                     {
                         ("display name", locale.DisplayName),
                         ("title", info.Title),
                         ("subtitle", info.Subtitle),
                     })
            {
                if (string.IsNullOrWhiteSpace(Resolve(key))) problems.Add($"{name} key '{key}' did not resolve");
            }

            Console.WriteLine($"manifest: {Resolve(locale.DisplayName)} / {Resolve(info.Title)} · {Resolve(info.Subtitle)} " +
                              $"({info.Category}, {info.DefaultColumns}x{info.DefaultRows}, presets={info.PresetSpanList})");
        }

        foreach (var problem in problems) Console.WriteLine($"  [FAIL] manifest: {problem}");
        return problems.Count == 0;
    }

    private static (Window Window, TickClockView View) Show(double width, double height, double radius)
    {
        var view = new TickClockView(new TickClockModel());

        // 宿主卡片：白底 + 大圆角（真实运行时由 Widget.axaml 的卡片 Border 提供）
        var card = new Border
        {
            Background = Brushes.White,
            CornerRadius = new CornerRadius(radius),
            ClipToBounds = true,
            Child = view,
        };

        var window = new Window
        {
            WindowDecorations = WindowDecorations.None,
            Width = width,
            Height = height,
            Background = Brushes.White,
            Content = card,
        };

        window.Show();
        Repaint(window);
        return (window, view);
    }

    /// <summary>
    /// 参考图像素探针：量出卡片边缘、刻度外端/内端、直边刻度间距，
    /// 以及沿 45° 对角线的角部刻度分布（用来判断刻度是径向还是法线方向）。
    /// </summary>
    private static int Probe(string path)
    {
        using var bitmap = SKBitmap.Decode(path);
        int w = bitmap.Width, h = bitmap.Height;
        int cx = w / 2, cy = h / 2;
        Console.WriteLine($"image: {w}x{h} center=({cx},{cy})");

        static bool IsCard(SKColor c) => c.Red > 238 && c.Green > 238 && c.Blue > 238;
        static bool IsInk(SKColor c) => c.Red is > 60 and < 225 && Math.Abs(c.Red - c.Green) < 18 && Math.Abs(c.Green - c.Blue) < 18;

        int top = 0;
        while (top < cy && !IsCard(bitmap.GetPixel(cx, top))) top++;
        int left = 0;
        while (left < cx && !IsCard(bitmap.GetPixel(left, cy))) left++;
        Console.WriteLine($"card top y={top}, left x={left}");

        int firstInk = -1, lastInk = -1;
        for (int y = top; y < top + (cy - top) / 2; y++)
        {
            var c = bitmap.GetPixel(cx, y);
            if (!IsInk(c)) continue;
            if (firstInk < 0) firstInk = y;
            lastInk = y;
        }
        Console.WriteLine($"vertical scan: ink runs y={firstInk}..{lastInk}  inset={firstInk - top} len={lastInk - firstInk + 1}");

        // 水平扫描穿过上边刻度中部，量相邻刻度的间距
        int scanY = firstInk < 0 ? top + 24 : (firstInk + lastInk) / 2;
        var runs = new List<(int Start, int End)>();
        int runStart = -1;
        for (int x = 0; x < w; x++)
        {
            bool ink = IsInk(bitmap.GetPixel(x, scanY));
            if (ink && runStart < 0) runStart = x;
            if (!ink && runStart >= 0) { runs.Add((runStart, x - 1)); runStart = -1; }
        }
        if (runs.Count > 1)
        {
            var centers = runs.Select(r => (r.Start + r.End) / 2.0).ToList();
            var gaps = centers.Zip(centers.Skip(1), (a, b) => b - a).Select(d => Math.Round(d, 1)).ToList();
            Console.WriteLine($"top row y={scanY}: {runs.Count} ticks, centers={string.Join(",", centers.Select(c => Math.Round(c, 0)))}");
            Console.WriteLine($"gaps={string.Join(",", gaps)}");
        }

        // 沿 45°（左上角）射线扫描：看角部刻度的径向位置
        Console.WriteLine("diagonal 135deg (center -> top-left):");
        for (int d = 0; d < Math.Min(cx, cy) - 2; d++)
        {
            int x = cx - d, y = cy - d;
            var c = bitmap.GetPixel(x, y);
            if (IsInk(c)) Console.WriteLine($"  d={d} ({x},{y}) rgb={c.Red},{c.Green},{c.Blue}");
        }

        return 0;
    }

    /// <summary>
    /// 连通域探针：把左上角区域的灰色像素分块，算每根刻度线的质心、主轴方向、
    /// 以及“主轴 vs 指向卡片中心”的夹角——用来判定刻度是径向还是弧线法线。
    /// </summary>
    private static int ProbeCorners(string path)
    {
        using var bitmap = SKBitmap.Decode(path);
        int w = bitmap.Width, h = bitmap.Height;
        int cx = w / 2, cy = h / 2;
        int n = 175;

        static bool IsInk(SKColor c) => c.Red is > 50 and < 215 && Math.Abs(c.Red - c.Green) < 16 && Math.Abs(c.Green - c.Blue) < 16;

        bool[,] ink = new bool[n, n];
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
                ink[x, y] = IsInk(bitmap.GetPixel(x, y));

        bool[,] seen = new bool[n, n];
        var results = new List<string>();
        var queue = new Queue<(int X, int Y)>();
        for (int y = 0; y < n; y++)
        {
            for (int x = 0; x < n; x++)
            {
                if (!ink[x, y] || seen[x, y]) continue;
                queue.Clear();
                queue.Enqueue((x, y));
                seen[x, y] = true;
                var pts = new List<(int X, int Y)>();
                while (queue.Count > 0)
                {
                    var (px, py) = queue.Dequeue();
                    pts.Add((px, py));
                    for (int dy = -1; dy <= 1; dy++)
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            int nx = px + dx, ny = py + dy;
                            if (nx < 0 || ny < 0 || nx >= n || ny >= n || seen[nx, ny] || !ink[nx, ny]) continue;
                            seen[nx, ny] = true;
                            queue.Enqueue((nx, ny));
                        }
                }
                if (pts.Count < 12) continue;

                double mx = pts.Average(p => p.X), my = pts.Average(p => p.Y);
                double sxx = 0, syy = 0, sxy = 0;
                foreach (var (px, py) in pts)
                {
                    double dx = px - mx, dy = py - my;
                    sxx += dx * dx; syy += dy * dy; sxy += dx * dy;
                }
                double theta = 0.5 * Math.Atan2(2 * sxy, sxx - syy);
                double angleDeg = theta * 180 / Math.PI;
                double radialDeg = Math.Atan2(cy - my, cx - mx) * 180 / Math.PI;
                double diff = ((angleDeg - radialDeg + 90) % 180 + 180) % 180 - 90;

                int minX = pts.Min(p => p.X), maxX = pts.Max(p => p.X);
                int minY = pts.Min(p => p.Y), maxY = pts.Max(p => p.Y);
                results.Add($"  ink @({mx:F0},{my:F0}) area={pts.Count} bbox={maxX - minX + 1}x{maxY - minY + 1} " +
                            $"axis={angleDeg:F0}deg radial={radialDeg:F0}deg diff={diff:F0}deg");
            }
        }

        Console.WriteLine($"card={w}x{h} center=({cx},{cy}); components in top-left {n}px:");
        foreach (var line in results) Console.WriteLine(line);
        return 0;
    }

    /// <summary>扫描图像中列的所有深色像素区间（用来量时间的实际墨迹高度）。</summary>
    private static int InkRows(string path)
    {
        using var bitmap = SKBitmap.Decode(path);
        int w = bitmap.Width, h = bitmap.Height;
        int x0 = (int)(w * 0.14), x1 = (int)(w * 0.86);
        var runs = new List<(int Start, int End)>();
        int start = -1;
        for (int y = 0; y < h; y++)
        {
            bool ink = false;
            for (int x = x0; x < x1; x++)
            {
                var c = bitmap.GetPixel(x, y);
                if (c.Red < 150 && Math.Abs(c.Red - c.Green) < 24 && Math.Abs(c.Green - c.Blue) < 24)
                {
                    ink = true;
                    break;
                }
            }
            if (ink && start < 0) start = y;
            if (!ink && start >= 0) { runs.Add((start, y - 1)); start = -1; }
        }
        if (start >= 0) runs.Add((start, h - 1));

        Console.WriteLine($"{path} ({w}x{h}) dark-row bands:");
        foreach (var (a, b) in runs)
            Console.WriteLine($"  y={a}..{b}  height={b - a + 1}  ({100.0 * (b - a + 1) / h:F1}% of card)");
        return 0;
    }

    /// <summary>把参考图与渲染图上下拼成一张对比图（各缩放到 430×430）。</summary>
    private static int Compare(string referencePath, string renderPath, string outPath)
    {
        using var reference = SKImage.FromBitmap(SKBitmap.Decode(referencePath));
        using var render = SKImage.FromBitmap(SKBitmap.Decode(renderPath));
        using var dest = new SKBitmap(440, 880);
        using var canvas = new SKCanvas(dest);
        canvas.Clear(new SKColor(235, 235, 235));
        var sampling = new SKSamplingOptions(SKFilterMode.Linear);
        canvas.DrawImage(reference, new SKRect(0, 0, reference.Width, reference.Height), new SKRect(5, 5, 435, 435), sampling);
        canvas.DrawImage(render, new SKRect(0, 0, render.Width, render.Height), new SKRect(5, 445, 435, 875), sampling);
        using var image = SKImage.FromBitmap(dest);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        using var stream = File.Create(outPath);
        data.SaveTo(stream);
        Console.WriteLine($"compare -> {outPath}");
        return 0;
    }

    /// <summary>把参考图的四个角各裁 30% 并放大到 600×600，用最近邻保留像素边界。</summary>
    private static int Crop(string source, string outDir)
    {
        using var bitmap = SKBitmap.Decode(source);
        Console.WriteLine($"source: {bitmap.Width}x{bitmap.Height}");
        int cw = Math.Max(1, (int)(bitmap.Width * 0.3));
        int ch = Math.Max(1, (int)(bitmap.Height * 0.3));
        (string Name, int X, int Y)[] corners =
        [
            ("corner-tl", 0, 0),
            ("corner-tr", bitmap.Width - cw, 0),
            ("corner-bl", 0, bitmap.Height - ch),
            ("corner-br", bitmap.Width - cw, bitmap.Height - ch),
            ("top-mid", bitmap.Width / 2 - 90, 0),
        ];

        Directory.CreateDirectory(outDir);
        using var sourceImage = SKImage.FromBitmap(bitmap);
        foreach (var (name, x, y) in corners)
        {
            using var dest = new SKBitmap(600, 600);
            using var canvas = new SKCanvas(dest);
            canvas.DrawImage(sourceImage,
                new SKRect(x, y, x + cw, y + ch),
                new SKRect(0, 0, 600, 600),
                new SKSamplingOptions(SKFilterMode.Nearest));
            using var image = SKImage.FromBitmap(dest);
            using var data = image.Encode(SKEncodedImageFormat.Png, 100);
            using var stream = File.Create(Path.Combine(outDir, name + ".png"));
            data.SaveTo(stream);
            Console.WriteLine($"  {name} <- ({x},{y}) {cw}x{ch}");
        }
        return 0;
    }

    private static void Repaint(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>
    /// 自适应契约：任何卡片尺寸下时间盒（以及可见的星期/日期）都必须完整落在卡片内，
    /// 且时间文本永远非空。覆盖 1x1 单格、2x2、4x4、横向长条、竖向长条五种形态。
    /// </summary>
    private static int Sweep()
    {
        (double W, double H)[] sizes =
        [
            (56, 56), (80, 80), (90, 90), (110, 110), (120, 120), (160, 160), (200, 200),
            (260, 260), (360, 360), (430, 430), (600, 600), (600, 300), (300, 600), (900, 420),
        ];

        int failures = 0;
        foreach (var (w, h) in sizes)
        {
            var radius = Math.Min(46, Math.Min(w, h) * 0.107);
            var (window, view) = Show(w, h, radius);

            var timeBox = view.FindControl<Control>("TimeLabel")!;
            var weekday = view.FindControl<TextBlock>("WeekdayLabel")!;
            var date = view.FindControl<TextBlock>("DateLabel")!;
            var time = view.FindControl<TimeInkText>("TimeLabel")!;

            var problems = new List<string>();
            if (!Inside(timeBox, view)) problems.Add("time box escapes card");
            if (weekday.IsVisible && !Inside(weekday, view)) problems.Add("weekday escapes card");
            if (date.IsVisible && !Inside(date, view)) problems.Add("date escapes card");
            if (timeBox.Bounds.Width < 8 || timeBox.Bounds.Height < 4) problems.Add($"time box collapsed ({timeBox.Bounds.Width:F1}x{timeBox.Bounds.Height:F1})");
            if (string.IsNullOrWhiteSpace(time.Text)) problems.Add("time text empty");
            if (weekday.IsVisible && weekday.FontSize < 6) problems.Add("weekday font too small");
            if (date.IsVisible && date.FontSize < 6) problems.Add("date font too small");
            if (timeBox.Bounds.Width > view.Bounds.Width * 0.9 + 0.5) problems.Add("time box wider than 90% of card");

            string label = $"{w}x{h}";
            if (problems.Count == 0)
            {
                Console.WriteLine($"  [PASS] {label}  time={timeBox.Bounds.Width:F1}x{timeBox.Bounds.Height:F1} " +
                                  $"weekday={(weekday.IsVisible ? "on" : "off")} date={(date.IsVisible ? "on" : "off")}");
            }
            else
            {
                Console.WriteLine($"  [FAIL] {label}: {string.Join("; ", problems)}");
                failures++;
            }

            window.Close();
            Dispatcher.UIThread.RunJobs();
        }

        Console.WriteLine();
        Console.WriteLine(failures == 0 ? "ALL PASS" : $"{failures} FAILURE(S)");
        return failures == 0 ? 0 : 1;
    }

    private static bool Inside(Control control, Visual host)
    {
        var origin = control.TranslatePoint(new Point(0, 0), host);
        if (origin is null) return false;
        var p = origin.Value;
        return p.X >= -0.5 && p.Y >= -0.5
            && p.X + control.Bounds.Width <= host.Bounds.Width + 0.5
            && p.Y + control.Bounds.Height <= host.Bounds.Height + 0.5;
    }

    private static double Arg(string[] args, int index, double fallback) =>
        args.Length > index && double.TryParse(args[index], out var value) ? value : fallback;
}
