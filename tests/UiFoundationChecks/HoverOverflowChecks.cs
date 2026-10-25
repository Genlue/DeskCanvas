using System;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DeskCanvas.Core.Interfaces;
using DeskCanvas.Core.Models.Settings;
using DeskCanvas.Views;
using Folders.Models;
using Folders.Views;

namespace UiFoundationChecks;

internal static partial class Program
{
    private static IAppSettingsProvider LiquidGlassTheme() => new StubSettings(new AppSettings(
        new Theme(false, null, 0.3, false, false, "Inter", SurfaceStyle.LiquidGlass),
        [], new Layout(GridMode.Manual, true, false, true, false),
        new Dimensions(72, 10, 36), new Region("zh-Hans"), false, null));

    /// <summary>
    /// The hover zoom must paint OUTSIDE the card, up to the grid cell's edge — on the REAL host
    /// chain (a <see cref="Widget"/> window with the real app styles), not on a hand-built
    /// Border+Presenter stand-in. That distinction is the whole point: the host's content template
    /// wraps the view in a <c>ContentControl</c> whose bounds are the card rectangle and which
    /// clips by default, so a stand-in without it reported "paints outside" while the shipped
    /// widget still cut the icon at the card edge.
    /// <para>
    /// Shipped geometry of a 1×1 单文件 / 单链接 card: cell 92 DIP, widget margin 10 → card 72, and
    /// the view sets <c>Margin="0"</c> so it fills the card — the icon IS the card at 图标大小 100 %.
    /// The room left for the zoom is that 10 DIP band, so the cap lands at 92 DIP (≈128 %): the
    /// whole cell. The 110 % case is the one a default-ish setting actually asks for, and it must
    /// already bleed past the card (3.6 DIP per side) instead of stopping at its edge.
    /// </para>
    /// </summary>
    private static void CheckHoverZoomOverflow()
    {
        Console.WriteLine("--- 悬停放大: 真实宿主下图标能画出卡片 (液态玻璃, 92 DIP 单元) ---");
        RunOverflowCase(110, expectFullCell: false);
        RunOverflowCase(200, expectFullCell: true);
    }

    private static void RunOverflowCase(double hoverPercent, bool expectFullCell)
    {
        var view = new SingleFile(new SingleFileModel(null, 100, hoverPercent), null!);
        var host = BuildDecoupledWidget(() => view, LiquidGlassTheme(), wrapPositionProxy: false, size: 92);
        host.Show();
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();

        var presenter = host.FindControl<ContentPresenter>("ContentPresenter")!;
        var frame = view.FindControl<Border>("EmptyPlaceholder")!;
        var margin = host.WidgetMargin.Left;
        var cell = host.ClientSize.Width;
        var card = cell - 2 * margin;

        Check(presenter.Clip is null,
            $"{hoverPercent:0}%: 液态玻璃 + AllowsCardOverflow ⇒ 宿主不给 ContentPresenter 设圆角 Clip (实际 {presenter.Clip?.GetType().Name ?? "null"})");

        // Every clipper in the live tree — the memory lesson: a zoomed icon has met three different
        // cuts (the view's own bounds, the host's rounded presenter clip, and the template wrapper).
        var clippers = host.GetVisualDescendants().OfType<Visual>()
            .Where(v => v.Bounds.Width > 0 && v.Bounds.Height > 0 && (v.ClipToBounds || v.Clip is not null))
            .Select(v => $"{v.GetType().Name}@{v.Bounds.Width:0}×{v.Bounds.Height:0}"
                         + $"{(v.ClipToBounds ? " ClipToBounds" : "")}{(v.Clip is not null ? " Clip" : "")}")
            .ToList();
        Check(!clippers.Any(c => c.StartsWith("ContentControl@", StringComparison.Ordinal)),
            $"{hoverPercent:0}%: 宿主内容模板里的 ContentControl 不再裁到卡片矩形"
            + $"（当前裁剪者: {(clippers.Count == 0 ? "无" : string.Join(", ", clippers))}）");

        var scale = (double)typeof(SingleFile)
            .GetMethod("HoverScaleFor", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(view, [frame])!;
        frame.RenderTransformOrigin = new RelativePoint(0.5, 0.5, RelativeUnit.Relative);
        frame.RenderTransform = new ScaleTransform(scale, scale);
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();

        var root = (Control)host.Content!;
        var size = (int)Math.Ceiling(cell);
        using var rtb = new RenderTargetBitmap(new PixelSize(size, size), new Vector(96, 96));
        rtb.Render(root);
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"deskcanvas-hover-{hoverPercent:0}.png");
        rtb.Save(path);

        var (outside, box) = MeasurePainted(path, margin);
        Console.WriteLine($"    {hoverPercent:0}%: icon {frame.Bounds.Width:0} → scale {scale:0.###}"
                          + $"，卡片外 {outside} px，绘制范围 {box}（cell 0..{size}，card {margin:0}..{card + margin:0}）");

        Check(outside > 0, $"{hoverPercent:0}%: 放大的图标确实画到了卡片外（{outside} px）");
        if (box is { } b)
        {
            Check(b.Right - b.Left > card + 0.5,
                $"{hoverPercent:0}%: 绘制宽度 {b.Right - b.Left} px 超过卡片 {card:0} px");
            if (expectFullCell)
                Check(b.Left <= margin + 0.5 && b.Right >= cell - margin - 0.5,
                    $"{hoverPercent:0}%: 顶格时铺满整个单元（{b}）");
        }
        else
        {
            Check(false, $"{hoverPercent:0}%: 渲染结果为空");
        }

        host.Close();
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>Alpha-covered pixel count outside the card square + the painted bounding box.</summary>
    private static (int Outside, (int Left, int Top, int Right, int Bottom)? Box) MeasurePainted(string png, double margin)
    {
        using var bmp = new Bitmap(png);
        var w = bmp.PixelSize.Width;
        var h = bmp.PixelSize.Height;
        var stride = w * 4;
        var buffer = Marshal.AllocHGlobal(stride * h);
        try
        {
            bmp.CopyPixels(new PixelRect(0, 0, w, h), buffer, stride * h, stride);
            var bytes = new byte[stride * h];
            Marshal.Copy(buffer, bytes, 0, bytes.Length);

            int outside = 0, left = int.MaxValue, top = int.MaxValue, right = -1, bottom = -1;
            var lo = (int)Math.Round(margin);
            var hi = w - lo;
            for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                if (bytes[y * stride + x * 4 + 3] <= 8) continue;
                if (x < left) left = x;
                if (y < top) top = y;
                if (x > right) right = x;
                if (y > bottom) bottom = y;
                if (x < lo || y < lo || x >= hi || y >= hi) outside++;
            }

            return right < 0 ? (0, null) : (outside, (left, top, right + 1, bottom + 1));
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }
}
