// Media widget session checks (P8 batch E — Picture / Music / Map / Pomodoro).
//
// Run: dotnet run --project tests/MediaWidgetSessionChecks
//
// Pins the batch E session gating: the Picture playback beats (slideshow, cross-fade, GIF)
// and the Music 250 ms progress tick follow the injected session's RenderActive (the SMTC
// shared engine deliberately untouched); the Pomodoro countdown gates its 1 s tick without
// losing countdown state (the gated stretch is replayed from the recorded wall-clock
// boundary); Map's auto theme resolves per window. Direct construction only — the host
// pipeline is covered by tests/SessionHookChecks.
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Skia;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using DeskCanvas.Contracts;
using DeskCanvas.Runtime;
using Music.Models;
using Music.Services;
using Music.ViewModels;
using Picture.Models;
using Picture.ViewModels;
using Pomodoro.Models;
using Pomodoro.ViewModels;
using Pomodoro.Views;
using PictureWidget = Picture.Views.PictureView;
using MusicWidget = Music.Views.Music;
using MapViewWidget = Map.Views.MapView;

AppBuilder.Configure<ProbeApp>().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).UseSkia().SetupWithoutStarting();
Application.Current!.Styles.Add(new FluentTheme());

var failures = new List<string>();
var checks = 0;
void Check(bool condition, string message)
{
    checks++;
    Console.WriteLine($"[{(condition ? "PASS" : "FAIL")}] {message}");
    if (!condition) failures.Add(message);
}

// --- rig helpers -------------------------------------------------------------------------

// A minimal 1x1 PNG: enough for PictureImageLoader (Skia codec) to produce a real bitmap.
const string Png1x1Base64 = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==";
// A different 1x1 PNG (grey+alpha) — a replaced cover on the same track.
const string Png1x1WhiteBase64 = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+ip1sAAAAASUVORK5CYII=";
var pictureDir = Path.Combine(Path.GetTempPath(), "MediaWidgetSessionChecks-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(pictureDir);
string WritePng(string name)
{
    var path = Path.Combine(pictureDir, name + ".png");
    File.WriteAllBytes(path, Convert.FromBase64String(Png1x1Base64));
    return path;
}

PictureModel TwoPictureModel() => new(
    Items: [new PictureItem { Path = WritePng("alpha"), Name = "Alpha" },
            new PictureItem { Path = WritePng("beta"), Name = "Beta" }],
    Interval: SlideshowInterval.Seconds10,
    Order: PlayOrder.Sequential);

IWidgetSession VisibleSession()
{
    var session = WidgetSessionRegistry.GetOrCreate(WidgetInstanceId.New());
    session.SetActivityAsync(new ActivitySnapshot(ActivityReason.DesktopVisible)).AsTask().GetAwaiter().GetResult();
    return session;
}

IWidgetSession CoveredSession()
{
    var session = WidgetSessionRegistry.GetOrCreate(WidgetInstanceId.New());
    session.SetActivityAsync(new ActivitySnapshot(ActivityReason.ScreenCovered)).AsTask().GetAwaiter().GetResult();
    return session;
}

T VmOf<T>(UserControl view, string field = "viewModel") =>
    (T)(view.GetType().GetField(field, BindingFlags.NonPublic | BindingFlags.Instance)
        ?? throw new InvalidOperationException($"field {field} missing on {view.GetType().Name}"))
        .GetValue(view)!;

string Source(string repoRelativePath)
{
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir != null && !File.Exists(Path.Combine(dir.FullName, "src", "DeskCanvas", "Views", "Widget.axaml.cs")))
        dir = dir.Parent;
    if (dir == null) throw new InvalidOperationException("repository root not found");
    return File.ReadAllText(Path.Combine(dir.FullName, repoRelativePath.Replace('/', Path.DirectorySeparatorChar)));
}

/// <summary>The body of one method, from its declaration through its closing brace
/// (members sit at a 4-space indent, so "\n    }" is the method's own closing brace).</summary>
string MethodBody(string source, string declaration)
{
    var start = source.IndexOf(declaration, StringComparison.Ordinal);
    if (start < 0) return string.Empty;
    var end = source.IndexOf("\n    }", start + declaration.Length, StringComparison.Ordinal);
    return end < 0 ? source[start..] : source[start..(end + 6)];
}

