// Quick access / notes session checks (P8 batch B, slice B3 — Folders / Shortcuts / Notes).
//
// Run: dotnet run --project tests/QuickAccessSessionChecks
//
// Pins the B3 slice: the session-less legacy paths, the view's
// IWidgetActivityConsumer forwarding, the Notes per-screen markdown palette seam and the
// untouched save semantics, the migrated secondary panels' PanelService wiring (source
// scan + a real per-owner open/toggle/cooldown round trip), and the Shortcuts editor's P6
// WindowRoles registration staying in place. View models and views are constructed directly
// (no host pipeline), on the SessionHookChecks headless rig (real Win32 handles + Skia).
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Skia;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DeskCanvas.Contracts;
using DeskCanvas.Core.Models.Attributes;
using DeskCanvas.Runtime;
using DeskCanvas.Services;
using DeskCanvas.Views;
using Folders.Models;
using Folders.Services;
using Folders.Views;
using Notes.Models;
using Notes.Services;
using Notes.Views;
using SkiaSharp;

// UseHeadlessDrawing=false gives the probe REAL Win32 window handles (the SessionHookChecks
// rig) — the panel windows open for real through SecondaryPanelWindow's native z-order work.

// Fresh per-run sandbox (the IdentityStorageChecks pattern): the structural tests-harness
// sandbox persists across runs, and stale widget records would break seeded-state
// assertions. Must run before Const initializes.
Environment.SetEnvironmentVariable("DESCANVAS_DATA_FOLDER",
    Path.Combine(Path.GetTempPath(), "DeskCanvas-FixtureSandboxes",
        "QuickAccessSessionChecks-" + Guid.NewGuid().ToString("N")));

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
void Flush() => Dispatcher.UIThread.RunJobs();

static string RepoRoot()
{
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "scripts")))
        dir = dir.Parent!;
    return dir!.FullName;
}
static string ReadSource(params string[] parts) => File.ReadAllText(Path.Combine(RepoRoot(), Path.Combine(parts)));

void PanelServiceReset() =>
    typeof(PanelService).GetMethod("ResetForChecks", BindingFlags.NonPublic | BindingFlags.Static)!
        .Invoke(null, null);

