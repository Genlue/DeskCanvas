using System.Reflection;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DeskCanvas.Services;
using DeskCanvas.Views;
using SharedShell = DeskCanvas.UI.Controls.SecondaryPanelShell;
using SharedDrag = DeskCanvas.UI.Controls.PanelDragBehavior;
using SharedPort = DeskCanvas.UI.Controls.GlassSurfaceBase;
using LegacyShell = DeskCanvas.Views.Controls.SecondaryPanelShell;
using LegacyDrag = DeskCanvas.Views.Controls.PanelDragBehavior;
using LegacyGlass = DeskCanvas.Views.Controls.LiquidGlassSurface;

AppBuilder.Configure<ProbeApp>().UseHeadless(new AvaloniaHeadlessPlatformOptions()).SetupWithoutStarting();
var failures = new List<string>();
var checks = 0;
void Check(bool condition, string message)
{
    checks++;
    Console.WriteLine($"[{(condition ? "PASS" : "FAIL")}] {message}");
    if (!condition) failures.Add(message);
}
void Flush() => Dispatcher.UIThread.RunJobs();

// =========================================================================================
// 1. The glass-surface port (DeskCanvas.UI.GlassSurfaceBase)
// =========================================================================================
Console.WriteLine("--- Glass surface port: contract ---");
Check(typeof(LegacyGlass).IsSubclassOf(typeof(SharedPort)),
    "the host LiquidGlassSurface implements the shared GlassSurfaceBase port");
Check(typeof(SharedPort).IsSubclassOf(typeof(Control)),
    "the port is a control (it slots into the shared shell's visual tree)");

// The port must be implementable WITHOUT the host pipeline — a second, independent
// implementation proves the contract is self-contained (stub used only as a contract probe;
// the production implementation count stays one, per the single-owner rule).
var stub = new ProbeSurface();
stub.CornerRadius = new CornerRadius(12);
Check(stub.CornerRadius == new CornerRadius(12), "port: corner radius is settable on an independent implementation");
stub.RequestRender(immediate: true);
stub.SetAnimationFrameScale(0.9, new Point(0.5, 0.5));
Check(stub.RenderRequests.SequenceEqual([true]) && stub.FrameScales.Count == 1,
    "port: render request and animation-frame scale dispatch on the port type");

// =========================================================================================
// 2. The shared shell standalone (no host facade): template, parts, close semantics
// =========================================================================================
Console.WriteLine("--- Shared shell (UI type) standalone ---");
var sharedShell = new SharedShell { Title = "Shared", Subtitle = "port slice", CloseButtonLabel = "Close panel" };
var sharedWindow = new Window { Width = 420, Height = 320, SystemDecorations = SystemDecorations.None };
sharedWindow.Content = sharedShell;
sharedWindow.Show();
Flush();
Check(sharedShell.IsTemplateApplied, "the shared shell template applies under the shared styles alone");
Check(sharedShell.Backdrop is not null && sharedShell.CardBorder is not null
      && sharedShell.HeaderDragArea is not null && sharedShell.CloseButton is not null
      && sharedShell.ContentHost is not null,
    "all chrome parts resolve without the host assembly in the tree");
Check(sharedShell.CardBorder!.Opacity == 0.0, "the card starts at Opacity=0 (late template application cannot flash)");
Check(sharedShell.GlassSurface is null, "the glass slot is null by default (the shell renders with an unpainted card)");

var closeRequests = 0;
sharedShell.CloseRequested += (_, e) => { closeRequests++; e.Handled = true; };
var onClick = typeof(Button).GetMethod("OnClick", BindingFlags.Instance | BindingFlags.NonPublic, null, Array.Empty<Type>(), null)
              ?? typeof(Button).GetMethod("OnClick", BindingFlags.Instance | BindingFlags.NonPublic, null, [typeof(RoutedEventArgs)], null)!;
onClick.Invoke(sharedShell.CloseButton, onClick.GetParameters().Length == 0
    ? Array.Empty<object?>()
    : [new RoutedEventArgs(Button.ClickEvent)]);
Flush();
Check(closeRequests == 1, "the shared close button raises exactly one CloseRequested");

Flush();
Check(AutomationProperties.GetName(sharedShell.CloseButton!) == "Close panel",
    "the injected close-button label reaches the accessible name at template time");

// Drag contract lives in the shared library and attaches through the shared shell.
Check(SharedDrag.GetIsDragHandle(sharedShell.HeaderDragArea!),
    "the shared shell marks the header as the shared drag handle");
var probeButton = new Button();
var probeGrid = new Grid();
probeGrid.Children.Add(probeButton);
Check(SharedDrag.IsDragExcluded(probeButton, probeGrid),
    "a Button subtree is excluded from the drag handle");
Check(!SharedDrag.IsDragExcluded(new Panel(), probeGrid), "a plain subtree is draggable");