// =========================================================================================
// 1. Picture: the playback beats follow the session's RenderActive; state is never lost
// =========================================================================================
Console.WriteLine("--- Picture: slideshow/transition/GIF gating ---");

var pLegacy = new PictureViewModel(TwoPictureModel());
Check(pLegacy.SessionForChecks == null, "1.1 a session-less PictureViewModel keeps the legacy path");
Check(pLegacy.IsSlideshowRunning, "1.2 a session-less PictureViewModel keeps the slideshow armed");
pLegacy.OnSessionActivity(new ActivitySnapshot(ActivityReason.ScreenCovered));
Check(pLegacy.IsSlideshowRunning, "1.3 a session-less PictureViewModel ignores activity pushes");
pLegacy.Dispose();

var pVisible = new PictureViewModel(TwoPictureModel(), VisibleSession());
Check(pVisible.IsSlideshowRunning && pVisible.HasPictures, "1.4 an injected, visible session starts playing");
pVisible.OnSessionActivity(new ActivitySnapshot(ActivityReason.ScreenCovered));
Check(!pVisible.IsSlideshowRunning && !pVisible.IsTransitionRunning && !pVisible.IsAnimationRunning,
    "1.5 RenderActive=false stops every playback beat (slideshow, cross-fade, GIF)");
Check(pVisible.HasPictures && pVisible.Caption == "Alpha", "1.6 the gated stop keeps the decoded state and the current picture");
pVisible.OnSessionActivity(new ActivitySnapshot(ActivityReason.DesktopVisible));
Check(pVisible.IsSlideshowRunning && pVisible.Caption == "Alpha",
    "1.7 the reopen re-arms the slideshow and keeps the carousel index (nothing lost)");
pVisible.Dispose();

var pCovered = new PictureViewModel(TwoPictureModel(), CoveredSession());
Check(!pCovered.IsSlideshowRunning && pCovered.PendingReloadForChecks,
    "1.8 a covered session starts stopped and flags the initial load as pending");
Check(!pCovered.HasPictures, "1.9 no decode happens while covered");
pCovered.OnSessionActivity(new ActivitySnapshot(ActivityReason.DesktopVisible));
Check(pCovered.HasPictures && pCovered.IsSlideshowRunning, "1.10 the reopen replays the pending load and re-arms playback");
pCovered.Dispose();

var pFade = new PictureViewModel(TwoPictureModel(), VisibleSession());
pFade.NextPicture(); // cross-fade armed (immediate=false)
Check(pFade.IsTransitionRunning && pFade.Caption == "Beta", "1.11 a manual switch arms the cross-fade");
pFade.OnSessionActivity(new ActivitySnapshot(ActivityReason.ScreenCovered));
Check(!pFade.IsTransitionRunning, "1.12 the gate stops the cross-fade tick mid-transition");
pFade.OnSessionActivity(new ActivitySnapshot(ActivityReason.DesktopVisible));
Check(pFade.Caption == "Beta" && pFade.HasPictures && !pFade.IsTransitionRunning,
    "1.13 the reopen keeps the switched picture and does not resurrect the fade");
pFade.Dispose();

pVisible = new PictureViewModel(TwoPictureModel(), VisibleSession());
pVisible.OnSessionActivity(new ActivitySnapshot(ActivityReason.ScreenCovered));
pVisible.Dispose();
Check(!pVisible.IsSlideshowRunning, "1.14 a disposed gated view model never re-arms a timer");

var pPreview = new PictureViewModel(TwoPictureModel(), CoveredSession());
pPreview.OnSessionActivity(ActivitySnapshot.Preview);
Check(pPreview.IsSlideshowRunning, "1.15 the preview snapshot (RenderActive=true) keeps playback alive");
pPreview.Dispose();

