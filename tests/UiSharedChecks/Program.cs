using DeskCanvas.UI;

var failures = new List<string>();
void Check(string name, bool ok) { Console.WriteLine($"[{(ok ? "PASS" : "FAIL")}] {name}"); if (!ok) failures.Add(name); }

var clock = new ClockStatusPresenter();
var countdown = new CountdownStatusPresenter();
Check("clock and countdown expose shared production type", clock.Shared.GetType() == countdown.Shared.GetType());
var changes = 0;
clock.Shared.Changed += (_, _) => changes++;
clock.ShowTime("12:00");
clock.ShowTime("12:00");
Check("duplicate status does not notify", changes == 1);
clock.ShowUnavailable();
Check("error is retryable", clock.Shared.Current is { Kind: SharedStatusKind.Error, CanRetry: true });
countdown.ShowNoEvents();
Check("empty state is explicit", countdown.Shared.Current.Kind == SharedStatusKind.Empty);

Console.WriteLine(failures.Count == 0 ? "UiSharedChecks ALL PASS" : $"UiSharedChecks FAILED: {string.Join(", ", failures)}");
return failures.Count == 0 ? 0 : 1;
