namespace TickClock.Models;

/// <summary>
/// 刻度时钟（复刻 MIUI 桌面方卡）：一圈刻度 + 红色星期 + 超大时间 + 底部日期。
/// </summary>
public record TickClockModel(
    bool ShowWeekday = true,
    bool ShowDate = true,
    bool Use24Hour = true)
{
    /// <summary>参考图的朱红（全彩主题下“周X”固定用它，不再开放自定义）。</summary>
    public const string AccentColor = "#EE3B33";
}