var pvView = new PictureWidget(TwoPictureModel(), null, VisibleSession());
Check(pvView is IWidgetActivityConsumer, "1.16 PictureView implements IWidgetActivityConsumer");
var pvVm = VmOf<PictureViewModel>(pvView);
Check(pvVm.SessionForChecks != null, "1.17 the view forwards the injected session to the view model");
pvView.OnSessionActivity(new ActivitySnapshot(ActivityReason.ScreenCovered));
Check(!pvVm.IsSlideshowRunning, "1.18 the view's activity push gates the view model");
pvVm.Dispose();
var pvLegacy = new PictureWidget(new PictureModel());
Check(VmOf<PictureViewModel>(pvLegacy).SessionForChecks == null, "1.19 the legacy PictureView constructor stays session-less");

// =========================================================================================
// 2. Music: the 250 ms progress tick follows the session; the shared SMTC engine is untouched
// =========================================================================================
Console.WriteLine("--- Music: progress tick gating (SMTC unaffected) ---");

var mLegacy = new MusicViewModel(new MusicModel());
Check(mLegacy.IsProgressTimerRunningForChecks, "2.1 a session-less MusicViewModel keeps the progress tick running");
mLegacy.OnSessionActivity(new ActivitySnapshot(ActivityReason.ScreenCovered));
Check(mLegacy.IsProgressTimerRunningForChecks, "2.2 a session-less MusicViewModel ignores activity pushes");
mLegacy.Dispose();

var mVisible = new MusicViewModel(new MusicModel(), VisibleSession());
Check(mVisible.IsProgressTimerRunningForChecks, "2.3 an injected, visible session starts the progress tick");
mVisible.OnSessionActivity(new ActivitySnapshot(ActivityReason.ScreenCovered));
Check(!mVisible.IsProgressTimerRunningForChecks, "2.4 RenderActive=false stops only the progress tick");
mVisible.OnSessionActivity(new ActivitySnapshot(ActivityReason.DesktopVisible));
Check(mVisible.IsProgressTimerRunningForChecks, "2.5 the reopen restarts the progress tick");
mVisible.Dispose();

var mCovered = new MusicViewModel(new MusicModel(), CoveredSession());
Check(!mCovered.IsProgressTimerRunningForChecks, "2.6 a covered session starts without the progress tick");
mCovered.Suspend();
mCovered.Resume(); // host suspension path with the gate still closed: must stay stopped
Check(!mCovered.IsProgressTimerRunningForChecks, "2.7 Resume with the gate closed stays stopped (both gates reconciled)");
mCovered.OnSessionActivity(new ActivitySnapshot(ActivityReason.DesktopVisible));
Check(mCovered.IsProgressTimerRunningForChecks, "2.8 the reopen restarts the tick after the suspended stretch");
mCovered.Dispose();

var musicView = new MusicWidget(new MusicModel(), VisibleSession());
Check(musicView is IWidgetActivityConsumer, "2.9 Music implements IWidgetActivityConsumer");
var musicVm = VmOf<MusicViewModel>(musicView);
Check(musicVm.SessionForChecks != null && musicVm.IsProgressTimerRunningForChecks,
    "2.10 the view forwards the injected session to the view model");
musicView.OnSessionActivity(new ActivitySnapshot(ActivityReason.ScreenCovered));
Check(!musicVm.IsProgressTimerRunningForChecks, "2.11 the view's activity push gates the progress tick");
musicVm.Dispose();

var musicSource = Source("src/Widgets/Lifestyle/Music/ViewModels/MusicViewModel.cs");
var pushBody = MethodBody(musicSource, "public void OnSessionActivity");
Check(!string.IsNullOrEmpty(pushBody) && !pushBody.Contains("PausePolling") && !pushBody.Contains("ResumePolling"),
    "2.12 (source) the session push never touches the shared SMTC poll engine");

// =========================================================================================
// 3. Pomodoro: the 1 s countdown tick is gated, the countdown DATA never is
// =========================================================================================
Console.WriteLine("--- Pomodoro: countdown state preservation ---");

var fLegacy = new PomodoroViewModel(new PomodoroModel(SoundEnabled: false));
fLegacy.StartOrPause();
Check(fLegacy.IsTimerEnabledForChecks && fLegacy.IsRunning, "3.1 a session-less PomodoroViewModel keeps the legacy always-on tick");
fLegacy.OnSessionActivity(new ActivitySnapshot(ActivityReason.ScreenCovered));
Check(fLegacy.IsTimerEnabledForChecks, "3.2 a session-less PomodoroViewModel ignores activity pushes");
fLegacy.Dispose();

