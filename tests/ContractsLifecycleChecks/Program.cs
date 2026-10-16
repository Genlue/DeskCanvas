using DeskCanvas.Contracts;
using DeskCanvas.Runtime;

var failures = new List<string>();
var passed = 0;
void Check(string name, bool ok)
{
    if (ok) { passed++; Console.WriteLine($"[PASS] {name}"); }
    else { failures.Add(name); Console.WriteLine($"[FAIL] {name}"); }
}

var id = WidgetInstanceId.New();
Check("instance id is stable and non-empty", !id.IsEmpty && id.Equals(id));
var descriptor = new WidgetInstanceDescriptor("Clock");
Check("descriptor allocates identity independently of widget type", !descriptor.InstanceId.IsEmpty && descriptor.WidgetId == "Clock");
var activity = new ActivitySnapshot(ActivityReason.DesktopVisible | ActivityReason.ScreenCovered);
Check("activity reasons compose without boolean overwrite", !activity.IsVisible && activity.RenderActive == false);
Check("preview is renderable but not polling", ActivitySnapshot.Preview.RenderActive && !ActivitySnapshot.Preview.PollingActive);

var calls = new List<string>();
var session = new WidgetSession(id,
    initialize: _ => { calls.Add("init"); return ValueTask.CompletedTask; },
    applySettings: (_, _) => { calls.Add("settings"); return ValueTask.CompletedTask; },
    setActivity: (_, _) => { calls.Add("activity"); return ValueTask.CompletedTask; },
    dispose: _ => { calls.Add("dispose"); return ValueTask.CompletedTask; });
Check("new session starts Created", session.State == WidgetLifecycleState.Created);
await session.InitializeAsync();
Check("initialize reaches Ready", session.State == WidgetLifecycleState.Ready && calls.SequenceEqual(new[] { "init" }));
var gen = session.Generation;
await Task.WhenAll(session.ApplySettingsAsync(new { Value = 1 }).AsTask(), session.SetActivityAsync(new ActivitySnapshot(ActivityReason.DesktopVisible)).AsTask());
Check("commands are serialized and advance generation", session.Generation >= gen + 2 && calls.Count == 3);
await session.DisposeAsync();
await session.DisposeAsync();
Check("dispose is idempotent", session.State == WidgetLifecycleState.Disposed && calls.Count(x => x == "dispose") == 1);
try { await session.SetActivityAsync(new ActivitySnapshot()); Check("disposed rejects commands", false); }
catch (InvalidOperationException) { Check("disposed rejects commands", true); }

var faulted = new WidgetSession(WidgetInstanceId.New(), initialize: _ => ValueTask.FromException(new InvalidOperationException("boom")));
try { await faulted.InitializeAsync(); } catch (InvalidOperationException) { }
Check("initialization failure is observable", faulted.State == WidgetLifecycleState.Faulted && faulted.Fault is not null);
try { await faulted.ApplySettingsAsync(null); Check("faulted session rejects work", false); }
catch (InvalidOperationException) { Check("faulted session rejects work", true); }
await faulted.DisposeAsync();

Console.WriteLine(failures.Count == 0 ? $"\nContractsLifecycleChecks ALL PASS ({passed} 项)" : $"\nContractsLifecycleChecks FAILED: {string.Join(", ", failures)}");
return failures.Count == 0 ? 0 : 1;
