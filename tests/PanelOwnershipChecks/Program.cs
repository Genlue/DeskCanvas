// Panel ownership checks (P6 slice 1 — the PanelService identity model).
//
// Run: dotnet run --project tests/PanelOwnershipChecks
// (also picked up by scripts/verify.ps1 like the other *Checks suites)
//
// Uses Avalonia.Headless with a minimal SecondaryPanelWindow probe (no shell, no card) to pin
// the PanelService contract: process-singleton vs per-owner identities, cooldown, owner-window
// lifetime coupling, display-change dismissal, the legacy helper compatibility and the
// factory-failure cleanup. Every case starts from a clean service state (ResetForChecks,
// reached through reflection — the fixture assembly has no InternalsVisibleTo).
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using DeskCanvas.Contracts;
using DeskCanvas.Services;
using DeskCanvas.Views;

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
void Reset() =>
    typeof(PanelService).GetMethod("ResetForChecks", BindingFlags.NonPublic | BindingFlags.Static)!
        .Invoke(null, null);

// =========================================================================================
// 1. ProcessSingleton scope: open → toggle → cooldown → reopen
// =========================================================================================
Console.WriteLine("--- ProcessSingleton scope ---");
Reset();
var d1 = PanelService.Open(new PanelRequest(default, null, "dice", PanelScope.ProcessSingleton, () => new ProbePanel()));
Check(d1 == PanelOpenDecision.Opened, "1.1 process-singleton first open returns Opened");
var d2 = PanelService.Open(new PanelRequest(default, null, "dice", PanelScope.ProcessSingleton, () => new ProbePanel()));
Check(d2 == PanelOpenDecision.ToggledClosed, "1.2 second open on the same identity toggles the live panel closed");
Flush();
Check(PanelService.IsCoolingDown("process|dice", 60), "1.3 the toggled identity is cooling down after the close");
System.Threading.Thread.Sleep(80);
Check(!PanelService.IsCoolingDown("process|dice", 60), "1.4 the cooldown expires");
var d3 = PanelService.Open(new PanelRequest(default, null, "dice", PanelScope.ProcessSingleton, () => new ProbePanel(), CooldownMs: 60));
Check(d3 == PanelOpenDecision.Opened, "1.5 the identity can be reopened after the cooldown");
var d4 = PanelService.Open(new PanelRequest(default, null, "other", PanelScope.ProcessSingleton, () => new ProbePanel(), CooldownMs: 60));
Check(d4 == PanelOpenDecision.Opened, "1.6 a different PanelKey opens independently of the cooling one");

// =========================================================================================
// 2. PerOwner scope: two owners, same PanelKey — independent panels
// =========================================================================================
Console.WriteLine("--- PerOwner scope ---");
Reset();
var ownerA = NewOwnerWindow();
var ownerB = NewOwnerWindow();
ownerA.Show();
ownerB.Show();
Flush();
var idA = new WidgetInstanceId(Guid.NewGuid());
var idB = new WidgetInstanceId(Guid.NewGuid());
var dA1 = PanelService.Open(new PanelRequest(idA, ownerA, "weather", PanelScope.PerOwner, () => new ProbePanel(), CooldownMs: 0));
var dB1 = PanelService.Open(new PanelRequest(idB, ownerB, "weather", PanelScope.PerOwner, () => new ProbePanel(), CooldownMs: 0));
Check(dA1 == PanelOpenDecision.Opened && dB1 == PanelOpenDecision.Opened,
    "2.1 two owners with the same PanelKey each open their own panel");
var dA2 = PanelService.Open(new PanelRequest(idA, ownerA, "weather", PanelScope.PerOwner, () => new ProbePanel(), CooldownMs: 0));
Check(dA2 == PanelOpenDecision.ToggledClosed, "2.2 re-opening the same owner toggles only its own panel closed");
Flush();
Check(PanelService.IsCoolingDown("owner:" + idA.Value + "|weather", 10_000),
    "2.3 owner A's identity is cooling down after its toggle");
Check(!PanelService.IsCoolingDown("owner:" + idB.Value + "|weather", 10_000),
    "2.4 owner B's identity did not inherit owner A's cooldown");
var dB2 = PanelService.Open(new PanelRequest(idB, ownerB, "weather", PanelScope.PerOwner, () => new ProbePanel(), CooldownMs: 0));
Check(dB2 == PanelOpenDecision.ToggledClosed,
    "2.5 the other owner's panel was unaffected by A's toggle (still live, now toggles closed)");
