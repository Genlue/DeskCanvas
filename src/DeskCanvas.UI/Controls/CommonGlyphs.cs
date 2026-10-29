using Avalonia.Media;

namespace DeskCanvas.UI.Controls;

/// <summary>
/// The general-purpose icon set shared by the host and the widgets (开发规范 §4.4).
/// <para>
/// Emoji and decorative Unicode characters are text glyphs: <c>Fill</c>/<c>Stroke</c> do not apply,
/// so they cannot follow the theme foreground, and their fixed baseline padding cannot be centred in
/// a round button. These marks replace them, and every one is <b>framed on a square 24 canvas</b>
/// (<see cref="GlyphGeometry.Square"/>) so an equally square <c>Path</c> / <c>PathIcon</c> box centres
/// it — Avalonia's stretch anchors a geometry's bounds at the box's top-left, so an un-framed mark
/// ends up visibly off-centre in its button.
/// </para>
/// <para>
/// Mark names describe the shape, not the usage: the same globe serves "open the website" in several
/// plugins. Add here when a second consumer appears; a mark that is genuinely local to one view can
/// stay inline in that view's AXAML.
/// </para>
/// </summary>
public static class CommonGlyphs
{
    // ---- time & weather -------------------------------------------------------------------

    /// <summary>Clock face (categories, history).</summary>
    public static readonly Geometry Clock = GlyphGeometry.Square(StreamGeometry.Parse(
        "M11.99 2C6.47 2 2 6.48 2 12s4.47 10 9.99 10C17.52 22 22 17.52 22 12S17.52 2 11.99 2z" +
        "M12 20c-4.42 0-8-3.58-8-8s3.58-8 8-8 8 3.58 8 8-3.58 8-8 8zm.5-13H11v6l5.25 3.15.75-1.23" +
        "-4.5-2.67z"));

    /// <summary>Sun (weather, brightness).</summary>
    public static readonly Geometry Sun = GlyphGeometry.Square(StreamGeometry.Parse(
        "M6.76 4.84l-1.8-1.79-1.41 1.41 1.79 1.79 1.42-1.41zM4 10.5H1v2h3v-2zm9-9.95h-2V3.5h2V.55z" +
        "m7.45 3.91l-1.41-1.41-1.79 1.79 1.41 1.41 1.79-1.79zm-3.21 13.7l1.79 1.8 1.41-1.41-1.8-1.79" +
        "-1.4 1.4zM20 10.5v2h3v-2h-3zm-8-5c-3.31 0-6 2.69-6 6s2.69 6 6 6 6-2.69 6-6-2.69-6-6-6z" +
        "m-1 16.95h2V19.5h-2v2.95zm-7.45-3.91l1.41 1.41 1.79-1.8-1.41-1.41-1.79 1.8z"));

    // ---- data & system --------------------------------------------------------------------

    /// <summary>Bar chart (system status, list mode).</summary>
    public static readonly Geometry Chart = GlyphGeometry.Square(StreamGeometry.Parse(
        "M5 9.2h3V19H5zM10.6 5h2.8v14h-2.8zm5.6 8H19v6h-2.8z"));

    /// <summary>Ticked circle (productivity, "done" states).</summary>
    public static readonly Geometry CheckCircle = GlyphGeometry.Square(StreamGeometry.Parse(
        "M12 2C6.48 2 2 6.48 2 12s4.48 10 10 10 10-4.48 10-10S17.52 2 12 2zm-2 15l-5-5 1.41-1.41" +
        "L10 14.17l7.59-7.59L19 8l-9 9z"));

    /// <summary>Nine-tile grid (launcher, app categories).</summary>
    public static readonly Geometry Apps = GlyphGeometry.Square(StreamGeometry.Parse(
        "M4 8h4V4H4v4zm6 12h4v-4h-4v4zm-6 0h4v-4H4v4zm0-6h4v-4H4v4zm6 0h4v-4h-4v4zm6-10v4h4V4h-4z" +
        "m-6 4h4V4h-4v4zm6 6h4v-4h-4v4zm0 6h4v-4h-4v4z"));

    /// <summary>Wrench (utilities, "build").</summary>
    public static readonly Geometry Wrench = GlyphGeometry.Square(StreamGeometry.Parse(
        "M22.7 19l-9.1-9.1c.9-2.3.4-5-1.5-6.9-2-2-5-2.4-7.4-1.3L9 6 6 9 1.6 4.7C.4 7.1.9 10.1 " +
        "2.9 12.1c1.9 1.9 4.6 2.4 6.9 1.5l9.1 9.1c.4.4 1 .4 1.4 0l2.3-2.3c.5-.4.5-1.1.1-1.4z"));

