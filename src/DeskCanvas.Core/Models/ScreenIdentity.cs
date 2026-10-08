using System;
using System.Collections.Generic;
using System.Linq;

namespace DeskCanvas.Core.Models;

/// <summary>
/// Runtime identity of an attached display, captured from Windows (device name,
/// EDID friendly name) and Avalonia (working-area size, scaling, primary flag).
/// Used to match stored per-screen configurations (<see cref="ScreenLayout"/>).
/// </summary>
/// <param name="DeviceName">Windows device name (e.g. <c>"\\.\DISPLAY1"</c>).</param>
/// <param name="FriendlyName">EDID friendly name (e.g. <c>"DELL U2723QE"</c>).</param>
/// <param name="Width">Working-area width (physical pixels).</param>
/// <param name="Height">Working-area height (physical pixels).</param>
/// <param name="IsPrimary">True when this is the primary screen.</param>
/// <param name="Scaling">DPI scale of the screen.</param>
/// <param name="HardwareId">EDID hardware id of the monitor (e.g. <c>"DELA0D2"</c>), stable
/// across ports, adapters and driver reinstalls; empty when the monitor exposes none.</param>
/// <param name="MonitorId">Windows monitor instance/interface id, distinguishing monitors of the same model.</param>
/// <param name="PhysicalIdentity">Detailed physical monitor identity from CCD/SetupAPI, if resolved.</param>
/// <param name="BindingStatus">Binding status resolved for this screen.</param>
public record ScreenIdentity(
    string DeviceName,
    string FriendlyName,
    int Width,
    int Height,
    bool IsPrimary,
    double Scaling,
    string HardwareId = "",
    string MonitorId = "",
    PhysicalMonitorIdentity? PhysicalIdentity = null,
    BindingStatus BindingStatus = BindingStatus.Bound)
{
    /// <summary>Friendly name reported when no real monitor identity could be resolved
    /// (remote-tools virtual screens, identity-less indirect display drivers).</summary>
    public const string FallbackName = "Screen";

    /// <summary>
    /// True when the display exposed no stable identity at all: no EDID hardware id and no
    /// monitor/adapter name — only the generic fallback. Two unrelated screens (a remote
    /// tool's virtual screen today, a driver quirk tomorrow) then produce the same
    /// <see cref="Key"/>, so such a screen must never claim a stored configuration by key.
    /// </summary>
    public bool IsAnonymous => HardwareId.Length == 0 && MonitorId.Length == 0 && FriendlyName == FallbackName;

    /// <summary>
    /// Auto-match key: <c>"FriendlyName|WidthxHeight"</c> — the same shape as
    /// <see cref="ScreenLayout.Key"/>.
    /// </summary>
    public string Key => $"{FriendlyName}|{Width}x{Height}";

    /// <summary>
    /// Canonical PnP device instance id for this screen.
    /// </summary>
    public string PnpInstanceId => PhysicalIdentity?.DeviceInstanceId
        ?? PhysicalMonitorIdentity.NormalizeDeviceInstanceId(MonitorId);
}

/// <summary>
/// Result of matching a single screen against stored configurations.
/// </summary>
public sealed record ScreenMatch(
    ScreenIdentity Screen,
    ScreenLayout? Config,
    BindingStatus Status,
    string? MatchReason = null);

/// <summary>
/// Result of matching a whole collection of attached screens against stored configurations.
/// </summary>
public sealed record ScreenBatchResult(
    IReadOnlyList<ScreenMatch> Matches,
    IReadOnlyList<ScreenLayout> UnmatchedConfigs);

