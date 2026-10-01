# Memory / cache-bloat audit — uWidgets widget assemblies

Scope audited (as assigned): `src/Widgets/Picture`, `Music`, `Monitor`, `Tools`, `Weather`, `Notes`,
`Calendar`, `Progress`, `Fixed`, `Search`. All claims below were read from source; file:line cited.
No code was changed.

**Live-config caveat.** The desktop under investigation runs: Notes, Tools (Translator + Clipboard),
Folders, Calendar (Month), Weather (Forecast), Monitor (MultiDashboard), Progress, Music,
Clock (FramelessDigital). **Picture is not enabled**, so the GIF findings (#7/#8) cannot explain the
current ~700 MB. **Folders and Clock were outside my assigned scope** and are only flagged as
unverified leads at the end — they are in the live set and deserve their own pass.

Host behaviour that multiplies every leak below:
* `src/uWidgets/Views/Widget.axaml.cs:787-790` — on any settings change the host calls
  `Refresh()` only for `IWidgetSelfRefreshing` views; **all other views are re-created**
  (`ContentPresenter.Content = userControl()`). Weather (Forecast/AirQuality/Pressure/Temperature/
  SunriseSunset), Monitor (SingleMetric, MultiDashboard) and Calendar (Month, Date) are **not**
  `IWidgetSelfRefreshing` (grep of `: UserControl, IWidgetSelfRefreshing` → only Folders/Music/Notes/
  Fixed/Picture/Reminders/Clock/Progress/Search/Tools views). So **one widget instance is leaked per
  settings save** for every "never disposed" item below.
* `src/uWidgets/Views/Pages/Gallery.axaml.cs:60-70` builds one **real** widget control per
  `WidgetInfo` attribute (`widgetFactory.CreateControl(...)`), and `Gallery.axaml:45` puts it in a
  `ContentPresenter`. Visiting the Gallery page instantiates Music, Weather, Monitor/SingleMetric,
  Calendar/Month … → the same leaks, once per visit.
* `src/uWidgets/Services/UpdateTimer.cs:12,28-46` — the static timer subscribers are a plain
  `List<Action>` (strong refs). Unsubscribing is the *only* way to release; forgetting it leaks the
  subscriber **and** keeps its work running forever (`TimerService.cs:9-24`).

---

## 1. Ranked findings (biggest first)

### #1 (dominant) — Clipboard history keeps one **full-resolution** `Bitmap` per image item, never disposed
* `Tools/Services/ClipboardMonitorService.cs:131` — `item.Thumbnail = new Bitmap(filePath);`
  `filePath` is the PNG written at the **original** clipboard image size
  (`:110-127`, encoded from the raw DIB at `:107-118`). `ClipboardItem.Thumbnail` is
  `Avalonia.Media.Imaging.Bitmap` (`Tools/Models/ClipboardItem.cs:20-21`).
* History is bounded in **count**, not in bytes: `MaxHistoryCount = 20` (`Tools/Models/ClipboardModel.cs:4,10`,
  clamped 5–100).
* Arithmetic (BGRA8 = 4 B/px, decode is uncompressed):
  * 1920×1080 → 1920·1080·4 = **8,294,400 B (7.9 MiB)** × 20 = **158 MiB**
  * 2560×1440 → 14,745,600 B (14.1 MiB) × 20 = **281 MiB**
  * 3840×2160 → 33,177,600 B (31.6 MiB) × 20 = **633 MiB**
* **Nothing ever disposes them**: `TrimHistory` (`:252-263`), `RemoveItem` (`:177-188`) and
  `ClearAll` (`:190-207`) delete the PNG file but never call `item.Thumbnail?.Dispose()`. `LoadHistory`
  (`:265-293`, decode at `:282`) re-creates them all at startup.
  Unmanaged pixel blocks create **no GC pressure**, so reclamation waits for a finalizer/GC that the
  managed heap has no reason to trigger → the bytes stay resident.
* Growth: **bounded by 20 items but effectively unbounded in bytes**; this alone can exceed the
  200 MB target with 12–13 ordinary 1080p screenshots, and explains ~700 MB at 4K/2-monitor captures.
* Minimal fix (3 lines + 1 decode change):
  1. `item.Thumbnail?.Dispose()` before removal in `RemoveItem`, `TrimHistory` and `ClearAll`;
     also dispose the outgoing item when `History.Clear()` runs.
  2. Replace `new Bitmap(filePath)` (24/131) with a real thumbnail:
     `Bitmap.DecodeToWidth(File.OpenRead(filePath), 320, BitmapInterpolationMode.LowQuality)` — the card
     is ~100 px tall (`Tools/Views/ClipboardView.axaml`), so 320 px wide ≈ 320·240·4 = **307 KB** per item
     instead of 8–33 MB (≈ **97 % reduction**).
  3. Persist a small `thumbnail_{id}.png` (or reuse the source PNG scaled) so history reload stays cheap.

### #2 — Per-capture Large Object Heap spikes in the DIB read path (2 × full-size copies)
* `Tools/Services/ClipboardNative.cs:179` `size = (int)GlobalSize(hMem)` (the whole DIB),
  `:205` `totalFileSize = 14 + size`,
  `:208` `using var ms = new MemoryStream(totalFileSize)` → backing array of `totalFileSize`,
  `:219` `byte[] dibBytes = new byte[size]` → **second full-size copy**, `:220` `Marshal.Copy`,
  `:224` `SKBitmap.Decode(ms)` → third (native) full-size buffer.
* Arithmetic: 3840×2160 DIB = 33,177,600 B → **2 × 33.2 MB managed LOH allocations per capture**
  (+33.2 MB native), i.e. **66 MB of LOH garbage per screenshot**; 1920×1080 → 2 × 8.29 MB.
* Anything ≥ 85 KB goes to the LOH, which is never compacted by default (only swept on a gen2 GC) →
  this is the classic "private bytes grow, managed heap looks fine" signature; it repeats on every
  clipboard image copy for the whole session.
* Also `ClipboardMonitorService.cs:113-118`: `SKImage.FromBitmap` + `Encode(Png, 95)` allocate an
  `SKData` holding the entire compressed PNG (typically 1–10 MB for a 4K screenshot) per capture.
  (`95` is ignored for PNG — wasted CPU only.)
* Minimal fix: drop the `dibBytes` copy — `ms.SetLength(totalFileSize); ms.Position = 14;
  Marshal.Copy(ptr, ms.GetBuffer()!, 14, size);` (one copy instead of two), and reuse a cached
  `byte[]`/`MemoryStream` field instead of allocating per capture. Better: copy the DIB pixel rows
  straight into an `SKBitmap` and skip the BMP wrapper + `SKBitmap.Decode` entirely (−33 MB native).

### #3 — Music: `MediaManagerService` is never disposed; `MusicViewModel.Dispose()` is never called; its 250 ms `DispatcherTimer` keeps ticking forever
* `Music/Views/Music.axaml.cs:31-39` — the view only hooks `Loaded`/`SizeChanged`; **there is no
  `Unloaded` handler and no `viewModel.Dispose()`** anywhere in the Music widget
  (`grep Dispose|Unloaded src/Widgets/Music` → only the definitions at `MusicViewModel.cs:347` and
  `MediaManagerService.cs:445`).
* `MusicViewModel.cs:159-164` — `timer = new DispatcherTimer { Interval = 250 ms }; timer.Tick += OnTimerTick; timer.Start();`
  in the constructor. A running `DispatcherTimer` is rooted by the dispatcher, and its `Tick`
  delegate roots the view model → **the whole VM is unreachable-but-alive and fires 4×/s forever**.
* `MusicViewModel.cs:347-353` `Dispose()` stops the timer and unsubscribes its own two events but
  **does not call `mediaService.Dispose()`** and sets `CoverBitmap = null` **without disposing the bitmap**
  (`:352`).
* `MediaManagerService.cs:40-56` — `GlobalSystemMediaTransportControlsSessionManager.RequestAsync()`,
  subscribes `SessionsChanged` + `CurrentSessionChanged` (`:43-44`) and starts
  `pollTimer = new Timer(...)` at **1500 ms forever** (`:48-56`); `:175-177` subscribes three SMTC
  session events; only `Dispose()` (`:445-458`) tears those down.
* Effect: every Music widget instance **and every Gallery visit** (`Gallery.axaml.cs:63`) permanently adds
  one SMTC client + 5 event subscriptions + an async 1.5 s poll loop
  (`MediaManagerService.cs:75-143` → `GetSessions()`:81, `GetPlaybackInfo()` per rule per session:88-114,
  `TryGetMediaPropertiesAsync()`:214) that keeps invoking `TrackChanged` into the leaked VM
  (`MusicViewModel.cs:193-229`, which also `Dispatcher.UIThread.Post`s on every track event).
* Cover art, per track change: `MediaManagerService.cs:263-267` copies the whole thumbnail into a fresh
  `byte[]` (typically 60–600 KB → **LOH over 85 KB**), and `MusicViewModel.cs:217-218`
  `new Bitmap(ms)` decodes it at **full** size: 500×500·4 = **1.0 MB**, 1500×1500·4 = **9 MB**,
  3000×3000·4 = **36 MB**; the previous bitmap is replaced (`:218`) and the final one nulled (`:352`)
  without `Dispose()` → finalizer-dependent retention.
* Growth: **unbounded** (one leaked service + timer per instance).
* Minimal fix: (a) `Music.axaml.cs` — `Unloaded += (_, _) => viewModel.Dispose();`;
  (b) `MusicViewModel.Dispose` — `mediaService.Dispose(); coverBitmap?.Dispose(); coverBitmap = null;`
  and dispose the old cover in `OnTrackChanged` before assigning;
  (c) decode a capped cover (`Bitmap.DecodeToWidth(ms, 256, BitmapInterpolationMode.LowQuality)`) and
  store the thumbnail `byte[]` only when the encoded size is small.

### #4 — Weather: `ForecastViewModel`/`AirQualityViewModel` subscribe to the static 1-hour timer and are never disposed (each also owns an `HttpClient`)
* `Weather/ViewModels/ForecastViewModel.cs:20` `TimerService.Timer1Hour.Subscribe(UpdateForecast);`
  and `:21` an immediate `UpdateForecast()` (network call in the constructor).
  `Dispose()` exists (`:185-189`) but **no caller**: `Weather/Views/Forecast.axaml.cs:24-28`
  `OnUnloaded` only detaches the view's own handlers; `AirQuality.axaml.cs:13`,
  `Pressure.axaml.cs:15`, `Temperature.axaml.cs:13`, `SunriseSunset.axaml.cs:15` create
  `new ForecastViewModel(model)` / `new AirQualityViewModel(model)` and nothing disposes them
  (grep `Dispose` in `src/Widgets/Weather` → definitions only).
* Because the static list holds a strong `Action` (`UpdateTimer.cs:12,32`), the VM — plus its 24
  `HourlyForecastViewModel` + 7 `DailyForecastViewModel` + metric objects — stays reachable forever and
  re-fetches hourly: network I/O, `ReadAsStringAsync` string, `JsonSerializer.Deserialize`
  (`OpenMeteoWeatherProvider.cs:32-39`) → **unbounded, one per view instance** (and one per Gallery
  visit / per settings save, since `Forecast` is not `IWidgetSelfRefreshing`).
* `OpenMeteoWeatherProvider.cs:14` creates its **own** `HttpClient` per provider
  (`ProxySettings.CreateHttpClient()`); `ForecastViewModel.cs:18` creates a provider per VM and
  `ForecastSettings.axaml.cs:16` a provider per settings view — none is disposed → leaked
  `SocketsHttpHandler` + connection pool + sockets per instance.
* Size per leak: forecast VMs ≈ 10–40 KB managed; the `HttpClient`/handler and pooled socket buffers
  (≈ 32 KB/connection) are the larger part. Not a single big block, but **purely additive**.
* Minimal fix: call `viewModel.Dispose()` from every Weather view's `OnUnloaded` (`Forecast.axaml.cs:24`
  is the existing hook; add handlers to AirQuality/Pressure/Temperature/UVIndex), and inside
  `ForecastViewModel.Dispose`/`AirQualityViewModel.Dispose` dispose the provider (make
  `OpenMeteoWeatherProvider` `IDisposable` and dispose its `HttpClient`).