// =========================================================================================
// 3. Glass slot mirroring (shared shell + independent port surface)
// =========================================================================================
Console.WriteLine("--- Glass slot mirroring ---");
var slotShell = new SharedShell { Title = "slot" };
var slotWindow = new Window { Width = 420, Height = 320, SystemDecorations = SystemDecorations.None };
slotWindow.Content = slotShell;
slotWindow.Show();
Flush();
var mirror = new ProbeSurface();
slotShell.GlassSurface = mirror;
Flush();
Check(!mirror.IsVisible, "an assigned surface adopts UseRenderedGlass=false (hidden by default)");
slotShell.UseRenderedGlass = true;
Flush();
Check(mirror.IsVisible, "UseRenderedGlass=true mirrors onto the port surface's visibility");
Check(mirror.CornerRadius == slotShell.CornerRadius,
    $"the assigned surface adopts the card radius ({mirror.CornerRadius.TopLeft})");
slotShell.CornerRadius = new CornerRadius(12);
Flush();
Check(mirror.CornerRadius == new CornerRadius(12), "a later card-radius change keeps the surface in phase");
slotShell.UseRenderedGlass = false;
Flush();
Check(!mirror.IsVisible, "UseRenderedGlass=false mirrors onto the port surface's visibility");
var mirror2 = new ProbeSurface();
slotShell.GlassSurface = mirror2;
Flush();
Check(!mirror2.IsVisible && mirror2.CornerRadius == new CornerRadius(12),
    "a replacement surface re-syncs visibility and radius immediately");

// =========================================================================================
Console.WriteLine("--- Derived-type selector experiment ---");
var derivedProbe = new DerivedShellProbe();
var derivedWindow = new Window { Width = 420, Height = 320, SystemDecorations = SystemDecorations.None };
derivedWindow.Content = derivedProbe;
derivedWindow.Show();
Flush();
Check(!(derivedProbe.IsTemplateApplied && derivedProbe.CloseButton is not null),
    $"Avalonia type selectors match the exact type only (derived probe: template={derivedProbe.IsTemplateApplied}, closeButton={derivedProbe.CloseButton is not null}) — derived facades are styled by the host shim, not the shared type selector");

// =========================================================================================
// 4. Host facade: port slot, decomposition, locale, legacy URI
// =========================================================================================
Console.WriteLine("--- Host facade (legacy type) ---");
Check(typeof(LegacyShell).BaseType == typeof(SharedShell), "the host facade executes the shared shell implementation");
var facadeShell = new LegacyShell();
Check(facadeShell.GlassSurface is LegacyGlass,
    "the facade instantiates the host LiquidGlassSurface into the shared port slot");
var facadeWindow = new Window { Width = 420, Height = 320, SystemDecorations = SystemDecorations.None };
facadeWindow.Content = facadeShell;
facadeWindow.Show();
Flush();
Check(facadeShell.IsTemplateApplied, "the facade shell template applies through the shared styles");
var glassInTree = facadeWindow.GetVisualDescendants().OfType<LegacyGlass>().ToList();
Check(glassInTree.Count == 1 && ReferenceEquals(glassInTree[0], facadeShell.GlassSurface),
    $"the facade's port surface is the only glass surface in the tree (count={glassInTree.Count}, closeButton={facadeShell.CloseButton is not null})");
if (facadeShell.CloseButton is { } facadeCloseButton)
    Check(AutomationProperties.GetName(facadeCloseButton) == DeskCanvas.Locales.Locale.SecondaryPanel_Close,
        "the facade injects the host locale for the close button at template time");

// Acrylic appearance decomposition (pure resolution, no window/theme service needed).
var acrylicTheme = new DeskCanvas.Core.Models.Settings.Theme(
    DarkMode: true, AccentColor: null, OpacityLevel: 0.9, Monochrome: false, UseNativeFrame: false, FontFamily: "Inter",
    Surface: DeskCanvas.Core.Models.Settings.SurfaceStyle.Acrylic);
var acrylicAppearance = PanelAppearanceResolver.Resolve(acrylicTheme, isDark: true, PanelAppearanceResolver.DefaultCornerRadius);
facadeShell.SurfaceAppearance = acrylicAppearance;
Flush();
Check(!facadeShell.UseRenderedGlass && facadeShell.GlassMaterial is null,
    "acrylic appearance: no rendered glass and no material");
Check(facadeShell.CardBackground is SolidColorBrush { Color: { A: 198, R: 28, G: 28, B: 32 } },
    "acrylic appearance: the card fill alpha is the clamped opacity result");
Check(facadeShell.CornerRadius == new CornerRadius(PanelAppearanceResolver.DefaultCornerRadius),
    "acrylic appearance: the resolver radius reaches the shared shell card");
Check(((LegacyGlass)facadeShell.GlassSurface!).Material is null,
    "acrylic appearance: the port surface material stays null");

// Glass appearance decomposition: material flows to the port surface through the facade.
var glassTheme = new DeskCanvas.Core.Models.Settings.Theme(
    DarkMode: true, AccentColor: null, OpacityLevel: 1.0, Monochrome: false, UseNativeFrame: false, FontFamily: "Inter",
    Surface: DeskCanvas.Core.Models.Settings.SurfaceStyle.LiquidGlass);
