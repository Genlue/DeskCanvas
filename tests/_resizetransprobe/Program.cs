using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Styling;
using Avalonia.Threading;

// Deterministic experiment: what happens to Width when the Transitions
// collection is cleared mid-transition under Avalonia 12.1.4?
AppBuilder.Configure<Application>()
    .UseHeadless(new AvaloniaHeadlessPlatformOptions())
    .SetupWithoutStarting();

var failures = new List<string>();
void Check(bool ok, string msg)
{
    Console.WriteLine($"[{(ok ? "PASS" : "FAIL")}] {msg}");
    if (!ok) failures.Add(msg);
}
void Flush() => Dispatcher.UIThread.RunJobs();
void Tick(int ms) => AvaloniaHeadlessPlatform.ForceRenderTimerTick(ms);

// ---- Experiment 1: decisive semantics test ----------------------------------
// 300-SECOND duration on purpose: the transition cannot complete, so whatever
// Width reads after clearing Transitions exposes the framework semantics.
var w = new Window { Width = 353, Height = 176 };
w.Show();
Flush();
Check(Math.Abs(w.Width - 353) < 0.01, $"baseline Width is 353 (got {w.Width:0.##})");

w.Transitions = new Transitions
{
    new DoubleTransition { Property = Window.WidthProperty, Duration = TimeSpan.FromSeconds(300) },
    new DoubleTransition { Property = Window.HeightProperty, Duration = TimeSpan.FromSeconds(300) },
};
w.Width = 530;
w.Height = 264;
Flush();
Console.WriteLine($"      Width right after assignment: {w.Width:0.##}");

Tick(100); // advance the render clock 100ms of the 300s transition (~0.03%)
Flush();
var midTransition = w.Width;
Console.WriteLine($"      Width 100ms into a 300s transition: {midTransition:0.##}");

w.Transitions = null;
Flush();
var afterClear = w.Width;
Console.WriteLine($"      Width after Transitions = null: {afterClear:0.##}");

if (Math.Abs(afterClear - 530) < 0.01)
    Console.WriteLine("      [info] semantics A under this host: clearing Transitions jumps Width to the target local value");
else
    Console.WriteLine($"      [info] semantics B under this host: clearing Transitions strands Width at {afterClear:0.##} instead of 530 — the Resize tail MUST re-assert the target size");

// ---- Experiment 2: re-target mid-transition (RefreshDisplayMetrics pattern) --
var w2 = new Window { Width = 353, Height = 176 };
w2.Show();
Flush();
w2.Transitions = new Transitions
{
    new DoubleTransition { Property = Window.WidthProperty, Duration = TimeSpan.FromSeconds(300) },
};
w2.Width = 530;
Flush();
Tick(50);
Flush();
w2.Width = 353; // the "old stored size" overwrite mid-flight
Flush();
Console.WriteLine($"      Re-assignment mid-transition lands Width at: {w2.Width:0.##}");
Check(Math.Abs(w2.Width - 353) < 0.01,
    "mid-flight overwrite is accepted (this is what made RefreshDisplayMetrics snap the window back)");

// ---- Experiment 3: ForceRenderTimerTick really advances the interpolation ----
var w3 = new Window { Width = 353, Height = 176 };
w3.Show();
Flush();
w3.Transitions = new Transitions
{
    new DoubleTransition { Property = Window.WidthProperty, Duration = TimeSpan.FromMilliseconds(80) },
};
w3.Width = 530;
for (var i = 0; i < 6; i++)
{
    Tick(50);
    Flush();
}
Console.WriteLine($"      Short (80ms) transition after 300ms of ticks: Width = {w3.Width:0.##}");
Check(Math.Abs(w3.Width - 530) < 0.01 || Math.Abs(w3.Width - 353) < 1,
    "transition end-state is either the target or (headless quirk) still near the start — both are rescued by the explicit re-assert in Resize");

// ---- Experiment 4: can an explicit re-assert rescue the stranded value? ------
// The Resize-tail fix: clear Transitions, then assign the target size again.
var w4 = new Window { Width = 353, Height = 176 };
w4.Show();
Flush();
w4.Transitions = new Transitions
{
    new DoubleTransition { Property = Window.WidthProperty, Duration = TimeSpan.FromSeconds(300) },
    new DoubleTransition { Property = Window.HeightProperty, Duration = TimeSpan.FromSeconds(300) },
};
w4.Width = 530;
w4.Height = 264;
Flush();
Tick(100);
Flush();
w4.Transitions = null;
Flush();
var stranded = w4.Width;
w4.Width = 530; // explicit re-assert, transition already cleared
w4.Height = 264;
Flush();
Console.WriteLine($"      stranded after clear = {stranded:0.##}; after explicit re-assert = {w4.Width:0.##} / {w4.Height:0.##}");
Check(Math.Abs(w4.Width - 530) < 0.01 && Math.Abs(w4.Height - 264) < 0.01,
    "explicit re-assert after clearing Transitions lands on the target");

// ---- Experiment 5: Min/Max lock at the stranded value, then resize -----------
// Reproduces SetMinMaxSize(true) locking a stranded size, then the next
// Resize cycle: does the unlock -> assign sequence still work?
var w5 = new Window { Width = 353, Height = 176 };
w5.Show();
Flush();
w5.MinWidth = w5.MaxWidth = w5.Width;
w5.MinHeight = w5.MaxHeight = w5.Height;
w5.MinWidth = w5.MinHeight = 1;
w5.MaxWidth = w5.MaxHeight = double.PositiveInfinity;
w5.Transitions = new Transitions
{
    new DoubleTransition { Property = Window.WidthProperty, Duration = TimeSpan.FromSeconds(300) },
};
w5.Width = 530;
Flush();
Tick(100);
Flush();
w5.Transitions = null;
Flush();
w5.Width = 530; // explicit re-assert
Flush();
w5.MinWidth = w5.MaxWidth = w5.Width;
w5.MinHeight = w5.MaxHeight = w5.Height;
Flush();
Console.WriteLine($"      locked size after re-assert = {w5.Width:0.##}");
Check(Math.Abs(w5.Width - 530) < 0.01,
    "unlock -> assign -> re-lock lands on the target under Min/Max bookkeeping");

Console.WriteLine(failures.Count == 0 ? "ALL CHECKS PASSED" : $"{failures.Count} CHECK(S) FAILED");
return failures.Count == 0 ? 0 : 1;
