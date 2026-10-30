namespace Progress.Models;

public enum ProgressMode
{
    Year = 0,
    Month = 1,
    Week = 2,
    Day = 3,
    Life = 4
}

public enum DayGranularity
{
    Minutes5 = 0,   // 288 dots (24 * 12)
    Minutes10 = 1,  // 144 dots (12 * 12)
    Minutes15 = 2,  // 96 dots
    Minutes30 = 3,  // 48 dots
    Hour1 = 4       // 24 dots
}

public enum DotShape
{
    Circle = 0,
    Squircle = 1
}
