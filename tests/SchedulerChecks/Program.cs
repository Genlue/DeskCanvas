using DeskCanvas.Core.Scheduling;

var failures = new List<string>();
void Check(string name, bool ok) { Console.WriteLine($"[{(ok ? "PASS" : "FAIL")}] {name}"); if (!ok) failures.Add(name); }

await using var scheduler = new WidgetScheduler();
var count = 0;
using var lease = scheduler.Schedule(new SchedulerJobOptions { OwnerId = "clock-1", Period = TimeSpan.FromMilliseconds(20), StartImmediately = true }, _ => { Interlocked.Increment(ref count); return ValueTask.CompletedTask; });
await Task.Delay(90);
Check("periodic callback runs", count >= 2);
var beforePause = count; scheduler.Pause("clock-1"); await Task.Delay(70); Check("owner pause stops callback", count == beforePause);
scheduler.Resume("clock-1"); await Task.Delay(60); Check("owner resume restarts callback", count > beforePause);
await lease.DisposeAsync(); var jobsAfterDispose = scheduler.Snapshot(); Check("lease disposal removes job", jobsAfterDispose.Count == 0);

var failed = 0;
using var bad = scheduler.Schedule(new SchedulerJobOptions { OwnerId = "bad", Period = TimeSpan.FromMilliseconds(10) }, _ => { Interlocked.Increment(ref failed); throw new InvalidOperationException("probe"); });
await Task.Delay(40); Check("callback failure isolated", failed > 0 && scheduler.Snapshot().Single().Failed > 0);

Console.WriteLine(failures.Count == 0 ? "SchedulerChecks ALL PASS" : $"SchedulerChecks FAILED: {string.Join(", ", failures)}");
return failures.Count == 0 ? 0 : 1;
