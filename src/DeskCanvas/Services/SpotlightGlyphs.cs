using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace DeskCanvas.Services;

/// <summary>
/// Which mark the search field shows in front of the query.
/// <para>
/// macOS Spotlight answers "what am I looking at" with the field's leading glyph: the app grid,
/// a document, a calculation. The launcher follows the same rule — the glyph is the <b>kind of the
/// current search object</b>, not one fixed brand mark, so an app search never wears the file icon
/// and a formula never wears the App Store mark. Every category of the bar has its own artwork, so
/// a locked 图片 / 文件夹 / 组件 / 剪贴板 / 网页 search says what it searches before a keystroke.
/// </para>
/// </summary>
public enum SpotlightGlyphKind
{
    /// <summary>Nothing matched yet, or a plain text query: the magnifier.</summary>
    Search,

    /// <summary>The app grid / an app hit: the App Store mark.</summary>
    Apps,

    /// <summary>A document hit (the 文件 category): the document mark.</summary>
    Files,

    /// <summary>A directory hit (the 文件夹 category): the folder mark.</summary>
    Folder,

    /// <summary>An image hit (the 图片 category): the picture mark.</summary>
    Image,

    /// <summary>A widget-owned row / a workflow (the 组件 category): the tile mark.</summary>
    Widget,

    /// <summary>A clipboard entry (the 剪贴板 category): the clip mark.</summary>
    Clipboard,

    /// <summary>A web search (the 网页 category): the browser mark.</summary>
    Web,

    /// <summary>An inline calculation: the calculator mark.</summary>
    Calculator,
}

/// <summary>
/// The geometry behind <see cref="SpotlightGlyphKind"/>.
/// <para>
/// <b>Filled marks</b> come from the user's own SVG files, kept verbatim as path data on their
/// 1024-unit grid: the app store, the document (文件), the folder (文件夹), the picture, the widget
/// tile, the paperclip (剪贴板), the browser and the magnifier (搜索). They are <b>filled</b>, not
/// stroked, with the theme's primary text brush — the artwork carries no colour of its own on
/// purpose, so it reads black on the light card and light on the dark one exactly like the rest of
/// the field's chrome.
/// </para>
/// <para>
/// <b>Drawn marks</b> (calculator) stay stroked: it is ours, and a stroke keeps the 20 px optical
/// weight identical to the text beside it.
/// </para>
/// <para>
/// Geometries are parsed once and cached: the converter that feeds the Path runs on every binding
/// update (each keystroke that changes the kind), and re-parsing a 1 KB path there would be waste.
/// </para>
/// </summary>
public static class SpotlightGlyphs
{
    /// <summary>
    /// App Store mark, from the user's <c>appstore.svg</c> (iconfont, 1024 grid, filled outline).
    /// Two subpaths: the left leg with its foot, and the right leg + crossbar.
    /// </summary>
    private const string AppsPath =
        "M179.2 810.666667l-4.266667 4.266666c-17.066667 25.6-8.533333 59.733333 17.066667 72.533334" +
        "s59.733333 8.533333 72.533333-17.066667l4.266667-4.266667 51.2-89.6h-85.333333c-21.333333 0" +
        "-42.666667 12.8-55.466667 34.133334z m320-192h-85.333333l238.933333-409.6c17.066667-25.6" +
        " 8.533333-59.733333-17.066667-72.533334s-59.733333-8.533333-72.533333 17.066667l-4.266667" +
        " 4.266667L512 238.933333l-46.933333-81.066666c-12.8-29.866667-46.933333-38.4-72.533334" +
        "-25.6s-38.4 42.666667-21.333333 72.533333c0 0 0 4.266667 4.266667 4.266667l76.8 136.533333" +
        "-157.866667 273.066667H136.533333c-29.866667 0-51.2 25.6-51.2 51.2 0 29.866667 25.6 55.466667" +
        " 51.2 55.466666h469.333334l-38.4-64c-12.8-25.6-38.4-42.666667-68.266667-42.666666z m388.266667" +
        " 0h-153.6l-128-221.866667-25.6 42.666667c-21.333333 38.4-21.333333 89.6 0 128l174.933333" +
        " 302.933333c12.8 25.6 46.933333 38.4 72.533333 25.6 25.6-12.8 38.4-46.933333 25.6-72.533333" +
        " 0 0 0-4.266667-4.266666-4.266667L793.6 725.333333h89.6c29.866667 0 51.2-25.6 51.2-51.2" +
        " 4.266667-29.866667-21.333333-55.466667-46.933333-55.466666z";

