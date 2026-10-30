using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Shortcuts.Models;

namespace Shortcuts.Services.Executors;

/// <summary>
/// HTTP request with explicit method/headers/query/body, charset-aware decoding and a truncation
/// flag. A non-2xx response keeps its body as data and still reports an error, so an error route
/// can inspect what the server said.
/// </summary>
internal sealed class HttpRequestExecutor : IActionExecutor
{
    private const int MaxBodyChars = 20000;

    private static readonly HttpClient Client = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = true,
        PooledConnectionLifetime = TimeSpan.FromMinutes(2),
    })
    {
        Timeout = Timeout.InfiniteTimeSpan,
    };

    public async Task<ActionResult> ExecuteAsync(ShortcutAction action, RunContext context)
    {
        var url = context.Text(action, "url").Trim();
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return ActionResult.Fail("InvalidUrl", Loc.Get("Shortcuts_Error_InvalidUrl"), "url");
        }

        uri = AppendQuery(uri, context.Text(action, "query"));
        var method = context.Text(action, "method").ToUpperInvariant();
        var timeout = TimeSpan.FromSeconds(Math.Clamp(context.Number(action, "timeout"), 1, 60));
        var retries = (int)Math.Clamp(context.Number(action, "retry"), 0, 3);

        using var request = new HttpRequestMessage(new HttpMethod(method), uri);
        ApplyHeaders(request, context.Text(action, "headers"));

        if (method is "POST" or "PUT" or "PATCH")
        {
            var body = context.Text(action, "body");
            var contentType = context.Text(action, "contentType");
            request.Content = new StringContent(body, Encoding.UTF8,
                string.IsNullOrWhiteSpace(contentType) ? "application/json" : contentType);
        }

        Exception? lastError = null;
        for (var attempt = 0; attempt <= retries; attempt++)
        {
            context.Token.ThrowIfCancellationRequested();
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(context.Token);
            timeoutCts.CancelAfter(timeout);

            try
            {
                using var response = await Client
                    .SendAsync(request, HttpCompletionOption.ResponseContentRead, timeoutCts.Token)
                    .ConfigureAwait(true);

                var bytes = await response.Content.ReadAsByteArrayAsync(timeoutCts.Token).ConfigureAwait(true);
                var charset = response.Content.Headers.ContentType?.CharSet;
                var body = Decode(bytes, charset);
                var truncated = body.Length > MaxBodyChars;
                if (truncated) body = body[..MaxBodyChars];

                var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var header in response.Headers.Concat(response.Content.Headers))
                    headers[header.Key] = string.Join(", ", header.Value);

                var outputs = new Dictionary<string, RunValue>
                {
                    ["body"] = RunValue.FromText(body),
                    ["statusCode"] = RunValue.FromNumber((int)response.StatusCode),
                    ["headers"] = RunValue.FromJson(JsonSerializer.Serialize(headers)),
                    ["truncated"] = RunValue.FromBoolean(truncated),
                };

                var summary = truncated
                    ? Loc.Format("Shortcuts_Output_HttpTruncated", (int)response.StatusCode, MaxBodyChars)
                    : Loc.Format("Shortcuts_Output_Http", (int)response.StatusCode, body.Length);

                if (!response.IsSuccessStatusCode)
                {
                    var error = new ActionError("HttpStatus",
                        Loc.Format("Shortcuts_Error_HttpStatus", (int)response.StatusCode,
                            response.ReasonPhrase ?? string.Empty));
                    return new ActionResult(outputs, summary, error);
                }

                return new ActionResult(outputs, summary, null);
            }
            catch (OperationCanceledException) when (context.Token.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                lastError = new TimeoutException(Loc.Get("Shortcuts_Error_Timeout"));
            }
            catch (HttpRequestException ex)
            {
                lastError = ex;
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException)
            {
                lastError = ex;
            }

            if (attempt < retries) await Task.Delay(TimeSpan.FromMilliseconds(400 * (attempt + 1)), context.Token)
                .ConfigureAwait(true);
        }

        return ActionResult.Fail("HttpFailed",
            Loc.Format("Shortcuts_Error_HttpFailed", lastError?.Message ?? string.Empty), "url");
    }

    private static Uri AppendQuery(Uri uri, string query)
    {
        var trimmed = query.Trim().TrimStart('?');
        if (trimmed.Length == 0) return uri;

        var separator = string.IsNullOrEmpty(uri.Query) ? "?" : "&";
        return Uri.TryCreate(uri + separator + trimmed, UriKind.Absolute, out var combined) ? combined : uri;
    }

    /// <summary>Parse "Key: Value" lines; a malformed line is ignored rather than aborting.</summary>
    private static void ApplyHeaders(HttpRequestMessage request, string raw)
    {
        foreach (var line in raw.Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var index = line.IndexOf(':');
            if (index <= 0) continue;

            var name = line[..index].Trim();
            var value = line[(index + 1)..].Trim();
            if (name.Length == 0) continue;

            if (name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase))
            {
                if (request.Content != null &&
                    MediaTypeHeaderValue.TryParse(value, out var parsedContentType))
                {
                    request.Content.Headers.ContentType = parsedContentType;
                }

                continue;
            }

            try
            {
                request.Headers.TryAddWithoutValidation(name, value);
            }
            catch
            {
                // A header the platform refuses is skipped: one bad header must not fail the step.
            }
        }
    }

    /// <summary>Decode using the response charset, then UTF-8, then Latin-1 as a last resort.</summary>
    private static string Decode(byte[] bytes, string? charset)
    {
        if (bytes.Length == 0) return string.Empty;

        if (!string.IsNullOrWhiteSpace(charset))
        {
            try
            {
                return Encoding.GetEncoding(charset.Trim('"')).GetString(bytes);
            }
            catch (ArgumentException)
            {
                // Unknown charset: fall through to the default.
            }
        }

        try
        {
            return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return Encoding.Latin1.GetString(bytes);
        }
    }
}