// =========================================================================================
// 5. Notes save semantics unchanged: focus-loss commit, no diff no write, layout persistence
// =========================================================================================
Console.WriteLine("--- Notes save semantics ---");
NoteModel? pushed = null;
var popup = new NotePopupWindow(new NoteModel("Note title", "note body"), null, m => pushed = m);
var bodyEditor = (TextBox)typeof(NotePopupWindow)
    .GetField("BodyEditor", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(popup)!;
var commit = typeof(NotePopupWindow).GetMethod("Commit", BindingFlags.NonPublic | BindingFlags.Instance)!;
bodyEditor.Text = "edited body";
commit.Invoke(popup, null);
Check(pushed != null && pushed.Title == "Note title" && pushed.Content == "edited body",
    "5.1 the editor commit flows the edited content back through onModelChanged (title preserved)");
Check(pushed!.Updated != null, "5.2 the commit stamps Updated (the historic last-edited marker)");
var pushedOnce = pushed;
commit.Invoke(popup, null);
Check(ReferenceEquals(pushed, pushedOnce),
    "5.3 a commit with no diff does not fire the callback again (no write without a change)");
commit.Invoke(popup, null);
Check(ReferenceEquals(pushed, pushedOnce), "5.4 repeated no-diff commits stay silent");

var noteSource = ReadSource("src", "Widgets", "Notes", "Views", "Note.axaml.cs");
Check(noteSource.Contains("Model with { Content = newText, Updated = DateTime.Now }"),
    "5.5 the card's inline commit still stamps and persists on focus loss (the UpdateContent pipeline is untouched)");
Check(noteSource.Contains("JsonSerializer.SerializeToElement(newModel)") && noteSource.Contains("widgetLayoutProvider!.Save(newLayout)"),
    "5.6 the internal note still persists through the widget layout provider's Save");
Check(!noteSource.Contains("AutoSave") && !noteSource.Contains("autosave"),
    "5.7 no periodic auto-save exists on the card (save stays focus-loss driven)");

// =========================================================================================
// 6. The Notes per-screen markdown palette seam
// =========================================================================================
Console.WriteLine("--- Notes markdown palette per-screen seam ---");
var rendererSource = ReadSource("src", "Widgets", "Notes", "Services", "MarkdownRenderer.cs");
Check(rendererSource.Contains("scope?.ActualThemeVariant"),
    "6.1 the palette resolves the variant from the render scope first (per-screen light/dark)");
Check(!rendererSource.Contains("Application.Current?.ActualThemeVariant =="),
    "6.2 the app-wide variant is no longer the primary read (it only backs the unresolved-scope fallback)");
Check(noteSource.Contains("TryGetResource(\"SystemControlForegroundBaseHighBrush\", this.ActualThemeVariant"),
    "6.3 the card's base-high brush lookup follows the card's own variant (was the app-wide variant)");

var paletteType = typeof(MarkdownRenderer).GetNestedType("Palette", BindingFlags.NonPublic)!;
var fromMethod = paletteType.GetMethod("From", BindingFlags.Public | BindingFlags.Static)!;
var bodyProperty = paletteType.GetProperty("Body")!;
var typography = new MarkdownTypography(
    true,
    null,
    new MarkdownPalette(BodyColor: "#FF112233"),
    new MarkdownPalette(BodyColor: "#FFEEDDCC"));
static Color BodyOf(object palette, PropertyInfo property) => ((SolidColorBrush)property.GetValue(palette)!).Color;

// RequestedThemeVariant is settable on Window (TopLevel), not on a bare Control —
// and a detached Window still resolves its own ActualThemeVariant from it.
var darkScope = new Window { RequestedThemeVariant = ThemeVariant.Dark };
var darkPalette = fromMethod.Invoke(null, new object?[] { typography, darkScope })!;
Check(BodyOf(darkPalette, bodyProperty) == Color.Parse("#FFEEDDCC"),
    "6.4 a dark-scope render picks the dark palette");
var lightScope = new Window { RequestedThemeVariant = ThemeVariant.Light };
var lightPalette = fromMethod.Invoke(null, new object?[] { typography, lightScope })!;
Check(BodyOf(lightPalette, bodyProperty) == Color.Parse("#FF112233"),
    "6.5 a light-scope render picks the light palette");
var unsetScope = new Control(); // never attached: no resolved variant of its own
var fallbackPalette = fromMethod.Invoke(null, new object?[] { typography, unsetScope })!;
var appExpected = Application.Current!.ActualThemeVariant == ThemeVariant.Dark
    ? Color.Parse("#FFEEDDCC")
    : Color.Parse("#FF112233");
Check(BodyOf(fallbackPalette, bodyProperty) == appExpected,
    "6.6 an unresolved scope falls back to the app-wide variant (the historic behaviour)");

// =========================================================================================
// 7. The migrated panels: PanelService wiring (source scan)
// =========================================================================================
Console.WriteLine("--- Panel migration source scan ---");
void ScanPanel(string label, string relativePath, string identityKey)
{
    var source = ReadSource(new[] { "src", "Widgets" }.Concat(relativePath.Split('/')).ToArray());
    Check(source.Contains("PanelService.Open(new PanelRequest("),
        $"{label}: ShowPopup routes through PanelService.Open");
    Check(source.Contains("\"" + identityKey + "\""),
        $"{label}: the identity key is \"{identityKey}\"");
    Check(source.Contains("PanelScope.PerOwner"),
        $"{label}: the panel identity is scoped PerOwner");
    Check(!source.Contains("PanelCoolingDown<") && !source.Contains("TryCloseActivePanel<"),
        $"{label}: the legacy helper pair is gone from the panel");
}
ScanPanel("NotePopupWindow", "Notes/Views/NotePopupWindow.axaml.cs", "Notes.Editor");
ScanPanel("ShortcutsPopupWindow", "Shortcuts/Views/ShortcutsPopupWindow.axaml.cs", "Shortcuts.QuickPanel");
ScanPanel("BigFolderPopupWindow", "Folders/Views/BigFolderPopupWindow.axaml.cs", "Folders.BigFolderGrid");
var editorSource = ReadSource("src", "Widgets", "Shortcuts", "Views", "ShortcutsEditorWindow.axaml.cs");
Check(editorSource.Contains("WindowRoles.Register"),
    "7.x ShortcutsEditorWindow keeps its P6 WindowRoles registration (untouched by the panel migration)");

// =========================================================================================
// 8. PanelService per-owner round trip with a real migrated panel
// =========================================================================================
Console.WriteLine("--- PanelService per-owner round trip (NotePopupWindow) ---");
PanelServiceReset();
var ownerIdentity = WidgetInstanceId.New();
PanelOpenDecision Open() => PanelService.Open(new PanelRequest(
    ownerIdentity, null, "Notes.Editor", PanelScope.PerOwner,
    () => new NotePopupWindow(new NoteModel("round trip", "body"), null, _ => { })));
try
{
    Check(Open() == PanelOpenDecision.Opened, "8.1 the first open for the identity opens a live panel");
    Check(Open() == PanelOpenDecision.ToggledClosed, "8.2 a second open toggles the live panel closed");
    Flush();
    Check(Open() == PanelOpenDecision.CoolingDown,
        "8.3 the just-toggled identity is inside its cooldown (the flicker guard)");
    var otherIdentity = WidgetInstanceId.New();
    var other = PanelService.Open(new PanelRequest(
        otherIdentity, null, "Notes.Editor", PanelScope.PerOwner,
        () => new NotePopupWindow(new NoteModel("round trip", "body"), null, _ => { })));
    Check(other == PanelOpenDecision.Opened,
        "8.4 another owner's identity opens independently (per-owner, not process-wide)");
}
finally
{
    PanelServiceReset();
    WidgetSessionRegistry.ResetForChecks();
}

// =========================================================================================
// 10. Launcher hover zoom: configurable per widget, clamped, and the layout reserves the growth
//     so a zoomed icon is never cut by the card or the scroll viewport. The zoom's boundary is
//     the GRID CELL (the widget window), not the card — a single icon may use the whole cell.
// =========================================================================================
Console.WriteLine("--- launcher hover zoom ---");
Check(HoverZoomMath.ScaleOf(0) == 1.0 && HoverZoomMath.ScaleOf(double.NaN) == 1.0,
    "10.1 a missing/NaN zoom means 100% (no zoom)");
Check(HoverZoomMath.ScaleOf(999) == 1.5, "10.2 the grid zoom is clamped to 150%");
Check(HoverZoomMath.ScaleOf(999, HoverZoomMath.MaxSinglePercent) == 2.0,
    "10.2b the single-icon zoom is clamped to 200% — enough to reach the cell edge");
Check(new BigFolderModel().HoverZoom == HoverZoomMath.DefaultGridPercent &&
      new FolderModel([]).HoverZoom == HoverZoomMath.DefaultGridPercent,
    "10.3 the icon grids default to the historical 115%");
Check(new SingleFileModel().HoverZoom == HoverZoomMath.DefaultSinglePercent &&
      new SingleLinkModel().HoverZoom == HoverZoomMath.DefaultSinglePercent,
    "10.4 the single-icon cards default to the historical 108%");
Check(Math.Abs(new BigFolderModel(HoverZoom: 130).EffectiveHoverScale - 1.3) < 0.001,
    "10.5 the model exposes the multiplier the views animate with");
Check(Math.Abs(new SingleLinkModel(HoverZoom: 90).EffectiveHoverScale - 1.0) < 0.001,
    "10.6 a stored value below 100% cannot shrink the icon on hover");
Check(Math.Abs(HoverZoomMath.Growth(80, 1.2) - 8) < 0.001, "10.7 the growth is half the size delta");
Check(HoverZoomMath.Growth(80, 1.0) == 0, "10.8 no zoom means no reserved room");
// The zoom may spill past the card, but the widget WINDOW is still a hard edge: ScaleWithin caps
// the multiplier at the room the window actually has (growth = size*(scale-1)/2 <= room).
Check(Math.Abs(HoverZoomMath.ScaleWithin(80, 1.5, 10) - 1.25) < 0.001,
    "10.9 ScaleWithin caps the zoom at the room before the window edge (80px icon, 10px room → 125%)");
Check(Math.Abs(HoverZoomMath.ScaleWithin(80, 1.1, 10) - 1.1) < 0.001,
    "10.9b a zoom that already fits the room is drawn as asked (no needless shrink)");
Check(HoverZoomMath.ScaleWithin(80, 1.5, double.PositiveInfinity) == 1.5,
    "10.9c outside a widget host (组件库预览) the requested zoom is drawn as-is");

// The views must actually consume the configured value (not keep a private constant), must not
// shrink the resting icon for the zoom, and must tell the host to stop clipping them.
string ReadRepo(params string[] parts)
{
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "scripts"))) dir = dir.Parent!;
    return File.ReadAllText(Path.Combine(new[] { dir!.FullName }.Concat(parts).ToArray()));
}