* Note: weather payloads are **not** an LOH problem — the 7-day hourly JSON is ~6–20 KB, well under 85 KB.

### #5 — Calendar: static theme event never unsubscribed → every Month view is rooted forever
* `Calendar/Views/Month.axaml.cs:30-31`
  `Application.Current.ActualThemeVariantChanged += OnActualThemeVariantChanged;`
  `OnUnloaded` (`:34-37`) calls `viewModel.Stop()` only — **there is no `-=`** (`grep` for
  `Application.Current.*+=` in `src/Widgets` shows only `Month.axaml.cs:31` and the correctly
  unsubscribed `Notes/Views/Note.axaml.cs:63`).
* `Application.Current` lives for the whole process → each Month control (widget **and** each Gallery
  preview **and** each settings-save re-creation, since Month is not `IWidgetSelfRefreshing`) is
  permanently rooted together with its `DataContext` (`MonthCalendarViewModel` holds `Days` (up to 42
  `DayViewModel`), `WeekHeaders`, `CurrentWeek`) and its handler runs on every theme flip.
* Growth: **unbounded** (one view + VM + DataContext per instance).
* Minimal fix: in `OnUnloaded` add
  `if (Application.Current != null) Application.Current.ActualThemeVariantChanged -= OnActualThemeVariantChanged;`
  and also detach `SizeChanged` (mirror `Note.axaml.cs:460-472`).

