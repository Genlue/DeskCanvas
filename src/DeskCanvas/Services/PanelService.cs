using System;
using System.Collections.Generic;
using Avalonia.Controls;
using DeskCanvas.Contracts;
using DeskCanvas.Views;

namespace DeskCanvas.Services;

/// <summary>How a panel's single-instance identity is scoped.</summary>
public enum PanelScope
{
    /// <summary>One live panel per owner component instance: two widgets of the same kind
    /// each keep their own panel (the weather panels' rule since P6).</summary>
    PerOwner,

    /// <summary>One live panel per process, replacing the legacy per-CLR-type key with an
    /// explicit, stable name (the dice panel keeps its historic singleton this way).</summary>
    ProcessSingleton
}

/// <summary>A single open request for a secondary panel, resolved by <see cref="PanelService"/>.</summary>
/// <param name="OwnerInstanceId">Stable identity of the owning widget component. Empty falls
/// back to the owner window's hash for the identity key.</param>
/// <param name="OwnerWindow">Window the panel is shown (and pinned above the widget band)
/// against; may be null.</param>
/// <param name="PanelKey">Stable panel name inside its scope (e.g. "weather"). For the process
/// scope the value must keep the semantics of the legacy per-type key.</param>
/// <param name="Scope">How the panel's single-instance identity is scoped.</param>
/// <param name="Factory">Builds the panel window; called at most once per successful open.</param>
/// <param name="CooldownMs">Re-open flicker guard applied to the identity key after a close.</param>
public sealed record PanelRequest(
    WidgetInstanceId OwnerInstanceId,
    Window? OwnerWindow,
    string PanelKey,
    PanelScope Scope,
    Func<SecondaryPanelWindow> Factory,
    int CooldownMs = 250);

/// <summary>What <see cref="PanelService.Open"/> decided for a request.</summary>
public enum PanelOpenDecision
{
    /// <summary>A new panel was created and shown.</summary>
    Opened,

    /// <summary>The identity already had a live panel; it was closed (animated) instead.</summary>
    ToggledClosed,

    /// <summary>The identity is inside its post-close cooldown; nothing was opened.</summary>
    CoolingDown
}

/// <summary>
/// Owns the secondary panels' single-instance state: which panels are alive, under which
/// identity (per owner component or process-wide), and how long ago each identity was closed.
/// <para>
/// Panels self-register at construction (under their legacy process/type key, so every
/// pre-service consumer keeps working bit-identically) and unregister through their own
/// <c>Closed</c> event. <see cref="Open"/> rebinds a freshly constructed panel to the
/// request's identity before showing it, so one service call covers the whole historical
/// <c>PanelCoolingDown / TryCloseActivePanel / new / ShowAsSecondaryPanel</c> pattern.
/// </para>
/// </summary>
/// <remarks>
/// One lock (the WidgetZOrder gate pattern) guards all state. The factory and the actual
/// show run outside the lock — they build and realize Avalonia windows on the UI thread,
/// exactly as the unsynchronized pre-service code did.
/// </remarks>
public static class PanelService
{
    private const string PerOwnerPrefix = "owner:";
    private const string WindowFallbackPrefix = "win:";
    private const string ProcessScopeKey = "process";
    private const string KeySeparator = "|";

    private sealed record PanelRegistration(
        string IdentityKey,
        string ScopeKey,
        SecondaryPanelWindow Panel,
        Window? OwnerWindow,
        EventHandler? OwnerClosedHandler);

    private static readonly object gate = new();
    private static readonly Dictionary<string, SecondaryPanelWindow> activePanels = new();
    private static readonly Dictionary<string, DateTime> lastCloseTimes = new();
    private static readonly List<PanelRegistration> registrations = [];

    /// <summary>The panel most recently self-registered (UI-thread watermark). Lets
    /// <see cref="Open"/> drop the registration of a factory whose constructor threw — a
    /// window that never loaded never fires <c>Closed</c>, so nothing else would.</summary>
    private static SecondaryPanelWindow? lastRegisteredPanel;

    // ---- identity keys ---------------------------------------------------------------

    /// <summary>The pre-service key: the panel's concrete CLR type inside the process scope.
    /// Panels register under it at construction; the legacy
    /// <c>PanelCoolingDown&lt;T&gt;</c>/<c>TryCloseActivePanel&lt;T&gt;</c> helpers resolve the
    /// same key, which keeps their behaviour bit-identical.</summary>
    internal static string LegacyKeyFor(Type panelType) => ProcessScopeKey + KeySeparator + panelType.FullName;

    private static (string ScopeKey, string IdentityKey) BuildKeys(in PanelRequest request)
    {
        var scopeKey = request.Scope == PanelScope.ProcessSingleton
            ? ProcessScopeKey
            : OwnerScopeKey(request.OwnerInstanceId, request.OwnerWindow);
        return (scopeKey, scopeKey + KeySeparator + request.PanelKey);
    }

