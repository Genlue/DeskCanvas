using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Avalonia.Media.Imaging;
using Docnet.Core;
using Docnet.Core.Models;
using SkiaSharp;

namespace DeskCanvas.Services;

/// <summary>
/// What the preview pane should draw for a file. The flavor is a <b>content</b> classification,
/// not a UI one: the pane turns it into a localized type line (图片 / PDF 文档 / 演示文稿 …), and
/// the service never produces user-facing copy itself.
/// </summary>
public enum SpotlightPreviewFlavor
{
    Unknown,
    Image,
    Pdf,
    Document,
    Presentation,
    Spreadsheet,
    Text,
    Video,
    Audio,
    Archive,
    Folder,
}

/// <summary>One language-neutral fact the pane appends to its label/value list.</summary>
public enum SpotlightPreviewFactKind
{
    /// <summary>Pixel size of an image ("4032 × 3024").</summary>
    Dimensions,

    /// <summary>Last write time, already formatted ("2026-10-21 23:04").</summary>
    Modified,

    /// <summary>Entries directly under a folder ("128", "999+").</summary>
    Items,

    /// <summary>Worksheet names of a workbook ("Sheet1, 数据").</summary>
    Sheets,

    /// <summary>Page count of a document ("12").</summary>
    Pages,
}

public sealed record SpotlightPreviewFact(SpotlightPreviewFactKind Kind, string Value);

/// <summary>
/// The rich part of a preview: a hero image (thumbnail), a short text excerpt, and the facts the
/// pane adds to its detail rows.
/// <para>
/// Everything here is optional by design — a file with no thumbnail handler and no readable text
/// still previews as its icon plus location/size, which is the pane's baseline.
/// </para>
/// </summary>
public sealed record SpotlightPreviewContent(
    SpotlightPreviewFlavor Flavor,
    Bitmap? Media,
    string Excerpt,
    IReadOnlyList<SpotlightPreviewFact> Facts)
{
    public static SpotlightPreviewContent For(SpotlightPreviewFlavor flavor)
        => new(flavor, null, string.Empty, Array.Empty<SpotlightPreviewFact>());
}

/// <summary>
/// Builds the rich preview for a Spotlight file hit.
/// <para>
/// <b>Three sources, in this order:</b>
/// </para>
/// <list type="number">
/// <item>
/// A dependency-free fallback for OOXML packages: the thumbnail PowerPoint/Word embed in
/// <c>docProps/thumbnail.jpeg</c>, plus the document's own text (slide titles and bullets,
/// paragraphs, sheet names). It is a local zip entry, so it is cheaper than starting a shell
/// handler — and it is what keeps presentations and documents useful on a machine without
/// Office installed, where the shell has no handler to call.
/// </item>
/// <item>
/// The shell's own thumbnail (<see cref="ShellIconService.GetThumbnailAsync"/>) — the same image
/// Explorer draws. That covers images, PDFs, Office documents and videos on any machine whose
/// registered handlers can render them, with no decoder of our own.
/// </item>
/// <item>
/// The head of a text file, for the formats that have no thumbnail at all.
/// </item>
/// </list>
/// <para>
/// <b>Why the pane can afford this:</b> the service is only ever called for the selected row,
/// behind a debounce (see <c>SpotlightViewModel.BindPreview</c>), and every expensive step runs on
/// the thread pool. Nothing here touches the UI thread, and nothing throws: a corrupt file
/// degrades to the icon preview.
/// </para>
/// </summary>
public sealed class SpotlightPreviewService
{
    /// <summary>Characters kept from a text file. Enough for a paragraph or two, not a scrollbar.</summary>
    private const int ExcerptChars = 1400;

    /// <summary>Text files above this size are not read at all (a 200 MB log is not a preview).</summary>
    private const long TextFileLimit = 4L * 1024 * 1024;

    /// <summary>OOXML packages above this size are not unzipped (keeps a huge deck off the hot path).</summary>
    private const long OfficeFileLimit = 80L * 1024 * 1024;

