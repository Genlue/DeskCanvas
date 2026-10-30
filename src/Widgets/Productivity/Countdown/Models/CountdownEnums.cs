namespace Countdown.Models;

public enum CountdownCardStyle
{
    MinimalNumber = 0,
    RingProgress = 1,
    EventList = 2
}

public enum CountdownGranularity
{
    DaysOnly = 0,
    Auto24Hours = 1,
    AlwaysDetailed = 2
}

public enum RepeatCycle
{
    None = 0,
    Yearly = 1,
    Monthly = 2,
    Weekly = 3
}

public enum CountDirection
{
    Auto = 0,
    Countdown = 1,
    Countup = 2
}
