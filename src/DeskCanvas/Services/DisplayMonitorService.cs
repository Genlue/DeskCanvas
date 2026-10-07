using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using Avalonia.Threading;
using DeskCanvas.Core.Interfaces;
using DeskCanvas.Core.Models;

namespace DeskCanvas.Services;

/// <summary>
/// An attached screen: the Avalonia screen geometry, the Windows identity
/// (device name + EDID friendly name) and the matched stored configuration.
/// </summary>
/// <param name="Screen">Avalonia screen (bounds / working area / scaling / primary flag).</param>
/// <param name="Identity">Windows identity used for matching (device name + friendly name + size).</param>
/// <param name="Config">The stored per-screen configuration matched to this screen; <c>null</c> = new screen with no configuration yet.</param>
public sealed record AttachedScreen(Screen Screen, ScreenIdentity Identity, ScreenLayout? Config);

/// <summary>
/// Tracks the attached displays, builds each screen's Windows identity (device
/// name + EDID friendly name + resolution) and matches it to the stored
/// per-screen configurations (<see cref="ScreenMatcher"/>).
/// <para>
/// Display changes (plug / unplug / resolution / DPI / arrangement) are detected
/// by polling the screen signature and raising <see cref="ScreensChanged"/> so the
/// widget factory can hide widgets of removed screens and recreate them when a
/// screen comes back.
/// </para>
/// </summary>
public class DisplayMonitorService(ILayoutProvider layoutProvider)
{
    private Window? anchor;
    private DispatcherTimer? poller;
    private string lastSignature = "";
    private string committedTopology = "";
    private string pendingTopology = "";
    private DateTime pendingSince;
    private bool refreshing;
    private DateTime notificationSettleUntil;

    /// <summary>True while Windows and Avalonia are settling a display change.</summary>
    public bool IsTopologyChanging { get; private set; }

    /// <summary>Raised before publishing a changed topology so windows can suspend position saves.</summary>
    public event EventHandler? ScreensChanging;

    /// <summary>Suspend immediately on a native notification, before Avalonia refreshes its cached screens.</summary>
    public void NotifyDisplaySettingsChanged()
    {
        notificationSettleUntil = DateTime.UtcNow.AddMilliseconds(750);
        pendingTopology = "";
        if (!IsTopologyChanging)
        {
            IsTopologyChanging = true;
            if (poller != null) poller.Interval = TimeSpan.FromMilliseconds(250);
            ScreensChanging?.Invoke(this, EventArgs.Empty);
        }
        Dispatcher.UIThread.Post(Refresh, DispatcherPriority.Background);
    }

    /// <summary>Attached screens (geometry + identity + matched configuration), refreshed by <see cref="Refresh"/>. Ordered by screen index.</summary>
    public IReadOnlyList<AttachedScreen> Attached { get; private set; } = [];

    /// <summary>Raised after the attached screen set changed (plug / unplug / resolution / DPI).</summary>
    public event EventHandler? ScreensChanged;

    /// <summary>
    /// Start watching display changes. Call once with any window (a widget or the
    /// settings window); the service keeps a reference to it as the screens anchor
    /// and re-arms itself whenever a window activates.
    /// </summary>
    public void Attach(Window window)
    {
        if (anchor == null)
        {
            anchor = window;
            window.Activated += (_, _) =>
            {
                Refresh();
                EnsurePoller();
            };
            EnsurePoller();
            Refresh();
        }
        else if (!ReferenceEquals(anchor, window))
        {
            // A widget / settings window re-arms the poller (in case the anchor
            // window ever closed); the anchor itself is never replaced.
            EnsurePoller();
        }
    }

    private void EnsurePoller()
    {
        if (poller != null) return;
        poller = new DispatcherTimer(TimeSpan.FromSeconds(1.5), DispatcherPriority.Background, (_, _) => Refresh());
        poller.Start();
    }

    /// <summary>Recalculate the attached screen list and raise <see cref="ScreensChanged"/> on any change.</summary>
    public void Refresh()
    {
        if (anchor == null || refreshing) return;
        refreshing = true;
        try { RefreshCore(); }
        finally { refreshing = false; }
    }