var glassAppearance = PanelAppearanceResolver.Resolve(glassTheme, isDark: true, PanelAppearanceResolver.DefaultCornerRadius);
facadeShell.SurfaceAppearance = glassAppearance;
Flush();
Check(facadeShell.UseRenderedGlass && ReferenceEquals(facadeShell.GlassMaterial, glassTheme),
    "glass appearance: rendered glass on and the theme kept as GlassMaterial");
Check(ReferenceEquals(((LegacyGlass)facadeShell.GlassSurface!).Material, glassTheme),
    "glass appearance: the port surface receives the theme through the facade's GlassMaterial path");
Check(facadeShell.GlassSurface!.IsVisible, "glass appearance: the port surface is visible");

// The old style URI keeps loading (compat shim → shared template source).
var shimInclude = new StyleInclude(new Uri("avares://DeskCanvas/Styles/Controls/SecondaryPanelShell.axaml"))
{
    Source = new Uri("avares://DeskCanvas/Styles/Controls/SecondaryPanelShell.axaml")
};
Application.Current!.Styles.Add(shimInclude);
Flush();
Check(shimInclude.Loaded is Styles shimStyles && shimStyles.Count > 0,
    "the legacy avares://DeskCanvas shell style URI still loads (compat shim)");

// =========================================================================================
// 5. Drag behavior: one property identity across host facade and shared implementation
// =========================================================================================
Console.WriteLine("--- Drag behavior identity ---");
Check(ReferenceEquals(LegacyDrag.IsDragHandleProperty, SharedDrag.IsDragHandleProperty)
      && ReferenceEquals(LegacyDrag.IsInteractiveProperty, SharedDrag.IsInteractiveProperty),
    "host facade attached properties ARE the shared property instances");
var dragProbe = new Panel();
LegacyDrag.SetIsDragHandle(dragProbe, true);
Check(SharedDrag.GetIsDragHandle(dragProbe), "host setter writes shared storage");
SharedDrag.SetIsDragHandle(dragProbe, false);
Check(!LegacyDrag.GetIsDragHandle(dragProbe), "shared setter writes the same storage");

// =========================================================================================
// 6. The panel window drives the glass through the port
// =========================================================================================
Console.WriteLine("--- Panel window port usage ---");
Check(PanelAppearanceBinder.ActiveCount == 0, "no panel binders are active before the probe");
var panel = new ProbePanelWindow();
panel.Show();
Flush();
var connected = (SharedShell?)typeof(SecondaryPanelWindow)
    .GetProperty("PanelShell", BindingFlags.Instance | BindingFlags.NonPublic)
    ?.GetValue(panel);
Check(connected is not null && connected.IsTemplateApplied, "the panel window connected the shell");
Check(connected!.GlassSurface is LegacyGlass, "the window's shell surface is the host port implementation");
connected.GlassSurface!.SetAnimationFrameScale(0.9, new Point(0.5, 0.5));
connected.GlassSurface!.RequestRender(immediate: true);
Flush();
Check(true, "port render request and animation scale execute on the real surface without throwing (headless)");
Check(PanelAppearanceBinder.ActiveCount >= 1, "the appearance binder attached to the shown panel");

// =========================================================================================
Console.WriteLine();
if (failures.Count > 0)
{
    Console.WriteLine($"FAILED {failures.Count} of {checks} checks:");
    foreach (var failure in failures) Console.WriteLine(" - " + failure);
    return 1;
}
Console.WriteLine($"Panel shell port checks passed: {checks}/{checks}.");
return 0;

// =========================================================================================

/// <summary>An independent GlassSurfaceBase implementation used only to prove the port contract.</summary>
sealed class ProbeSurface : SharedPort
{
    public List<bool> RenderRequests { get; } = new();
    public List<(double Scale, Point Anchor)> FrameScales { get; } = new();

    public override CornerRadius CornerRadius { get; set; }

    public override void RequestRender(bool immediate) => RenderRequests.Add(immediate);

    public override void SetAnimationFrameScale(double scale, Point anchorRelativeToSurface)
        => FrameScales.Add((scale, anchorRelativeToSurface));
}

/// <summary>A derived shell: probes whether the type selector matches subclasses.</summary>
sealed class DerivedShellProbe : SharedShell { }

/// <summary>A real secondary panel window (the abstract base demands only the spawn center).</summary>
sealed class ProbePanelWindow : SecondaryPanelWindow
{
    public ProbePanelWindow()
    {
        SystemDecorations = SystemDecorations.None;
        ShowInTaskbar = false;
        Width = 420;
        Height = 320;
        Content = new LegacyShell { Title = "probe" };
        InitializePanel();
    }

    protected override Point? SpawnScreenCenter => null;
}

sealed class ProbeApp : Application
{
    public override void Initialize()
    {
        Styles.Add(new FluentTheme());
        // The shared shell styles (UI type) + the host shim (facade type) — exactly the two
        // sources an embedding app mounts.
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