    /// <summary>Puzzle piece (container categories, extensions).</summary>
    public static readonly Geometry Puzzle = GlyphGeometry.Square(StreamGeometry.Parse(
        "M20.5 11H19V7c0-1.1-.9-2-2-2h-4V3.5C13 2.12 11.88 1 10.5 1S8 2.12 8 3.5V5H4c-1.1 0-1.99.9" +
        "-1.99 2v3.8H3.5c1.49 0 2.7 1.21 2.7 2.7s-1.21 2.7-2.7 2.7H2V20c0 1.1.9 2 2 2h3.8v-1.5" +
        "c0-1.49 1.21-2.7 2.7-2.7 1.49 0 2.7 1.21 2.7 2.7V22H17c1.1 0 2-.9 2-2v-4h1.5c1.38 0 2.5" +
        "-1.12 2.5-2.5S21.88 11 20.5 11z"));

    /// <summary>Pencil (edit, rename).</summary>
    public static readonly Geometry Pencil = GlyphGeometry.Square(StreamGeometry.Parse(
        "M4,20 L5.4,15.6 L15.6,5.4 L18.6,8.4 L8.4,18.6 Z M16.9,4.1 L19.9,7.1 L18.6,8.4 L15.6,5.4 Z"));

    /// <summary>Ruler (metrics, sizing).</summary>
    public static readonly Geometry Ruler = GlyphGeometry.Square(StreamGeometry.Parse(
        "M21 6H3c-1.1 0-2 .9-2 2v8c0 1.1.9 2 2 2h18c1.1 0 2-.9 2-2V8c0-1.1-.9-2-2-2z" +
        "M7 14H5v-4h2v4zm4 2H9v-2h2v2zm0-4H9V8h2v4zm4 4h-2v-2h2v2zm0-4h-2V8h2v4zm4 4h-2v-4h2v4z"));

    /// <summary>Frame (grid / centring).</summary>
    public static readonly Geometry Frame = GlyphGeometry.Square(StreamGeometry.Parse(
        "M3 3h8v2H5v6H3V3zm18 0v8h-2V5h-6V3h8zM3 21v-8h2v6h6v2H3zm18 0h-8v-2h6v-6h2v8z"));

    /// <summary>Sparkles (smoothing, enhancement).</summary>
    public static readonly Geometry Sparkle = GlyphGeometry.Square(StreamGeometry.Parse(
        "M12 2l1.9 5.1L19 9l-5.1 1.9L12 16l-1.9-5.1L5 9l5.1-1.9L12 2zm6 11l1.1 2.9L22 17l-2.9 1.1" +
        "L18 21l-1.1-2.9L14 17l2.9-1.1L18 13zM6 14l.9 2.1L9 17l-2.1.9L6 20l-.9-2.1L3 17l2.1-.9L6 14z"));

    /// <summary>Trash (clear, delete).</summary>
    public static readonly Geometry Trash = GlyphGeometry.Square(StreamGeometry.Parse(
        "M6 19c0 1.1.9 2 2 2h8c1.1 0 2-.9 2-2V7H6v12zM19 4h-3.5l-1-1h-5l-1 1H5v2h14V4z"));

    // ---- content & actions ----------------------------------------------------------------

    /// <summary>Globe (open a website, language).</summary>
    public static readonly Geometry Globe = GlyphGeometry.Square(StreamGeometry.Parse(
        "M12 2C6.48 2 2 6.48 2 12s4.48 10 10 10 10-4.48 10-10S17.52 2 12 2zm-1 17.93c-3.95-.49-7" +
        "-3.85-7-7.93 0-.62.08-1.21.21-1.79L9 15v1c0 1.1.9 2 2 2v1.93zm6.9-2.54c-.26-.81-1-1.39" +
        "-1.9-1.39h-1v-3c0-.55-.45-1-1-1H8v-2h2c.55 0 1-.45 1-1V7h2c1.1 0 2-.9 2-2v-.41" +
        "c2.93 1.19 5 4.06 5 7.41 0 2.08-.8 3.97-2.1 5.39z"));

    /// <summary>Die (random / "one more").</summary>
    public static readonly Geometry Dice = GlyphGeometry.Square(StreamGeometry.Parse(
        "M19 3H5c-1.1 0-2 .9-2 2v14c0 1.1.9 2 2 2h14c1.1 0 2-.9 2-2V5c0-1.1-.9-2-2-2zM7.5 18" +
        "c-.83 0-1.5-.67-1.5-1.5S6.67 15 7.5 15s1.5.67 1.5 1.5S8.33 18 7.5 18zm0-9C6.67 9 6 8.33 6 " +
        "7.5S6.67 6 7.5 6 9 6.67 9 7.5 8.33 9 7.5 9zm4.5 4.5c-.83 0-1.5-.67-1.5-1.5s.67-1.5 1.5-1.5 " +
        "1.5.67 1.5 1.5-.67 1.5-1.5 1.5zm4.5 4.5c-.83 0-1.5-.67-1.5-1.5s.67-1.5 1.5-1.5 1.5.67 1.5 " +
        "1.5-.67 1.5-1.5 1.5zm0-9c-.83 0-1.5-.67-1.5-1.5S15.67 6 16.5 6s1.5.67 1.5 1.5S17.33 9 " +
        "16.5 9z"));