    private void RefreshCore()
    {
        if (anchor == null) return;

        var screens = anchor.Screens.All;
        if (screens.Count == 0)
        {
            if (!IsTopologyChanging)
            {
                IsTopologyChanging = true;
                if (poller != null) poller.Interval = TimeSpan.FromMilliseconds(250);
                ScreensChanging?.Invoke(this, EventArgs.Empty);
            }
            pendingTopology = "";
            return;
        }

        var devices = EnumerateDevices();
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var candidates = screens.Select(screen =>
        {
            var device = MatchDevice(screen, devices, used,
                Attached.FirstOrDefault(a => a.Screen.Bounds == screen.Bounds)?.Identity.DeviceName);
            return new AttachedScreen(screen, new ScreenIdentity(
                device?.Name ?? string.Empty,
                device?.FriendlyName ?? ScreenIdentity.FallbackName,
                screen.Bounds.Width, screen.Bounds.Height, screen.IsPrimary, screen.Scaling,
                device?.HardwareId ?? string.Empty, device?.MonitorId ?? string.Empty), null);
        }).ToList();
        var topology = string.Join("|", candidates.Select(a =>
            FormattableString.Invariant($"{a.Identity.DeviceName}:{a.Identity.MonitorId}:{a.Identity.Key}@{a.Screen.Bounds}:{a.Screen.WorkingArea}:{a.Screen.Scaling:R}:{a.Screen.IsPrimary}")).OrderBy(s => s, StringComparer.Ordinal));

        // During transitions both APIs report independently cached desktops.
        // Stable disagreement is still disagreement; never commit a guessed or
        // anonymous replacement for a named Win32 monitor, even on first attach.
        if (!DisplayTopology.HasSameGeometry(
                screens.Select(screen => (screen.Bounds.X, screen.Bounds.Y, screen.Bounds.Width, screen.Bounds.Height)),
                devices.Select(device => (device.X, device.Y, device.Width, device.Height)))
            || candidates.Any(a => string.IsNullOrEmpty(a.Identity.DeviceName)))
        {
            if (!IsTopologyChanging)
            {
                IsTopologyChanging = true;
                if (poller != null) poller.Interval = TimeSpan.FromMilliseconds(250);
                ScreensChanging?.Invoke(this, EventArgs.Empty);
            }
            pendingTopology = "";
            return;
        }

        if (committedTopology.Length > 0 && topology != committedTopology)
        {
            if (!IsTopologyChanging)
            {
                IsTopologyChanging = true;
                if (poller != null) poller.Interval = TimeSpan.FromMilliseconds(250);
                ScreensChanging?.Invoke(this, EventArgs.Empty);
            }
            if (pendingTopology != topology)
            {
                pendingTopology = topology;
                pendingSince = DateTime.UtcNow;
                return;
            }
            if (DateTime.UtcNow - pendingSince < TimeSpan.FromMilliseconds(750)) return;
        }

        var wasChanging = IsTopologyChanging;
        if (wasChanging && DateTime.UtcNow < notificationSettleUntil) return;
        committedTopology = topology;
        pendingTopology = "";

        // Clean redundant empty duplicates first (twin-key drift from stale
        // EnsureConfig); matching then works against a tidy config set.
        var originalLayout = layoutProvider.Get();
        var storedLayout = originalLayout.Deduplicate();

        // Consuming match: each stored entry matches at most ONE screen, so twin
        // screens with identical keys can never both own the same widgets.
        var consumedConfigIds = new HashSet<string>(StringComparer.Ordinal);

        var attached = new List<AttachedScreen>();

        // Geometry must agree across Win32 and Avalonia before assigning a
        // device identity. Unresolved screens cannot borrow another monitor's layout.
        foreach (var candidate in candidates.OrderBy(a => a.Identity.DeviceName, StringComparer.OrdinalIgnoreCase))
        {
            var screen = candidate.Screen;
            var identity = candidate.Identity;

            var config = ScreenMatcher.Match(storedLayout.Screens, identity, consumedConfigIds,
                candidates.Count(a => string.Equals(a.Identity.HardwareId, identity.HardwareId,
                    StringComparison.OrdinalIgnoreCase)) == 1,
                allowLegacyTwinUpgrade: !string.IsNullOrEmpty(identity.MonitorId));
            var changed = false;
            if (config != null && config.Key != null && config.Key != identity.Key && config.Key.EndsWith($"|{identity.Width}x{identity.Height}"))
            {
                // A hardware-id match may also fix a stale key: the entry followed this monitor
                // across a rename (driver update, EDID change), so its key now names it again.
                config = config with { Key = identity.Key };
                changed = true;
            }
            if (config != null && !string.IsNullOrEmpty(identity.HardwareId) && config.HardwareId != identity.HardwareId)
            {
                // Pin the entry to this monitor's EDID hardware id: from now on the entry can
                // only be matched by the physical monitor it was created for, no matter which
                // name a virtual screen or a driver quirk reports.
                config = config with { HardwareId = identity.HardwareId };
                changed = true;
            }
            if (config != null && !string.IsNullOrEmpty(identity.MonitorId) && config.MonitorId != identity.MonitorId)
            {
                config = config with { MonitorId = identity.MonitorId };
                changed = true;
            }
            if (changed)
            {
                storedLayout = storedLayout.WithScreen(config!);
            }
            attached.Add(new AttachedScreen(screen, identity, config));
        }

        // Attached is ALWAYS refreshed (queries see the latest configs even when
        // the geometry signature did not change — e.g. right after EnsureConfig
        // created a new entry); ScreensChanged fires only on actual changes.
        Attached = attached;
        // Publish the complete identity/configuration snapshot before notifying
        // layout consumers, and persist all backfills in a single transaction.
        if (!ReferenceEquals(storedLayout, originalLayout))
            layoutProvider.Save(storedLayout);

        var signature = topology + string.Join("|", attached.Select(a => a.Config?.Id ?? "-"));
        IsTopologyChanging = false;
        if (poller != null) poller.Interval = TimeSpan.FromSeconds(1.5);
        if (signature == lastSignature && !wasChanging) return;

        lastSignature = signature;

        // A disconnected configuration belongs to the user even if it contains
        // copies of another screen's widgets. Topology observation never deletes it.
        ScreensChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The attached screen the window currently sits on.</summary>
    public AttachedScreen? Find(Window window) =>
        window.Screens.ScreenFromWindow(window) is { } screen ? Find(screen) : null;

    /// <summary>The attached screen matching an Avalonia screen (by bounds).</summary>
    public AttachedScreen? Find(Screen screen) =>
        Attached.FirstOrDefault(a => a.Screen.Bounds == screen.Bounds);

    /// <summary>The attached screen whose stored configuration has the given id.</summary>
    public AttachedScreen? FindByConfigId(string configId)
    {
        var attached = Attached.FirstOrDefault(a => a.Config?.Id == configId);
        return attached == null ? null : attached with { Config = layoutProvider.Get().FindById(configId) ?? attached.Config };
    }

    /// <summary>
    /// The stored configuration of the screen the window currently sits on, re-read by
    /// id from the layout provider. <see cref="AttachedScreen.Config"/> entries are
    /// snapshots taken at the last <see cref="Refresh"/> — reading values through them
    /// can lag up to one poll interval behind a just-saved change (grid editor saves,
    /// screen-settings edits), so value consumers (grid / margin / radius / scale)
    /// resolve through this instead of the snapshot.
    /// </summary>
    public ScreenLayout? CurrentConfig(Window window)
    {
        var snapshot = Find(window)?.Config;
        if (snapshot == null) return null;
        return layoutProvider.Get().FindById(snapshot.Id) ?? snapshot;
    }

    /// <summary>
    /// Ensure a stored configuration exists for an attached screen (new screens
    /// get a fresh entry on their first use) and return it.
    /// </summary>
    public ScreenLayout EnsureConfig(AttachedScreen attached)
    {
        if (attached.Config != null)
            return layoutProvider.Get().FindById(attached.Config.Id) ?? attached.Config;

        var entry = new ScreenLayout(
            Guid.NewGuid().ToString("N"),
            attached.Identity.Key,
            null,
            null,
            null,
            null,
            [],
            HardwareId: string.IsNullOrEmpty(attached.Identity.HardwareId) ? null : attached.Identity.HardwareId,
            MonitorId: string.IsNullOrEmpty(attached.Identity.MonitorId) ? null : attached.Identity.MonitorId);
        var screens = layoutProvider.Get().UpsertScreen(entry);
        layoutProvider.Save(screens);

        // Re-run matching so the in-memory list sees the new entry immediately.
        // Without this, a twin screen (same key, another attached display)
        // matching against the stale list would create a SECOND identical entry.
        Refresh();

        var list = Attached.ToList();
        var index = list.FindIndex(a => a.Screen.Bounds == attached.Screen.Bounds);
        if (index >= 0)
        {
            list[index] = attached with { Config = entry };
            Attached = list;
        }

        // Refresh publishes ScreensChanged; WidgetFactory owns migration and
        // closes live legacy windows before changing their coordinate format.

        // Reconciliation can migrate legacy widgets into this new bucket; callers
        // must receive that current entry rather than the empty creation snapshot.
        return layoutProvider.Get().FindById(entry.Id) ?? entry;
    }

    /// <summary>
    /// Idempotent migration of the legacy "primary" entry: every widget whose
    /// absolute desktop position sits inside ANOTHER attached screen's working
    /// area is moved into that screen's own per-screen entry (coordinates
    /// converted from absolute desktop pixels to relative-to-that-screen).
    /// <para>
    /// v1 layout files store ALL widgets in a single legacy primary entry with
    /// absolute coordinates — including widgets physically placed on a second
    /// screen. While they render correctly, per-screen grids / sizing / editing
    /// then resolve against the wrong screen's grid (the mess reported in the
    /// multi-screen cleanup). After the split, the primary entry owns only what
    /// is really on the primary screen and every screen's grid applies to its
    /// own widgets.
    /// </para>
    /// </summary>
    public void MigrateLegacyWidgets()
    {
        var stored = layoutProvider.Get();
        var primary = stored.FindById(ScreensLayout.LegacyPrimaryId);
        // Only unmigrated legacy v1 buckets (Key == null) need migration. Real v2 screens must never be touched.
        if (primary == null || primary.Key != null) return;
        var primaryScreen = Attached.FirstOrDefault(a => a.Config?.Id == primary.Id);
        if (primaryScreen == null || IsTopologyChanging) return;

        var migrated = stored.MigrateLegacyWidgets(widget =>
        {
            var centerX = widget.X + widget.Width / 2.0;
            var centerY = widget.Y + widget.Height / 2.0;

            var owner = Attached.FirstOrDefault(a =>
                a.Config != null
                && a.Config.Id != primary.Id
                && a.Screen.WorkingArea.Contains(new PixelPoint((int) centerX, (int) centerY)));

            return owner == null ? null : (owner.Config!.Id, owner.Screen.WorkingArea.X, owner.Screen.WorkingArea.Y);
        });

        // Convert only once the remaining bucket is wholly on its real screen.
        // Unknown/offline widget positions stay absolute until their monitor returns.
        var remaining = migrated.FindById(primary.Id)!;
        if (remaining.Layout.All(widget => primaryScreen.Screen.WorkingArea.Contains(
                new PixelPoint(widget.X + widget.Width / 2, widget.Y + widget.Height / 2))))
        {
            var area = primaryScreen.Screen.WorkingArea;
            migrated = migrated.WithScreen(remaining with
            {
                Key = primaryScreen.Identity.Key,
                Layout = remaining.Layout.Select(widget => widget with
                {
                    X = widget.X - area.X,
                    Y = widget.Y - area.Y
                }).ToList()
            });
        }

        if (ReferenceEquals(migrated, stored)) return;
        Attached = Attached.Select(a => a.Config == null ? a
            : a with { Config = migrated.FindById(a.Config.Id) ?? a.Config }).ToList();
        layoutProvider.Save(migrated);
        Refresh();
    }

    // ---------- Win32 enumeration ----------

    private const int CCHDEVICENAME = 32;
    private const uint DISPLAY_DEVICE_ATTACHED_TO_DESKTOP = 0x1;
    private const uint ENUM_CURRENT_SETTINGS = 0xFFFFFFFF;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DISPLAY_DEVICE
    {
        public uint cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CCHDEVICENAME)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public uint StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplayDevices(string? lpDevice, uint iDevNum, ref DISPLAY_DEVICE lpDisplayDevice, uint dwFlags);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CCHDEVICENAME)] public string dmDeviceName;
        public short dmSpecVersion;
        public short dmDriverVersion;
        public short dmSize;
        public short dmDriverExtra;
        public int dmFields;
        public int dmPositionX;
        public int dmPositionY;
        public int dmDisplayOrientation;
        public int dmDisplayFixedOutput;
        public short dmColor;
        public short dmDuplex;
        public short dmYResolution;
        public short dmTTOption;
        public short dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public short dmLogPixels;
        public int dmBitsPerPel;
        public int dmPelsWidth;
        public int dmPelsHeight;
        public int dmDisplayFlags;
        public int dmDisplayFrequency;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplaySettings(string? lpszDeviceName, uint iModeNum, ref DEVMODE lpDevMode);

    private sealed record Win32Device(string Name, string FriendlyName, string HardwareId, string MonitorId, int X, int Y, int Width, int Height);

    private static Dictionary<string, string>? cachedWmiNames;
    private static DateTime lastWmiQuery = DateTime.MinValue;

