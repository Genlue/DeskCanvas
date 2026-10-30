using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Tools.Services.Translation;

public class DeepLXTranslationEngine : ITranslationEngine
{
    public string Name => "DeepLX";

    public string CustomUrl { get; set; } = "http://127.0.0.1:1188/translate";

    private static readonly HttpClient httpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(8)
    };

    public async Task<string> TranslateAsync(string text, string sourceLang, string targetLang, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;

        var url = string.IsNullOrWhiteSpace(CustomUrl) ? "http://127.0.0.1:1188/translate" : CustomUrl.Trim();

        var body = new
        {
            text = text,
            source_lang = NormalizeLang(sourceLang),
            target_lang = NormalizeLang(targetLang)
        };

        var json = JsonSerializer.Serialize(body);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");

        using var response = await httpClient.PostAsync(url, content, cancellationToken);
        response.EnsureSuccessStatusCode();

        var respString = await response.Content.ReadAsStringSafeAsync(cancellationToken);
        using var doc = JsonDocument.Parse(respString);

        if (doc.RootElement.TryGetProperty("data", out var data))
        {
            var result = data.GetString();
            if (!string.IsNullOrEmpty(result))
            {
                return result;
            }
        }

        bool isZh = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.Equals("zh", StringComparison.OrdinalIgnoreCase);
        throw new InvalidOperationException(isZh ? "DeepLX 接口未返回有效翻译内容" : "DeepLX endpoint did not return valid translated content");
    }

    private static string NormalizeLang(string lang) => lang.ToLowerInvariant() switch
    {
        "auto" => "auto",
        "zh" or "zh-cn" or "zh-hans" => "ZH",
        "en" => "EN",
        "ja" => "JA",
        "ko" => "KO",
        "fr" => "FR",
        "de" => "DE",
        "es" => "ES",
        "ru" => "RU",
        _ => lang.ToUpperInvariant()
    };
}