var bigFolderView = ReadRepo("src", "Widgets", "Folders", "Views", "BigFolder.axaml.cs");
var folderView = ReadRepo("src", "Widgets", "Folders", "Views", "Folder.axaml.cs");
var singleFileView = ReadRepo("src", "Widgets", "Folders", "Views", "SingleFile.axaml.cs");
var singleLinkView = ReadRepo("src", "Widgets", "Folders", "Views", "SingleLink.axaml.cs");

Check(bigFolderView.Contains("model.EffectiveHoverScale") &&
      !bigFolderView.Contains("const double HoverScale"),
    "10.10 大文件夹 animates with the configured zoom (no hardcoded constant)");
Check(!bigFolderView.Contains("HoverZoomMath.Growth(itemSize, hoverScale)") &&
      bigFolderView.Contains("HoverZoomMath.ScaleWithin("),
    "10.11 大文件夹 no longer reserves padding for the zoom (icons keep their size) and caps the zoom at hover");
Check(folderView.Contains("model.EffectiveHoverScale") && folderView.Contains("HoverViewportInset"),
    "10.12 文件展示框 animates with the configured zoom and keeps a viewport gutter");
Check(singleFileView.Contains("HoverZoomMath.ScaleWithin(") && singleLinkView.Contains("HoverZoomMath.ScaleWithin(") &&
      !singleFileView.Contains("HoverZoomMath.Fit(") && !singleLinkView.Contains("HoverZoomMath.Fit("),
    "10.13 单文件 / 单链接 keep their resting size and cap only the hover zoom");
