using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Tools.Models;

namespace Tools.Services.Translation;

public class TranslationService
{
    private static TranslationService? instance;
    public static TranslationService Instance => instance ??= new TranslationService();

    private readonly YoudaoTranslationEngine youdao = new();
    private readonly MyMemoryTranslationEngine myMemory = new();
    private readonly BaiduTranslationEngine baidu = new();
    private readonly DeepLXTranslationEngine deepLX = new();

    public IReadOnlyList<string> AvailableEngines { get; } =
    [
        "Youdao",
        "MyMemory",
        "Baidu",
        "DeepLX"
    ];

    public void Configure(TranslatorModel model)
    {
        baidu.AppId = model.BaiduAppId;
        baidu.SecretKey = model.BaiduSecretKey;
        deepLX.CustomUrl = model.CustomUrl;
    }

    public async Task<string> TranslateAsync(string text, string sourceLang, string targetLang, string engineName, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;

        ITranslationEngine engine = engineName switch
        {
            "MyMemory" => myMemory,
            "Baidu" => baidu,
            "DeepLX" => deepLX,
            _ => youdao
        };

        return await engine.TranslateAsync(text, sourceLang, targetLang, ct);
    }
}
