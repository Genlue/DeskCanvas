using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using DeskCanvas.Core;
using DeskCanvas.Core.Interfaces;
using DeskCanvas.Core.Models;
using DeskCanvas.Core.Models.Attributes;
using DeskCanvas.Views;
using Microsoft.Extensions.DependencyInjection;

namespace DeskCanvas.Services;

/// <summary>
/// 开发者工具：把每个组件形态按其 <see cref="WidgetInfoAttribute.PresetSpans"/> 逐尺寸离屏渲染成 PNG，
/// 落到 <c>src/Widgets/&lt;Category&gt;/&lt;Widget&gt;/Preview/</c>，作为仓库里的可视化留档。
///
/// <para>
/// 用法（开发机 + 便携模式；参数缺省时取 exe 同级的 Widgets 目录）：
/// <c>DeskCanvas.exe --export-widget-previews &lt;仓库 src/Widgets 路径&gt;</c>
/// </para>
///
/// <para>
/// 口径：1 格 = 96 DIP（默认栅格 = 1920 宽屏的 5%），导出 2× 像素；统一深色卡底；
/// 无边框组件（<see cref="IFramelessWidget"/>）保持透明底，不画卡框。
/// 预览反映导出当时的真实数据状态（天气/音乐等联网组件依赖当次请求结果）。
/// </para>
/// </summary>
public static class WidgetPreviewExporter
{
    public const string Flag = "--export-widget-previews";

    private const double CellSize = 96;
    private const double Scale = 2;

    /// <summary>联网组件的首个数据帧不是同步就绪的，给它一点时间再截。</summary>
    private const int DataSettleMs = 900;

    /// <summary>每次改尺寸后等一个布局帧，避免截到上一个跨度的布局。</summary>
    private const int LayoutSettleMs = 120;

    public static bool IsRequested(string[] args) =>
        args.Any(a => string.Equals(a, Flag, StringComparison.OrdinalIgnoreCase));

    /// <summary>参数里 <see cref="Flag"/> 后面那一段就是输出根目录；没有则用 exe 的 Widgets 目录。</summary>
    public static string ResolveOutputRoot(string[] args)
    {
        for (var i = 0; i < args.Length; i++)
        {
            if (!string.Equals(args[i], Flag, StringComparison.OrdinalIgnoreCase)) continue;
            if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
                return Path.GetFullPath(args[i + 1]);
        }

        return Const.WidgetsFolder;
    }

