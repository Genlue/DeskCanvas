namespace DeskCanvas.Views;

/// <summary>
/// The motion vocabulary every surface in the app shares: one set of durations, one set of travel
/// distances, one stagger cadence.
/// <para>
/// These numbers used to be duplicated per window (<c>SummonScaleMs</c> here, <c>OpenDuration</c>
/// there), which is how two panels end up opening with the same intent but different timings — the
/// thing that reads as "not standardised" even when each animation is individually fine. The values
/// are the mod's budgets (动效系统交接 §4.3): 0.42 s open / 0.26 s close on the bounce spring, and a
/// 1.25 s appearance reserved for fades only.
/// </para>
/// <para>
/// Rules of thumb that the names encode:
/// </para>
/// <list type="bullet">
///   <item><description><b>Expand</b> is for things getting bigger or arriving: 320 ms, bounce.</description></item>
///   <item><description><b>Collapse</b> is for things leaving: 180 ms, critically damped — an exit
///   that overshoots reads as hesitation.</description></item>
///   <item><description><b>Fade</b> never overshoots (alpha clips at 1), so it is always smooth.</description></item>
///   <item><description><b>Rise</b> is the direction an appearing surface travels: it comes down
///   from above by a few DIP so "appearing" has a direction instead of being a plain scale-up.</description></item>
/// </list>
/// </summary>
internal static class MotionTokens
{
    /// <summary>Open / grow / arrive. Bounce spring.</summary>
    public const int ExpandMs = 320;

    /// <summary>Close / shrink / leave. Critically damped.</summary>
    public const int CollapseMs = 180;

    /// <summary>Opacity-only flights. Critically damped — an overshooting alpha would clip.</summary>
    public const int FadeMs = 200;

    /// <summary>
    /// Padding added to a flight's duration before the settle pass runs, so the landing never races
    /// the transition's last frame.
    /// </summary>
    public const int SettlePadMs = 60;

    /// <summary>Starting scale of an appearing surface (0.94 = a 6% micro-scale, not a zoom).</summary>
    public const double SummonScaleFrom = 0.94;

    /// <summary>Where the rise starts, in DIP above the resting position (自上而下落定).</summary>
    public const double SummonRiseFrom = -7;

    /// <summary>
    /// How long a newly revealed pane waits before its content fades in, so the surface has visibly
    /// made room before anything is drawn in it.
    /// </summary>
    public const int PaneRevealDelayMs = 70;

    /// <summary>How far a revealed pane's content travels up as it fades in, in DIP.</summary>
    public const double PaneRevealRiseFrom = 8;

    /// <summary>Delay between two consecutive result rows appearing.</summary>
    public const int RowStaggerStepMs = 14;

    /// <summary>Per-row fade/rise duration. The whole cascade stays under ~120 ms + 8×14 ms.</summary>
    public const int RowStaggerMs = 120;

    /// <summary>How far a result row travels up as it appears, in DIP.</summary>
    public const double RowRiseFrom = 4;
}
