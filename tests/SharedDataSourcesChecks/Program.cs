using DeskCanvas.Runtime;
using Music.Models;
using Music.Services;
using Storage.Services;

var failures = new List<string>();
void Check(string n, bool ok) { Console.WriteLine($"[{(ok ? "PASS" : "FAIL")}] {n}"); if (!ok) failures.Add(n); }

// ---------------------------------------------------------------------------
// Storage: shared DriveInfo enumeration
// ---------------------------------------------------------------------------

var fixture = new SharedDriveSnapshot[]
{
    new("C:\\", DriveType.Fixed, 400_000_000_000, 100_000_000_000, "Windows", "NTFS"),
    new("D:\\", DriveType.Fixed, 200_000_000_000, 50_000_000_000, "", ""),
    // Legacy semantics: a non-empty VolumeLabel always wins; an empty one falls back to the
    // localized type label. Keep E unlabeled so the Removable fallback path is exercised.
    new("E:\\", DriveType.Removable, 64_000_000_000, 10_000_000_000, "", "FAT32"),
    new("F:\\", DriveType.Network, 1_000_000_000_000, 250_000_000_000, "NAS", "NFS"),
    new("G:\\", DriveType.CDRom, 8_000_000_000, 0, "DVD", "CDFS"),
};

var storageFetches = 0;
var storageGate = new TaskCompletionSource<IReadOnlyList<SharedDriveSnapshot>>(TaskCreationOptions.RunContinuationsAsynchronously);
var storageShared = new StorageDrivesSharedProvider((_, _) => { storageFetches++; return storageGate.Task; });
var viewLease = storageShared.AcquireAsync("storage-view");
var settingsLease = storageShared.AcquireAsync("storage-settings");
storageGate.SetResult(fixture);
var a = await viewLease;
var b = await settingsLease;
Check("view and settings single-flight one drive enumeration", storageFetches == 1 && storageShared.Statistics.Coalesced == 1 && a.Snapshot.Value?.Count == 5);
Check("storage consumers hold independent owner leases", storageShared.Statistics.ActiveOwners == 2);
a.Dispose();
Check("releasing one storage consumer keeps the other active", storageShared.Statistics.ActiveOwners == 1);
b.Dispose();
Check("all storage owners release", storageShared.Statistics.ActiveOwners == 0);

var storageTtlReads = 0;
var storageTtl = new StorageDrivesSharedProvider((_, _) => { storageTtlReads++; return Task.FromResult<IReadOnlyList<SharedDriveSnapshot>>(fixture); }, TimeSpan.FromMilliseconds(60));
using (await storageTtl.AcquireAsync("first")) Check("storage TTL reuses fresh snapshot", storageTtlReads == 1);
await Task.Delay(100);
using (await storageTtl.AcquireAsync("second")) Check("storage TTL expiry re-enumerates drives", storageTtlReads == 2);

var storageSlowGate = new TaskCompletionSource<IReadOnlyList<SharedDriveSnapshot>>(TaskCreationOptions.RunContinuationsAsynchronously);
var storageSlowReads = 0;
var storageCancellable = new StorageDrivesSharedProvider((_, _) => { storageSlowReads++; return storageSlowGate.Task; });
using var storageCts = new CancellationTokenSource();
var cancelledStorage = storageCancellable.AcquireAsync("cancelled-view", storageCts.Token);
var survivingStorage = storageCancellable.AcquireAsync("surviving-settings");
storageCts.Cancel();
try { await cancelledStorage; Check("cancelled storage owner throws", false); }
catch (OperationCanceledException) { Check("cancelling a storage view releases only its wait", storageCancellable.Statistics.ActiveOwners == 1); }
storageSlowGate.SetResult(fixture);
using (var survivor = await survivingStorage)
    Check("settings page completes from the original enumeration", storageSlowReads == 1 && survivor.Snapshot.Value?.Count == 5);
Check("cancelled and surviving storage owners both release", storageCancellable.Statistics.ActiveOwners == 0);

// ---------------------------------------------------------------------------
// StorageService facade: projection equivalence over the shared snapshot
// ---------------------------------------------------------------------------

var facadeReads = 0;
var facadeProvider = new StorageDrivesSharedProvider((_, _) => { facadeReads++; return Task.FromResult<IReadOnlyList<SharedDriveSnapshot>>(fixture); }, TimeSpan.FromMinutes(1));
StorageService.DriveSource = facadeProvider;

