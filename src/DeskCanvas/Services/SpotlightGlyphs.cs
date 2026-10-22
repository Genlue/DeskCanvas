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
/// <b>Filled marks</b> (every category artwork) come from the user's own SVG files, kept verbatim
/// as path data on their 1024-unit grid. They are <b>filled</b>, not stroked, with the theme's
/// primary text brush — the artwork carries no colour of its own on purpose, so it reads black on
/// the light card and light on the dark one exactly like the rest of the field's chrome.
/// </para>
/// <para>
/// <b>Drawn marks</b> (magnifier / calculator) stay stroked: they are ours, and a stroke keeps the
/// 20 px optical weight identical to the text beside them.
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

    /// <summary>Document mark, from the user's <c>file.svg</c> (iconfont, 1024 grid, filled outline).</summary>
    private const string FilesPath =
        "M854.6 288.6L639.4 73.4c-6-6-14.1-9.4-22.6-9.4H192c-17.7 0-32 14.3-32 32v832c0 17.7 14.3 32 32" +
        " 32h640c17.7 0 32-14.3 32-32V311.3c0-8.5-3.4-16.7-9.4-22.7zM790.2 326H602V137.8L790.2 326z" +
        " m1.8 562H232V136h302v216c0 23.2 18.8 42 42 42h216v494z";

    /// <summary>Folder mark, from the user's <c>folder.svg</c> (iconfont, 1024 grid, filled outline).</summary>
    private const string FolderPath =
        "M85.312 153.6v716.8h853.376V288H471.36L364.416 153.6h-279.04zM0 64h404.288L511.36 198.4H1024V960H0V64z";

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

    /// <summary>Clip mark, from the user's <c>clip.svg</c> (iconfont, 1024 grid, filled outline).</summary>
    private const string ClipboardPath =
        "M839.36 582.485333L593.706667 827.306667c-91.904 91.584-240.917333 91.584-332.842667 0a233.664" +
        " 233.664 0 0 1 0.021333-331.306667l327.552-326.4c55.637333-55.466667 146.176-55.424 201.813334" +
        " 0a141.44 141.44 0 0 1 0.106666 200.597333L462.741333 696.661333a50.346667 50.346667 0 0" +
        " 1-70.869333 0.064 49.429333 49.429333 0 0 1 0.106667-70.229333l245.568-244.714667a42.666667" +
        " 42.666667 0 0 0-60.224-60.458666L331.733333 566.058667a134.741333 134.741333 0 0 0-0.085333" +
        " 191.125333 135.68 135.68 0 0 0 191.317333-0.085333l327.637334-326.485334c89.024-88.746667" +
        " 88.96-232.682667-0.128-321.472C761.557333 20.544 617.173333 20.48 528.213333 109.141333" +
        "l-327.573333 326.4c-125.290667 124.842667-125.290667 327.338667-0.021333 452.181334 125.226667" +
        " 124.8 328.106667 124.778667 453.312 0l245.674666-244.821334a42.666667 42.666667 0 1 0" +
        "-60.245333-60.437333z";

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

    /// <summary>Magnifier, 20-unit grid, stroked (the launcher's own fallback mark).</summary>
    private const string SearchPath = "M16.5,9 A7.5,7.5 0 1 1 1.5,9 A7.5,7.5 0 1 1 16.5,9 M14.6,14.6 L19.5,19.5";

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

    /// <summary>True when this kind is drawn from SVG artwork (filled), not from a stroked path.</summary>
    public static bool IsFilled(SpotlightGlyphKind kind) => kind != SpotlightGlyphKind.Search
        && kind != SpotlightGlyphKind.Calculator;

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
                _ => SearchPath,
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
