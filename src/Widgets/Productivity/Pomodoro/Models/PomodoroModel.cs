namespace Pomodoro.Models;

public enum PomodoroPhase
{
    Focus,
    ShortBreak,
    LongBreak
}

public enum PomodoroState
{
    Idle,
    Running,
    Paused
}

public record PomodoroModel(
    int FocusMinutes = 25,
    int ShortBreakMinutes = 5,
    int LongBreakMinutes = 15,
    int LongBreakInterval = 4,
    bool AutoStartBreaks = false,
    bool AutoStartFocus = false,
    bool SoundEnabled = true
);
