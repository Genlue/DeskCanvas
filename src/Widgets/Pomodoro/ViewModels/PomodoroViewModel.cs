using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Media;
using DeskCanvas.Services;
using Avalonia.Threading;
using Pomodoro.Locales;
using Pomodoro.Models;
using DeskCanvas.Contracts;

namespace Pomodoro.ViewModels;

public class PomodoroViewModel : INotifyPropertyChanged
{
    private PomodoroModel model;
    private PomodoroPhase phase = PomodoroPhase.Focus;
    private PomodoroState state = PomodoroState.Idle;
    private int remainingSeconds;
    private int totalSeconds;
    private int completedSessions;
    private readonly DispatcherTimer timer;
    private readonly IWidgetSession? session;
    private bool disposed;

    // Session gate (see OnSessionActivity): false while the widget's screens are covered.
    // Session-less widgets (gallery preview, legacy layout entries) stay always-on.
    private bool renderActive;

    // The wall-clock boundary recorded when a RUNNING countdown is gated (screens covered).
    // The 1 s tick drives the countdown itself, so the gated stretch is replayed from this
    // boundary on reopen — the countdown is user data and must neither lose progress nor
    // silently pause in wall-clock terms. Null while the countdown is not gated.
    private DateTime? gateClosedAtUtc;

    public PomodoroViewModel(PomodoroModel? initialModel = null, IWidgetSession? session = null)
    {
        model = initialModel ?? new PomodoroModel();
        this.session = session;
        renderActive = session?.Activity.RenderActive ?? true;
        totalSeconds = model.FocusMinutes * 60;
        remainingSeconds = totalSeconds;

        timer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        timer.Tick += OnTimerTick;
    }

    public PomodoroModel Model => model;

    /// <summary>Diagnostics: whether <see cref="Dispose"/> tore this view model down.</summary>
    public bool IsDisposed => disposed;

    /// <summary>
    /// Teardown (view Unloaded): stops the countdown and detaches its tick handler. A running
    /// DispatcherTimer roots this whole view model from the dispatcher — without this, a widget
    /// deleted mid-session keeps ticking, beeping and spawning toast processes forever.
    /// Idempotent.
    /// </summary>
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;

