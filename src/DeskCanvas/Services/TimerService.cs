using System;

namespace DeskCanvas.Services;

public static class TimerService
{
    public static readonly UpdateTimer Timer100Ms = new(TimeSpan.FromMilliseconds(100));
    
    public static readonly UpdateTimer Timer1Second = new(TimeSpan.FromSeconds(1));
    
    public static readonly UpdateTimer Timer5Seconds = new(TimeSpan.FromSeconds(5));
    
    public static readonly UpdateTimer Timer1Minute = new(TimeSpan.FromMinutes(1));
    
    public static readonly UpdateTimer Timer5Minutes = new(TimeSpan.FromMinutes(5));
    
    public static readonly UpdateTimer Timer1Hour = new(TimeSpan.FromHours(1));

    public static readonly UpdateTimer Timer1Day = new(TimeSpan.FromDays(1));

    /// <summary>Every shared timer, for the host-wide pause used during fullscreen applications.</summary>
    private static readonly UpdateTimer[] AllTimers =
    [
        Timer100Ms, Timer1Second, Timer5Seconds, Timer1Minute, Timer5Minutes, Timer1Hour, Timer1Day
    ];

    /// <summary>Stop delivering ticks to every widget (subscriptions are kept).</summary>
    public static void PauseAll()
    {
        foreach (var timer in AllTimers) timer.Pause();
    }

    /// <summary>Resume tick delivery after <see cref="PauseAll"/>.</summary>
    public static void ResumeAll()
    {
        foreach (var timer in AllTimers) timer.Resume();
    }
}