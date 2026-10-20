// Headless render + geometry probe for the Dice card.
// Usage:
//   DiceRender <width> <height> <count> <out.png> [diceType] [faceStyle]
//   DiceRender sweep            -> asserts the "never overflows the face / tray" contract
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Dice.Models;
using Dice.Views;
using Dice.Views.Controls;

internal static class Program
{
    private static int failures;

    private static double Arg(string[] args, int index, double fallback) =>
        args.Length > index && double.TryParse(args[index], out var value) ? value : fallback;

    [STAThread]
    private static int Main(string[] args)
    {
        AppBuilder.Configure<Application>()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .UseSkia()
            .SetupWithoutStarting();

        Application.Current!.Styles.Add(new Avalonia.Themes.Fluent.FluentTheme());
        Application.Current.RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Light;

        if (args.Length > 0 && args[0] == "sweep")
        {
            return Sweep();
        }

        var width = Arg(args, 0, 200);
        var height = Arg(args, 1, 200);
        var count = (int)Arg(args, 2, 2);
        var outPath = args.Length > 3 ? args[3] : Path.Combine(AppContext.BaseDirectory, "dice.png");
        var type = args.Length > 4 && int.TryParse(args[4], out var t) ? (DiceType)t : DiceType.D6;
        var style = args.Length > 5 && int.TryParse(args[5], out var s) ? (DiceFaceStyle)s : DiceFaceStyle.Dots;

        var view = Render(width, height, count, type, style, outPath);
        Dump(view, $"{width}x{height} count={count} type={type} style={style}");
        return 0;
    }

    private static DiceView Render(double width, double height, int count, DiceType type, DiceFaceStyle style, string? outPath)
    {
        var view = new DiceView(new DiceModel(count, type, style)) { Width = width, Height = height };
        var window = new Window
        {
            WindowDecorations = WindowDecorations.None,
            Width = width,
            Height = height,
            Background = Brushes.White,
            Content = view,
        };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();

        if (outPath != null)
        {
            window.CaptureRenderedFrame()?.Save(outPath);
        }

        return view;
    }

    private static void Dump(DiceView view, string label)
    {
        var tier = view.FindControl<Grid>("SmallLayout")!.IsVisible ? "Small(2x2)"
            : view.FindControl<Grid>("WideLayout")!.IsVisible ? "Wide(4x2)"
            : "Cell(1x1)";
        Console.WriteLine($"{label} tier={tier}");

        foreach (var face in view.GetVisualDescendants().OfType<DiceFaceControl>())
        {
            if (!face.IsEffectivelyVisible) continue;
            var border = face.FindControl<Border>("FaceBorder")!;
            var scaler = face.FindControl<Viewbox>("ContentScaler")!;
            var content = face.FindControl<Panel>("ContentPanel")!;
            Console.WriteLine(
                $"  face FaceSize={face.FaceSize:F1} box={face.Bounds.Width:F1} " +
                $"border={border.Bounds.Width:F1} inner={scaler.Bounds.Width:F1}x{scaler.Bounds.Height:F1} " +
                $"content={content.Bounds.Width:F1}x{content.Bounds.Height:F1} " +
                $"scale={(scaler.Bounds.Width / 36d):F2} " +
                $"borderOverflows={border.Bounds.Width > face.Bounds.Width + 0.5} " +
                $"contentOverflows={scaler.Bounds.Width > border.Bounds.Width + 0.5}");
        }

        var tray = view.GetVisualDescendants().OfType<DiceTrayPanel>().FirstOrDefault(t => t.IsEffectivelyVisible);
        if (tray != null)
        {
            Console.WriteLine($"  tray={tray.Bounds.Width:F1}x{tray.Bounds.Height:F1} children={tray.Children.Count}");
            foreach (var child in tray.Children)
            {
                var b = child.Bounds;
                Console.WriteLine($"    child @({b.X:F1},{b.Y:F1}) {b.Width:F1}x{b.Height:F1} " +
                                  $"outX={b.Right > tray.Bounds.Width + 0.5 || b.X < -0.5} " +
                                  $"outY={b.Bottom > tray.Bounds.Height + 0.5 || b.Y < -0.5}");
            }
        }
    }