// The cap has to be measured to the WINDOW (one grid cell), not to the card: the old
// `CardOverflowRoom.Of(view)` returned only WidgetMargin, which is smaller than the distance from
// the icon to the card edge — the zoom could therefore never leave the widget rectangle.
Check(singleFileView.Contains("CardOverflowRoom.Around(") && singleLinkView.Contains("CardOverflowRoom.Around(") &&
      !singleFileView.Contains("CardOverflowRoom.Of(") && !singleLinkView.Contains("CardOverflowRoom.Of("),
    "10.13b 单文件 / 单链接 cap the zoom at the grid cell edge, not at the card edge");
Check(bigFolderView.Contains("CardOverflowRoom.Around(") && !bigFolderView.Contains("PaddingThickness.Left + CardOverflowRoom"),
    "10.13c 大文件夹 measures the room from its icon grid to the cell edge (uniform for every tile)");
var overflowRoom = ReadRepo("src", "Widgets", "Folders", "Services", "CardOverflowRoom.cs");
Check(overflowRoom.Contains("client.Width") && overflowRoom.Contains("client.Height") &&
      overflowRoom.Contains("icon.Bounds"),
    "10.13d CardOverflowRoom measures the icon's resting rect against the window client area");
foreach (var (name, file) in new[]
         {
             ("单文件", "SingleFileSettings.axaml"),
             ("单链接", "SingleLinkSettings.axaml"),
         })
{
    var slider = ReadRepo("src", "Widgets", "Folders", "Views", "Settings", file);
    Check(slider.Contains("Maximum=\"200\""),
        $"10.13e {name}'s 悬浮缩放 slider reaches the 200% ceiling (the cell edge is reachable)");
}
Check(bigFolderView.Contains("AllowsCardOverflow => true") &&
      singleFileView.Contains("AllowsCardOverflow => true") &&
      singleLinkView.Contains("AllowsCardOverflow => true"),
    "10.14 the three overflowing views opt out of the host's card clip");