    private static string OwnerScopeKey(WidgetInstanceId ownerInstanceId, Window? ownerWindow) =>
        ownerInstanceId.IsEmpty
            ? WindowFallbackPrefix + (ownerWindow?.GetHashCode() ?? 0)
            : PerOwnerPrefix + ownerInstanceId.Value;

    // ---- open / toggle ----------------------------------------------------------------

    /// <summary>
    /// Open the panel described by <paramref name="request"/>: cooling-down identities are
    /// refused, a live panel for the same identity is toggled closed (animated), and only
    /// otherwise a new panel is built, bound to the identity and shown pinned above the
    /// widget band.
    /// </summary>
    public static PanelOpenDecision Open(in PanelRequest request)
    {
        var (scopeKey, identityKey) = BuildKeys(request);

        lock (gate)
        {
            if (IsCoolingDownLocked(identityKey, request.CooldownMs))
                return PanelOpenDecision.CoolingDown;

            if (activePanels.TryGetValue(identityKey, out var active))
            {
                // Toggle path: the panel's own Close plays the exit animation; unregistering
                // and the cooldown recording happen when Closed actually fires.
                try { active.Close(); }
                catch { /* A panel already closing must not break the toggle. */ }
                return PanelOpenDecision.ToggledClosed;
            }
        }

        // The factory runs outside the lock (it builds Avalonia windows). Panel construction
        // self-registers under the legacy type key; the watermark captured below lets a
        // throwing constructor's ghost registration be dropped.
        var pendingBefore = lastRegisteredPanel;
        SecondaryPanelWindow panel;
        try
        {
            panel = request.Factory();
        }
        catch
        {
            DropGhostRegistration(pendingBefore);
            throw;
        }

        lock (gate)
        {
            // Rebind: move the freshly constructed panel from its construction-time legacy
            // key to the request's identity, and wire the owner-closed guard before the
            // panel becomes visible.
            UnregisterLocked(panel, recordClose: false);

            EventHandler? ownerClosedHandler = null;
            if (request.Scope == PanelScope.PerOwner && request.OwnerWindow != null)
            {
                var ownerWindow = request.OwnerWindow;
                ownerClosedHandler = (_, _) => CloseForOwner(ownerWindow);
                request.OwnerWindow.Closed += ownerClosedHandler;
            }

            registrations.Add(new PanelRegistration(identityKey, scopeKey, panel, request.OwnerWindow, ownerClosedHandler));
            activePanels[identityKey] = panel;
        }

        try
        {
            panel.ShowAsSecondaryPanel(request.OwnerWindow);
        }
        catch
        {
            // The panel was never really shown; drop its registration so it can neither
            // block the next open nor start a cooldown (the legacy AbandonPanel contract).
            AbandonPanel(panel);
            throw;
        }

        return PanelOpenDecision.Opened;
    }

    // ---- owner-scoped closes -----------------------------------------------------------

    /// <summary>Close every live panel bound to the owner component with this identity (animated).</summary>
    public static void CloseForOwner(WidgetInstanceId instanceId)
    {
        if (instanceId.IsEmpty) return;
        CloseForScopeKey(PerOwnerPrefix + instanceId.Value);
    }

    /// <summary>Close every live panel shown against this owner window (animated). Resolves
    /// to the same registrations the identity-based overload closes.</summary>
    public static void CloseForOwner(Window ownerWindow)
    {
        List<SecondaryPanelWindow> targets;
        lock (gate)
        {
            targets = [];
            foreach (var registration in registrations)
                if (ReferenceEquals(registration.OwnerWindow, ownerWindow))
                    targets.Add(registration.Panel);
        }
        ClosePanels(targets);
    }

    private static void CloseForScopeKey(string scopeKey)
    {
        List<SecondaryPanelWindow> targets;
        lock (gate)
        {
            targets = [];
            foreach (var registration in registrations)
                if (registration.ScopeKey == scopeKey)
                    targets.Add(registration.Panel);
        }
        ClosePanels(targets);
    }

    /// <summary>Animated close for live owner-bound panels; a panel already closing is a no-op in its own guard.</summary>
    private static void ClosePanels(List<SecondaryPanelWindow> panels)
    {
        foreach (var panel in panels)
        {
            try { panel.Close(); }
            catch { /* A panel already closing must not interrupt the pass. */ }
        }
    }

    // ---- display changes -----------------------------------------------------------------

    /// <summary>Dismiss every live panel before Windows relocates windows across displays.
    /// Hides first and closes through the base Window path (no exit animation), exactly like
    /// the pre-service <c>SecondaryPanelWindow.CloseForDisplayChange</c>.</summary>
    public static void CloseForDisplayChange()
    {
        List<SecondaryPanelWindow> panels;
        lock (gate)
        {
            panels = new List<SecondaryPanelWindow>(activePanels.Values);
        }
        foreach (var panel in panels)
        {
            try
            {
                panel.Hide();
                ((Window)panel).Close();
            }
            catch { /* Panels may already be closing because they lost focus. */ }
        }
    }

