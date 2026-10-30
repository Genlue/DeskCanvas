using System;
using System.Collections.Generic;
using System.Globalization;
using Search.Locales;

namespace Search.Models;

public static class PresetEngines
{
    private static bool IsZh => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.Equals("zh", StringComparison.OrdinalIgnoreCase);

    public static List<SearchEngine> GetDefaults()
    {
        bool zh = IsZh;
        return
        [
            new SearchEngine
            {
                Id = "google",
                Name = "Google",
                UrlTemplate = "https://www.google.com/search?q={q}",
                IconKey = "Google",
                IconColor = "#4285F4",
                Category = Locale.Search_Category_General,
                IsEnabled = true,
                IsCustom = false,
                Priority = 0
            },
            new SearchEngine
            {
                Id = "bing",
                Name = zh ? "必应" : "Bing",
                UrlTemplate = "https://www.bing.com/search?q={q}",
                IconKey = "Bing",
                IconColor = "#008373",
                Category = Locale.Search_Category_General,
                IsEnabled = true,
                IsCustom = false,
                Priority = 1
            },
            new SearchEngine
            {
                Id = "baidu",
                Name = zh ? "百度" : "Baidu",
                UrlTemplate = "https://www.baidu.com/s?wd={q}",
                IconKey = "Baidu",
                IconColor = "#2932E1",
                Category = Locale.Search_Category_General,
                IsEnabled = true,
                IsCustom = false,
                Priority = 2
            },
            new SearchEngine
            {
                Id = "duckduckgo",
                Name = "DuckDuckGo",
                UrlTemplate = "https://duckduckgo.com/?q={q}",
                IconKey = "DuckDuckGo",
                IconColor = "#DE5833",
                Category = Locale.Search_Category_General,
                IsEnabled = true,
                IsCustom = false,
                Priority = 3
            },
            new SearchEngine
            {
                Id = "github",
                Name = "GitHub",
                UrlTemplate = "https://github.com/search?q={q}",
                IconKey = "GitHub",
                IconColor = "#24292E",
                Category = Locale.Search_Category_Dev,
                IsEnabled = true,
                IsCustom = false,
                Priority = 4
            },
            new SearchEngine
            {
                Id = "bilibili",
                Name = zh ? "哔哩哔哩" : "Bilibili",
                UrlTemplate = "https://search.bilibili.com/all?keyword={q}",
                IconKey = "Bilibili",
                IconColor = "#00AEEC",
                Category = Locale.Search_Category_Media,
                IsEnabled = true,
                IsCustom = false,
                Priority = 5
            },
            new SearchEngine
            {
                Id = "zhihu",
                Name = zh ? "知乎" : "Zhihu",
                UrlTemplate = "https://www.zhihu.com/search?type=content&q={q}",
                IconKey = "Zhihu",
                IconColor = "#0084FF",
                Category = Locale.Search_Category_Community,
                IsEnabled = true,
                IsCustom = false,
                Priority = 6
            },
            new SearchEngine
            {
                Id = "metaso",
                Name = zh ? "秘塔 AI" : "Metaso AI",
                UrlTemplate = "https://metaso.cn/?q={q}",
                IconKey = "Metaso",
                IconColor = "#175CD3",
                Category = Locale.Search_Category_AI,
                IsEnabled = true,
                IsCustom = false,
                Priority = 7
            },
            new SearchEngine
            {
                Id = "kimi",
                Name = "Kimi",
                UrlTemplate = "https://kimi.moonshot.cn/?q={q}",
                IconKey = "Kimi",
                IconColor = "#1677FF",
                Category = Locale.Search_Category_AI,
                IsEnabled = true,
                IsCustom = false,
                Priority = 8
            },
            new SearchEngine
            {
                Id = "youtube",
                Name = "YouTube",
                UrlTemplate = "https://www.youtube.com/results?search_query={q}",
                IconKey = "YouTube",
                IconColor = "#FF0000",
                Category = Locale.Search_Category_Media,
                IsEnabled = true,
                IsCustom = false,
                Priority = 9
            }
        ];
    }
}