    /// <summary>Document mark, from the user's <c>files.svg</c> (iconfont, 1024 grid, filled): two sheets, the front one with its folded corner.</summary>
    private const string FilesPath =
        "M469.333333 42.666667C399.146667 42.666667 341.333333 100.48 341.333333 170.666667v469.333333c0 " +
        "70.186667 57.813333 128 128 128h341.333334c70.186667 0 128-57.813333 " +
        "128-128V341.333333a42.666667 42.666667 0 0 0-42.666667-42.666666 42.666667 42.666667 0 0 " +
        "0-42.666667 42.666666v298.666667c0 24.064-18.602667 42.666667-42.666666 " +
        "42.666667h-341.333334c-24.064 0-42.666667-18.602667-42.666666-42.666667V170.666667c0-24.064 " +
        "18.602667-42.666667 42.666666-42.666667h170.666667a42.666667 42.666667 0 0 0 42.666667-42.666667" +
        " 42.666667 42.666667 0 0 0-42.666667-42.666666z M639.914667 42.666667A42.666667 42.666667 0 0 0 " +
        "597.333333 85.333333v213.333334c0 46.634667 38.698667 85.333333 85.333334 " +
        "85.333333h213.333333a42.666667 42.666667 0 0 0 42.666667-42.581333 145.066667 145.066667 0 0 " +
        "0-42.666667-103.082667L742.997333 85.333333a145.066667 145.066667 0 0 " +
        "0-103.082666-42.666666zM682.666667 145.749333L835.584 298.666667H682.666667zM213.333333 " +
        "256C143.146667 256 85.333333 313.813333 85.333333 384v469.333333c0 70.186667 57.813333 128 128 " +
        "128h341.333334c45.653333 0 88.021333-24.448 110.848-64a42.666667 42.666667 0 0 0-15.616-58.24 " +
        "42.666667 42.666667 0 0 0-58.325334 15.573334c-7.68 13.226667-21.589333 21.333333-36.906666 " +
        "21.333333H213.333333c-24.064 0-42.666667-18.602667-42.666666-42.666667V384c0-24.064 " +
        "18.602667-42.666667 42.666666-42.666667a42.666667 42.666667 0 0 0 42.666667-42.666666 42.666667 " +
        "42.666667 0 0 0-42.666667-42.666667z";

    /// <summary>Folder mark, from the user's <c>folder.svg</c> (iconfont, 1024 grid, filled).</summary>
    private const string FolderPath =
        "M810.666667 85.333333a85.333333 85.333333 0 0 1 85.333333 85.333334v152.021333c36.821333 " +
        "9.493333 64 42.88 64 82.645333v405.333334a128 128 0 0 1-128 128H192a128 128 0 0 " +
        "1-128-128V298.666667a85.376 85.376 0 0 1 64-82.645334V170.666667a85.333333 85.333333 0 0 1 " +
        "85.333333-85.333334h597.333334zM128.149333 296.170667L128 298.666667v512a64 64 0 0 0 60.245333 " +
        "63.893333L192 874.666667h640a64 64 0 0 0 63.893333-60.245334L896 810.666667V405.333333a21.333333" +
        " 21.333333 0 0 0-18.837333-21.184L874.666667 384H638.165333l-122.069333-101.717333a21.333333 " +
        "21.333333 0 0 0-10.688-4.736l-2.986667-0.213334H149.333333a21.333333 21.333333 0 0 0-21.184 " +
        "18.837334zM535.189333 213.333333l127.978667 106.666667H832V170.666667a21.333333 21.333333 0 0 " +
        "0-18.837333-21.184L810.666667 149.333333H213.333333a21.333333 21.333333 0 0 0-21.184 " +
        "18.837334L192 170.666667v42.666666h343.168z";

    /// <summary>Picture mark, from the user's <c>photo.svg</c> (iconfont, 1024 grid, filled outline).</summary>
    private const string ImagePath =
        "M361.984 576l-148.010667 192 596.010667 0-192-256-148.010667 192zM896 809.984q0 34.005333" +
        "-25.984 59.989333t-59.989333 25.984l-596.010667 0q-34.005333 0-59.989333-25.984t-25.984" +
        "-59.989333l0-596.010667q0-34.005333 25.984-59.989333t59.989333-25.984l596.010667 0q34.005333" +
        " 0 59.989333 25.984t25.984 59.989333l0 596.010667z";