### #6 — Monitor: leaked 1 Hz static subscription that runs WMI forever; WMI COM results never disposed
* `Monitor/ViewModels/SingleMetricViewModel.cs:8` — the class is **not** `IDisposable` and `:16`
  `TimerService.Timer1Second.Subscribe(Update);` has **no unsubscribe anywhere**;
  `Monitor/Views/SingleMetric.axaml.cs:22-26` `OnUnloaded` detaches only `SizeChanged`/`Unloaded`.
  → every `SingleMetric` instance (each Gallery visit — `Monitor/AssemblyInfo.cs:11` registers it —
  and each settings save) leaks the VM **and keeps issuing one WMI query per second forever**.
  `MultiDashboardViewModel.cs:26,50-54` does this correctly (field-cached delegate + unsubscribe).
* WMI results are never disposed anywhere in `Monitor/Services/MetricService.cs`:
  `searcher.Get()` returns a `ManagementObjectCollection` (and each element is a `ManagementObject`),
  both `IDisposable`, but only the `searcher` is in a `using`
  (`:47, :61, :81, :99, :109, :134`). Every call therefore leaves an `IEnumWbemClassObject` COM
  enumerator + RCWs to the finalizer queue → COM/RCW backlog and fragmentation that shows up as
  growing private bytes.