    /// <summary>
    /// The contract from the reported defect: at ANY card size, every die face stays inside
    /// the tray and every pip/number stays inside its face. Sizes cover the real 2x2 band
    /// (including the degenerate small end where the old fallback blew the faces up to 48).
    /// </summary>
    private static int Sweep()
    {
        double[] sides = [48, 56, 64, 72, 80, 88, 96, 104, 112, 120, 128, 140, 152, 176, 200, 240, 320];
        int[] counts = [1, 2, 3, 4, 5, 6];
        (DiceType Type, DiceFaceStyle Style)[] modes =
        [
            (DiceType.D6, DiceFaceStyle.Dots),
            (DiceType.D20, DiceFaceStyle.Numbers),
            (DiceType.D100, DiceFaceStyle.Numbers),
        ];

        foreach (var side in sides)
        {
            foreach (var count in counts)
            {
                foreach (var mode in modes)
                {
                    var view = Render(side, side, count, mode.Type, mode.Style, null);
                    Check(view, side, count, mode.Type, mode.Style);
                }
            }
        }

        Console.WriteLine();
        Console.WriteLine(failures == 0 ? "ALL PASS" : $"{failures} FAILURE(S)");
        return failures == 0 ? 0 : 1;
    }

    private static void Check(DiceView view, double side, int count, DiceType type, DiceFaceStyle style)
    {
        var label = $"{side}x{side} n={count} {type}/{style}";
        var tray = view.GetVisualDescendants().OfType<DiceTrayPanel>().FirstOrDefault(t => t.IsEffectivelyVisible);
        if (tray == null)
        {
            // 1x1 tier renders a single face instead of a tray. That is exactly where a fixed
            // 48/64 face used to overflow a smaller cell and get its pips clipped by the card.
            var cell = view.FindControl<Grid>("CellLayout")!;
            if (!cell.IsVisible)
            {
                Fail(label, "no visible tray");
                return;
            }

            // The face is wrapped in a down-only Viewbox, so what must fit the cell is the
            // Viewbox (the scaled footprint), not the face's pre-scale bounds.
            var cellFace = view.FindControl<DiceFaceControl>("CellDiceFace")!;
            Viewbox? scaler = null;
            for (var node = cellFace.GetVisualParent(); node != null; node = node.GetVisualParent())
            {
                if (node is Viewbox box) { scaler = box; break; }
                if (node is Grid) break;
            }
            if (scaler == null)
            {
                Fail(label, "cell face is not wrapped in a scaler");
                return;
            }

            if (scaler.Bounds.Width > cell.Bounds.Width + 0.5 || scaler.Bounds.Height > cell.Bounds.Height + 0.5)
            {
                Fail(label, $"scaled face {scaler.Bounds.Width:F1}x{scaler.Bounds.Height:F1} escapes cell " +
                            $"{cell.Bounds.Width:F1}x{cell.Bounds.Height:F1}");
                return;
            }

            Pass(label + " (1x1)");
            return;
        }

        foreach (var child in tray.Children)
        {
            var b = child.Bounds;
            if (b.Right > tray.Bounds.Width + 0.5 || b.Bottom > tray.Bounds.Height + 0.5 || b.X < -0.5 || b.Y < -0.5)
            {
                Fail(label, $"die escapes tray: @({b.X:F1},{b.Y:F1}) {b.Width:F1}x{b.Height:F1} " +
                            $"tray {tray.Bounds.Width:F1}x{tray.Bounds.Height:F1}");
                return;
            }
        }

        foreach (var face in tray.Children.OfType<DiceFaceControl>())
        {
            var border = face.FindControl<Border>("FaceBorder")!;
            var scaler = face.FindControl<Viewbox>("ContentScaler")!;
            if (border.Bounds.Width > face.Bounds.Width + 0.5)
            {
                Fail(label, $"face border {border.Bounds.Width:F1} > slot {face.Bounds.Width:F1}");
                return;
            }

            if (scaler.Bounds.Width > border.Bounds.Width + 0.5 || scaler.Bounds.Height > border.Bounds.Height + 0.5)
            {
                Fail(label, $"face content {scaler.Bounds.Width:F1}x{scaler.Bounds.Height:F1} > face {border.Bounds.Width:F1}");
                return;
            }
        }

        // Header must never push the panel entry point out of the card.
        var header = view.FindControl<Grid>("SmallHeader");
        var sumPill = view.FindControl<Border>("SmallSumPill");
        if (header is { IsEffectivelyVisible: true } && view.FindControl<Grid>("SmallLayout")!.IsVisible)
        {
            var layout = view.FindControl<Grid>("SmallLayout")!;
            if (header.Bounds.Width > layout.Bounds.Width + 0.5)
            {
                Fail(label, $"header {header.Bounds.Width:F1} wider than card content {layout.Bounds.Width:F1}");
                return;
            }

            if (sumPill is { IsEffectivelyVisible: true } && sumPill.Bounds.Right > header.Bounds.Width + 0.5)
            {
                Fail(label, $"sum pill escapes header (right {sumPill.Bounds.Right:F1} > {header.Bounds.Width:F1})");
                return;
            }
        }

        Pass(label);
    }

    private static void Pass(string label) => Console.WriteLine($"  [PASS] {label}");

    private static void Fail(string label, string message)
    {
        Console.WriteLine($"  [FAIL] {label}: {message}");
        failures++;
    }
}