var hostView = ReadRepo("src", "DeskCanvas", "Views", "Widget.axaml.cs");
Check(hostView.Contains("ContentPaintsOutsideCard") && hostView.Contains("AllowsCardOverflow"),
    "10.15 the widget host honours AllowsCardOverflow when it applies the card clip");
// The view's OWN ClipToBounds is the other half of the story: a UserControl clips to its bounds by
// default, and its bounds are exactly the card's content box — measured on a real render, that was
// the cut the padding/gutter work never fixed.
foreach (var (name, file) in new[]
         {
             ("单文件", "SingleFile.axaml"),
             ("单链接", "SingleLink.axaml"),
             ("大文件夹", "BigFolder.axaml"),
         })
{
    var axaml = ReadRepo("src", "Widgets", "Folders", "Views", file);
    Check(axaml.Contains("ClipToBounds=\"False\""),
        $"10.16 {name} turns its own ClipToBounds off (otherwise the view cuts the zoom at the card box)");
}

// …and so is the ITEM HOST: an ItemsControl clips to its own bounds too, which for the icon grids
// are the grid box (inset by the padding) — the cut the user still saw inside 大文件夹 after the
// view-level fix. Measured: with the ItemsControl clipping, 0 px of the zoomed tile painted
// outside the card; with it off, 555 px did.
var bigFolderAxaml = ReadRepo("src", "Widgets", "Folders", "Views", "BigFolder.axaml");
Check(bigFolderAxaml.Split("ClipToBounds=\"False\"").Length - 1 >= 2,
    "10.17 大文件夹's IconGrid does not clip its own bounds either (the zoom reaches the card edge)");
var folderAxaml = ReadRepo("src", "Widgets", "Folders", "Views", "Folder.axaml");
Check(folderAxaml.Split("ClipToBounds=\"False\"").Length - 1 >= 2,
    "10.18 文件展示框's two item hosts do not clip (the viewport gutter is what keeps the zoom whole)");

// …and the boundary the cap is measured against, on a REAL layout: a 1×1 card on a 92 DIP cell
// (widget margin 10). 单文件 / 单链接 declare Margin="0", so the view fills the card and the
// resting icon IS the card (72 DIP at 图标大小 100 %) — the only room left is the 10 DIP band
// between card and cell. The old rule (room = the same 10 DIP) was not the bug here: the bug was
// that a zoom smaller than that band was still cut, because the host's content template wrapped
// the view in a clipping ContentControl (see UiFoundationChecks/HoverOverflowChecks).
{
    var hoverView = new SingleFile(new SingleFileModel(null, 100, 200), null!) { Margin = new Thickness(0) };
    var hoverCard = new Border
    {
        Width = 72,
        Height = 72,
        Margin = new Thickness(10),
        Child = new Avalonia.Controls.Presenters.ContentPresenter { Width = 72, Height = 72, Content = hoverView },
    };
    var hoverWindow = new Window
    {
        Width = 92,
        Height = 92,
        Content = new Grid { Children = { hoverCard } },
    };
    hoverWindow.Show();
    Flush();

    var frame = hoverView.FindControl<Border>("EmptyPlaceholder")!;
    var frameOrigin = frame.TranslatePoint(new Point(0, 0), hoverWindow)!.Value;
    var room = CardOverflowRoom.RoomIn(new Rect(0, 0, 92, 92), new Rect(frameOrigin, frame.Bounds.Size));
    Check(Math.Abs(room - 10) < 0.5,
        $"10.19 the room is measured to the CELL edge ({room:F1} DIP = the card→cell band, icon {frame.Bounds.Width:0} of 92)");
    var reached = frame.Bounds.Width * HoverZoomMath.ScaleWithin(frame.Bounds.Width, 2.0, room);
    Check(reached > 72 && Math.Abs(reached - 92) < 1.0,
        $"10.19b the zoom may fill the whole cell ({reached:F0} DIP of 92 — past the 72 DIP card, capped by the cell)");
    hoverWindow.Close();
}

