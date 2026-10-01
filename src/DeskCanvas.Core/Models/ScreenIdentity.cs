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
public record ScreenIdentity(
    string DeviceName,
    string FriendlyName,
    int Width,
    int Height,
    bool IsPrimary,
    double Scaling,
    string HardwareId = "")
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
    public bool IsAnonymous => HardwareId.Length == 0 && FriendlyName == FallbackName;

    /// <summary>
    /// Auto-match key: <c>"FriendlyName|WidthxHeight"</c> — the same shape as
    /// <see cref="ScreenLayout.Key"/>.
    /// </summary>
    public string Key => $"{FriendlyName}|{Width}x{Height}";
}

/// <summary>
/// Matches stored <see cref="ScreenLayout"/> entries to attached <see cref="ScreenIdentity"/>s.
/// Priority: ① manual rebinding (<see cref="ScreenLayout.DeviceName"/>), ② monitor hardware id
/// (<see cref="ScreenLayout.HardwareId"/>), ③ identity <see cref="ScreenLayout.Key"/>,
/// ④ the legacy "primary" entry (Key = null).
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
    /// the SAME key never matches the same entry twice. Matching priority:
    /// ① manual device binding ② monitor hardware id ③ identity key
    /// ④ legacy primary entry (key = null, follows the current primary screen).
    /// <para>
    /// An anonymous screen (no hardware id, no real name — the remote-tool virtual
    /// screen case) is matched by ① / ② only: its <see cref="ScreenIdentity.Key"/> is
    /// shared by every identity-less screen, so key-matching it would hand the stored
    /// configuration — and its widgets — to whichever virtual screen shows up next.
    /// </para>
    /// </summary>
    public static ScreenLayout? Match(IReadOnlyList<ScreenLayout> entries, ScreenIdentity screen, ISet<string>? consumedIds)
    {
        ScreenLayout? Find(Func<ScreenLayout, bool> predicate) =>
            entries.FirstOrDefault(entry => predicate(entry) && (consumedIds == null || !consumedIds.Contains(entry.Id)));

        if (!string.IsNullOrEmpty(screen.DeviceName))
        {
            var bound = Find(entry => entry.DeviceName == screen.DeviceName);
            if (bound != null)
            {
                consumedIds?.Add(bound.Id);
                return bound;
            }
        }

        if (!string.IsNullOrEmpty(screen.HardwareId))
        {
            var byHardware = Find(entry => entry.HardwareId == screen.HardwareId);
            if (byHardware != null)
            {
                consumedIds?.Add(byHardware.Id);
                return byHardware;
            }
        }

        if (!screen.IsAnonymous)
        {
            var byKey = Find(entry => entry.Key == screen.Key && entry.Key != null);
            if (byKey != null)
            {
                consumedIds?.Add(byKey.Id);
                return byKey;
            }

            // Fallback for upgrade from older builds where Key contained GPU adapter name instead of monitor friendly name
            var byResolution = Find(entry => entry.Key != null && entry.Key.EndsWith($"|{screen.Width}x{screen.Height}")
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

        // Legacy primary entry (Key = null) follows whichever screen is primary — but an
        // anonymous screen must not claim it either: a remote tool can mark its virtual
        // screen primary, and the legacy entry then would play its widgets there.
        if (screen.IsPrimary && !screen.IsAnonymous)
        {
            var legacy = Find(entry => entry.Key == null && entry.Id == ScreensLayout.LegacyPrimaryId);
            if (legacy != null)
            {
                consumedIds?.Add(legacy.Id);
                return legacy;
            }
        }

        return null;
    }
}