* Live config runs MultiDashboard → `MultiDashboardViewModel.cs:26` (1 Hz) and `UpdateAsync:39-48`
  issues one query per metric per second; `GetNetworkUsage` issues **two** (`MetricService.cs:99` and
  `:109`) → ~5 WMI queries/s ≈ **432,000 queries/day**, each on a fresh `Task.Run`
  (`MetricService.cs:29-33`) and each leaving undisposed COM objects.
* Also per tick: `MultiDashboardViewModel.cs:46` allocates a new `MetricViewModel` record per item
  (4/s) plus a `MetricService.GetMetricIcon` lookup — bounded garbage (icons are `Lazy<StreamGeometry>`
  statics, `MetricIcon.cs:7-20`, one geometry per type: fine).
* Minimal fix (low risk, mechanical):
  * `using var results = searcher.Get(); foreach (ManagementBaseObject o in results) { using var obj = (ManagementObject)o; … }`
  * load the property **inside** the `using`, then `return` after the loop (current early `return`
    inside `foreach` skips disposal of the remaining objects).
  * make `SingleMetricViewModel` implement `IDisposable` (unsubscribe `Update`) and call it from
    `SingleMetric.axaml.cs:22`'s `OnUnloaded`.
  * Best structural win: replace CPU/RAM/disk WMI with `GetSystemTimes`/`GlobalMemoryStatusEx`/
    `GetDiskFreeSpaceEx` P/Invoke (already used elsewhere in this repo for other Win32 calls) and
    sample **once per second for all subscribers** instead of per widget.

### #7 — Picture: every frame of an animated GIF is retained; one animation can hold ~64 MiB
*(Not in the current live config — ranked by size, not by current impact.)*
* `Picture/Services/PictureImageLoader.cs:59` `AnimatedPixelBudget = 16.0 * 1024 * 1024` (16 MP);
  `DimensionForFrameBudget` (`:277-291`) shrinks the canvas so that **frames × W × H ≤ 16 MP**, and
  `DecodeAnimatedFrames` (`:148-270`) keeps **one `WriteableBitmap` per frame**
  (`ConvertSkBitmapToWriteable` at `:245-250`, each `new WriteableBitmap` at `:355-359` with its own
  BGRA buffer).
* Arithmetic: 16,777,216 px × 4 B = **67,108,864 B = 64 MiB for a single animated image**.
  `PictureViewModel.cs:32` `MaxCacheCount = 2` → **up to 128 MiB** resident from the picture widget
  alone. Worked examples: 480×480 / 300 frames → per-frame budget 55,924 px → 236×236 → 236·236·300 =
  16.7 Mpx = 66.8 MB; 1920×1080 / 60 frames → 705×396 → 16.75 Mpx = 67.0 MB.