        timer.Stop();
        timer.Tick -= OnTimerTick;
        State = PomodoroState.Idle;
    }

    public PomodoroPhase Phase
    {
        get => phase;
        private set
        {
            if (phase != value)
            {
                phase = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(PhaseTitle));
                OnPropertyChanged(nameof(PhaseBrush));
                OnPropertyChanged(nameof(IsFocusPhase));
                OnPropertyChanged(nameof(IsShortBreakPhase));
                OnPropertyChanged(nameof(IsLongBreakPhase));
            }
        }
    }

    public PomodoroState State
    {
        get => state;
        private set
        {
            if (state != value)
            {
                state = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsRunning));
                OnPropertyChanged(nameof(PlayPauseIconData));
                OnPropertyChanged(nameof(PlayPauseTooltip));
            }
        }
    }

    public int RemainingSeconds
    {
        get => remainingSeconds;
        private set
        {
            if (remainingSeconds != value)
            {
                remainingSeconds = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(TimeText));
                OnPropertyChanged(nameof(StrokeDashOffset));
                OnPropertyChanged(nameof(ProgressFraction));
            }
        }
    }

    public int TotalSeconds
    {
        get => totalSeconds;
        private set
        {
            if (totalSeconds != value)
            {
                totalSeconds = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(StrokeDashOffset));
                OnPropertyChanged(nameof(ProgressFraction));
            }
        }
    }

    public int CompletedSessions
    {
        get => completedSessions;
        private set
        {
            if (completedSessions != value)
            {
                completedSessions = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(SessionCounterText));
                OnPropertyChanged(nameof(CycleProgressText));
            }
        }
    }

    public bool IsRunning => State == PomodoroState.Running;
    public bool IsFocusPhase => Phase == PomodoroPhase.Focus;
    public bool IsShortBreakPhase => Phase == PomodoroPhase.ShortBreak;
    public bool IsLongBreakPhase => Phase == PomodoroPhase.LongBreak;

    public string TimeText => $"{(RemainingSeconds / 60):D2}:{(RemainingSeconds % 60):D2}";

    public string PhaseTitle => Phase switch
    {
        PomodoroPhase.Focus => Locale.Pomodoro_Phase_Focus,
        PomodoroPhase.ShortBreak => Locale.Pomodoro_Phase_ShortBreak,
        PomodoroPhase.LongBreak => Locale.Pomodoro_Phase_LongBreak,
        _ => Locale.Pomodoro_Phase_Focus
    };

    public IBrush PhaseBrush
    {
        get
        {
            var theme = PanelAppearanceResolver.ResolveEffectiveTheme(PanelAppearanceResolver.TryResolveSettingsProvider());
            if (theme is { IsColorful: false, Monochrome: true }
                && ThemeScopeResolver.ResolveBrush(null, "SystemControlForegroundAccentBrush",
                    Application.Current?.ActualThemeVariant) is { } mono)
                return mono;

            return Phase switch
            {
                PomodoroPhase.Focus => new SolidColorBrush(Color.Parse("#FF5A5F")),
                PomodoroPhase.ShortBreak => new SolidColorBrush(Color.Parse("#34C759")),
                PomodoroPhase.LongBreak => new SolidColorBrush(Color.Parse("#007AFF")),
                _ => new SolidColorBrush(Color.Parse("#FF5A5F"))
            };
        }
    }

    public double ProgressFraction => TotalSeconds > 0 ? (double)RemainingSeconds / TotalSeconds : 0.0;

    /// <summary>
    /// For EllipseGeometry radius 48, stroke 7: circumference ~301.59, dash unit ~43.085.
    /// Starts at 0 (full circle), increases to 43.085 as time expires.
    /// </summary>
    public double StrokeDashOffset => 43.085 * (1.0 - ProgressFraction);

    public string SessionCounterText => $"× {CompletedSessions}";

    private static bool IsZh => System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.Equals("zh", StringComparison.OrdinalIgnoreCase);

    public string CycleProgressText
    {
        get
        {
            int interval = Math.Max(1, model.LongBreakInterval);
            int currentInCycle = (CompletedSessions % interval) + 1;
            return IsZh ? $"周期 {currentInCycle}/{interval}" : $"Cycle {currentInCycle}/{interval}";
        }
    }

    // Play icon: triangle, Pause icon: two bars
    public string PlayPauseIconData => IsRunning
        ? "M6 19h4V5H6v14zm8-14v14h4V5h-4z"
        : "M8 5v14l11-7z";

    public string PlayPauseTooltip => IsRunning ? Locale.Pomodoro_Pause : Locale.Pomodoro_Start;

    private int GetPhaseMinutes(PomodoroPhase p) => p switch
    {
        PomodoroPhase.Focus => Math.Max(1, model.FocusMinutes),
        PomodoroPhase.ShortBreak => Math.Max(1, model.ShortBreakMinutes),
        PomodoroPhase.LongBreak => Math.Max(1, model.LongBreakMinutes),
        _ => 25
    };

    public void UpdateModel(PomodoroModel newModel)
    {
        model = newModel;
        if (State == PomodoroState.Idle)
        {
            TotalSeconds = GetPhaseMinutes(Phase) * 60;
            RemainingSeconds = TotalSeconds;
        }
        OnPropertyChanged(nameof(CycleProgressText));
    }

    public void StartOrPause()
    {
        if (IsRunning)
        {
            State = PomodoroState.Paused;
        }
        else
        {
            if (RemainingSeconds <= 0)
            {
                TotalSeconds = GetPhaseMinutes(Phase) * 60;
                RemainingSeconds = TotalSeconds;
            }
            State = PomodoroState.Running;
        }
        SyncTimerRunState();
    }

    public void Reset()
    {
        State = PomodoroState.Idle;
        TotalSeconds = GetPhaseMinutes(Phase) * 60;
        RemainingSeconds = TotalSeconds;
        SyncTimerRunState();
    }

    public void Skip()
    {
        AdvanceToNextPhase(autoStart: false);
    }

    public void SwitchPhase(PomodoroPhase next)
    {
        State = PomodoroState.Idle;
        Phase = next;
        TotalSeconds = GetPhaseMinutes(Phase) * 60;
        RemainingSeconds = TotalSeconds;
        SyncTimerRunState();
    }

    private void OnTimerTick(object? sender, EventArgs e)
    {
        if (RemainingSeconds > 0)
        {
            RemainingSeconds--;
        }
        else
        {
            // Phase complete!
            SendPhaseCompleteNotification();

            bool autoStart;
            if (Phase == PomodoroPhase.Focus)
            {
                CompletedSessions++;
                autoStart = model.AutoStartBreaks;
            }
            else
            {
                autoStart = model.AutoStartFocus;
            }

            AdvanceToNextPhase(autoStart);
        }
    }

    private void SendPhaseCompleteNotification()
    {
        string title;
        string message;

        switch (Phase)
        {
            case PomodoroPhase.Focus:
                title = IsZh ? "番茄钟专注完成！" : "Focus Session Complete!";
                message = IsZh ? "太棒了，完成了一个番茄钟！放松一下吧。" : "Great job completing a Pomodoro session! Time to take a break.";
                break;
            case PomodoroPhase.ShortBreak:
                title = IsZh ? "短休结束" : "Short Break Over";
                message = IsZh ? "休息结束，准备开始新的专注！" : "Break is over, ready for the next focus session!";
                break;
            case PomodoroPhase.LongBreak:
                title = IsZh ? "长休结束" : "Long Break Over";
                message = IsZh ? "长休结束，精神满满，开启新一轮专注！" : "Long break is over! Refreshed and ready for a new cycle!";
                break;
            default:
                title = IsZh ? "番茄钟" : "Pomodoro";
                message = IsZh ? "计时完成！" : "Timer complete!";
                break;
        }

        NotificationHelper.Send(title, message, model.SoundEnabled);
    }

    private void AdvanceToNextPhase(bool autoStart)
    {
        if (Phase == PomodoroPhase.Focus)
        {
            int interval = Math.Max(1, model.LongBreakInterval);
            if (CompletedSessions > 0 && CompletedSessions % interval == 0)
            {
                Phase = PomodoroPhase.LongBreak;
            }
            else
            {
                Phase = PomodoroPhase.ShortBreak;
            }
        }
        else
        {
            Phase = PomodoroPhase.Focus;
        }

        TotalSeconds = GetPhaseMinutes(Phase) * 60;
        RemainingSeconds = TotalSeconds;

        State = autoStart ? PomodoroState.Running : PomodoroState.Idle;
        SyncTimerRunState();
    }

    // ------------------------------------------------------------------
    //  Session gating (host: IWidgetActivityConsumer)
    // ------------------------------------------------------------------

    /// <summary>
    /// Host activity push (the <see cref="DeskCanvas.Runtime.IWidgetActivityConsumer"/> path,
    /// invoked on the UI thread). The countdown is USER FUNCTION DATA: the gate never resets,
    /// pauses or loses it — a running countdown only stops its 1 s tick while the screens are
    /// covered, the wall-clock boundary is recorded, and the reopen replays the gated stretch
    /// (phase completions included) before the tick resumes. Session-less view models ignore
    /// the push entirely (the host never sends one to them).
    /// </summary>
    public void OnSessionActivity(ActivitySnapshot activity)
    {
        if (session == null) return;
        renderActive = activity.RenderActive;
        ReconcileSessionGating();
    }

    /// <summary>
    /// Reconciles the tick subscription against the session gate for a RUNNING countdown:
    /// gate close → stop the tick and record the boundary (once); gate open → replay the
    /// gated stretch from the boundary, then restart the tick if the countdown is still
    /// running. Non-running states simply stop the tick and drop any stale boundary.
    /// </summary>
    private void ReconcileSessionGating()
    {
        if (disposed) return;

        if (State != PomodoroState.Running)
        {
            gateClosedAtUtc = null;
            timer.Stop();
            return;
        }

        if (!renderActive)
        {
            timer.Stop();
            gateClosedAtUtc ??= DateTime.UtcNow;
            return;
        }

        var closedAt = gateClosedAtUtc;
        gateClosedAtUtc = null;
        if (closedAt.HasValue)
        {
            var gatedSeconds = (int)(DateTime.UtcNow - closedAt.Value).TotalSeconds;
            if (gatedSeconds > 0)
            {
                CompensateGatedSeconds(gatedSeconds);
                if (disposed) return;
            }
        }

        SyncTimerRunState();
    }

    /// <summary>Replays <paramref name="seconds"/> of wall-clock countdown through the regular
    /// tick handler, so phase completions, session counters and auto-starts behave exactly as
    /// they would have live. Stops early once the countdown is no longer running.</summary>
    private void CompensateGatedSeconds(int seconds)
    {
        for (int i = 0; i < seconds; i++)
        {
            if (disposed || State != PomodoroState.Running) break;
            OnTimerTick(timer, EventArgs.Empty);
        }
    }

    /// <summary>
    /// Plain run-state sync for user transitions (start/pause/reset/skip/phase switch and the
    /// auto-advance inside the tick): the 1 s DispatcherTimer runs exactly while the countdown
    /// is Running AND the session renders. A start issued while gated records its wall-clock
    /// boundary here (the press), so the reopen replays the stretch from the right point; the
    /// heavy boundary bookkeeping for gate pushes belongs to <see cref="ReconcileSessionGating"/>.
    /// </summary>
    private void SyncTimerRunState()
    {
        if (disposed) return;
        if (State != PomodoroState.Running)
        {
            gateClosedAtUtc = null;
            timer.Stop();
            return;
        }

        if (renderActive)
        {
            if (!timer.IsEnabled) timer.Start();
        }
        else
        {
            timer.Stop();
            gateClosedAtUtc ??= DateTime.UtcNow;
        }
    }

    // ------------------------------------------------------------------
    //  Check seams (tests/MediaWidgetSessionChecks)
    // ------------------------------------------------------------------

    /// <summary>Check seam: whether the 1 s countdown timer is currently running.</summary>
    public bool IsTimerEnabledForChecks => timer.IsEnabled;

    /// <summary>Check seam: the latest session gate value.</summary>
    public bool RenderActiveForChecks => renderActive;

    /// <summary>Check seam: the recorded gate boundary of a gated RUNNING countdown.</summary>
    public DateTime? GateClosedAtForChecks => gateClosedAtUtc;

    /// <summary>Check seam: the injected session (null on the legacy path).</summary>
    public IWidgetSession? SessionForChecks => session;

    /// <summary>Check seam: drives <paramref name="seconds"/> of countdown by hand, exercising
    /// the replay path exactly as the reopen push does.</summary>
    public void CompensateGatedSecondsForChecks(int seconds) => CompensateGatedSeconds(seconds);

    /// <summary>Check seam: backdates the recorded gate boundary, so a reopen push replays a
    /// deterministic number of gated seconds.</summary>
    public void BackdateGateBoundaryForChecks(TimeSpan age)
    {
        if (gateClosedAtUtc.HasValue)
            gateClosedAtUtc = gateClosedAtUtc.Value - age;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? propName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propName));
}