#pragma warning disable CA1416
    private static Dictionary<string, string> GetWmiMonitorNames()
    {
        if (cachedWmiNames != null && (DateTime.UtcNow - lastWmiQuery).TotalSeconds < 30)
            return cachedWmiNames;

        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var searcher = new System.Management.ManagementObjectSearcher(@"root\wmi", "SELECT InstanceName, UserFriendlyName, ProductCodeID FROM WmiMonitorID");
            foreach (var obj in searcher.Get())
            {
                var instanceName = obj["InstanceName"]?.ToString() ?? "";
                var nameCodes = obj["UserFriendlyName"] as ushort[];
                var productCodes = obj["ProductCodeID"] as ushort[];

                string friendly = "";
                if (nameCodes != null)
                {
                    friendly = new string(nameCodes.Where(c => c != 0).Select(c => (char)c).ToArray()).Trim();
                }
                if (string.IsNullOrEmpty(friendly) && productCodes != null)
                {
                    friendly = new string(productCodes.Where(c => c != 0).Select(c => (char)c).ToArray()).Trim();
                }

                if (!string.IsNullOrEmpty(friendly))
                {
                    var parts = instanceName.Split('\\');
                    if (parts.Length > 1) dict[parts[1]] = friendly;
                    dict[instanceName] = friendly;
                }
            }
        }
        catch
        {
            // Ignore WMI query failures
        }

        lastWmiQuery = DateTime.UtcNow;
        return cachedWmiNames = dict;
    }