    public static async Task<int> RunAsync(IServiceProvider services, string outputRoot)
    {
        var assemblyProvider = services.GetRequiredService<IAssemblyProvider>();
        var factory = services.GetRequiredService<IWidgetFactory<Window, UserControl>>();
        var appSettings = services.GetRequiredService<IAppSettingsProvider>();

        // 预览图要和机器上的主题设置解耦：统一按深色导出，仓库里的图才彼此一致。
        if (Application.Current is { } app) app.RequestedThemeVariant = ThemeVariant.Dark;

        var folders = MapWidgetFolders(outputRoot);
        var radius = appSettings.Get().Dimensions.Radius;
        var margin = appSettings.Get().Dimensions.Margin;
        var written = 0;
        var failed = new List<string>();
        var stale = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var window = new Window
        {
            WindowDecorations = WindowDecorations.None,
            ShowInTaskbar = false,            CanResize = false,
            Background = Brushes.Transparent,
            TransparencyLevelHint = [WindowTransparencyLevel.Transparent],
            Width = 320,
            Height = 320,
            Position = new PixelPoint(-16000, -16000),
        };
        window.Show();

        try
        {
            foreach (var group in assemblyProvider.GetAssemblyInfos(Const.WidgetsFolder))
            {
                var info = group.OrderByDescending(a => a.Version).FirstOrDefault();
                if (info is null) continue;

                Assembly assembly;
                try
                {
                    assembly = assemblyProvider.LoadAssembly(info.AssemblyName);
                }
                catch (Exception ex)
                {
                    failed.Add($"{info.AssemblyName}: load — {ex.Message}");
                    continue;
                }

                if (!folders.TryGetValue(info.AssemblyName, out var widgetFolder))
                {
                    // 输出目录里可能残留已删除组件的 DLL；没有源码目录就没有可写的位置。
                    continue;
                }

                var previewDir = Path.Combine(widgetFolder, "Preview");

                foreach (var widgetInfo in assembly.GetCustomAttributes<WidgetInfoAttribute>())
                {
                    IReadOnlyList<(int Columns, int Rows)> spans = widgetInfo.PresetSpans.Count > 0
                        ? widgetInfo.PresetSpans
                        : [(widgetInfo.DefaultColumns, widgetInfo.DefaultRows)];

                    UserControl control;
                    try
                    {
                        control = factory.CreateControl(widgetInfo.ViewType);
                    }
                    catch (Exception ex)
                    {
                        failed.Add($"{info.AssemblyName}/{widgetInfo.ViewType.Name}: create — {ex.Message}");
                        continue;
                    }

                    var frameless = control is IFramelessWidget { IsFrameless: true };
                    // 真实窗口里卡片比栅格单元小一圈（Dimensions.Margin），内容因此有内缩；
                    // 这里卡片铺满整个单元，用 Padding 还原同样的内容内缩。整卡铺满的
                    // 两个组件（聚合卡、笔记）按宿主规则吃满四角，不加内缩。
                    var inset = frameless || WidgetContentHost.IsFlush(control) ? 0 : margin;
                    var frame = new Border
                    {
                        CornerRadius = frameless ? new CornerRadius(0) : new CornerRadius(radius),
                        ClipToBounds = !frameless,
                        Background = frameless ? Brushes.Transparent : CardBrush(),
                        BorderBrush = frameless ? null : CardBorderBrush(),
                        BorderThickness = frameless ? new Thickness(0) : new Thickness(1),
                        Padding = new Thickness(inset),
                        Child = control,
                    };

                    window.Content = frame;

                    // 让控件挂到可视树上跑完 Loaded、异步取数与首次布局，再截图。
                    await Task.Delay(DataSettleMs);
                    if (!Directory.Exists(previewDir)) Directory.CreateDirectory(previewDir);

                    var slug = Slug(widgetInfo.ViewType.Name);
                    foreach (var (columns, rows) in spans)
                    {
                        var width = columns * CellSize;
                        var height = rows * CellSize;
                        var path = Path.Combine(previewDir, $"{slug}@{columns}x{rows}.png");
                        try
                        {
                            window.Width = width;
                            window.Height = height;
                            frame.Width = width;
                            frame.Height = height;

                            // 窗口改尺寸是异步落到平台层的：不给它一个渲染帧，
                            // 截图会拿到上一次（更大）跨度的布局并被裁掉四周。
                            window.UpdateLayout();
                            frame.UpdateLayout();
                            await Task.Delay(LayoutSettleMs);

                            using var bitmap = new RenderTargetBitmap(
                                new PixelSize((int) (width * Scale), (int) (height * Scale)),
                                new Vector(96 * Scale, 96 * Scale));
                            bitmap.Render(frame);
                            using (var stream = File.Create(path)) bitmap.Save(stream, new PngBitmapEncoderOptions());
                            written++;
                            stale.Add(path);
                        }
                        catch (Exception ex)
                        {
                            failed.Add($"{info.AssemblyName}/{widgetInfo.ViewType.Name}@{columns}x{rows}: render — {ex.Message}");
                        }
                    }
                }

                PruneStalePreviews(previewDir, stale);
            }
        }
        finally
        {
            window.Close();
        }

        Console.WriteLine($"[preview-export] wrote {written} PNG(s) under {outputRoot}");
        foreach (var f in failed) Console.WriteLine($"[preview-export] FAILED {f}");
        return failed.Count == 0 ? 0 : 1;
    }

    /// <summary>程序集名 → 组件源码目录（目录名与工程名一致，程序集名可能被钉成历史值）。</summary>
    private static Dictionary<string, string> MapWidgetFolders(string root)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(root)) return map;

        foreach (var csproj in Directory.EnumerateFiles(root, "*.csproj", SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(csproj);
            var explicitName = Regex.Match(text, "<AssemblyName>([^<]+)</AssemblyName>");
            var assemblyName = explicitName.Success
                ? explicitName.Groups[1].Value.Trim()
                : Path.GetFileNameWithoutExtension(csproj);
            map[assemblyName] = Path.GetDirectoryName(csproj)!;
        }

        return map;
    }

    /// <summary>删掉本次没产出对应形态的旧图，避免改过 presetSpans 后留下幽灵文件。</summary>
    private static void PruneStalePreviews(string previewDir, HashSet<string> keep)
    {
        if (!Directory.Exists(previewDir)) return;
        foreach (var png in Directory.EnumerateFiles(previewDir, "*.png"))
        {
            if (!keep.Contains(png)) File.Delete(png);
        }
    }

    private static IBrush CardBrush() => new SolidColorBrush(Color.Parse("#1C1C1E"));

    private static IBrush CardBorderBrush() => new SolidColorBrush(Color.Parse("#26FFFFFF"));

    /// <summary>ViewType 名 → kebab-case 文件名（AnalogIII → analog-iii，UVIndex → uv-index）。</summary>
    private static string Slug(string typeName)
    {
        var s = Regex.Replace(typeName, "([A-Z]+)([A-Z][a-z])", "$1-$2");
        s = Regex.Replace(s, "(?<=[a-z0-9])([A-Z])", "-$1");
        return s.ToLowerInvariant();
    }
}