    /// <summary>Heart outline (like).</summary>
    public static readonly Geometry HeartOutline = GlyphGeometry.Square(StreamGeometry.Parse(
        "M16.5 3c-1.74 0-3.41.81-4.5 2.09C10.91 3.81 9.24 3 7.5 3 4.42 3 2 5.42 2 8.5c0 3.78 3.4 " +
        "6.86 8.55 11.54L12 21.35l1.45-1.32C18.6 15.36 22 12.28 22 8.5 22 5.42 19.58 3 16.5 3z" +
        "m-4.4 15.55l-.1.1-.1-.1C7.14 14.24 4 11.39 4 8.5 4 6.5 5.5 5 7.5 5c1.54 0 3.04.99 3.57 " +
        "2.36h1.87C13.46 5.99 14.96 5 16.5 5c2 0 3.5 1.5 3.5 3.5 0 2.89-3.14 5.74-7.9 10.05z"));

    /// <summary>Heart, filled (liked).</summary>
    public static readonly Geometry HeartFilled = GlyphGeometry.Square(StreamGeometry.Parse(
        "M12 21.35l-1.45-1.32C5.4 15.36 2 12.28 2 8.5 2 5.42 4.42 3 7.5 3c1.74 0 3.41.81 4.5 " +
        "2.09C13.09 3.81 14.76 3 16.5 3 19.58 3 22 5.42 22 8.5c0 3.78-3.4 6.86-8.55 11.54L12 21.35z"));

    /// <summary>Star outline (favourite).</summary>
    public static readonly Geometry StarOutline = GlyphGeometry.Square(StreamGeometry.Parse(
        "M22 9.24l-7.19-.62L12 2 9.19 8.63 2 9.24l5.46 4.73L5.82 21 12 17.27 18.18 21l-1.63-7.03" +
        "L22 9.24zM12 15.4l-3.76 2.27 1-4.28-3.32-2.88 4.38-.38L12 6.1l1.71 4.04 4.38.38-3.32 " +
        "2.88 1 4.28L12 15.4z"));

    /// <summary>Star, filled (favourited).</summary>
    public static readonly Geometry StarFilled = GlyphGeometry.Square(StreamGeometry.Parse(
        "M12 17.27L18.18 21l-1.64-7.03L22 9.24l-7.19-.61L12 2 9.19 8.63 2 9.24l5.46 4.73L5.82 21z"));

    /// <summary>Music note (audio / lifestyle categories).</summary>
    public static readonly Geometry Music = GlyphGeometry.Square(StreamGeometry.Parse(
        "M12 3v10.55c-.59-.34-1.27-.55-2-.55-2.21 0-4 1.79-4 4s1.79 4 4 4 4-1.79 4-4V7h4V3h-6z"));

    /// <summary>Two overlapping pages (copy).</summary>
    public static readonly Geometry Copy = GlyphGeometry.Square(StreamGeometry.Parse(
        "M16 1H4c-1.1 0-2 .9-2 2v14h2V3h12V1zm3 4H8c-1.1 0-2 .9-2 2v14c0 1.1.9 2 2 2h11c1.1 0 2" +
        "-.9 2-2V7c0-1.1-.9-2-2-2zm0 16H8V7h11v14z"));

    /// <summary>A page with text lines (Markdown / document).</summary>
    public static readonly Geometry Document = GlyphGeometry.Square(StreamGeometry.Parse(
        "M14 2H6c-1.1 0-1.99.9-1.99 2L4 20c0 1.1.89 2 1.99 2H18c1.1 0 2-.9 2-2V8l-6-6zm2 16H8v-2" +
        "h8v2zm0-4H8v-2h8v2zm-3-5V3.5L18.5 9H13z"));

    /// <summary>Magnifier.</summary>
    public static readonly Geometry Search = GlyphGeometry.Square(StreamGeometry.Parse(
        "M15.5 14h-.79l-.28-.27C15.41 12.59 16 11.11 16 9.5 16 5.91 13.09 3 9.5 3S3 5.91 3 9.5 " +
        "5.91 16 9.5 16c1.61 0 3.09-.59 4.23-1.57l.27.28v.79l5 4.99L20.49 19l-4.99-5zm-6 0" +
        "C7.01 14 5 11.99 5 9.5S7.01 5 9.5 5 14 7.01 14 9.5 11.99 14 9.5 14z"));

    /// <summary>Bolt (charging, power).</summary>
    public static readonly Geometry Bolt = GlyphGeometry.Square(StreamGeometry.Parse(
        "M13,2 L5,14 L11,14 L10,22 L19,9 L12.5,9 Z"));

    /// <summary>
    /// Diagonal arrow (north-east). Square bounds on purpose: rotate it in place to get the other
    /// seven directions (picture alignment presets). Stroked, so it is authored with square bounds
    /// around its own centre instead of being framed — a frame would be stroked as a diagonal.
    /// </summary>
    public static readonly Geometry ArrowNorthEast = StreamGeometry.Parse(
        "M7,17 L17,7 M10,7 L17,7 L17,14");

    /// <summary>Check mark (menu selection), filled.</summary>
    public static readonly Geometry Check = GlyphGeometry.Square(StreamGeometry.Parse(
        "M9 16.17L4.83 12l-1.42 1.41L9 19 21 7l-1.41-1.41z"));
}