var fVisible = new PomodoroViewModel(new PomodoroModel(SoundEnabled: false), VisibleSession());
fVisible.StartOrPause();
Check(fVisible.IsTimerEnabledForChecks && fVisible.RemainingSeconds == 25 * 60,
    "3.3 an injected, visible session ticks while running");
fVisible.OnSessionActivity(new ActivitySnapshot(ActivityReason.ScreenCovered));
Check(!fVisible.IsTimerEnabledForChecks, "3.4 RenderActive=false stops only the 1 s tick");
Check(fVisible.IsRunning && fVisible.RemainingSeconds == 25 * 60,
    "3.5 the gated stop keeps the phase and the remaining time (state not lost)");
Check(fVisible.GateClosedAtForChecks != null, "3.6 the gated stretch boundary is recorded");
fVisible.OnSessionActivity(new ActivitySnapshot(ActivityReason.DesktopVisible));
Check(fVisible.IsTimerEnabledForChecks && fVisible.RemainingSeconds == 25 * 60,
    "3.7 an immediate reopen restarts the tick without replaying anything");

fVisible.OnSessionActivity(new ActivitySnapshot(ActivityReason.ScreenCovered));
fVisible.BackdateGateBoundaryForChecks(TimeSpan.FromSeconds(10));
fVisible.OnSessionActivity(new ActivitySnapshot(ActivityReason.DesktopVisible));
Check(fVisible.RemainingSeconds == 25 * 60 - 10, "3.8 the reopen replays the gated wall-clock stretch (10 s here)");
fVisible.Dispose();

var fComplete = new PomodoroViewModel(new PomodoroModel(SoundEnabled: false), VisibleSession());
fComplete.StartOrPause();
fComplete.OnSessionActivity(new ActivitySnapshot(ActivityReason.ScreenCovered));
fComplete.BackdateGateBoundaryForChecks(TimeSpan.FromSeconds(25 * 60 + 7));
fComplete.OnSessionActivity(new ActivitySnapshot(ActivityReason.DesktopVisible));
Check(fComplete.CompletedSessions == 1, "3.9 a gated stretch that outlasts the phase completes the session");
Check(fComplete.Phase == PomodoroPhase.ShortBreak && fComplete.RemainingSeconds == 5 * 60,
    "3.10 the completion advanced to the next phase with its full budget");
Check(!fComplete.IsTimerEnabledForChecks, "3.11 no auto-start (model default) keeps the tick off after the advance");
fComplete.Dispose();

var fGated = new PomodoroViewModel(new PomodoroModel(SoundEnabled: false), CoveredSession());
fGated.StartOrPause();
Check(fGated.IsRunning && !fGated.IsTimerEnabledForChecks && fGated.GateClosedAtForChecks != null,
    "3.12 starting while gated runs in wall-clock terms (tick off, boundary at the press)");
fGated.BackdateGateBoundaryForChecks(TimeSpan.FromSeconds(5));
fGated.OnSessionActivity(new ActivitySnapshot(ActivityReason.DesktopVisible));
Check(fGated.IsTimerEnabledForChecks && fGated.RemainingSeconds == 25 * 60 - 5,
    "3.13 the reopen replays from the press boundary");
fGated.StartOrPause(); // pause
Check(!fGated.IsTimerEnabledForChecks && !fGated.IsRunning, "3.14 pausing stops the tick through the same reconciler");
fGated.Dispose();

var fPauseGated = new PomodoroViewModel(new PomodoroModel(SoundEnabled: false), VisibleSession());
fPauseGated.StartOrPause();
fPauseGated.OnSessionActivity(new ActivitySnapshot(ActivityReason.ScreenCovered));
fPauseGated.StartOrPause(); // pause while gated
Check(fPauseGated.GateClosedAtForChecks == null, "3.15 pausing clears the stale gate boundary");
fPauseGated.OnSessionActivity(new ActivitySnapshot(ActivityReason.DesktopVisible));
Check(!fPauseGated.IsTimerEnabledForChecks && fPauseGated.RemainingSeconds == 25 * 60,
    "3.16 a paused countdown stays paused across the reopen");
