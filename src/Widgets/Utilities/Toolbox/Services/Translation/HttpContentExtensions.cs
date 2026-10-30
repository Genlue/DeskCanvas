using System;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Tools.Services.Translation;

public static class HttpContentExtensions
{
    static HttpContentExtensions()
    {
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        }
        catch { }
    }

    /// <summary>
    /// Safely reads HTTP content as string without throwing "The character set provided in ContentType is invalid".
    /// Handles non-standard charset headers (such as Youdao's "charset=UTF8" without hyphen),
    /// unsupported encodings, and malformed header values.
    /// </summary>
    public static async Task<string> ReadAsStringSafeAsync(this HttpContent content, CancellationToken cancellationToken = default)
    {
        var bytes = await content.ReadAsByteArrayAsync(cancellationToken);
        if (bytes.Length == 0) return string.Empty;

        // Inspect charset from ContentType header if available
        var charset = content.Headers.ContentType?.CharSet;
        if (!string.IsNullOrWhiteSpace(charset))
        {
            charset = charset.Trim('\"', '\'', ' ');

            // Normalize common UTF-8 variations
            if (charset.Equals("utf8", StringComparison.OrdinalIgnoreCase) ||
                charset.Equals("utf-8", StringComparison.OrdinalIgnoreCase))
            {
                return Encoding.UTF8.GetString(bytes);
            }

            try
            {
                var encoding = Encoding.GetEncoding(charset);
                return encoding.GetString(bytes);
            }
            catch
            {
                // Fallback to UTF-8 if charset name is unknown or invalid in .NET
            }
        }

        // Default fallback: UTF-8
        return Encoding.UTF8.GetString(bytes);
    }
}