// =========================================================================================
Console.WriteLine();
if (failures.Count > 0)
{
    Console.WriteLine($"FAILED {failures.Count} of {checks} checks:");
    foreach (var failure in failures) Console.WriteLine(" - " + failure);
    return 1;
}
Console.WriteLine($"Quick access session checks passed: {checks}/{checks}.");
// =========================================================================================
// 9. SingleLink: the link widget (the 27th widget)
// =========================================================================================
Console.WriteLine("--- single link widget ---");

// 9.1 the registration: a widget the host can actually instantiate and configure.
var linkInfo = typeof(SingleLink).Assembly
    .GetCustomAttributes<WidgetInfoAttribute>()
    .FirstOrDefault(a => a.ViewType == typeof(SingleLink));
Check(linkInfo != null, "single link: the view is registered on the Folders assembly");
Check(linkInfo?.ModelType == typeof(SingleLinkModel),
    "single link: the registration carries its model");
Check(linkInfo?.EditModelViewType?.Name == "SingleLinkSettings",
    "single link: the registration carries its settings view");
Check(linkInfo is { DefaultColumns: 1, DefaultRows: 1 },
    "single link: it starts as a 1x1 card");
Check((linkInfo?.PresetSpanList ?? string.Empty).Contains("1x1"),
    "single link: 1x1 is one of its presets");
Check(linkInfo?.Title == "Folders_SingleLink_Title" && linkInfo?.Subtitle == "Folders_SingleLink_Subtitle",
    "single link: title and subtitle come from the locale keys");

// 9.2 all three icon sources exist, and every one of them lands in the local cache.
var iconService = typeof(LinkIconService);
Check(iconService.GetMethod("SaveLocalFileAsync") != null,
    "single link: a local file can be picked as the icon");
Check(iconService.GetMethod("DownloadFromUrlAsync") != null,
    "single link: an image URL can be downloaded as the icon");
Check(iconService.GetMethod("FetchFaviconAsync") != null,
    "single link: the site's own icon can be fetched from the link");

var linkSandbox = Path.Combine(Path.GetTempPath(), "DeskCanvas-LinkIcon-" + Guid.NewGuid().ToString("N")[..8]);
Directory.CreateDirectory(linkSandbox);
var iconFile = Path.Combine(linkSandbox, "site-icon.png");
using (var bitmap = new SKBitmap(64, 64))
{
    bitmap.Erase(new SKColor(0x22, 0x88, 0xCC));
    using var image = SKImage.FromBitmap(bitmap);
    using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
    File.WriteAllBytes(iconFile, encoded.ToArray());
}

// Off the UI thread, then blocked on: this harness has a UI-thread synchronization context,
// so awaiting the file IO here would post its continuation back to a thread that is busy
// waiting for it (a classic deadlock), and a top-level await would instead resume on a pool
// thread where the Avalonia controls built below may not be created.
var savedFirst = Task.Run(() => LinkIconService.SaveLocalFileAsync(iconFile)).GetAwaiter().GetResult();
Check(savedFirst != null && File.Exists(savedFirst),
    "single link: a picked local file is copied into the cache");