fPauseGated.Dispose();

var fOverlap = new PomodoroViewModel(new PomodoroModel(SoundEnabled: false), VisibleSession());
fOverlap.StartOrPause();
fOverlap.OnSessionActivity(new ActivitySnapshot(ActivityReason.ScreenCovered));
fOverlap.BackdateGateBoundaryForChecks(TimeSpan.FromSeconds(100));
fOverlap.OnSessionActivity(new ActivitySnapshot(ActivityReason.ScreenCovered)); // overlapping push must not reset the boundary
fOverlap.OnSessionActivity(new ActivitySnapshot(ActivityReason.DesktopVisible));
Check(fOverlap.RemainingSeconds == 25 * 60 - 100, "3.17 overlapping covered pushes record the boundary once (no double replay)");
fOverlap.Dispose();

var fDispose = new PomodoroViewModel(new PomodoroModel(SoundEnabled: false), CoveredSession());
fDispose.StartOrPause();
fDispose.Dispose();
Check(fDispose.IsDisposed && !fDispose.IsTimerEnabledForChecks, "3.18 dispose while gated tears down cleanly");

var fView = new PomodoroView(new PomodoroModel(SoundEnabled: false), null, VisibleSession());
Check(fView is IWidgetActivityConsumer, "3.19 PomodoroView implements IWidgetActivityConsumer");
var fViewVm = VmOf<PomodoroViewModel>(fView);
Check(fViewVm.SessionForChecks != null, "3.20 the view forwards the injected session to the view model");
fView.OnSessionActivity(new ActivitySnapshot(ActivityReason.ScreenCovered));
fViewVm.StartOrPause();
Check(fViewVm.IsRunning && !fViewVm.IsTimerEnabledForChecks, "3.21 the view's gated session keeps the countdown data intact");
fViewVm.Dispose();

// =========================================================================================
// 4. Map: per-screen auto theme (window variant wins over the uniform Application variant)
// =========================================================================================
Console.WriteLine("--- Map: per-screen auto theme ---");

Check(MapViewWidget.ResolveAutoIsDark(ThemeVariant.Dark, ThemeVariant.Light),
    "5.1 the host window's Dark variant wins over a Light uniform variant");
Check(!MapViewWidget.ResolveAutoIsDark(ThemeVariant.Light, ThemeVariant.Dark),
    "5.2 the host window's Light variant wins over a Dark uniform variant");
Check(MapViewWidget.ResolveAutoIsDark(null, ThemeVariant.Dark), "5.3 a detached control falls back to the uniform variant");
Check(!MapViewWidget.ResolveAutoIsDark(ThemeVariant.Default, ThemeVariant.Light),
    "5.4 the Default (unattached) variant falls through to the uniform variant");

var mapSource = Source("src/Widgets/Lifestyle/Map/Views/MapView.axaml.cs");
Check(mapSource.Contains("ResolveAutoIsDark(ActualThemeVariant, Application.Current?.ActualThemeVariant)"),
    "5.5 (source) the AutoTheme branch resolves the variant per window first");
Check(!mapSource.Contains("IWidgetActivityConsumer"),
    "5.6 (source) MapView stays session-free — its only timer is the interaction-driven geocode debounce");

// =========================================================================================
// 6. Wiring source scan (guards the batch E wiring against accidental removal)
// =========================================================================================
Console.WriteLine("--- Wiring source scan ---");

Check(Source("src/Widgets/Lifestyle/Picture/Views/PictureView.axaml.cs").Contains("IWidgetActivityConsumer"),
    "6.1 (source) PictureView declares the activity consumer");
var pomodoroSource = Source("src/Widgets/Productivity/Pomodoro/ViewModels/PomodoroViewModel.cs");
Check(pomodoroSource.Contains("CompensateGatedSeconds") && pomodoroSource.Contains("gateClosedAtUtc"),
    "6.2 (source) the Pomodoro countdown replays the gated stretch from a recorded boundary");
Check(pomodoroSource.Contains("State == PomodoroState.Running && renderActive") ||
      pomodoroSource.Contains("State != PomodoroState.Running"),
    "6.3 (source) the Pomodoro tick subscription is reconciled from state AND gate (no toggling)");