Flush();
var dF1 = PanelService.Open(new PanelRequest(default, ownerA, "orphan", PanelScope.PerOwner, () => new ProbePanel(), CooldownMs: 0));
Check(dF1 == PanelOpenDecision.Opened, "2.6 an owner without an instance id falls back to the window-hash key");
var dF2 = PanelService.Open(new PanelRequest(default, ownerA, "orphan", PanelScope.PerOwner, () => new ProbePanel(), CooldownMs: 0));
Check(dF2 == PanelOpenDecision.ToggledClosed, "2.7 the window-hash fallback key still enforces single-instance per owner window");

// =========================================================================================
// 3. Cooldown: close → immediate reopen refused → expired reopen accepted
// =========================================================================================
Console.WriteLine("--- Cooldown guard ---");
Reset();
ProbePanel? captured = null;
var idC = new WidgetInstanceId(Guid.NewGuid());
PanelRequest CooledRequest() => new(idC, ownerA, "cd", PanelScope.PerOwner, () => captured = new ProbePanel(), CooldownMs: 80);
Check(PanelService.Open(CooledRequest()) == PanelOpenDecision.Opened, "3.1 the request opens");
Flush();
captured!.Close();
Flush();
Check(PanelService.IsCoolingDown("owner:" + idC.Value + "|cd", 80), "3.2 immediately after a close the identity is cooling down");
Check(PanelService.Open(CooledRequest()) == PanelOpenDecision.CoolingDown, "3.3 open during the cooldown returns CoolingDown (no new panel)");
System.Threading.Thread.Sleep(100);
Check(!PanelService.IsCoolingDown("owner:" + idC.Value + "|cd", 80), "3.4 the cooldown expires");
Check(PanelService.Open(CooledRequest()) == PanelOpenDecision.Opened, "3.5 open after the cooldown returns Opened");

// =========================================================================================
// 4. Owner-window lifetime: the panel dies with its owner
// =========================================================================================
Console.WriteLine("--- Owner-window lifetime coupling ---");
Reset();
var ownerC = NewOwnerWindow();
ownerC.Show();
Flush();
var idD = new WidgetInstanceId(Guid.NewGuid());
Check(PanelService.Open(new PanelRequest(idD, ownerC, "bound", PanelScope.PerOwner, () => new ProbePanel(), CooldownMs: 0))
        == PanelOpenDecision.Opened, "4.1 the owner-bound panel opens");
Flush();
ownerC.Close();
Flush();
Check(PanelService.IsCoolingDown("owner:" + idD.Value + "|bound", 10_000),
    "4.2 closing the owner window closed the panel bound to it (close recorded through its own Closed)");

// =========================================================================================
// 5. CloseForDisplayChange dismisses every live panel
// =========================================================================================
Console.WriteLine("--- Display-change dismissal ---");
Reset();
var idE1 = new WidgetInstanceId(Guid.NewGuid());
var idE2 = new WidgetInstanceId(Guid.NewGuid());
PanelService.Open(new PanelRequest(idE1, ownerA, "dc1", PanelScope.PerOwner, () => new ProbePanel(), CooldownMs: 0));
PanelService.Open(new PanelRequest(idE2, ownerB, "dc2", PanelScope.PerOwner, () => new ProbePanel(), CooldownMs: 0));
Flush();
SecondaryPanelWindow.CloseForDisplayChange();
Flush();
Check(PanelService.IsCoolingDown("owner:" + idE1.Value + "|dc1", 10_000), "5.1 the first panel was closed by the display-change pass");
Check(PanelService.IsCoolingDown("owner:" + idE2.Value + "|dc2", 10_000), "5.2 the second panel was closed too");

// =========================================================================================
// 6. Legacy helper compatibility: the 'process|Type' key, bit-identical
// =========================================================================================
Console.WriteLine("--- Legacy helper compatibility ---");
Reset();
Check(!LegacyProbePanel.TryCloseActive(), "6.1 legacy TryCloseActivePanel with no live instance returns false");
Check(!LegacyProbePanel.CoolingDown(), "6.2 legacy PanelCoolingDown with no close history returns false");
var legacy = new LegacyProbePanel();
legacy.ShowViaLegacyPath(null);
Flush();
Check(LegacyProbePanel.TryCloseActive(), "6.3 legacy TryCloseActivePanel closes the live instance and reports true");
Flush();
Check(LegacyProbePanel.CoolingDown(), "6.4 legacy PanelCoolingDown reports the cooldown right after the close");
Check(PanelService.IsCoolingDown("process|" + typeof(LegacyProbePanel).FullName!, 10_000),
    "6.5 the legacy helpers resolve to the same 'process|Type' key the service registers at construction");
