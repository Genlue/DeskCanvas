using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Tools.Services.Translation;

public class MyMemoryTranslationEngine : ITranslationEngine
{
    public string Name => "MyMemory";

    private static readonly HttpClient httpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(8)
    };

    public async Task<string> TranslateAsync(string text, string sourceLang, string targetLang, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;

        string s = NormalizeLang(sourceLang, isSource: true);
        string t = NormalizeLang(targetLang, isSource: false);
        string langPair = $"{s}|{t}";

        string url = $"https://api.mymemory.translated.net/get?q={Uri.EscapeDataString(text)}&langpair={langPair}";

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("User-Agent", "DeskCanvas/1.7.2");

        using var response = await httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringSafeAsync(cancellationToken);
        using var doc = JsonDocument.Parse(json);

        if (doc.RootElement.TryGetProperty("responseData", out var respData) &&
            respData.TryGetProperty("translatedText", out var transText))
        {
            var result = transText.GetString();
            if (!string.IsNullOrEmpty(result))
            {
                return result;
            }
        }

        throw new InvalidOperationException("MyMemory 未返回有效翻译结果");
    }

    private static string NormalizeLang(string lang, bool isSource) => lang.ToLowerInvariant() switch
    {
        "auto" => isSource ? "autodetect" : "zh-CN",
        "zh" or "zh-cn" or "zh-hans" => "zh-CN",
        "en" => "en",
        "ja" => "ja",
        "ko" => "ko",
        "fr" => "fr",
        "de" => "de",
        "es" => "es",
        "ru" => "ru",
        _ => lang
    };
}