    // ---- cooldown bookkeeping --------------------------------------------------------------

    /// <summary>True within <paramref name="cooldownMs"/> after <paramref name="identityKey"/> was last closed (re-open flicker guard).</summary>
    public static bool IsCoolingDown(string identityKey, int cooldownMs)
    {
        lock (gate) return IsCoolingDownLocked(identityKey, cooldownMs);
    }

    /// <summary>Record a close of <paramref name="identityKey"/> right now (starts its cooldown window).</summary>
    public static void RecordClosed(string identityKey)
    {
        lock (gate) lastCloseTimes[identityKey] = DateTime.UtcNow;
    }

    private static bool IsCoolingDownLocked(string identityKey, int cooldownMs) =>
        lastCloseTimes.TryGetValue(identityKey, out var closedAt)
        && (DateTime.UtcNow - closedAt).TotalMilliseconds < cooldownMs;

    // ---- panel lifecycle (called by SecondaryPanelWindow) -----------------------------------

    /// <summary>Register a panel that just constructed itself: under its legacy process/type
    /// key, so pre-service consumers observe the exact state the old static dictionary held.
    /// Idempotent per panel instance.</summary>
    internal static void RegisterPanel(SecondaryPanelWindow panel)
    {
        lock (gate)
        {
            foreach (var registration in registrations)
                if (ReferenceEquals(registration.Panel, panel))
                    return;

            var legacyKey = LegacyKeyFor(panel.GetType());
            registrations.Add(new PanelRegistration(legacyKey, ProcessScopeKey, panel, null, null));
            activePanels[legacyKey] = panel;
            lastRegisteredPanel = panel;
        }
    }

    /// <summary>The panel's window really closed: unregister and start its identity's cooldown.</summary>
    internal static void UnregisterPanel(SecondaryPanelWindow panel)
    {
        lock (gate) UnregisterLocked(panel, recordClose: true);
    }

    /// <summary>Drop a panel whose construction/show failed before it was ever live. Never
    /// starts a cooldown — that is what distinguishes it from a real close.</summary>
    internal static void AbandonPanel(SecondaryPanelWindow panel)
    {
        lock (gate) UnregisterLocked(panel, recordClose: false);
    }

    /// <summary>Legacy toggle helper: close the live panel registered under the type's
    /// process/type key. Returns false when there is none.</summary>
    internal static bool TryCloseFor(Type panelType)
    {
        SecondaryPanelWindow? panel;
        lock (gate)
        {
            if (!activePanels.TryGetValue(LegacyKeyFor(panelType), out panel)) return false;
        }
        try { panel.Close(); }
        catch { }
        return true;
    }

    /// <summary>Legacy cooldown helper resolved through the type's process/type key.</summary>
    internal static bool IsCoolingDownFor(Type panelType, int cooldownMs)
        => IsCoolingDown(LegacyKeyFor(panelType), cooldownMs);

    /// <summary>Remove a panel's registration, its owner-window guard and its active entry.</summary>
    private static void UnregisterLocked(SecondaryPanelWindow panel, bool recordClose)
    {
        var index = -1;
        PanelRegistration? found = null;
        for (var i = 0; i < registrations.Count; i++)
        {
            if (ReferenceEquals(registrations[i].Panel, panel))
            {
                index = i;
                found = registrations[i];
                break;
            }
        }
        if (found == null) return;

        registrations.RemoveAt(index);
        if (found.OwnerClosedHandler != null && found.OwnerWindow != null)
        {
            try { found.OwnerWindow.Closed -= found.OwnerClosedHandler; }
            catch { /* The owner window is already gone. */ }
        }
        if (activePanels.TryGetValue(found.IdentityKey, out var current) && ReferenceEquals(current, panel))
            activePanels.Remove(found.IdentityKey);
        if (recordClose)
            lastCloseTimes[found.IdentityKey] = DateTime.UtcNow;
    }

    /// <summary>Drop the ghost a throwing factory left behind: the newest self-registration
    /// (the watermark moved during the failed factory call) is removed without recording a
    /// close. UI-thread only, like everything that touches the watermark.</summary>
    private static void DropGhostRegistration(SecondaryPanelWindow? pendingBefore)
    {
        lock (gate)
        {
            if (lastRegisteredPanel is { } ghost && !ReferenceEquals(ghost, pendingBefore))
            {
                UnregisterLocked(ghost, recordClose: false);
                lastRegisteredPanel = pendingBefore;
            }
        }
    }

    /// <summary>Clear every piece of service state for fixture isolation (checks only — production never calls this).</summary>
    internal static void ResetForChecks()
    {
        lock (gate)
        {
            foreach (var registration in registrations)
            {
                if (registration.OwnerClosedHandler == null || registration.OwnerWindow == null) continue;
                try { registration.OwnerWindow.Closed -= registration.OwnerClosedHandler; }
                catch { }
            }
            registrations.Clear();
            activePanels.Clear();
            lastCloseTimes.Clear();
            lastRegisteredPanel = null;
        }
    }
}