var isZh = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.Equals("zh", StringComparison.OrdinalIgnoreCase);
var all = StorageService.GetAllCandidateDrives();
// Legacy DriveLetter is the root path with only the trailing slash trimmed, e.g. "C:".
Check("facade enumerates every includable drive and excludes others", all.Count == 4 && !all.Any(d => d.DriveType == DriveType.CDRom));
Check("facade sorts drives by letter", all.Select(d => d.DriveLetter).SequenceEqual(new[] { "C:", "D:", "E:", "F:" }));

var sys = all.Single(d => d.RootPath == "C:\\");
Check("system drive projection equivalence", sys.IsSystemDrive && sys.DriveLetter == "C:" && sys.ShortTitle == "C:"
    && sys.VolumeLabel == "Windows" && sys.DisplayTitle == "C: Windows" && sys.DriveFormat == "NTFS");
Check("usage math equivalence (400/100 GB)", sys.TotalBytes == 400_000_000_000 && sys.FreeBytes == 100_000_000_000
    && sys.UsedBytes == 300_000_000_000 && Math.Abs(sys.UsageFraction - 0.75) < 1e-9 && sys.UsagePercent == 75);

var unlabeled = all.Single(d => d.RootPath == "D:\\");
Check("fallback label and NTFS default preserved", unlabeled.VolumeLabel == (isZh ? "本地磁盘" : "Local Disk") && unlabeled.DriveFormat == "NTFS" && !unlabeled.IsSystemDrive);
var removable = all.Single(d => d.DriveType == DriveType.Removable);
Check("removable fallback label preserved", removable.VolumeLabel == (isZh ? "可移动磁盘" : "Removable Disk"));

var filtered = StorageService.GetDrives(includeRemovable: false, includeNetwork: false, hiddenDrives: ["c:\\"]);
Check("type and hidden-drive filtering equivalence", filtered.Count == 1 && filtered[0].DriveLetter == "D:");
var withNetwork = StorageService.GetDrives(includeRemovable: false, includeNetwork: true);
Check("network inclusion filter equivalence", withNetwork.Count == 3 && withNetwork.All(d => d.DriveType is DriveType.Fixed or DriveType.Network));

var requestsBefore = facadeProvider.Statistics.Requests;
_ = StorageService.GetDrives();
_ = StorageService.GetAllCandidateDrives();
Check("facade calls share one snapshot round", facadeReads == 1 && facadeProvider.Statistics.Requests == requestsBefore + 2 && facadeProvider.Statistics.CacheHits >= 2);

var failing = new StorageDrivesSharedProvider((_, _) => Task.FromException<IReadOnlyList<SharedDriveSnapshot>>(new IOException("offline")), TimeSpan.FromMinutes(1));
StorageService.DriveSource = failing;
var empty = StorageService.GetAllCandidateDrives();
Check("facade returns empty list when the shared fetch fails", empty.Count == 0 && failing.Statistics.Failures == 1);
StorageService.DriveSource = facadeProvider;

// ---------------------------------------------------------------------------
// MediaSession: two consumers share one SMTC round
// ---------------------------------------------------------------------------

var playing = new SharedMediaSnapshot("cloudmusic", "Song", "Artist", "Album", "NetEase", true,
    TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(200), true, true, true, new byte[] { 1, 2, 3 });

var directReads = 0;
var directGate = new TaskCompletionSource<SharedMediaSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
var directProvider = new MediaSessionSharedProvider((_, _) => { directReads++; return directGate.Task; });
var musicWait = directProvider.AcquireAsync("music-view");
var settingsWait = directProvider.AcquireAsync("music-settings");
directGate.SetResult(playing);
using (var m1 = await musicWait)
using (var m2 = await settingsWait)
    Check("media provider coalesces view and settings into one SMTC round", directReads == 1 && directProvider.Statistics.Coalesced == 1 && m1.Snapshot.Value?.Title == "Song");

// Full facade wiring: Music view + Music settings + a late joiner over one provider.
var smtcReads = 0;
SharedMediaSnapshot smtcReply = playing;
TaskCompletionSource<SharedMediaSnapshot>? pendingGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
var mediaProvider = new MediaSessionSharedProvider((_, _) =>
{
    smtcReads++;
    var gate = pendingGate;
    return gate != null ? gate.Task : Task.FromResult(smtcReply);
}, TimeSpan.FromMilliseconds(120));

var trackChangedView = 0; var timelineChangedView = 0;
var trackChangedSettings = 0; var timelineChangedSettings = 0;
MediaTrackInfo? viewTrack = null; MediaTrackInfo? settingsTrack = null;

