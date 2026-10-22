using System;
using System.Collections.Generic;
using System.IO;

namespace DeskCanvas.Services;

/// <summary>
/// How a file hit is bucketed by the category bar — and by the preview pane, which asks the same
/// question ("is this a picture?") when it decides whether a thumbnail is worth requesting.
/// <para>
/// The three file categories are mutually exclusive: an image belongs to 图片 only, a directory
/// to 文件夹 only, and everything else to 文件. Without that rule an image would appear under both
/// 图片 and 文件, and the two chips would look like a bug instead of two views.
/// </para>
/// </summary>
public static class SpotlightFileKinds
{
    /// <summary>
    /// Extensions Windows itself treats as pictures, plus the raw formats photographers search
    /// for. Deliberately extension-only: opening every hit to sniff its bytes would put IO on the
    /// keystroke path.
    /// </summary>
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        "png", "jpg", "jpeg", "jpe", "jfif", "gif", "bmp", "dib", "webp", "svg", "svgz",
        "ico", "tif", "tiff", "heic", "heif", "avif", "jxl", "psd", "emf", "wmf",
        "raw", "cr2", "cr3", "nef", "arw", "dng", "orf", "rw2", "pef", "srw",
    };

    /// <summary>True when the path's extension is a known image format (folders are not images).</summary>
    public static bool IsImage(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;

        var extension = Path.GetExtension(path);
        return extension.Length > 1 && ImageExtensions.Contains(extension[1..]);
    }
}