#pragma warning restore CA1416

    private static List<Win32Device> EnumerateDevices()
    {
        var result = new List<Win32Device>();
        var wmiNames = GetWmiMonitorNames();

        for (uint index = 0; ; index++)
        {
            var device = new DISPLAY_DEVICE { cb = (uint) Marshal.SizeOf<DISPLAY_DEVICE>() };
            if (!EnumDisplayDevices(null, index, ref device, 0)) break;
            if ((device.StateFlags & DISPLAY_DEVICE_ATTACHED_TO_DESKTOP) == 0) continue;

            var mode = new DEVMODE { dmSize = (short) Marshal.SizeOf<DEVMODE>() };
            if (!EnumDisplaySettings(device.DeviceName, ENUM_CURRENT_SETTINGS, ref mode)) continue;

            // Probe monitor attached to this display adapter
            string friendlyName = string.Empty;
            string hardwareId = string.Empty;
            string monitorId = string.Empty;

            for (uint monIndex = 0; ; monIndex++)
            {
                var monitor = new DISPLAY_DEVICE { cb = (uint) Marshal.SizeOf<DISPLAY_DEVICE>() };
                if (!EnumDisplayDevices(device.DeviceName, monIndex, ref monitor, 1 /* EDID_PHYSICAL_MONITOR */))
                    break;

                var rawName = monitor.DeviceString?.Trim() ?? string.Empty;
                var devId = monitor.DeviceID ?? string.Empty;

                if (!string.IsNullOrEmpty(devId))
                {
                    monitorId = devId.Trim().ToUpperInvariant();
                    var parts = devId.Split(new[] { '\\', '#' }, StringSplitOptions.RemoveEmptyEntries);
                    var displayPart = Array.FindIndex(parts, part => part.Equals("DISPLAY", StringComparison.OrdinalIgnoreCase));
                    if (displayPart >= 0 && displayPart + 1 < parts.Length)
                    {
                        hardwareId = parts[displayPart + 1];
                    }
                }

                if (!string.IsNullOrEmpty(rawName)
                    && !rawName.Equals("Generic PnP Monitor", StringComparison.OrdinalIgnoreCase)
                    && !rawName.Equals("通用即插即用监视器", StringComparison.OrdinalIgnoreCase))
                {
                    friendlyName = rawName;
                    break;
                }
            }

            // Check WMI friendly name by hardware ID
            if (!string.IsNullOrEmpty(hardwareId) && wmiNames.TryGetValue(hardwareId, out var wmiFriendly) && !string.IsNullOrEmpty(wmiFriendly))
            {
                friendlyName = wmiFriendly;
            }

            if (string.IsNullOrEmpty(friendlyName))
            {
                if (!string.IsNullOrEmpty(hardwareId))
                {
                    friendlyName = hardwareId;
                }
                else if (!string.IsNullOrEmpty(device.DeviceString) && device.DeviceString.Contains("Virtual", StringComparison.OrdinalIgnoreCase))
                {
                    friendlyName = device.DeviceString;
                }
                else
                {
                    friendlyName = !string.IsNullOrWhiteSpace(device.DeviceString) ? device.DeviceString : "Screen";
                }
            }

            result.Add(new Win32Device(
                device.DeviceName,
                friendlyName,
                hardwareId,
                monitorId,
                mode.dmPositionX,
                mode.dmPositionY,
                mode.dmPelsWidth,
                mode.dmPelsHeight));
        }

        return result;
    }

    private static Win32Device? MatchDevice(Screen screen, List<Win32Device> devices, HashSet<string> used, string? previousDevice)
    {
        // 1. Exact coordinate and resolution match (dmPositionX/Y == screen.Bounds.X/Y && Width/Height)
        var matches = devices.Where(d => !used.Contains(d.Name)
            && d.X == screen.Bounds.X
            && d.Y == screen.Bounds.Y
            && d.Width == screen.Bounds.Width
            && d.Height == screen.Bounds.Height).ToList();
        // Clone mode exposes several outputs at the same desktop geometry.
        // Keep the previous owner if present; otherwise choose a deterministic
        // output for this single logical desktop instead of treating it as unstable.
        var chosenName = DisplayTopology.SelectDevice(matches.Select(d => d.Name), previousDevice);
        var exactPos = matches.FirstOrDefault(d => d.Name == chosenName);
        if (exactPos != null)
        {
            used.Add(exactPos.Name);
            return exactPos;
        }

        // The two APIs can momentarily disagree during hot-plug. Guessing by
        // resolution or enumeration order would give a surviving screen the
        // removed monitor's identity and widgets. Wait for a coherent snapshot.
        return null;
    }
}