    /// <summary>
    /// Tile mark, from the user's <c>widget.svg</c> (iconfont, 1024 grid, filled outline): two
    /// square tiles, a diamond and a rounded tile — the smallest shape that reads as "a board of
    /// widgets".
    /// </summary>
    private const string WidgetPath =
        "M426.666667 128H170.666667a42.666667 42.666667 0 0 0-42.666667 42.666667v256a42.666667 42.666667" +
        " 0 0 0 42.666667 42.666666h256a42.666667 42.666667 0 0 0 42.666666-42.666666V170.666667a42.666667" +
        " 42.666667 0 0 0-42.666666-42.666667zM384 384H213.333333V213.333333h170.666667v170.666667zM128" +
        " 853.333333a42.666667 42.666667 0 0 0 42.666667 42.666667h256a42.666667 42.666667 0 0 0" +
        " 42.666666-42.666667v-256a42.666667 42.666667 0 0 0-42.666666-42.666666H170.666667a42.666667" +
        " 42.666667 0 0 0-42.666667 42.666666v256z m85.333333-213.333333h170.666667v170.666667H213.333333" +
        "v-170.666667z m640 256a42.666667 42.666667 0 0 0 42.666667-42.666667v-256a42.666667 42.666667" +
        " 0 0 0-42.666667-42.666666h-256a42.666667 42.666667 0 0 0-42.666666 42.666666v256a42.666667" +
        " 42.666667 0 0 0 42.666666 42.666667h256z m-213.333333-256h170.666667v170.666667h-170.666667" +
        "v-170.666667z m115.498667-542.165333a42.624 42.624 0 0 0-60.330667 0l-170.666667 170.666666a" +
        "42.624 42.624 0 0 0 0 60.330667l170.666667 170.666667a42.538667 42.538667 0 0 0 60.330667 0" +
        "l170.666666-170.666667a42.624 42.624 0 0 0 0-60.330667l-170.666666-170.666666zM725.333333" +
        " 409.002667L614.997333 298.666667 725.333333 188.330667 835.669333 298.666667 725.333333 409.002667z";

    /// <summary>Clip mark, from the user's <c>clip.svg</c> (iconfont, 1024 grid, filled): the paperclip — "clip" is 剪贴板 here.</summary>
    private const string ClipboardPath =
        "M861.465718 104.803951 510.147304 475.408617 157.334863 109.286032c0 0-24.963555 " +
        "16.427134-24.787546 70.25918 0.242524 69.829392 57.171094 125.46041 109.395526 184.736445 " +
        "56.413847 64.034409 102.655008 112.7489 102.655008 112.7489l82.148975 86.376253-93.307106 " +
        "98.43694c-57.872059-23.357987-126.566604-10.066263-171.159218 38.703487-57.346079 " +
        "62.712297-52.997028 160.060439 9.698896 217.414705 62.736856 57.363476 160.068625 53.006238 " +
        "217.430054-9.716292 40.605813-44.381813 50.133817-106.083084 " +
        "30.096459-158.708652l91.38943-97.679693 93.696985 98.501408c-19.611663 52.806693-9.575076 " +
        "114.406656 31.397081 158.488641 57.874105 62.251809 155.23248 65.777099 217.479173 7.902994 " +
        "62.251809-57.864896 65.802682-155.252947 7.93267-217.48429-44.998867-48.38908-113.789603-" +
        "61.129242-171.46928-37.300534l-95.646384-99.249445 82.946131-88.643899c0 0 45.864584-49.079812 " +
        "101.736079-113.585965 51.739385-59.697637 108.196211-115.813702 107.85545-185.627744C886.581746 " +
        "121.010051 861.465718 104.803951 861.465718 104.803951zM331.035974 854.859765c-27.883047 " +
        "30.479175-75.16491 32.602535-105.655342 " +
        "4.727675-30.461779-27.864628-32.597419-75.173097-4.715395-105.652272 27.855418-30.489408 " +
        "75.178213-32.593326 105.655342-4.727675C356.809987 777.093611 358.903671 824.400033 331.035974 " +
        "854.859765zM485.548046 537.629727c14.579043-13.537318 37.355793-12.710486 50.903344 1.862417 " +
        "13.561877 14.563694 12.729929 37.356816-1.832741 50.903344-14.594393 13.551644-37.372166 " +
        "12.725836-50.918693-1.837858C470.138078 573.969377 470.97412 551.181371 485.548046 " +
        "537.629727zM803.46063 753.123686c28.126594 30.249955 26.393113 77.56661-3.850701 " +
        "105.692181-30.249955 28.125571-77.57275 " +
        "26.403347-105.697297-3.836375-28.111245-30.238698-26.38288-77.56661 " +
        "3.865028-105.692181C728.022497 721.16174 775.345292 722.894198 803.46063 753.123686z";