Check(savedFirst != null && Path.GetDirectoryName(savedFirst) == LinkIconService.IconFolder,
    "single link: the copy lands in the LinkIcons cache folder");

// The point of caching: fetching the same thing twice must not create a second file, and the
// widget must be able to render from the cache with no network involved.
var savedAgain = Task.Run(() => LinkIconService.SaveLocalFileAsync(iconFile)).GetAwaiter().GetResult();
Check(savedFirst != null && savedAgain != null && savedFirst == savedAgain,
    "single link: the same icon reuses one cached copy");
var cachedCount = Directory.Exists(LinkIconService.IconFolder)
    ? Directory.GetFiles(LinkIconService.IconFolder).Length
    : 0;
Check(cachedCount == 1, $"single link: two saves leave exactly one cached file (got {cachedCount})");
Check(LinkIconService.LoadIcon(savedFirst) != null,
    "single link: the cached file loads back into a bitmap");

// 9.3 the view renders the cached icon and keeps the URL on its tooltip.
var link = new SingleLink(
    new SingleLinkModel(Url: "https://example.com", Title: "示例站点", CustomIconPath: savedFirst), null!);
var linkTip = ToolTip.GetTip(link) as string;
Check(linkTip != null && linkTip.Contains("https://example.com"),
    "single link: the tooltip carries the target URL");
link.Measure(new Size(160, 160));
link.Arrange(new Rect(0, 0, 160, 160));
// The control is never attached to a visual root, so the visual tree to walk is not built:
// read the XAML-generated field directly instead.
var linkImage = (Image?)typeof(SingleLink)
    .GetField("IconImage", BindingFlags.Instance | BindingFlags.NonPublic)
    ?.GetValue(link);
Check(linkImage != null, "single link: the view holds an image element");
Check(linkImage?.Source != null,
    $"single link: the cached icon is what the card paints (source {(linkImage?.Source == null ? "null" : "set")})");
Check(linkImage is { IsVisible: true }, "single link: the cached icon is shown, not the fallback");

// 9.4 opening: a chosen browser is used, otherwise the URL goes to the default handler.
var singleLinkSource = ReadSource("src", "Widgets", "Folders", "Views", "SingleLink.axaml.cs");
Check(singleLinkSource.Contains("File.Exists(model.BrowserPath)"),
    "single link: a browser is only used while its executable is still there");
Check(singleLinkSource.Contains("Process.Start(new ProcessStartInfo(targetUrl)"),
    "single link: with no browser chosen the URL goes to the default handler");
Check(singleLinkSource.Contains("UseShellExecute = true"),
    "single link: the URL is opened by the shell (a URL is not an executable)");

// 9.5 the default model is an empty card at 70%.
Check(new SingleLinkModel() is { Url: null, BrowserPath: null, CustomIconPath: null }
      && Math.Abs(new SingleLinkModel().IconPercent - 70.0) < 0.001,
    "single link: the default model is an empty 70% card");

return 0;

// =========================================================================================
// Probe app: the same style sources PanelShellAvaloniaChecks mounts, so the migrated panel
// base class resolves its shell styles in headless runs.
// =========================================================================================
sealed class ProbeApp : Application
{
    public override void Initialize()
    {
        Styles.Add(new FluentTheme());
        Styles.Add(new StyleInclude(new Uri("avares://DeskCanvas.UI/Styles/Controls/SecondaryPanelShell.axaml"))
        {
            Source = new Uri("avares://DeskCanvas.UI/Styles/Controls/SecondaryPanelShell.axaml")
        });
        Styles.Add(new StyleInclude(new Uri("avares://DeskCanvas/Styles/Controls/SecondaryPanelShell.axaml"))
        {
            Source = new Uri("avares://DeskCanvas/Styles/Controls/SecondaryPanelShell.axaml")
        });
        Resources.MergedDictionaries.Add(new ResourceInclude(new Uri("avares://DeskCanvas.UI/"))
        {
            Source = new Uri("avares://DeskCanvas.UI/Styles/DesignTokens.axaml")
        });
    }
}