Check(Source("src/Widgets/Lifestyle/Picture/ViewModels/PictureViewModel.cs").Contains("UpdatePlaybackGating"),
    "6.4 (source) the Picture beats reconcile through a single gating state machine");
Check(Source("src/Widgets/Lifestyle/Music/ViewModels/MusicViewModel.cs").Contains("UpdateProgressTimer"),
    "6.5 (source) the Music progress tick reconciles through a single state machine");
var musicVmSource = Source("src/Widgets/Lifestyle/Music/ViewModels/MusicViewModel.cs");
var subscribeAt = musicVmSource.IndexOf("mediaService.TrackChanged += OnTrackChanged", StringComparison.Ordinal);
var replayAt = musicVmSource.IndexOf("mediaService.PublishCurrentTrack()", StringComparison.Ordinal);
Check(subscribeAt >= 0 && replayAt > subscribeAt,
    "6.6 (source) the Music view model replays the engine snapshot after subscribing (late-join convergence)");

// =========================================================================================
// 7. Music: live snapshot refresh — a consumer must converge without a manual refresh
// =========================================================================================
Console.WriteLine("--- Music: live snapshot refresh ---");

// The dispatcher has no event loop running in this probe: posted work (the view model's
// UI-thread marshalling) is drained explicitly.
void Pump(int milliseconds = 120)
{
    var until = Environment.TickCount64 + milliseconds;
    while (Environment.TickCount64 < until)
    {
        Dispatcher.UIThread.RunJobs();
        Thread.Sleep(5);
    }
    Dispatcher.UIThread.RunJobs();
}

var cover = Convert.FromBase64String(Png1x1Base64);
SharedMediaSnapshot live = new("qqmusic", "Song A", "Artist A", "Album A", "QQ Music", true,
    TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(200), true, true, true, cover);
var liveReads = 0;
var liveProvider = new MediaSessionSharedProvider((_, _) => { Interlocked.Increment(ref liveReads); return Task.FromResult(live); },
    TimeSpan.Zero);

// 7.1 fresh consumer: the engine's first round must reach the view model with no explicit refresh
var liveVm = new MusicViewModel(new MusicModel(), null, liveProvider);
Pump(300);
Check(liveVm.TrackTitle == "Song A" && liveVm.Artist == "Artist A" && liveVm.IsPlaying && liveVm.HasTrack,
    "7.1 a fresh view model shows the first snapshot without a refresh");
Check(liveVm.CoverBitmap != null, "7.2 the first snapshot's cover is decoded");

// A second engine consumer drives rounds the way the 1.5 s poll would (the injected provider has
// no timer by design).
var liveDriver = new MediaManagerService(liveProvider);

// 7.3 track change
live = new SharedMediaSnapshot("qqmusic", "Song B", "Artist B", "Album B", "QQ Music", true,
    TimeSpan.Zero, TimeSpan.FromSeconds(180), true, true, true, cover);
await liveDriver.RefreshActiveSessionAsync();
Pump();
Check(liveVm.TrackTitle == "Song B" && liveVm.Artist == "Artist B" && liveVm.Duration == TimeSpan.FromSeconds(180),
    "7.3 a track change reaches the live view model without a refresh");

// 7.4 playback state change (identity dimension)
live = live with { IsPlaying = false };
await liveDriver.RefreshActiveSessionAsync();
Pump();
Check(!liveVm.IsPlaying, "7.4 a pause reaches the live view model without a refresh");

// 7.5 progress only
live = live with { Position = TimeSpan.FromSeconds(42) };
await liveDriver.RefreshActiveSessionAsync();
Pump();
Check(liveVm.Position == TimeSpan.FromSeconds(42), "7.5 a seek reaches the live view model without a refresh");

// 7.6 album art that arrives one round after the track change
live = new SharedMediaSnapshot("qqmusic", "Song C", "Artist C", "Album C", "QQ Music", true,
    TimeSpan.Zero, TimeSpan.FromSeconds(180), true, true, true, null);