* Transient extra during decode: `retained[]` private copies (`:228-230`) of `decodeW×decodeH`
  (up to 2048×2048×4 = 16 MB each, several alive at once for dependency chains) + the scratch buffer
  (`:414-418`) — the `finally` at `:264-267` releases them, so this is a spike, not a leak.
* Growth: **bounded** (2 pictures) — but it is the largest per-widget block in the app and two GIFs
  alone exceed the whole 200 MB budget.
* Minimal fix: cut `AnimatedPixelBudget` to ~4 MP (16 MB) and/or use `MaxCacheCount = 1` while the
  current picture is animated (`PictureViewModel.cs:32, 189-197`); optionally keep every Nth frame
  with `DurationMs × N` instead of shrinking further.

### #8 — Picture: `disposed` HashSet retains one wrapper graph per picture ever disposed
* `PictureViewModel.cs:45` `private readonly HashSet<DecodedPicture> disposed` is only ever `Add`ed
  (`:249` — `if (!disposed.Add(picture)) return;`) and is **never cleared**, including in `Dispose()`
  (`:535-566`). Each entry keeps the `DecodedPicture` + its `List<PictureFrame>` + one `Bitmap`
  wrapper per frame alive (small managed objects; the native pixels are freed by `Dispose`).
  For a 300-frame GIF ≈ 300 × (32 B `PictureFrame` + 8 B list slot) + the `Bitmap` wrappers ≈ **≈ 20 KB
  retained per disposed animation, growing without bound** in a long-running slideshow.
* Related: `Picture/Views/PictureView.axaml.cs:41` `Unloaded += (_, _) => viewModel.Dispose();` — the
  lambda is never detached, and `PictureViewModel.ApplyModel:76` resets `isDisposed = false`, so a
  reloaded widget comes back with the same already-cleared cache; harmless but fragile.
* Minimal fix: drop `disposed` entirely (a disposed picture is never re-added to `pictureCache`; the
  `IsInUse`/`pendingDispose` checks already prevent double disposal) or `disposed.Clear()` when
  `pendingDispose` empties.

### #9 — Progress: view model never disposed → leaks the app-settings event subscription
* `Progress/Views/ProgressView.axaml.cs:30-31` wires `Loaded → Start` / `Unloaded → Stop`;
  `ProgressViewModel.Dispose()` (`:61-69`) is what removes `appSettingsProvider.DataChanged`
  (subscribed at `:26`) and it is never called (grep: no `viewModel.Dispose()` in the Progress widget).
  The app-settings provider is process-lifetime, so the VM (and its brush/`DotTooltipFunc` closures)
  stays rooted per instance.
* Steady garbage (bounded, not a leak): every 5 s tick (`TimerService.Timer5Seconds`) allocates
  3 fresh `SolidColorBrush` (`ProgressViewModel.cs:200, 207, 214`), a `RowDefinitions`, and a new
  `DotTooltipFunc` closure (`:265-271`).
* `Progress/Controls/DotMatrixControl.cs:181` allocates a `Pen` per render and `:171` a
  `SolidColorBrush` per render when none is bound; `:210-231` issues up to `TotalDots` (365) draw calls
  per render. Worse, `:263-264` calls `ToolTip.SetTip(this, text)` **and** `ToolTip.SetIsOpen(this, true)`
  on **every pointer-move event** — Avalonia creates/holds a tooltip per control and each move produces
  a popup open/close cycle, so sweeping the mouse over a 365-dot grid generates hundreds of popup
  open/close cycles. Minimal fix: cache `lastTooltipIndex` and only call `SetTip`/`SetIsOpen` when the
  hovered dot changes.
* Minimal fix for the leak: call `viewModel.Dispose()` in a real `Unloaded` handler
  (`ProgressView.axaml.cs:30-31`), and/or have `Stop()` also unsubscribe `DataChanged`.

### #10 — Fixed (aggregate): weather service/`HttpClient` never disposed + per-second `CultureInfo` allocation
* `Fixed/ViewModels/AggregateViewModel.cs:15` `private readonly OpenMeteoWeatherService weatherService = new();`
  and `OpenMeteoWeatherService.cs:25` `private readonly HttpClient httpClient = ProxySettings.CreateHttpClient();`
  — the class is not `IDisposable`; `AggregateViewModel.Dispose` (`:243-247`) unsubscribes the timers only
  (`AggregateView.axaml.cs:46-50` does call it) → one leaked `SocketsHttpHandler` + connection pool per
  Fixed widget instance.