    /// <summary>
    /// Browser mark, from the user's <c>browser.svg</c> (iconfont, 1024 grid, filled outline):
    /// the window frame with its three dots and the empty page below.
    /// </summary>
    private const string WebPath =
        "M896 256V149.482667C896 137.578667 886.421333 128 874.517333 128H149.482667C137.578667 128 128" +
        " 137.578667 128 149.482667V256h768z m0 85.333333H128v533.184C128 886.421333 137.578667 896" +
        " 149.482667 896h725.034666c11.904 0 21.482667-9.578667 21.482667-21.482667V341.333333zM42.666667" +
        " 149.482667A106.752 106.752 0 0 1 149.482667 42.666667h725.034666A106.752 106.752 0 0 1" +
        " 981.333333 149.482667v725.034666A106.752 106.752 0 0 1 874.517333 981.333333H149.482667" +
        "A106.752 106.752 0 0 1 42.666667 874.517333V149.482667zM192 234.666667a42.666667 42.666667" +
        " 0 1 1 0-85.333334 42.666667 42.666667 0 0 1 0 85.333334z m106.666667 0a42.666667 42.666667" +
        " 0 1 1 0-85.333334 42.666667 42.666667 0 0 1 0 85.333334z m106.666666 0a42.666667 42.666667" +
        " 0 1 1 0-85.333334 42.666667 42.666667 0 0 1 0 85.333334z";

    /// <summary>Magnifier, from the user's <c>search.svg</c> (iconfont, 1024 grid, filled).</summary>
    private const string SearchPath =
        "M644.096 251.904a277.333333 277.333333 0 1 0-392.192 392.192 277.333333 277.333333 0 0 0 " +
        "392.192-392.192zM191.573333 191.573333a362.666667 362.666667 0 0 1 541.269334 " +
        "480.938667l228.053333 228.053333-60.330667 60.330667-228.053333-228.053333A362.709333 362.709333" +
        " 0 0 1 191.573333 191.573333z";

    /// <summary>
    /// Magnifier, 20-unit grid, <b>stroked</b>: the launcher's own fallback mark, kept for the one
    /// path that asks <see cref="Stroke"/> for a geometry no artwork covers (every kind is filled
    /// now — see <see cref="IsFilled"/> — so this is a safety net, not a mark on screen).
    /// </summary>
    private const string FallbackStrokePath =
        "M16.5,9 A7.5,7.5 0 1 1 1.5,9 A7.5,7.5 0 1 1 16.5,9 M14.6,14.6 L19.5,19.5";

    /// <summary>
    /// Calculator, 20-unit grid, stroked: rounded body, a display line and four keys — the smallest
    /// shape that still reads as "a calculation" at 20 px.
    /// </summary>
    private const string CalculatorPath =
        "M3.4,2.6 h13.2 a1.6,1.6 0 0 1 1.6,1.6 v11.6 a1.6,1.6 0 0 1 -1.6,1.6 h-13.2 a1.6,1.6 0 0 1 -1.6,-1.6" +
        " v-11.6 a1.6,1.6 0 0 1 1.6,-1.6 z M5.6,5.6 h8.8 M6.2,10.4 h0.1 M10,10.4 h0.1 M13.8,10.4 h0.1" +
        " M6.2,13.6 h0.1 M10,13.6 h0.1 M13.8,13.6 h0.1";

    private static readonly object Gate = new();
    private static readonly Dictionary<SpotlightGlyphKind, Geometry> FillCache = [];
    private static readonly Dictionary<SpotlightGlyphKind, Geometry> StrokeCache = [];

