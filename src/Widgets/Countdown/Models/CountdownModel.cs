namespace Countdown.Models;

public record CountdownModel(
    string? TargetEventId = null,
    CountdownCardStyle Style = CountdownCardStyle.MinimalNumber,
    CountdownGranularity Granularity = CountdownGranularity.Auto24Hours,
    bool ShowProgress = true
);
