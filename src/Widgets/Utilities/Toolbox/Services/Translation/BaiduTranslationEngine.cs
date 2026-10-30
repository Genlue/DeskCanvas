using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Tools.Services.Translation;

public class BaiduTranslationEngine : ITranslationEngine
{
    public string Name => "Baidu";

    public string AppId { get; set; } = string.Empty;
    public string SecretKey { get; set; } = string.Empty;

    private static readonly HttpClient httpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(8)
    };

    public async Task<string> TranslateAsync(string text, string sourceLang, string targetLang, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;

        bool isZh = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.Equals("zh", StringComparison.OrdinalIgnoreCase);

        if (string.IsNullOrWhiteSpace(AppId) || string.IsNullOrWhiteSpace(SecretKey))
        {
            throw new InvalidOperationException(isZh ? "请先在设置中配置百度翻译 AppID 与密钥" : "Please configure Baidu Translate AppID and Secret Key in settings first");
        }

        string from = NormalizeLang(sourceLang);
        string to = NormalizeLang(targetLang);
        string salt = Random.Shared.Next(100000, 999999).ToString();

        string signRaw = $"{AppId}{text}{salt}{SecretKey}";
        string sign = ComputeMd5(signRaw);

        var postData = new Dictionary<string, string>
        {
            { "q", text },
            { "from", from },
            { "to", to },
            { "appid", AppId },
            { "salt", salt },
            { "sign", sign }
        };

        using var content = new FormUrlEncodedContent(postData);
        using var response = await httpClient.PostAsync("https://fanyi-api.baidu.com/api/trans/vip/translate", content, cancellationToken);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringSafeAsync(cancellationToken);
        using var doc = JsonDocument.Parse(json);

        if (doc.RootElement.TryGetProperty("error_code", out var errCode))
        {
            var code = errCode.GetString();
            if (code != "52000" && !string.IsNullOrEmpty(code))
            {
                var msg = doc.RootElement.TryGetProperty("error_msg", out var errMsg) ? errMsg.GetString() : code;
                throw new InvalidOperationException(isZh ? $"百度翻译错误 ({code}): {msg}" : $"Baidu Translate Error ({code}): {msg}");
            }
        }

        if (doc.RootElement.TryGetProperty("trans_result", out var transResult) && transResult.ValueKind == JsonValueKind.Array)
        {
            var sb = new StringBuilder();
            foreach (var item in transResult.EnumerateArray())
            {
                if (item.TryGetProperty("dst", out var dst))
                {
                    if (sb.Length > 0) sb.AppendLine();
                    sb.Append(dst.GetString());
                }
            }
            return sb.ToString();
        }

        throw new InvalidOperationException(isZh ? "百度翻译未返回有效结果" : "Baidu Translate did not return a valid result");
    }

    private static string ComputeMd5(string input)
    {
        var bytes = Encoding.UTF8.GetBytes(input);
        var hash = MD5.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string NormalizeLang(string lang) => lang.ToLowerInvariant() switch
    {
        "auto" => "auto",
        "zh" or "zh-cn" or "zh-hans" => "zh",
        "en" => "en",
        "ja" => "jp",
        "ko" => "kor",
        "fr" => "fra",
        "de" => "de",
        "es" => "spa",
        "ru" => "ru",
        _ => lang
    };
}
