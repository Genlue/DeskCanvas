using System;
using System.Globalization;
using Music.Models;

namespace Music.Services;

public static class PresetPlayers
{
    public static List<PlayerRule> CreateDefaultRules()
    {
        var isZh = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.Equals("zh", StringComparison.OrdinalIgnoreCase);
        return
        [
            new(isZh ? "网易云音乐" : "NetEase Cloud Music", "cloudmusic", true, 0),
            new(isZh ? "QQ音乐" : "QQ Music", "qqmusic", true, 1),
            new("Spotify", "spotify", true, 2),
            new("Apple Music", "applemusic", true, 3),
            new(isZh ? "汽水音乐" : "Soda Music", "qishui", true, 4),
            new(isZh ? "酷狗音乐" : "KuGou Music", "kugou", true, 5),
            new(isZh ? "酷我音乐" : "Kuwo Music", "kwmusic", true, 6),
            new("Foobar2000", "foobar2000", true, 7),
            new("YesPlayMusic", "yesplaymusic", true, 8),
            new(isZh ? "Windows 媒体播放器" : "Windows Media Player", "mediaplayer", true, 9),
            new(isZh ? "浏览器 (Edge/Chrome/Firefox)" : "Browsers (Edge/Chrome/Firefox)", "chrome|msedge|firefox|brave|43f3c7f1c0e34091", true, 10),
        ];
    }
}
