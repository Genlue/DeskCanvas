namespace Tools.Models;

public record TranslatorModel(
    string SourceLanguage = "auto",
    string TargetLanguage = "zh",
    string Engine = "Youdao",
    bool AutoTranslate = true,
    string BaiduAppId = "",
    string BaiduSecretKey = "",
    string CustomUrl = "http://127.0.0.1:1188/translate");