/// <summary>
/// Matches stored <see cref="ScreenLayout"/> entries to attached <see cref="ScreenIdentity"/>s.
/// Priority: ① Exact instance / PnP identity (<see cref="ScreenLayout.Binding"/> or <see cref="ScreenLayout.MonitorId"/>),
/// ② Verified serial number, ③ manual rebinding (<see cref="ScreenLayout.DeviceName"/> with no hardware conflict),
/// ④ monitor hardware id (<see cref="ScreenLayout.HardwareId"/>), ⑤ identity <see cref="ScreenLayout.Key"/>,
/// ⑥ legacy "primary" entry (Key = null).
/// </summary>
public static class ScreenMatcher
{
    /// <summary>
    /// Find the stored configuration for an attached screen (non-consuming; each
    /// entry may match several screens — use the consuming overload when matching
    /// a whole desktop so twin screens with identical keys do not share an entry).
    /// </summary>
    /// <returns>The matched entry, or <c>null</c> when the screen has no stored configuration.</returns>
    public static ScreenLayout? Match(IReadOnlyList<ScreenLayout> entries, ScreenIdentity screen) =>
        Match(entries, screen, null);

    /// <summary>
    /// Find the stored configuration for an attached screen, consuming the entry
    /// (its id is added to <paramref name="consumedIds"/>) so a twin screen with
    /// the SAME key never matches the same entry twice.
    /// </summary>
    public static ScreenLayout? Match(IReadOnlyList<ScreenLayout> entries, ScreenIdentity screen, ISet<string>? consumedIds,
        bool allowHardwareFallback = true, bool allowLegacyTwinUpgrade = false)
    {
        ScreenLayout? Find(Func<ScreenLayout, bool> predicate) =>
            entries.FirstOrDefault(entry => predicate(entry) && (consumedIds == null || !consumedIds.Contains(entry.Id)));

        // 1. Exact PnP instance / interface match (highest priority, immune to port/GDI shifts)
        var pnpId = screen.PnpInstanceId;
        if (!string.IsNullOrEmpty(pnpId) || !string.IsNullOrEmpty(screen.MonitorId))
        {
            var byInstance = Find(entry =>
            {
                if (entry.Binding != null && entry.Binding.MatchesInstance(pnpId))
                    return true;
                if (!string.IsNullOrEmpty(entry.MonitorId))
                {
                    if (string.Equals(entry.MonitorId, screen.MonitorId, StringComparison.OrdinalIgnoreCase))
                        return true;
                    var norm = PhysicalMonitorIdentity.NormalizeDeviceInstanceId(entry.MonitorId);
                    if (!string.IsNullOrEmpty(norm) && string.Equals(norm, pnpId, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
                return false;
            });

            if (byInstance != null && !HasHardwareConflict(byInstance, screen))
            {
                consumedIds?.Add(byInstance.Id);
                return byInstance;
            }
        }

        // 2. Verified serial number match (valid EDID serial number + matching model)
        if (screen.PhysicalIdentity?.SerialNumber is { } serial && PhysicalMonitorIdentity.IsValidSerialNumber(serial))
        {
            var bySerial = Find(entry =>
                (entry.Binding?.SerialNumber == serial || entry.Binding?.MatchesInstance(pnpId) == true)
                && string.Equals(entry.HardwareId, screen.HardwareId, StringComparison.OrdinalIgnoreCase));
            if (bySerial != null)
            {
                consumedIds?.Add(bySerial.Id);
                return bySerial;
            }
        }

        // 3. Manual device binding (DeviceName e.g. \\.\DISPLAY1)
        // CRITICAL GUARD: Only match if the target has no hardware identity conflict.
        // A notebook external screen becoming DISPLAY1 must NEVER adopt an internal screen's configuration!
        if (!string.IsNullOrEmpty(screen.DeviceName))
        {
            var bound = Find(entry => entry.DeviceName == screen.DeviceName);
            if (bound != null && !HasHardwareConflict(bound, screen))
            {
                consumedIds?.Add(bound.Id);
                return bound;
            }
        }

        // 4. Model / HardwareId fallback
        if (!string.IsNullOrEmpty(screen.HardwareId))
        {
            var hardwareEntries = entries.Where(entry => string.Equals(entry.HardwareId, screen.HardwareId,
                StringComparison.OrdinalIgnoreCase)).ToList();

            var byHardware = allowHardwareFallback && hardwareEntries.Count == 1
                ? Find(entry => entry.Id == hardwareEntries[0].Id && !HasHardwareConflict(entry, screen))
                : null;

            if (byHardware == null && allowLegacyTwinUpgrade)
            {
                byHardware = Find(entry => string.IsNullOrEmpty(entry.MonitorId) && entry.Binding == null
                    && string.Equals(entry.HardwareId, screen.HardwareId, StringComparison.OrdinalIgnoreCase));
            }

            if (byHardware != null)
            {
                consumedIds?.Add(byHardware.Id);
                return byHardware;
            }
        }

        // 5. Unpinned identity key (FriendlyName|WidthxHeight)
        if (!screen.IsAnonymous)
        {
            bool Unpinned(ScreenLayout entry) => string.IsNullOrEmpty(entry.HardwareId)
                && string.IsNullOrEmpty(entry.MonitorId) && entry.Binding == null;

            var byKey = Find(entry => Unpinned(entry) && entry.Key == screen.Key && entry.Key != null);
            if (byKey != null)
            {
                consumedIds?.Add(byKey.Id);
                return byKey;
            }

            // Fallback for upgrade from older builds where Key contained GPU adapter name
            var byResolution = Find(entry => Unpinned(entry) && entry.Key != null && entry.Key.EndsWith($"|{screen.Width}x{screen.Height}")
                && (entry.Key.Contains("GeForce", StringComparison.OrdinalIgnoreCase)
                    || entry.Key.Contains("Radeon", StringComparison.OrdinalIgnoreCase)
                    || entry.Key.Contains("Intel", StringComparison.OrdinalIgnoreCase)
                    || entry.Key.Contains("Graphics", StringComparison.OrdinalIgnoreCase)
                    || entry.Key.Contains("Virtual Display Adapter", StringComparison.OrdinalIgnoreCase)));
            if (byResolution != null)
            {
                consumedIds?.Add(byResolution.Id);
                return byResolution;
            }
        }

        // 6. Legacy primary entry (Key = null)
        if (screen.IsPrimary && !screen.IsAnonymous)
        {
            var legacy = Find(entry => entry.Key == null && entry.Id == ScreensLayout.LegacyPrimaryId
                && string.IsNullOrEmpty(entry.HardwareId) && string.IsNullOrEmpty(entry.MonitorId) && entry.Binding == null);
            if (legacy != null)
            {
                consumedIds?.Add(legacy.Id);
                return legacy;
            }
        }

        return null;
    }

    /// <summary>
    /// Check whether a configuration entry has a direct hardware or instance conflict with a candidate screen.
    /// </summary>
    public static bool HasHardwareConflict(ScreenLayout entry, ScreenIdentity screen)
    {
        // Different non-empty HardwareId (e.g. SAC2463 vs BOE0C8E) is always an absolute conflict
        if (!string.IsNullOrEmpty(entry.HardwareId) && !string.IsNullOrEmpty(screen.HardwareId))
        {
            if (!string.Equals(entry.HardwareId, screen.HardwareId, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Match a whole collection of attached screens against stored configurations in a single coherent transaction.
    /// Prevents order-dependent claiming and flags unconfigured or conflicting screens.
    /// </summary>
    public static ScreenBatchResult MatchBatch(IReadOnlyList<ScreenLayout> entries, IReadOnlyList<ScreenIdentity> screens)
    {
        var consumed = new HashSet<string>();
        var matches = new List<ScreenMatch>();

        foreach (var screen in screens)
        {
            var config = Match(entries, screen, consumed, allowHardwareFallback: false, allowLegacyTwinUpgrade: false);
            if (config != null)
            {
                matches.Add(new ScreenMatch(screen, config, BindingStatus.Bound, "ExactInstanceMatch"));
            }
            else if (screen.IsAnonymous)
            {
                matches.Add(new ScreenMatch(screen, null, BindingStatus.Unresolved, "AnonymousScreen"));
            }
            else
            {
                matches.Add(new ScreenMatch(screen, null, BindingStatus.Unconfigured, "NoMatchingConfig"));
            }
        }

        var matchedIds = matches.Where(m => m.Config != null).Select(m => m.Config!.Id).ToHashSet();
        var unmatchedConfigs = entries.Where(e => !matchedIds.Contains(e.Id)).ToList();

        return new ScreenBatchResult(matches, unmatchedConfigs);
    }
}