System.Threading.Thread.Sleep(300);
Check(!LegacyProbePanel.CoolingDown(), "6.6 the legacy cooldown expires (default 250ms)");

// =========================================================================================
// 7. Factory failure: the exception propagates and no ghost residue remains
// =========================================================================================
Console.WriteLine("--- Factory failure cleanup ---");
Reset();
var threw = false;
try
{
    PanelService.Open(new PanelRequest(default, null, "boom", PanelScope.ProcessSingleton, () =>
    {
        var ghost = new LegacyProbePanel(); // the constructor self-registers under the legacy type key
        throw new InvalidOperationException("factory failure");
    }));
}
catch (InvalidOperationException)
{
    threw = true;
}
Check(threw, "7.1 the factory's exception propagates out of Open");
Check(!LegacyProbePanel.TryCloseActive(), "7.2 the failed construction left no active-panel residue (the ghost was dropped)");
Check(!LegacyProbePanel.CoolingDown(10_000), "7.3 the failed construction recorded no cooldown");

// =========================================================================================
// 8. AbandonPanel compatibility: never-shown instances are dropped without a cooldown
// =========================================================================================
Console.WriteLine("--- AbandonPanel compatibility ---");
Reset();
var ghost = new LegacyProbePanel(); // registered by its constructor, never shown
LegacyProbePanel.Abandon(ghost);
Check(!LegacyProbePanel.TryCloseActive(), "8.1 AbandonPanel removes the never-shown instance's registration");
Check(!LegacyProbePanel.CoolingDown(10_000), "8.2 AbandonPanel does not start a cooldown");
var live = new LegacyProbePanel();
live.ShowViaLegacyPath(null);
Flush();
var stranger = new LegacyProbePanel(); // overwrites the legacy key, exactly like the old static dictionary did
Check(LegacyProbePanel.AbandonAndReport(live),
    "8.3 abandoning one instance leaves the key's current owner intact (the legacy toggle still finds it)");

// =========================================================================================
Console.WriteLine();
if (failures.Count > 0)
{
    Console.WriteLine($"FAILED {failures.Count} of {checks} checks:");
    foreach (var failure in failures) Console.WriteLine(" - " + failure);
    return 1;
}
Console.WriteLine($"Panel ownership checks passed: {checks}/{checks}.");
return 0;

// =========================================================================================

Window NewOwnerWindow() => new()
{
    Width = 100,
    Height = 80,
    SystemDecorations = SystemDecorations.None,
    ShowInTaskbar = false
};

/// <summary>A real secondary panel window (the abstract base demands only the spawn center).</summary>
class ProbePanel : SecondaryPanelWindow
{
    public ProbePanel()
    {
        SystemDecorations = SystemDecorations.None;
        ShowInTaskbar = false;
        Width = 320;
        Height = 240;
        Content = new Grid();
        InitializePanel();
    }

    protected override Point? SpawnScreenCenter => null;

    // Sibling activations during the checks must never auto-close a probe (the checks open
    // several panels in a row).
    protected override int DeactivateCloseGraceMs => int.MaxValue;
}

/// <summary>Exposes the legacy protected static helpers for the compatibility checks.</summary>
sealed class LegacyProbePanel : ProbePanel
{
    public static bool CoolingDown(int cooldownMs = 250) => PanelCoolingDown<LegacyProbePanel>(cooldownMs);
    public static bool TryCloseActive() => TryCloseActivePanel<LegacyProbePanel>();
    public static void Abandon(SecondaryPanelWindow panel) => AbandonPanel(panel);
    public static bool AbandonAndReport(SecondaryPanelWindow panel)
    {
        AbandonPanel(panel);
        // Report whether the instance is still reachable through the legacy toggle path.
        return TryCloseActivePanel<LegacyProbePanel>();
    }
    public void ShowViaLegacyPath(Window? owner) => ShowAsSecondaryPanel(owner);
}

sealed class ProbeApp : Application
{
    public override void Initialize()
    {
        Styles.Add(new FluentTheme());
        // The same style sources PanelShellAvaloniaChecks mounts: the shell styles keep every
        // resolver path exercised by the panel base class resolvable in headless runs.
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
