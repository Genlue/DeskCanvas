namespace Progress.Models;

public record ProgressModel(
    ProgressMode Mode = ProgressMode.Year,
    DayGranularity DayGranularity = DayGranularity.Minutes10,
    bool FollowAccentColor = true,
    string? PassedColor = null,
    string? CurrentColor = null,
    string? RemainingColor = null,
    DotShape DotShape = DotShape.Circle,
    bool ShowHeader = true,
    bool ShowPercentage = true,
    bool ShowCount = true,
    string? CustomTitle = null,
    int LifeExpectancyYears = 80,
    string? BirthDate = null
);
