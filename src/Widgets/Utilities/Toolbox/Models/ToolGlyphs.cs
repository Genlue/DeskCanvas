using Avalonia.Media;
using DeskCanvas.UI.Controls;

namespace Tools.Models;

/// <summary>
/// The Tools plugin's icon set, as geometry.
/// <para>
/// 开发规范 §4.4: a functional icon must be a vector path, never an emoji or a decorative
/// Unicode character. Emoji are text glyphs — <c>Fill</c>/<c>Stroke</c> do not apply to them, so
/// they cannot follow the theme foreground (6 variants: colour/monochrome × light/dark), their
/// fixed glyph padding cannot be centred in a 32 DIP badge, and their fallback rendering differs
/// between fonts. They were exactly the reported "二级面板里的 emoji 图标".
/// </para>
/// <para>
/// Every filled mark is framed on a square 24 canvas (<see cref="GlyphGeometry.Square"/>) so a
/// square <c>Path</c> box centres it: Avalonia's stretch anchors a geometry's bounds at the box's
/// top-left, so an un-framed 14 × 20 pin inside an 11 × 11 box sat 1.65 DIP left of centre — the
/// reported "置顶图标不在圆形中央". The one stroked mark (<see cref="Search"/>) is authored with
/// square bounds around its own centre instead, because a frame would be stroked too.
/// </para>
/// <para>
/// The marks are shared by the panel (32 DIP type badge, 11–13 DIP action marks), the widget card
/// and the calculator's history list, so the plugin reads as one icon family. Every geometry is
/// parsed once and immutable afterwards; Avalonia can share one instance between any number of
/// <see cref="Avalonia.Controls.Shapes.Path"/> / <see cref="Avalonia.Controls.PathIcon"/> elements.
/// </para>
/// </summary>
public static class ToolGlyphs
{
    /// <summary>Plain text entry: a document with text lines.</summary>
    public static readonly Geometry Text = GlyphGeometry.Square(StreamGeometry.Parse(
        "M14 2H6c-1.1 0-1.99.9-1.99 2L4 20c0 1.1.89 2 1.99 2H18c1.1 0 2-.9 2-2V8l-6-6z" +
        "m2 16H8v-2h8v2zm0-4H8v-2h8v2zm-3-5V3.5L18.5 9H13z"));

    /// <summary>Image entry: a photo frame with a horizon and a sun.</summary>
    public static readonly Geometry Image = GlyphGeometry.Square(StreamGeometry.Parse(
        "M21 19V5c0-1.1-.9-2-2-2H5c-1.1 0-2 .9-2 2v14c0 1.1.9 2 2 2h14c1.1 0 2-.9 2-2z" +
        "M8.5 13.5l2.5 3.01L14.5 12l4.5 6H5l3.5-4.5z"));

    /// <summary>File entry: a folder.</summary>
    public static readonly Geometry Files = GlyphGeometry.Square(StreamGeometry.Parse(
        "M10 4H4c-1.1 0-1.99.9-1.99 2L2 18c0 1.1.9 2 2 2h16c1.1 0 2-.9 2-2V8c0-1.1-.9-2-2-2h-8l-2-2z"));

    /// <summary>Unknown / empty entry: the clipboard itself (also the panel's empty-state mark).</summary>
    public static readonly Geometry Clipboard = GlyphGeometry.Square(StreamGeometry.Parse(
        "M19 3h-4.18C14.4 1.84 13.3 1 12 1c-1.3 0-2.4.84-2.82 2H5c-1.1 0-2 .9-2 2v14c0 1.1.9 2 2 2h14" +
        "c1.1 0 2-.9 2-2V5c0-1.1-.9-2-2-2zm-7 0c.55 0 1 .45 1 1s-.45 1-1 1-1-.45-1-1 .45-1 1-1z" +
        "m7 16H5V5h2v3h10V5h2v14z"));

    /// <summary>
    /// Pin silhouette: filled at rest and accent-filled once the entry is pinned, so one geometry
    /// covers both states and the button never changes its own box.
    /// </summary>
    public static readonly Geometry Pin = GlyphGeometry.Square(StreamGeometry.Parse(
        "M16 9V4h1c.55 0 1-.45 1-1s-.45-1-1-1H7c-.55 0-1 .45-1 1s.45 1 1 1h1v5c0 1.66-1.34 3-3 3v2" +
        "h5.97v7l1 1 1-1v-7H19v-2c-1.66 0-3-1.34-3-3z"));

    /// <summary>Copy mark: two overlapping pages (the calculator's history actions).</summary>
    public static readonly Geometry Copy = GlyphGeometry.Square(StreamGeometry.Parse(
        "M16 1H4c-1.1 0-2 .9-2 2v14h2V3h12V1zm3 4H8c-1.1 0-2 .9-2 2v14c0 1.1.9 2 2 2h11" +
        "c1.1 0 2-.9 2-2V7c0-1.1-.9-2-2-2zm0 16H8V7h11v14z"));

    /// <summary>
    /// Delete mark: a solid cross. Solid (not a stroked pair of lines) because
    /// <see cref="Avalonia.Controls.PathIcon"/> paints its geometry with a fill, and a filled cross
    /// keeps its visual weight at 9–11 DIP.
    /// </summary>
    public static readonly Geometry Close = GlyphGeometry.Square(StreamGeometry.Parse(
        "M19,6.41 L17.59,5 L12,10.59 L6.41,5 L5,6.41 L10.59,12 L5,17.59 L6.41,19 L12,13.41 " +
        "L17.59,19 L19,17.59 L13.41,12 Z"));

    /// <summary>
    /// Search field mark: the magnifier lens and handle, filled. Not a stroked arc on purpose: an
    /// arc's <see cref="Geometry.Bounds"/> includes its control points, so the drawn ink ends up
    /// offset inside the box (measured 4.5 DIP left in an 11 DIP box) — a defect this icon had
    /// before it was framed like every other mark.
    /// </summary>
    public static readonly Geometry Search = GlyphGeometry.Square(StreamGeometry.Parse(
        "M15.5 14h-.79l-.28-.27C15.41 12.59 16 11.11 16 9.5 16 5.91 13.09 3 9.5 3S3 5.91 3 9.5 " +
        "5.91 16 9.5 16c1.61 0 3.09-.59 4.23-1.57l.27.28v.79l5 4.99L20.49 19l-4.99-5z" +
        "m-6 0C7.01 14 5 11.99 5 9.5S7.01 5 9.5 5 14 7.01 14 9.5 11.99 14 9.5 14z"));

    /// <summary>The geometry for one clipboard entry type.</summary>
    public static Geometry ForType(ClipboardType type) => type switch
    {
        ClipboardType.Text => Text,
        ClipboardType.Image => Image,
        ClipboardType.Files => Files,
        _ => Clipboard
    };
}