* `AggregateViewModel.cs:169` `now.ToString("dddd", new CultureInfo("zh-CN"))` inside the **1-second tick**
  → a fresh `CultureInfo` + `DateTimeFormatInfo` allocated every second (≈ 86,400/day), plus
  `UpdateCalendar()` rebuilding a 35–42 element `DayCellViewModel` list every 2nd tick is hourly-only
  (`:146-150`) so it is fine. Fix: `private static readonly CultureInfo Zh = CultureInfo.GetCultureInfo("zh-CN");`
  (or drop the argument; `GetCultureInfo` returns a cached instance).
* `AggregateViewModel.cs:222` `public async void UpdateWeather()` — `async void`; exceptions after the
  first `await` go to the synchronisation context (crash/log noise) and the caller cannot await it.
  Prefer `Task` + explicit fire-and-forget.
* `OpenMeteoWeatherService.GetWeatherIcon` (`:110-138`) parses a `StreamGeometry` per call (hourly) —
  bounded, but it is trivially cacheable into a static array.

### #11 — Tools / Translator: no translation cache exists; only small per-request garbage
* There is **no translator memoization cache** in the codebase (searched `Dictionary|Cache|MemoryCache`
  in `src/Widgets/Tools`) — repeated identical auto-translations re-hit the network
  (`TranslatorView.axaml.cs:459-463` debounce at `:73-77`), which is a CPU/network issue, not memory.
* The engine `HttpClient`s are `static readonly` (`YoudaoTranslationEngine.cs:16`,
  `MyMemoryTranslationEngine.cs:13`, `BaiduTranslationEngine.cs:19`, `DeepLXTranslationEngine.cs:16`)
  and `TranslationService.Instance` is a process singleton (`TranslationService.cs:11-17`) → **bounded,
  not a leak**.
* `TranslatorView.axaml.cs:502-503` `cts?.Cancel(); cts = new CancellationTokenSource();` — the previous
  CTS is never `Dispose()`d (2 per debounced attempt). Small, bounded garbage; fix:
  `cts?.Cancel(); cts?.Dispose();`.
* `HttpContentExtensions.ReadAsStringSafeAsync` (`:25-56`) double-copies every response
  (`ReadAsByteArrayAsync` → `byte[]`, then `GetString` → `string`); only relevant for responses >85 KB
  (translation responses are not). `TranslatorView.axaml.cs:115-120` correctly stops both timers and
  cancels the CTS on `Unloaded`.
