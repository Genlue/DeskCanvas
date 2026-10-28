namespace Calendar.Models;

/// <summary>
/// Persisted settings for the 日期 widget.
/// </summary>
/// <param name="ShowLunar">Show the Chinese lunisolar date beside the day number. When off, the
/// lunar column is hidden and the day number is centred in the card. Default on.</param>
public record DateModel(bool ShowLunar = true);