    /// <summary>How many folder entries are counted before the count degrades to "999+".</summary>
    private const int FolderCountLimit = 999;

    /// <summary>Paragraphs/slides read out of one OOXML document.</summary>
    private const int OfficeParagraphLimit = 24;

    /// <summary>
    /// Extracted text/facts kept per path. Bounded because the entry carries its bitmap: a
    /// thumbnail is already held by <see cref="ShellIconService"/>, so this only pins it longer —
    /// 32 entries keeps that at the shell cache's own order of magnitude.
    /// </summary>
    private const int ContentCacheLimit = 32;

    private readonly ShellIconService icons;

    private readonly object gate = new();
    private readonly Dictionary<string, SpotlightPreviewContent> contentCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<string> contentCacheOrder = new();

    public SpotlightPreviewService(ShellIconService icons) => this.icons = icons;

    /// <summary>
    /// Load the preview for <paramref name="path"/>. Never throws and never returns null; the
    /// cancellation token stops the work when the selection moves on.
    /// </summary>
    public async Task<SpotlightPreviewContent> LoadAsync(string path, bool isFolder, CancellationToken cancellationToken)
    {
        var flavor = Classify(path, isFolder);

        // Network shares are excluded on purpose: a shell thumbnail handler or a text read on a
        // sleeping NAS can block for tens of seconds, and the launcher's preview is not worth a
        // hung worker. The row still previews as icon + location.
        if (IsNetworkPath(path)) return SpotlightPreviewContent.For(flavor);

        var cacheKey = CacheKey(path, isFolder);
        if (TryGetCached(cacheKey, out var cached) && cached.Flavor == flavor)
        {
            // An embedded OOXML thumbnail came from the file itself and is already in the cache;
            // a shell thumbnail is re-read from ShellIconService's own memory cache, which is
            // where the bitmap lives in the first place.
            var cachedMedia = cached.Media
                ?? await LoadThumbnailAsync(path, flavor, cancellationToken).ConfigureAwait(false);
            return cached with { Media = cachedMedia };
        }

        SpotlightPreviewContent content;
        try
        {
            content = await Task.Run(() => LoadLocal(path, flavor, cancellationToken), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            content = SpotlightPreviewContent.For(flavor);
        }

        if (content.Media == null)
        {
            var media = await LoadThumbnailAsync(path, flavor, cancellationToken).ConfigureAwait(false);
            content = content with { Media = media };
        }

        Store(cacheKey, content);
        return content;
    }

    /// <summary>The type line's classification. Extension-only, like the category bar.</summary>
    public static SpotlightPreviewFlavor Classify(string? path, bool isFolder)
    {
        if (isFolder) return SpotlightPreviewFlavor.Folder;
        if (string.IsNullOrWhiteSpace(path)) return SpotlightPreviewFlavor.Unknown;

        var extension = Path.GetExtension(path);
        if (extension.Length < 2)
        {
            // Extension-less text files a developer actually searches for (Makefile, Dockerfile).
            return TextExtensions.Contains(Path.GetFileName(path))
                ? SpotlightPreviewFlavor.Text
                : SpotlightPreviewFlavor.Unknown;
        }

        var key = extension[1..].ToLowerInvariant();

        if (SpotlightFileKinds.IsImage(path)) return SpotlightPreviewFlavor.Image;
        if (key == "pdf") return SpotlightPreviewFlavor.Pdf;
        if (DocumentExtensions.Contains(key)) return SpotlightPreviewFlavor.Document;
        if (PresentationExtensions.Contains(key)) return SpotlightPreviewFlavor.Presentation;
        if (SpreadsheetExtensions.Contains(key)) return SpotlightPreviewFlavor.Spreadsheet;
        if (TextExtensions.Contains(key)) return SpotlightPreviewFlavor.Text;
        if (VideoExtensions.Contains(key)) return SpotlightPreviewFlavor.Video;
        if (AudioExtensions.Contains(key)) return SpotlightPreviewFlavor.Audio;
        if (ArchiveExtensions.Contains(key)) return SpotlightPreviewFlavor.Archive;
        return SpotlightPreviewFlavor.Unknown;
    }

    // ---- local content ------------------------------------------------------------

    /// <summary>Everything that reads the file itself. Runs on the thread pool.</summary>
    private SpotlightPreviewContent LoadLocal(string path, SpotlightPreviewFlavor flavor, CancellationToken cancellationToken)
    {
        var facts = new List<SpotlightPreviewFact>(4);
        string excerpt = string.Empty;
        Bitmap? media = null;

        if (flavor == SpotlightPreviewFlavor.Folder)
        {
            var count = CountEntries(path);
            if (count >= 0) facts.Add(new SpotlightPreviewFact(SpotlightPreviewFactKind.Items, count >= FolderCountLimit ? $"{FolderCountLimit}+" : count.ToString()));
            AddModified(path, facts);
            return new SpotlightPreviewContent(flavor, null, string.Empty, facts);
        }

        AddModified(path, facts);

        switch (flavor)
        {
            case SpotlightPreviewFlavor.Image:
                // Decoded in-process instead of through the shell. A picture is the one type we
                // can always render ourselves, and the shell's thumbnail path is the least
                // reliable part of the system: it depends on a registered handler, on a
                // surrogate process, and on the caller's integrity level. Skia has none of
                // those failure modes, and it never needs a handler round-trip.
                AddImageDimensions(path, facts);
                media = DecodeImageThumbnail(path, cancellationToken);
                break;

            case SpotlightPreviewFlavor.Document:
            case SpotlightPreviewFlavor.Presentation:
            case SpotlightPreviewFlavor.Spreadsheet:
                // The embedded thumbnail is read before the shell is asked: it is a local zip
                // entry (microseconds) while the shell may have to start a handler process.
                var office = ReadOffice(path, flavor, cancellationToken);
                media = office.Media;
                excerpt = office.Excerpt;
                facts.InsertRange(0, office.Facts);
                break;

            case SpotlightPreviewFlavor.Text:
                excerpt = ReadTextExcerpt(path);
                break;

            case SpotlightPreviewFlavor.Pdf:
                // The shell has no PDF thumbnail handler here (and on any machine without a
                // reader that registers one), so a PDF needs its own rasteriser. Page one is
                // what Explorer's own preview shows; the page count is the one metadata a
                // rasteriser can give for free.
                media = RenderPdfFirstPage(path, out var pages);
                if (pages > 0)
                {
                    facts.Insert(0, new SpotlightPreviewFact(SpotlightPreviewFactKind.Pages, pages.ToString()));
                }
                break;
        }

        return new SpotlightPreviewContent(flavor, media, excerpt, facts);
    }

    /// <summary>Thumbnails are only worth asking for on flavors that can actually have one.</summary>
    private Task<Bitmap?> LoadThumbnailAsync(string path, SpotlightPreviewFlavor flavor, CancellationToken cancellationToken)
    {
        if (flavor is SpotlightPreviewFlavor.Text or SpotlightPreviewFlavor.Folder or SpotlightPreviewFlavor.Unknown)
        {
            // Unknown is excluded too: without a known type the shell almost always answers with
            // the generic icon, and an icon is exactly what the pane already shows.
            return Task.FromResult<Bitmap?>(null);
        }

        return icons.GetThumbnailAsync(path, ShellIconService.PreferredThumbnailPixels, cancellationToken);
    }

    private static void AddModified(string path, List<SpotlightPreviewFact> facts)
    {
        try
        {
            // Invariant formatting on purpose: the pane's labels are localized, a timestamp is
            // read the same way in every language.
            facts.Add(new SpotlightPreviewFact(
                SpotlightPreviewFactKind.Modified,
                File.GetLastWriteTime(path).ToString("yyyy-MM-dd HH:mm")));
        }
        catch
        {
            // A vanished file simply has no modified line.
        }
    }

    private static void AddImageDimensions(string path, List<SpotlightPreviewFact> facts)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var codec = SKCodec.Create(stream);
            if (codec != null && codec.Info.Width > 0 && codec.Info.Height > 0)
            {
                facts.Insert(0, new SpotlightPreviewFact(
                    SpotlightPreviewFactKind.Dimensions, $"{codec.Info.Width} × {codec.Info.Height}"));
            }
        }
        catch
        {
            // Dimensions are a nicety; an unreadable header must not lose the thumbnail.
        }
    }

    /// <summary>Refuse to decode absurd pixel counts (a 200 MP scan is not a preview).</summary>
    private const long MaxDecodedPixels = 80_000_000;

    /// <summary>
    /// Render a PDF's first page with PDFium and hand back a preview-sized bitmap.
    /// <para>
    /// Why this exists at all: <c>IShellItemImageFactory</c> with <c>SIIGBF_THUMBNAILONLY</c>
    /// returns nothing for a PDF unless some installed reader registered a thumbnail handler —
    /// Edge does not. Docnet.Core bundles pdfium per RID and renders into a BGRA buffer, which is
    /// wrapped straight into Skia, so nothing else in the dependency graph moves.
    /// </para>
    /// <para>
    /// The viewport is square (512×512) on purpose: Docnet scales the page to <b>fit</b> it
    /// without touching the aspect ratio, so a portrait page stays portrait instead of being
    /// stretched to fill. Returns null — and leaves the shell/icon path to answer — for an
    /// encrypted, truncated or otherwise unopenable file.
    /// </para>
    /// </summary>
    private static Bitmap? RenderPdfFirstPage(string path, out int pageCount)
    {
        pageCount = 0;
        try
        {
            using var reader = DocLib.Instance.GetDocReader(path, new PageDimensions(ShellIconService.PreferredThumbnailPixels, ShellIconService.PreferredThumbnailPixels));
            pageCount = reader.GetPageCount();
            if (pageCount <= 0) return null;

            using var page = reader.GetPageReader(0);
            var width = page.GetPageWidth();
            var height = page.GetPageHeight();
            if (width <= 0 || height <= 0) return null;

            var pixels = page.GetImage();
            if (pixels == null || pixels.Length < width * height * 4) return null;

            // PDFium's BGRA is straight (not premultiplied) alpha; marking it premul would darken
            // every transparent pixel on a page with a transparent background.
            using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Unpremul));
            var destination = bitmap.GetPixels();
            if (destination == IntPtr.Zero) return null;

            Marshal.Copy(pixels, 0, destination, width * height * 4);

            using var image = SKImage.FromBitmap(bitmap);
            using var data = image.Encode(SKEncodedImageFormat.Png, 92);
            if (data == null) return null;

            using var memory = new MemoryStream(data.ToArray(), writable: false);
            return new Bitmap(memory);
        }
        catch
        {
            // An encrypted or damaged PDF degrades to the shell handler and then to the icon.
            pageCount = 0;
            return null;
        }
    }

    /// <summary>
    /// Decode a picture to preview size without the shell.
    /// <para>
    /// This is deliberately independent of <see cref="ShellIconService.GetThumbnailAsync"/>: the
    /// shell thumbnail path needs a registered handler and a surrogate process, and returns
    /// nothing at all when either is missing — which is exactly the "images show a generic icon"
    /// failure. Skia reads the file directly, so a picture always previews.
    /// </para>
    /// <para>
    /// The header is read first and <c>GetScaledDimensions</c> asks the codec for its supported
    /// reduced size (JPEG gives 1/2, 1/4, 1/8), so a 24 MP photo is never decoded at full size
    /// just to fill a 240 DIP box. EXIF orientation is applied by hand: the shell used to do it
    /// for us, and a sideways portrait is the most visible possible preview bug.
    /// </para>
    /// </summary>
    private static Bitmap? DecodeImageThumbnail(string path, CancellationToken cancellationToken)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var codec = SKCodec.Create(stream);
            if (codec == null) return null;

            var source = codec.Info;
            if (source.Width <= 0 || source.Height <= 0) return null;
            if ((long)source.Width * source.Height > MaxDecodedPixels) return null;

            // Orientations 5–8 transpose the image, so the displayed box is the swapped size.
            var origin = codec.EncodedOrigin;
            var quarterTurn = origin is SKEncodedOrigin.RightTop or SKEncodedOrigin.LeftBottom;
            var displayWidth = quarterTurn ? source.Height : source.Width;
            var displayHeight = quarterTurn ? source.Width : source.Height;

            var scale = Math.Min(1.0, (double)ShellIconService.PreferredThumbnailPixels / Math.Max(displayWidth, displayHeight));
            var outputWidth = Math.Max(1, (int)Math.Round(displayWidth * scale));
            var outputHeight = Math.Max(1, (int)Math.Round(displayHeight * scale));

            // Ask the codec for the largest size it can decode natively below the target; the
            // last step to exactly outputWidth/Height is a normal resize.
            var supported = codec.GetScaledDimensions((float)scale);
            var decodeWidth = Math.Max(1, Math.Min(source.Width, supported.Width));
            var decodeHeight = Math.Max(1, Math.Min(source.Height, supported.Height));

            using var decoded = new SKBitmap(new SKImageInfo(decodeWidth, decodeHeight, SKColorType.Bgra8888, SKAlphaType.Premul));
            var result = codec.GetPixels(decoded.Info, decoded.GetPixels());
            if (result is not (SKCodecResult.Success or SKCodecResult.IncompleteInput)) return null;

            cancellationToken.ThrowIfCancellationRequested();

            using var scaled = new SKBitmap(new SKImageInfo(outputWidth, outputHeight, SKColorType.Bgra8888, SKAlphaType.Premul));
            using (var canvas = new SKCanvas(scaled))
            {
                canvas.Clear(SKColors.Transparent);
                ApplyOrientation(canvas, origin, outputWidth, outputHeight);

                // In the rotated space the drawing box is the source's aspect, i.e. swapped for
                // the quarter turns.
                var drawWidth = quarterTurn ? outputHeight : outputWidth;
                var drawHeight = quarterTurn ? outputWidth : outputHeight;
                using var image = SKImage.FromBitmap(decoded);
                canvas.DrawImage(image, new SKRect(0, 0, drawWidth, drawHeight),
                    new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
            }

            using var preview = SKImage.FromBitmap(scaled);
            using var data = preview.Encode(SKEncodedImageFormat.Png, 92);
            if (data == null) return null;

            using var memory = new MemoryStream(data.ToArray(), writable: false);
            return new Bitmap(memory);
        }
        catch
        {
            // A format Skia cannot read (HEIC, RAW, SVG) falls through to the shell handler.
            return null;
        }
    }

    /// <summary>Map an EXIF orientation onto the canvas, so the drawing rect lands upright.</summary>
    private static void ApplyOrientation(SKCanvas canvas, SKEncodedOrigin origin, int width, int height)
    {
        switch (origin)
        {
            case SKEncodedOrigin.TopRight:      // mirrored horizontally
                canvas.Translate(width, 0);
                canvas.Scale(-1, 1);
                break;
            case SKEncodedOrigin.BottomRight:   // 180°
                canvas.Translate(width, height);
                canvas.RotateDegrees(180);
                break;
            case SKEncodedOrigin.BottomLeft:    // mirrored vertically
                canvas.Translate(0, height);
                canvas.Scale(1, -1);
                break;
            case SKEncodedOrigin.RightTop:      // 90° clockwise (the common phone photo)
                canvas.Translate(width, 0);
                canvas.RotateDegrees(90);
                break;
            case SKEncodedOrigin.LeftBottom:    // 270° clockwise
                canvas.Translate(0, height);
                canvas.RotateDegrees(-90);
                break;

            // LeftTop (5) and RightBottom (7) are mirrored diagonals. A wrong mirror is worse
            // than an unmirrored photo, so they are drawn as stored.
        }
    }

    /// <summary>Direct children, capped. -1 when the folder cannot be listed (permissions, gone).</summary>
    private static int CountEntries(string path)
    {
        try
        {
            var count = 0;
            foreach (var _ in Directory.EnumerateFileSystemEntries(path))
            {
                if (++count >= FolderCountLimit) break;
            }

            return count;
        }
        catch
        {
            return -1;
        }
    }

    // ---- text ---------------------------------------------------------------------

    private static string ReadTextExcerpt(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length <= 0 || info.Length > TextFileLimit) return string.Empty;

            using var stream = File.OpenRead(path);
            var take = (int)Math.Min(info.Length, ExcerptChars * 4L);
            var buffer = new byte[take];
            var read = stream.Read(buffer, 0, take);
            if (read <= 0) return string.Empty;

            var text = Decode(buffer.AsSpan(0, read));
            return Clip(text);
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// Decode a text file's head. BOM first, then strict-ish UTF-8, then GB18030 — a Chinese
    /// Windows still ships plenty of GBK .txt files, and showing mojibake is worse than showing
    /// nothing.
    /// </summary>
    private static string Decode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return Encoding.UTF8.GetString(bytes[3..]);
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            return Encoding.Unicode.GetString(bytes[2..]);
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            return Encoding.BigEndianUnicode.GetString(bytes[2..]);

        // A NUL in the head is the classic binary marker; UTF-16 without a BOM is rare enough
        // that treating it as binary is the safer read.
        if (bytes.IndexOf((byte)0) >= 0) return string.Empty;

        var utf8 = new UTF8Encoding(false, false).GetString(bytes);
        if (utf8.IndexOf('\uFFFD') < 0) return utf8;

        try
        {
            return Gb18030.GetString(bytes);
        }
        catch
        {
            return utf8;
        }
    }

    /// <summary>Trim to the excerpt budget at a line boundary, and mark that it was cut.</summary>
    private static string Clip(string text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;

        text = text.Replace("\r\n", "\n").Replace('\r', '\n').TrimEnd();
        if (text.Length <= ExcerptChars) return text;

        var cut = text.LastIndexOf('\n', Math.Min(ExcerptChars, text.Length - 1));
        if (cut < ExcerptChars / 2) cut = ExcerptChars;
        return text[..cut].TrimEnd() + "\n…";
    }

    private static Encoding Gb18030
    {
        get
        {
            EnsureCodePages();
            return Encoding.GetEncoding("GB18030");
        }
    }

    private static bool codePagesReady;

    private static void EnsureCodePages()
    {
        if (codePagesReady) return;
        codePagesReady = true;
        try
        {
            // The provider ships in the shared framework on Windows; the guard keeps a trimmed
            // runtime from turning a legacy .txt into an exception.
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        }
        catch
        {
            // No legacy code pages: UTF-8 files still preview.
        }
    }

    // ---- OOXML --------------------------------------------------------------------

    private readonly record struct OfficePreview(string Excerpt, Bitmap? Media, IReadOnlyList<SpotlightPreviewFact> Facts);

    /// <summary>
    /// Read an OOXML package without opening Office: the embedded preview image, the sheet names,
    /// and the first few paragraphs/slides of text. Non-OOXML (legacy .doc/.ppt) and broken zips
    /// return an empty preview — the shell thumbnail still applies.
    /// </summary>
    private static OfficePreview ReadOffice(string path, SpotlightPreviewFlavor flavor, CancellationToken cancellationToken)
    {
        var facts = new List<SpotlightPreviewFact>(2);
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > OfficeFileLimit) return new OfficePreview(string.Empty, null, facts);

            using var archive = ZipFile.OpenRead(path);

            var media = ReadEmbeddedThumbnail(archive);
            var excerpt = flavor switch
            {
                SpotlightPreviewFlavor.Presentation => ReadSlides(archive, cancellationToken),
                SpotlightPreviewFlavor.Document => ReadDocument(archive, cancellationToken),
                SpotlightPreviewFlavor.Spreadsheet => ReadSheets(archive, facts),
                _ => string.Empty,
            };

            return new OfficePreview(excerpt, media, facts);
        }
        catch
        {
            return new OfficePreview(string.Empty, null, facts);
        }
    }

    /// <summary>PowerPoint and Word store the last-rendered preview page in docProps.</summary>
    private static Bitmap? ReadEmbeddedThumbnail(ZipArchive archive)
    {
        // Matched case-insensitively: the OPC spec lower-cases these names, but files written by
        // third-party exporters do not always agree, and a zip lookup is exact by default.
        var candidates = archive.Entries
            .Where(entry => ThumbnailEntryNames.Contains(entry.FullName))
            .ToList();

        foreach (var entry in candidates)
        {
            if (entry.Length <= 0 || entry.Length > 4L * 1024 * 1024) continue;

            try
            {
                using var entryStream = entry.Open();
                using var buffer = new MemoryStream((int)entry.Length);
                entryStream.CopyTo(buffer);
                buffer.Position = 0;
                return new Bitmap(buffer);
            }
            catch
            {
                // A WMF/EMF preview (some Office versions) is not decodable here; try the next.
            }
        }

        return null;
    }

    private static readonly HashSet<string> ThumbnailEntryNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "docProps/thumbnail.jpeg", "docProps/thumbnail.jpg", "docProps/thumbnail.png",
    };

    private static string ReadSlides(ZipArchive archive, CancellationToken cancellationToken)
    {
        var slides = archive.Entries
            .Select(entry => (Entry: entry, Number: SlideNumber(entry.FullName)))
            .Where(pair => pair.Number > 0)
            .OrderBy(pair => pair.Number)
            .Take(2)
            .ToList();

        var lines = new List<string>();
        foreach (var (entry, _) in slides)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var text = ReadParagraphs(entry, "p", "t", 8);
            if (text.Count > 0) lines.AddRange(text);
        }

        return Clip(string.Join("\n", lines));
    }

    private static string ReadDocument(ZipArchive archive, CancellationToken cancellationToken)
    {
        var entry = archive.GetEntry("word/document.xml");
        if (entry == null) return string.Empty;

        cancellationToken.ThrowIfCancellationRequested();
        var paragraphs = ReadParagraphs(entry, "p", "t", OfficeParagraphLimit);
        return Clip(string.Join("\n", paragraphs));
    }

    /// <summary>Sheet names are the one useful thing in a workbook without rendering its grid.</summary>
    private static string ReadSheets(ZipArchive archive, List<SpotlightPreviewFact> facts)
    {
        var entry = archive.GetEntry("xl/workbook.xml");
        if (entry == null) return string.Empty;

        try
        {
            using var stream = entry.Open();
            var document = XDocument.Load(stream);
            var names = document.Descendants()
                .Where(element => element.Name.LocalName == "sheet")
                .Select(element => (string?)element.Attribute("name"))
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Take(6)
                .ToList();

            if (names.Count > 0)
            {
                facts.Add(new SpotlightPreviewFact(SpotlightPreviewFactKind.Sheets, string.Join(", ", names!)));
            }
        }
        catch
        {
            // A workbook whose XML does not parse simply has no sheet line.
        }

        return string.Empty;
    }

    /// <summary>
    /// Join the text runs of the first <paramref name="limit"/> paragraphs of an OOXML part.
    /// <para>
    /// Matched by <b>local name</b> — <c>&lt;a:p&gt;</c> and <c>&lt;w:p&gt;</c> are both "p", and
    /// <c>&lt;a:t&gt;</c> / <c>&lt;w:t&gt;</c> both "t" — so one reader serves DrawingML slides and
    /// WordprocessingML paragraphs, and transitional vs strict OOXML (different namespace URIs for
    /// the same elements) needs no branching. Matching the *prefixed* name is the trap: LocalName
    /// never contains a colon.
    /// </para>
    /// </summary>
    private static List<string> ReadParagraphs(ZipArchiveEntry entry, string paragraphName, string runName, int limit)
    {
        var lines = new List<string>(limit);
        try
        {
            using var stream = entry.Open();
            var document = XDocument.Load(stream);

            foreach (var paragraph in document.Descendants().Where(element => element.Name.LocalName == paragraphName))
            {
                var text = string.Concat(paragraph.Descendants()
                    .Where(element => element.Name.LocalName == runName)
                    .Select(element => element.Value)).Trim();

                if (text.Length == 0) continue;
                lines.Add(text);
                if (lines.Count >= limit) break;
            }
        }
        catch
        {
            // Malformed part: keep whatever was read before the failure.
        }

        return lines;
    }

    /// <summary>"ppt/slides/slide12.xml" → 12; 0 for anything else (slide layouts, notes, rels).</summary>
    private static int SlideNumber(string entryName)
    {
        if (!entryName.StartsWith("ppt/slides/slide", StringComparison.OrdinalIgnoreCase)) return 0;
        if (!entryName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)) return 0;

        var digits = entryName.AsSpan("ppt/slides/slide".Length, entryName.Length - "ppt/slides/slide".Length - 4);
        return int.TryParse(digits, out var number) ? number : 0;
    }

    // ---- cache --------------------------------------------------------------------

    private static string CacheKey(string path, bool isFolder)
    {
        var stamp = 0L;
        try { stamp = isFolder ? Directory.GetLastWriteTimeUtc(path).Ticks : File.GetLastWriteTimeUtc(path).Ticks; }
        catch { }

        return $"{path.ToLowerInvariant()}|{stamp}|{isFolder}";
    }

    private bool TryGetCached(string key, out SpotlightPreviewContent content)
    {
        lock (gate)
        {
            return contentCache.TryGetValue(key, out content!);
        }
    }

    private void Store(string key, SpotlightPreviewContent content)
    {
        lock (gate)
        {
            if (contentCache.ContainsKey(key)) return;

            contentCache[key] = content;
            contentCacheOrder.Enqueue(key);
            while (contentCacheOrder.Count > ContentCacheLimit && contentCacheOrder.TryDequeue(out var oldest))
            {
                contentCache.Remove(oldest);
            }
        }
    }

    private static bool IsNetworkPath(string path)
        => path.StartsWith(@"\\", StringComparison.Ordinal);

    // ---- extension tables ---------------------------------------------------------

    private static readonly HashSet<string> DocumentExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        "doc", "docx", "docm", "dot", "dotx", "odt", "rtf", "wps", "pages",
    };

    private static readonly HashSet<string> PresentationExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        "ppt", "pptx", "pptm", "pps", "ppsx", "pot", "potx", "odp", "dps", "key",
    };

    private static readonly HashSet<string> SpreadsheetExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        "xls", "xlsx", "xlsm", "xlt", "xltx", "ods", "et",
    };

    /// <summary>
    /// Formats whose bytes are text. Deliberately broad: a developer searching for a source file
    /// expects to read it in the pane, and reading 1.4 KB of it is cheaper than any renderer.
    /// </summary>
    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        "txt", "md", "markdown", "rst", "adoc", "log", "csv", "tsv", "json", "jsonc", "xml", "yml",
        "yaml", "toml", "ini", "cfg", "conf", "properties", "env", "gitignore", "editorconfig",
        "cs", "ts", "js", "jsx", "tsx", "vue", "svelte", "py", "java", "kt", "kts", "c", "h",
        "cc", "cpp", "cxx", "hpp", "csx", "go", "rs", "rb", "php", "lua", "pl", "r", "swift",
        "sh", "bash", "zsh", "ps1", "psm1", "bat", "cmd", "sql", "html", "htm", "css", "scss",
        "less", "sass", "tex", "bib", "gradle", "csproj", "props", "targets", "sln", "axaml",
        "xaml", "resx", "manifest", "dockerfile", "makefile",
    };

    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        "mp4", "m4v", "mkv", "avi", "mov", "wmv", "flv", "webm", "mpg", "mpeg", "m2ts", "mts", "3gp",
    };

    private static readonly HashSet<string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        "mp3", "wav", "flac", "aac", "ogg", "oga", "m4a", "wma", "opus", "aiff", "ape",
    };

    private static readonly HashSet<string> ArchiveExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        "zip", "7z", "rar", "tar", "gz", "tgz", "bz2", "xz", "zst", "iso", "cab", "jar",
    };
}