    /// <summary>
    /// True when this kind is drawn from SVG artwork (filled), not from a stroked path.
    /// <para>
    /// Every category mark — the magnifier included — is now artwork from a <c>.svg</c> file, so the
    /// calculator is the only stroked kind left. The field's magnifier used to be a hand-drawn
    /// stroke; it is the same 1024-grid iconfont family as the rest now, which is what keeps the
    /// search mark's weight identical whether it sits in the field, in a 分类圆钮 or in a row.
    /// </para>
    /// </summary>
    public static bool IsFilled(SpotlightGlyphKind kind) => kind != SpotlightGlyphKind.Calculator;

    /// <summary>The mark to paint, as a <b>filled</b> geometry. Falls back to the magnifier.</summary>
    public static Geometry Fill(SpotlightGlyphKind kind)
    {
        lock (Gate)
        {
            if (FillCache.TryGetValue(kind, out var cached)) return cached;
            var path = kind switch
            {
                SpotlightGlyphKind.Apps => AppsPath,
                SpotlightGlyphKind.Files => FilesPath,
                SpotlightGlyphKind.Folder => FolderPath,
                SpotlightGlyphKind.Image => ImagePath,
                SpotlightGlyphKind.Widget => WidgetPath,
                SpotlightGlyphKind.Clipboard => ClipboardPath,
                SpotlightGlyphKind.Web => WebPath,
                _ => SearchPath,
            };
            var geometry = Geometry.Parse(path);
            FillCache[kind] = geometry;
            return geometry;
        }
    }

    /// <summary>The mark to paint, as a <b>stroked</b> geometry. Falls back to the magnifier.</summary>
    public static Geometry Stroke(SpotlightGlyphKind kind)
    {
        lock (Gate)
        {
            if (StrokeCache.TryGetValue(kind, out var cached)) return cached;
            var path = kind switch
            {
                SpotlightGlyphKind.Calculator => CalculatorPath,
                _ => FallbackStrokePath,
            };
            var geometry = Geometry.Parse(path);
            StrokeCache[kind] = geometry;
            return geometry;
        }
    }
}

/// <summary>
/// The same artwork as a <b>bitmap</b>, for the two places that can only show an image: the result
/// row's 28 px icon slot and the preview pane's 72 px icon.
/// <para>
/// A web-search row has no file behind it, so there is nothing for the shell icon service to
/// extract; without this it would wear the "first character of the name" placeholder, which for a
/// search action reads as a broken icon. Rendering the geometry into a bitmap keeps one artwork in
/// the codebase instead of shipping a second PNG of the same mark.
/// </para>
/// <para>
/// The colour is deliberately a mid grey rather than a theme brush: the cached bitmap outlives a
/// light/dark flip, and 50% grey is the one value that stays legible on both cards.
/// </para>
/// </summary>
public static class SpotlightGlyphImages
{
    private static readonly Color Ink = Color.FromRgb(0x8A, 0x8A, 0x8E);
    private static readonly Dictionary<(SpotlightGlyphKind Kind, int Pixels), Bitmap> Cache = [];

    /// <summary>The mark rasterised to <paramref name="pixels"/> square, cached per kind + size.</summary>
    public static Bitmap Get(SpotlightGlyphKind kind, int pixels)
    {
        if (pixels <= 0) pixels = 64;

        lock (Cache)
        {
            if (Cache.TryGetValue((kind, pixels), out var cached)) return cached;

            var bitmap = Render(kind, pixels);
            Cache[(kind, pixels)] = bitmap;
            return bitmap;
        }
    }

    private static Bitmap Render(SpotlightGlyphKind kind, int pixels)
    {
        var target = new RenderTargetBitmap(new PixelSize(pixels, pixels), new Vector(96, 96));
        var geometry = SpotlightGlyphs.Fill(kind);
        var bounds = geometry.Bounds;

        using (var context = target.CreateDrawingContext(false))
        {
            if (bounds.Width > 0 && bounds.Height > 0)
            {
                // 92% of the box: the artwork already carries its own margin, and a mark that
                // touches the edge reads larger than the 20 px field glyph it is a kind of.
                var scale = Math.Min(pixels / bounds.Width, pixels / bounds.Height) * 0.92;
                var offsetX = (pixels - bounds.Width * scale) / 2 - bounds.X * scale;
                var offsetY = (pixels - bounds.Height * scale) / 2 - bounds.Y * scale;
                using (context.PushTransform(Matrix.CreateScale(scale, scale) *
                                             Matrix.CreateTranslation(offsetX, offsetY)))
                {
                    context.DrawGeometry(new SolidColorBrush(Ink), null, geometry);
                }
            }
        }

        return target;
    }
}