* `Tools/Services/ClipboardMonitorService.cs:51-56` — the 400 ms singleton poll timer runs for the whole
  process lifetime and is **never stopped** even when no Clipboard widget exists; it is cheap
  (`GetClipboardSequenceNumber` only, `:65-74`) but it keeps capturing into `History` (and therefore
  keeping #1 alive) with no widget on screen.

### #12 — Notes: no leak found (reference implementation for teardown)
* `Notes/Views/Note.axaml.cs:57-68` subscribes `appSettingsProvider.DataChanged` (:58), the static
  `Application.Current.ActualThemeVariantChanged` (:63) and a tunnel `AddHandler` (:68);
  `OnUnloaded` (`:460-472`) removes **all** of them, stops+detaches the reload timer, and disposes the
  `FileSystemWatcher`. `Rebuild()` (`:94-125`) disposes the previous watcher before creating a new one
  (`:383-404`) — no watcher/thread leak.
* Minor, bounded: `Note.axaml.cs:373-376` serializes the whole model (`JsonSerializer.SerializeToElement`)
  and rewrites the layout file once per edit commit (LostFocus) — an LOH string only for notes >85 KB.
  `MarkdownRenderer.cs` keeps only static `MarkdownPipeline`/`Regex`/`FontFamily` (`:32-49`) — bounded,
  and each render builds a fresh control tree that is collected normally.

### #13 — Search: no leak found
* `Search/Services/SearchScrollHelper.cs:16-33` adds an anonymous handler **to the scroller itself**
  (self-referential, no static root). `SearchMenuHelper.cs:29-52` parses `Geometry` per menu open
  (bounded per open, ≤ 12 engines). `SearchViewModel.cs:164` caps recent history at 10 items.
  `SearchIconProvider.cs:20` is a static string dictionary (bounded). `SearchView` implements
  `IWidgetSelfRefreshing`, so it is refreshed in place rather than re-created.

---

## 2. Event-subscription and timer leak register

| # | Subscriber | Static / long-lived target | Unsubscribed? | file:line |
|---|---|---|---|---|
| L1 | `MusicViewModel` | own `DispatcherTimer` (250 ms, started in ctor) | **No** (Dispose never called) | `Music/ViewModels/MusicViewModel.cs:159-164`, `347-353`; `Music/Views/Music.axaml.cs:31-39` |
| L2 | `MediaManagerService` | SMTC manager events + SMTC session events + `System.Threading.Timer` 1.5 s | only in `Dispose()`, which no one calls | `Music/Services/MediaManagerService.cs:43-44,48-56,175-177,445-458` |
| L3 | `ForecastViewModel`, `AirQualityViewModel` | `TimerService.Timer1Hour` (static `List<Action>`) | **No** | `Weather/ViewModels/ForecastViewModel.cs:20`, `AirQualityViewModel.cs:17`; views: `Forecast.axaml.cs:24-28`, `AirQuality.axaml.cs:13`, `Pressure.axaml.cs:15`, `Temperature.axaml.cs:13`, `SunriseSunset.axaml.cs:15` |
| L4 | `Month` (view) | `Application.Current.ActualThemeVariantChanged` (process-lifetime) | **No** | `Calendar/Views/Month.axaml.cs:30-31` vs `34-37` |
| L5 | `SingleMetricViewModel` | `TimerService.Timer1Second` | **No** (class not `IDisposable`) | `Monitor/ViewModels/SingleMetricViewModel.cs:8,16`; `Monitor/Views/SingleMetric.axaml.cs:22-26` |
| L6 | `ProgressViewModel` | `IAppSettingsProvider.DataChanged` (singleton) | only in `Dispose()`, which no one calls | `Progress/ViewModels/ProgressViewModel.cs:26,61-69`; `Progress/Views/ProgressView.axaml.cs:30-31` |
| L7 | `ClipboardMonitorService` singleton | own `DispatcherTimer` 400 ms | never stopped (by design) | `Tools/Services/ClipboardMonitorService.cs:51-56` |
| L8 | `ClipboardView` / `TranslatorView` toast `DispatcherTimer` | own view | stopped only when it fires | `Tools/Views/ClipboardView.axaml.cs:36-44`, `TranslatorView.axaml.cs:79-87` |
| OK | `MultiDashboardViewModel` | `Timer1Second` | yes (delegate cached in a field) | `Monitor/ViewModels/MultiDashboardViewModel.cs:25-26,50-54` |
| OK | `AggregateViewModel` | `Timer1Second`, `Timer1Hour` | yes | `Fixed/ViewModels/AggregateViewModel.cs:101-102,243-247` |
| OK | `MonthCalendarViewModel` / `DateCalendarViewModel` | `Timer5Minutes` | yes via `Stop()` from `Unloaded` | `Calendar/Views/Month.axaml.cs:34-37`, `Date.axaml.cs:15` |
| OK | `ProgressViewModel.Stop` | `Timer5Seconds` | yes | `Progress/ViewModels/ProgressViewModel.cs:54-59` |
| OK | `Note` | settings + theme + watcher + reload timer | yes | `Notes/Views/Note.axaml.cs:460-472` |

COM / WinRT objects not disposed:
* `ManagementObjectCollection` from `searcher.Get()` and every `ManagementObject` — 6 call sites,
  `Monitor/Services/MetricService.cs:47,61,81,99,109,134` (early `return` inside `foreach` makes it worse).
* SMTC `GlobalSystemMediaTransportControlsSessionManager` / sessions (RCWs) — released only by
  `MediaManagerService.Dispose` (`:452-457`), which is never reached (L2).
* `HttpClient`/`SocketsHttpHandler` per Weather and Fixed view model — never disposed
  (`Weather/Services/OpenMeteoWeatherProvider.cs:14`, `Fixed/Services/OpenMeteoWeatherService.cs:25`).

---

## 3. Repeated allocations ≥ 85 KB (LOH) per tick / refresh

| Bytes | What | Where | Frequency |
|---|---|---|---|
| 8.29 MB (1080p) – 33.2 MB (4K) | `new byte[size]` copy of the clipboard DIB | `Tools/Services/ClipboardNative.cs:219` | per image copy |
| 8.29 MB – 33.2 MB | `MemoryStream(totalFileSize)` backing array (second copy) | `Tools/Services/ClipboardNative.cs:208` | per image copy |
| 33.2 MB (native) | `SKBitmap.Decode(ms)` full-size pixel buffer | `Tools/Services/ClipboardNative.cs:224` | per image copy |
| 1–10 MB | PNG encode result `SKData` | `Tools/Services/ClipboardMonitorService.cs:113-118` | per image copy |
| 8.29 MB – 33.2 MB (native) | full-resolution `Bitmap` kept in history | `Tools/Services/ClipboardMonitorService.cs:131`, `:282` | per image copy + per startup item |
| 60 KB – 600 KB | album-art `byte[]` (`ms.ToArray()`) | `Music/Services/MediaManagerService.cs:263-267` | per track change |
| 1 MB – 36 MB (native) | full-size cover `Bitmap` decode | `Music/ViewModels/MusicViewModel.cs:217-218` | per track change |
| 16 MB per frame (native, at the 4 MP/frame budget) | `new WriteableBitmap` per GIF frame | `Picture/Services/PictureImageLoader.cs:245-250, 355-359` | per decoded animation |
| up to 16 MB each (native) | `retained[]` dependency copies + scratch buffer | `Picture/Services/PictureImageLoader.cs:228-230, 414-418` | transient during GIF decode |
| 192 MB transient (native) for an 8000×6000 JPEG | last-resort `SKBitmap.Decode(path)` **before** resize | `Picture/Services/PictureImageLoader.cs:117-128` | only if both Skia codec and Avalonia decode fail |
| content-sized (LOH past 85 KB) | model serialization per edit commit | `Notes/Views/Note.axaml.cs:373` | per LostFocus commit |

Not an LOH problem (checked, deliberately listed so it is not chased): Weather/AirQuality JSON strings
(~6–20 KB, `Weather/Services/OpenMeteoWeatherProvider.cs:36,64`), translation responses,
Monitor/Progress/Calendar per-tick objects (all small, gen0).

---

## 4. Suggested order of work (highest MB per unit of risk)

1. **#1 clipboard thumbnails** — dispose on remove/trim/clear + decode at 320 px wide.
   Expected: **−150 MB … −600 MB** depending on captured image sizes, with ~10 lines changed.
2. **#2 DIB double copy** — remove one 8–33 MB LOH copy per capture and reuse the buffer.
   Expected: removes the recurring LOH churn that fragments the heap for the whole session.
3. **#3 Music** — `Unloaded → viewModel.Dispose()`, dispose the media service and the previous cover
   bitmap, cap the decoded cover. Stops an unbounded leak **plus** a permanent 1.5 s SMTC poll per instance.
4. **#4 Weather + #5 Calendar Month + #6 Monitor/SingleMetric + #9 Progress** — four one-to-five-line
   `Dispose`/unsubscribe wirings (`viewModel.Dispose()` on `Unloaded`, `-=` for the theme event,
   make `SingleMetricViewModel` disposable, dispose the WMI collection/objects). Together these stop
   every remaining unbounded growth source for the live widget set.
5. **#7/#8 Picture** — only matters if the Picture widget is enabled; lower the animated pixel budget to
   ~4 MP, cache one animation, drop the `disposed` HashSet.

## 5. Coverage gaps / leads I could not verify inside the assigned scope

* `src/Widgets/Folders` (live: Folders) — `Services/LiquidGlassPreRenderService.cs:39-46,62-65,195-215,291`
  (`locationCache`, `CachedPopupBitmap`) and `Views/BigFolder.axaml.cs:180`
  `LiquidGlassBridge.SubscribeWallpaperInvalidated(...)` look like a static subscription + bitmap cache
  pair; verify whether the subscription is ever removed (`Services/LiquidGlassBridge.cs:54`) and whether
  every cache entry is disposed.
* `src/Widgets/Clock/Views/FramelessDigital.axaml.cs:480-571` (live: Clock/FramelessDigital) — a bitmap
  cache with several explicit `Dispose` paths and a `preRenderCts`; needs its own eviction/dispose audit.
* `src/uWidgets/Services/UpdateTimer.cs:24` — 7 static `UpdateTimer`s each subscribe
  `SystemEvents.SessionSwitch` and are never disposed (process-scope; fine in practice).
* Localisation `Locale.Designer.cs` `ResourceManager` statics (`src/Widgets/*/Locales`) — bounded.
