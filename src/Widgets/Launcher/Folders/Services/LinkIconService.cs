using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using DeskCanvas.Core;

namespace Folders.Services;

/// <summary>
/// Service to manage, fetch, and cache icons for SingleLink widgets.
/// Icons are stored locally in Const.DataFolder\LinkIcons.
/// </summary>
public static class LinkIconService
{
    public static string IconFolder => Path.Combine(Const.DataFolder, "LinkIcons");

    private static readonly HttpClient HttpClient = new(new HttpClientHandler
    {
        AutomaticDecompression = DecompressionMethods.All,
        AllowAutoRedirect = true
    })
    {
        Timeout = TimeSpan.FromSeconds(8)
    };

    static LinkIconService()
    {
        if (!HttpClient.DefaultRequestHeaders.Contains("User-Agent"))
        {
            HttpClient.DefaultRequestHeaders.TryAddWithoutValidation(
                "User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
        }
    }

    public static void EnsureCacheDirectory()
    {
        try
        {
            if (!Directory.Exists(IconFolder))
            {
                Directory.CreateDirectory(IconFolder);
            }
        }
        catch
        {
            // Directory creation fallback
        }
    }

    /// <summary>
    /// Loads an icon from a local cached file path, trimming transparent margins for center alignment.
    /// </summary>
    public static Bitmap? LoadIcon(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;

        try
        {
            // A plain image file must be decoded directly: FolderIconService routes it through the
            // shell image factory with SIIGBF_ICONONLY, which answers with the file type's generic
            // icon (the Photos app icon) instead of the picture, so the stream fallback below would
            // never run for exactly the files that need it. That is what made a PNG/JPG chosen in
            // the icon picker look like "only .ico works".
            var ext = Path.GetExtension(path).ToLowerInvariant();
            if (ext is ".png" or ".jpg" or ".jpeg" or ".webp" or ".bmp" or ".gif")
            {
                using var imageStream = File.OpenRead(path);
                return FolderIconService.TrimTransparent(new Bitmap(imageStream));
            }

            // Everything else (.ico, .lnk, .url, folders): shell resolution first, stream decode as
            // the fallback. .svg stays on this path on purpose — Bitmap cannot decode SVG, so
            // sending it to the direct branch would only turn the generic icon into no icon.
            var bmp = FolderIconService.GetIcon(path);
            if (bmp != null)
            {
                return FolderIconService.TrimTransparent(bmp);
            }

            using var stream = File.OpenRead(path);
            var direct = new Bitmap(stream);
            return FolderIconService.TrimTransparent(direct);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Saves a user-selected local image file into the LinkIcons cache directory.
    /// </summary>
    public static async Task<string?> SaveLocalFileAsync(string sourceFilePath)
    {
        if (string.IsNullOrWhiteSpace(sourceFilePath) || !File.Exists(sourceFilePath)) return null;

        try
        {
            EnsureCacheDirectory();
            var bytes = await File.ReadAllBytesAsync(sourceFilePath);
            if (bytes.Length == 0) return null;

            var ext = Path.GetExtension(sourceFilePath);
            if (string.IsNullOrWhiteSpace(ext))
            {
                ext = DetectImageExtension(bytes) ?? ".png";
            }

            var hash = Convert.ToHexString(SHA256.HashData(bytes))[..16].ToLowerInvariant();
            var fileName = $"local_{hash}{ext}";
            var destPath = Path.Combine(IconFolder, fileName);

            if (!File.Exists(destPath))
            {
                await File.WriteAllBytesAsync(destPath, bytes);
            }

            return destPath;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Downloads an image from a direct web URL and saves it into the LinkIcons cache directory.
    /// </summary>
    public static async Task<string?> DownloadFromUrlAsync(string imageUrl)
    {
        if (string.IsNullOrWhiteSpace(imageUrl)) return null;

        var normalizedUrl = imageUrl.Trim();
        if (!normalizedUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            && !normalizedUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            normalizedUrl = $"https://{normalizedUrl}";
        }

        try
        {
            EnsureCacheDirectory();
            var response = await HttpClient.GetAsync(normalizedUrl);
            if (!response.IsSuccessStatusCode) return null;

            var bytes = await response.Content.ReadAsByteArrayAsync();
            if (bytes == null || bytes.Length < 16) return null;

            var ext = DetectImageExtension(bytes);
            if (ext == null)
            {
                // If not recognized magic bytes, check URL extension or fail
                var urlExt = Path.GetExtension(new Uri(normalizedUrl).AbsolutePath);
                if (urlExt is ".png" or ".ico" or ".jpg" or ".jpeg" or ".webp" or ".bmp" or ".svg")
                    ext = urlExt;
                else
                    return null; // Not a valid image
            }

            var hash = Convert.ToHexString(SHA256.HashData(bytes))[..16].ToLowerInvariant();
            var fileName = $"web_{hash}{ext}";
            var destPath = Path.Combine(IconFolder, fileName);

            if (!File.Exists(destPath))
            {
                await File.WriteAllBytesAsync(destPath, bytes);
            }

            return destPath;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Automatically fetches favicon for a web page URL.
    /// Tries domain/favicon.ico first, then falls back to Google's favicon service.
    /// Saves the downloaded icon into LinkIcons cache and returns the local file path.
    /// </summary>
    public static async Task<string?> FetchFaviconAsync(string webPageUrl)
    {
        if (string.IsNullOrWhiteSpace(webPageUrl)) return null;

        var normalized = webPageUrl.Trim();
        if (!normalized.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            && !normalized.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            normalized = $"https://{normalized}";
        }

        if (!Uri.TryCreate(normalized, UriKind.Absolute, out var uri))
        {
            return null;
        }

        var domain = uri.Host;
        if (string.IsNullOrWhiteSpace(domain)) return null;

        EnsureCacheDirectory();

        // 1. Try https://{domain}/favicon.ico
        try
        {
            var directFaviconUrl = $"{uri.Scheme}://{domain}/favicon.ico";
            var response = await HttpClient.GetAsync(directFaviconUrl);
            if (response.IsSuccessStatusCode)
            {
                var bytes = await response.Content.ReadAsByteArrayAsync();
                var ext = DetectImageExtension(bytes);
                if (ext != null && bytes.Length > 32)
                {
                    var hash = Convert.ToHexString(SHA256.HashData(bytes))[..16].ToLowerInvariant();
                    var fileName = $"fav_{SanitizeFileName(domain)}_{hash}{ext}";
                    var destPath = Path.Combine(IconFolder, fileName);
                    if (!File.Exists(destPath))
                    {
                        await File.WriteAllBytesAsync(destPath, bytes);
                    }
                    return destPath;
                }
            }
        }
        catch
        {
            // Fall through to Google Favicon service
        }

        // 2. Try Google Favicon Service: https://www.google.com/s2/favicons?domain={domain}&sz=128
        try
        {
            var googleFaviconUrl = $"https://www.google.com/s2/favicons?domain={Uri.EscapeDataString(domain)}&sz=128";
            var response = await HttpClient.GetAsync(googleFaviconUrl);
            if (response.IsSuccessStatusCode)
            {
                var bytes = await response.Content.ReadAsByteArrayAsync();
                var ext = DetectImageExtension(bytes) ?? ".png";
                if (bytes.Length > 64)
                {
                    var hash = Convert.ToHexString(SHA256.HashData(bytes))[..16].ToLowerInvariant();
                    var fileName = $"fav_{SanitizeFileName(domain)}_{hash}{ext}";
                    var destPath = Path.Combine(IconFolder, fileName);
                    if (!File.Exists(destPath))
                    {
                        await File.WriteAllBytesAsync(destPath, bytes);
                    }
                    return destPath;
                }
            }
        }
        catch
        {
            // Failed
        }

        return null;
    }

    private static string? DetectImageExtension(byte[] bytes)
    {
        if (bytes == null || bytes.Length < 4) return null;

        // PNG: 89 50 4E 47 0D 0A 1A 0A
        if (bytes.Length >= 8 && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47)
            return ".png";

        // ICO: 00 00 01 00
        if (bytes[0] == 0x00 && bytes[1] == 0x00 && bytes[2] == 0x01 && bytes[3] == 0x00)
            return ".ico";

        // JPEG: FF D8 FF
        if (bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
            return ".jpg";

        // GIF: 47 49 46 38
        if (bytes[0] == 0x47 && bytes[1] == 0x49 && bytes[2] == 0x46 && bytes[3] == 0x38)
            return ".gif";

        // WEBP: RIFF....WEBP
        if (bytes.Length >= 12 && bytes[0] == 0x52 && bytes[1] == 0x49 && bytes[2] == 0x46 && bytes[3] == 0x46
            && bytes[8] == 0x57 && bytes[9] == 0x45 && bytes[10] == 0x42 && bytes[11] == 0x50)
            return ".webp";

        // BMP: 42 4D
        if (bytes[0] == 0x42 && bytes[1] == 0x4D)
            return ".bmp";

        return null;
    }

    private static string SanitizeFileName(string input)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = input.ToCharArray();
        for (int i = 0; i < chars.Length; i++)
        {
            if (Array.IndexOf(invalid, chars[i]) >= 0)
                chars[i] = '_';
        }
        return new string(chars);
    }
}
