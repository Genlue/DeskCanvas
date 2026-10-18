using System.Net;
using System.Text;
using Shortcuts.Models;

namespace Shortcuts.Services.Executors;

/// <summary>
/// Minimal GET/POST action: http/https only, bounded response, explicit timeout. The body
/// becomes the action output so later steps can read it through a magic variable.
/// </summary>
internal sealed class HttpRequestExecutor : IActionExecutor
{
    private const int MaxResponseChars = 20_000;

    private static readonly HttpClient Client = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient(new HttpClientHandler
        {
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 5,
        })
        {
            // Per-request timeout below; the shared client must not impose its own.
            Timeout = Timeout.InfiniteTimeSpan,
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("DeskCanvas-Shortcuts/1.0");
        return client;
    }

    public async Task<string?> ExecuteAsync(ShortcutAction action, RunContext context)
    {
        var rawUrl = context.ResolveOrDefault(action, "url").Trim();
        if (rawUrl.Length == 0 || rawUrl == "https://")
            throw new ActionFailedException(Loc.Format("Shortcuts_Error_EmptyParam", Loc.Get("Shortcuts_Param_url")));

        if (!Uri.TryCreate(rawUrl, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new ActionFailedException(Loc.Get("Shortcuts_Error_InvalidUrl"));
        }

        var method = context.ResolveOrDefault(action, "method").ToUpperInvariant();
        if (method is not ("GET" or "POST")) method = "GET";

        var body = context.Resolve(action, "body");
        var timeoutSeconds = Math.Clamp(context.ResolveNumber(action, "timeout", 15), 1, 60);

        using var request = new HttpRequestMessage(
            method == "POST" ? HttpMethod.Post : HttpMethod.Get,
            uri);

        if (method == "POST")
        {
            var contentType = LooksLikeJson(body) ? "application/json" : "text/plain";
            request.Content = new StringContent(body, Encoding.UTF8, contentType);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(context.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

        try
        {
            using var response = await Client
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
                .ConfigureAwait(true);

            var text = await ReadCappedAsync(response, timeout.Token).ConfigureAwait(true);
            if (!response.IsSuccessStatusCode)
            {
                throw new ActionFailedException(Loc.Format("Shortcuts_Error_HttpStatus",
                    (int)response.StatusCode + " " + response.ReasonPhrase));
            }

            return text.Length > 0
                ? text
                : Loc.Format("Shortcuts_Output_HttpStatus", (int)response.StatusCode);
        }
        catch (OperationCanceledException) when (!context.Token.IsCancellationRequested)
        {
            throw new ActionFailedException(Loc.Get("Shortcuts_Error_HttpTimeout"));
        }
        catch (HttpRequestException ex)
        {
            throw new ActionFailedException(Loc.Format("Shortcuts_Error_HttpStatus", ex.Message));
        }
    }

    private static bool LooksLikeJson(string text)
    {
        var trimmed = text.TrimStart();
        return trimmed.StartsWith('{') || trimmed.StartsWith('[');
    }

    private static async Task<string> ReadCappedAsync(HttpResponseMessage response, CancellationToken token)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(true);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);

        var buffer = new char[4096];
        var builder = new StringBuilder(Math.Min(MaxResponseChars, 4096));
        while (builder.Length < MaxResponseChars)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(0,
                Math.Min(buffer.Length, MaxResponseChars - builder.Length)), token).ConfigureAwait(true);
            if (read == 0) break;
            builder.Append(buffer, 0, read);
        }

        return builder.ToString();
    }
}
