namespace Clock.Models;

/// <summary>
/// How the dial surface of 表盘-3 (AnalogIV) picks its background color.
/// </summary>
public enum ClockDialColorMode
{
    /// <summary>
    /// 跟随背景: the historic look — the dial uses the surface background
    /// (背景色设置; on the Colorful theme it stays the theme's fixed white/charcoal pair).
    /// </summary>
    BackgroundColor = 0,

    /// <summary>
    /// 黑白: fixed black-and-white — the exact pair 表盘-1 (AnalogI) wears on the Colorful
    /// theme: #EDEDED in light mode and #373738 in dark mode, i.e. the on-screen values of
    /// dial-1's inner face (its light "white dial" and dark "black dial" under the theme's
    /// constant black card).
    /// </summary>
    BlackWhite = 1,

    /// <summary>
    /// 自定义: user-picked colors, chosen separately for light and dark mode
    /// (<see cref="ClockModel.DialColorLight"/> / <see cref="ClockModel.DialColorDark"/>).
    /// </summary>
    Custom = 2
}

public record ClockModel(
    bool ShowSeconds = false, 
    bool ShowDate = false,
    bool Use24Hours = false, 
    string? TimeZoneId = null,
    ClockDialColorMode DialColorMode = ClockDialColorMode.BackgroundColor,
    string? DialColorLight = null,
    string? DialColorDark = null)
{
    /// <summary>Default dial color in light mode — 表盘-1's Colorful light dial face (the white dial).</summary>
    public const string DefaultDialColorLight = "#EDEDED";

    /// <summary>Default dial color in dark mode — 表盘-1's Colorful dark dial face (the black dial).</summary>
    public const string DefaultDialColorDark = "#373738";

    /// <summary>
    /// The 自定义 dial color in light mode; falls back to the 黑白 value when unset,
    /// so switching to 自定义 starts from the black-and-white pair.
    /// </summary>
    public string EffectiveDialColorLight => DialColorLight ?? DefaultDialColorLight;

    /// <summary>
    /// The 自定义 dial color in dark mode; falls back to the 黑白 value when unset,
    /// so switching to 自定义 starts from the black-and-white pair.
    /// </summary>
    public string EffectiveDialColorDark => DialColorDark ?? DefaultDialColorDark;
}