await liveDriver.RefreshActiveSessionAsync();
Pump();
Check(liveVm.TrackTitle == "Song C" && liveVm.CoverBitmap == null, "7.6 a cover-less track change clears the previous cover");
live = live with { ThumbnailData = cover };
await liveDriver.RefreshActiveSessionAsync();
Pump();
Check(liveVm.CoverBitmap != null, "7.7 a late-arriving cover is picked up on the next round");

// 7.8 a rebuilt view model (the view disposes on Unloaded and rebuilds on Loaded) must learn the
// current state on its own: the shared engine is still alive here (liveDriver holds it) and will
// not repeat TrackChanged for an unchanged identity.
var rebuiltVm = new MusicViewModel(new MusicModel(), null, liveProvider);
Pump(300);
Check(rebuiltVm.TrackTitle == "Song C" && rebuiltVm.Artist == "Artist C" && rebuiltVm.IsPlaying && rebuiltVm.CoverBitmap != null,
    "7.8 a rebuilt view model adopts the engine's current snapshot");

// 7.9 and it keeps receiving the next changes
live = new SharedMediaSnapshot("qqmusic", "Song D", "Artist D", "Album D", "QQ Music", false,
    TimeSpan.Zero, TimeSpan.FromSeconds(90), true, true, true, cover);
await liveDriver.RefreshActiveSessionAsync();
Pump();
Check(rebuiltVm.TrackTitle == "Song D" && !rebuiltVm.IsPlaying, "7.9 a rebuilt view model keeps receiving later changes");

// 7.10 ...and after it is rebuilt again while the engine stays alive (widget refresh cycle).
rebuiltVm.Dispose();
var rebuiltAgainVm = new MusicViewModel(new MusicModel(), null, liveProvider);
Pump(300);
Check(rebuiltAgainVm.TrackTitle == "Song D" && !rebuiltAgainVm.IsPlaying,
    "7.10 a second rebuild still adopts the current snapshot");

// 7.11 disposing is idempotent and the disposed view model never applies a queued callback
rebuiltAgainVm.Dispose();
rebuiltAgainVm.Dispose();
var titleAfterDispose = rebuiltAgainVm.TrackTitle;
live = live with { Title = "Song E" };
await liveDriver.RefreshActiveSessionAsync();
Pump();
Check(rebuiltAgainVm.TrackTitle == titleAfterDispose, "7.11 a disposed view model ignores later snapshots");

// 7.12 a real cover replacing a placeholder on the same track is an identity change too
var coverBefore = liveVm.CoverBitmap;
var secondCover = Convert.FromBase64String(Png1x1WhiteBase64);
live = live with { ThumbnailData = secondCover };
await liveDriver.RefreshActiveSessionAsync();
Pump();
Check(liveVm.CoverBitmap != null && !ReferenceEquals(coverBefore, liveVm.CoverBitmap),
    "7.12 a replaced cover on the same track reaches the UI");

// 7.13 the host suspension only stops polling; pushed snapshots must still apply, and the poll
// belongs to the engine again after resume.
liveVm.Suspend();
live = live with { Title = "Song F", Artist = "Artist F" };
await liveDriver.RefreshActiveSessionAsync();
Pump();
Check(liveVm.TrackTitle == "Song F" && liveVm.Artist == "Artist F",
    "7.13 a suspended view model still applies pushed snapshots");
liveVm.Resume();
live = live with { Title = "Song G" };
await liveDriver.RefreshActiveSessionAsync();
Pump();
Check(liveVm.TrackTitle == "Song G", "7.14 a resumed view model keeps updating");

liveVm.Dispose();
liveDriver.Dispose();
Pump(150);
Check(liveReads > 0, "7.15 the injected provider was actually read");

// =========================================================================================
Console.WriteLine();
if (failures.Count > 0)
{
    Console.WriteLine($"FAILED {failures.Count} of {checks} checks:");
    foreach (var failure in failures) Console.WriteLine(" - " + failure);
    return 1;
}
Console.WriteLine($"Media widget session checks passed: {checks}/{checks}.");
return 0;

// =========================================================================================

/// <summary>Headless application shell for direct widget construction.</summary>
sealed class ProbeApp : Application
{
    public override void Initialize() { }
}