var view = new MediaManagerService(mediaProvider);
view.TrackChanged += t => { trackChangedView++; viewTrack = t; };
view.TimelineChanged += (_, _) => timelineChangedView++;
await Task.Delay(60); // engine init refresh is now parked on the first-round gate

var settings = new MediaManagerService(mediaProvider);
settings.TrackChanged += t => { trackChangedSettings++; settingsTrack = t; };
settings.TimelineChanged += (_, _) => timelineChangedSettings++;

// Both consumers refresh while the first SMTC round is still in flight.
var firstRound = pendingGate;
var refreshView = view.RefreshActiveSessionAsync();
var refreshSettings = settings.RefreshActiveSessionAsync();
pendingGate = null; // later rounds resolve immediately
firstRound.SetResult(playing);
await Task.WhenAll(refreshView, refreshSettings);
await Task.Delay(40);

Check("view consumer receives the shared snapshot", trackChangedView == 1 && viewTrack?.Title == "Song" && viewTrack?.IsPlaying == true && viewTrack?.ThumbnailData is not null);
Check("settings consumer receives the shared snapshot", trackChangedSettings == 1 && settingsTrack?.Title == "Song" && settingsTrack?.PlayerName == "NetEase");
Check("each consumer projects its own mutable track model", viewTrack != null && settingsTrack != null && !ReferenceEquals(viewTrack, settingsTrack));

var readsBeforeLateJoin = smtcReads;
var late = new MediaManagerService(mediaProvider);
Check("late consumer pulls current track without a new SMTC round", smtcReads == readsBeforeLateJoin && late.CurrentTrack.Title == "Song" && late.CurrentTrack.IsPlaying);

// Same identity, new position: timeline-only fast path, no TrackChanged storm.
await Task.Delay(180);
smtcReply = playing with { Position = TimeSpan.FromSeconds(15) };
await view.RefreshActiveSessionAsync();
await Task.Delay(40);
await settings.RefreshActiveSessionAsync();
await Task.Delay(40);
Check("same identity refresh stays timeline-only and shares the fresh round",
    smtcReads == 2 && trackChangedView == 1 && trackChangedSettings == 1 && timelineChangedView >= 1 && timelineChangedSettings >= 1);
Check("both consumers advanced to the same timeline", view.CurrentTrack.Position == TimeSpan.FromSeconds(15) && settings.CurrentTrack.Position == TimeSpan.FromSeconds(15));

// Playback status change is identity: TrackChanged fires on both consumers from one round.
await Task.Delay(180);
smtcReply = playing with { IsPlaying = false };
await view.RefreshActiveSessionAsync();
await Task.Delay(40);
Check("play state change raises TrackChanged on both consumers from one round",
    smtcReads == 3 && trackChangedView == 2 && trackChangedSettings == 2 && !view.CurrentTrack.IsPlaying && !settings.CurrentTrack.IsPlaying);

// Empty session: the legacy reset-and-notify behavior is preserved.
await Task.Delay(180);
smtcReply = SharedMediaSnapshot.Empty;
await view.RefreshActiveSessionAsync();
await Task.Delay(40);
Check("no-session round resets both consumers", trackChangedView == 3 && trackChangedSettings == 3 && view.CurrentTrack.HasTrack == false && settings.CurrentTrack.HasTrack == false);

// Dispose isolation: tearing the view down must not stop the settings consumer's rounds.
view.Dispose();
await Task.Delay(180);
smtcReply = playing;
await settings.RefreshActiveSessionAsync();
await Task.Delay(40);
Check("disposed view stops receiving events", trackChangedView == 3);
Check("settings consumer keeps polling after view disposal", trackChangedSettings == 4 && settings.CurrentTrack.Title == "Song" && smtcReads == 5);

settings.Dispose();
late.Dispose();
await Task.Delay(200);
Check("engine shutdown leaves no background rounds", smtcReads == 5);

// After the last consumer leaves, a new facade rebuilds the engine (fresh init round).
var reborn = new MediaManagerService(mediaProvider);
await Task.Delay(60);
Check("re-acquiring after full release re-initializes the engine", smtcReads == 6 && reborn.CurrentTrack.Title == "Song");
reborn.Dispose();

Console.WriteLine(failures.Count == 0 ? "SharedDataSourcesChecks ALL PASS" : $"SharedDataSourcesChecks FAILED: {string.Join(", ", failures)}");
return failures.Count == 0 ? 0 : 1;