internal static class NotificationHelper
{
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool MessageBeep(uint uType);

    public static void Send(string title, string message, bool soundEnabled)
    {
        if (soundEnabled && OperatingSystem.IsWindows())
        {
            try
            {
                MessageBeep(0x00000040); // MB_ICONASTERISK
            }
            catch { }
        }

        if (!OperatingSystem.IsWindows()) return;

        System.Threading.Tasks.Task.Run(() =>
        {
            try
            {
                var script = $@"
[Windows.UI.Notifications.ToastNotificationManager, Windows.UI.Notifications, ContentType = WindowsRuntime] | Out-Null
$template = [Windows.UI.Notifications.ToastNotificationManager]::GetTemplateContent([Windows.UI.Notifications.ToastTemplateType]::ToastText02)
$textNodes = $template.GetElementsByTagName('text')
$textNodes.Item(0).AppendChild($template.CreateTextNode('{title.Replace("'", "''")}')) | Out-Null
$textNodes.Item(1).AppendChild($template.CreateTextNode('{message.Replace("'", "''")}')) | Out-Null
$toast = [Windows.UI.Notifications.ToastNotification]::new($template)
$appId = '{{1AC14E77-02E7-4E5D-B744-2EB1AE5198B7}}\WindowsPowerShell\v1.0\powershell.exe'
[Windows.UI.Notifications.ToastNotificationManager]::CreateToastNotifier($appId).Show($toast)
";
                var bytes = System.Text.Encoding.Unicode.GetBytes(script);
                var encoded = Convert.ToBase64String(bytes);

                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = $"-NoProfile -WindowStyle Hidden -EncodedCommand {encoded}",
                    CreateNoWindow = true,
                    UseShellExecute = false
                };
                using var proc = System.Diagnostics.Process.Start(psi);
            }
            catch { }
        });
    }
}
