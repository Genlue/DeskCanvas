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

            // Strict matching: do not allow unverified model fallbacks to cross-assign layouts
            var config = ScreenMatcher.Match(storedLayout.Screens, identity, consumedConfigIds,
                allowHardwareFallback: false,
                allowLegacyTwinUpgrade: false);

            var changed = false;
            if (config != null)
            {
                // Safe migration of legacy v2 configs without ScreenBinding:
                if (config.Binding == null && !string.IsNullOrEmpty(identity.HardwareId))
                {
                    if (string.IsNullOrEmpty(config.HardwareId) || string.Equals(config.HardwareId, identity.HardwareId, StringComparison.OrdinalIgnoreCase))
                    {
                        config = config with
                        {
                            HardwareId = identity.HardwareId,
                            MonitorId = string.IsNullOrEmpty(config.MonitorId) ? identity.MonitorId : config.MonitorId,
                            Binding = new ScreenBinding(
                                identity.PnpInstanceId,
                                DeviceInterfacePath: identity.PhysicalIdentity?.DeviceInterfacePath,
                                HardwareId: identity.HardwareId,
                                SerialNumber: identity.PhysicalIdentity?.SerialNumber,
                                Source: BindingSource.Migrated,
                                Status: BindingStatus.Bound)
                        };
                        changed = true;
                    }
                }
                else if (config.Binding != null && config.Binding.MatchesInstance(identity.PnpInstanceId))
                {
                    // Verified binding already matches: do not touch binding or hardware id
                    if (config.Key != null && config.Key != identity.Key && config.Key.EndsWith($"|{identity.Width}x{identity.Height}"))
                    {
                        config = config with { Key = identity.Key };
                        changed = true;
                    }
                }

                if (changed)
                {
                    storedLayout = storedLayout.WithScreen(config!);
                }
            }

            var status = config != null
                ? BindingStatus.Bound
                : (identity.IsAnonymous ? BindingStatus.Unresolved : BindingStatus.Unconfigured);

            attached.Add(new AttachedScreen(screen, identity with { BindingStatus = status }, config));
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

        var identity = attached.Identity;
        var pnpId = identity.PnpInstanceId;
        var hardwareId = string.IsNullOrEmpty(identity.HardwareId) ? null : identity.HardwareId;
        var monitorId = string.IsNullOrEmpty(identity.MonitorId) ? null : identity.MonitorId;

        var binding = !string.IsNullOrEmpty(pnpId)
            ? new ScreenBinding(
                pnpId,
                DeviceInterfacePath: identity.PhysicalIdentity?.DeviceInterfacePath ?? monitorId,
                HardwareId: hardwareId,
                SerialNumber: identity.PhysicalIdentity?.SerialNumber,
                Source: BindingSource.AutoDetected,
                Status: BindingStatus.Bound)
            : null;

        var entry = new ScreenLayout(
            Guid.NewGuid().ToString("N"),
            identity.Key,
            null,
            null,
            null,
            null,
            [],
            HardwareId: hardwareId,
            MonitorId: monitorId,
            Binding: binding);
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

    // ---------- Win32 enumeration (CCD + GDI fallback) ----------

    private const int CCHDEVICENAME = 32;
    private const uint DISPLAY_DEVICE_ATTACHED_TO_DESKTOP = 0x1;
    private const uint ENUM_CURRENT_SETTINGS = 0xFFFFFFFF;
    private const uint QDC_ONLY_ACTIVE_PATHS = 0x00000002;
    private const int ERROR_SUCCESS = 0;
    private const int ERROR_INSUFFICIENT_BUFFER = 122;

    [StructLayout(LayoutKind.Sequential)]
    private struct LUID
    {
        public uint LowPart;
        public int HighPart;

        public override string ToString() => $"0x{HighPart:X8}:{LowPart:X8}";
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_RATIONAL
    {
        public uint Numerator;
        public uint Denominator;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_PATH_SOURCE_INFO
    {
        public LUID adapterId;
        public uint id;
        public uint modeInfoIdx;
        public uint statusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_PATH_TARGET_INFO
    {
        public LUID adapterId;
        public uint id;
        public uint modeInfoIdx;
        public uint outputTechnology;
        public uint rotation;
        public uint scaling;
        public DISPLAYCONFIG_RATIONAL refreshRate;
        public uint scanLineOrdering;
        public uint targetAvailable;
        public uint statusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_PATH_INFO
    {
        public DISPLAYCONFIG_PATH_SOURCE_INFO sourceInfo;
        public DISPLAYCONFIG_PATH_TARGET_INFO targetInfo;
        public uint flags;
    }

    [StructLayout(LayoutKind.Explicit, Size = 64)]
    private struct DISPLAYCONFIG_MODE_INFO
    {
        [FieldOffset(0)] public uint infoType;
        [FieldOffset(4)] public uint id;
        [FieldOffset(8)] public LUID adapterId;
        [FieldOffset(16)] public uint sourceWidth;
        [FieldOffset(20)] public uint sourceHeight;
        [FieldOffset(24)] public uint sourcePixelFormat;
        [FieldOffset(28)] public int sourcePositionX;
        [FieldOffset(32)] public int sourcePositionY;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_DEVICE_INFO_HEADER
    {
        public uint type;
        public uint size;
        public LUID adapterId;
        public uint id;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DISPLAYCONFIG_SOURCE_DEVICE_NAME
    {
        public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CCHDEVICENAME)]
        public string viewGdiDeviceName;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DISPLAYCONFIG_TARGET_DEVICE_NAME
    {
        public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
        public uint flags;
        public uint outputTechnology;
        public ushort edidManufactureId;
        public ushort edidProductCodeId;
        public uint connectorInstance;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string monitorFriendlyDeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string monitorDevicePath;
    }

    [DllImport("user32.dll")]
    private static extern int GetDisplayConfigBufferSizes(
        uint flags,
        out uint numPathArrayElements,
        out uint numModeInfoArrayElements);

    [DllImport("user32.dll")]
    private static extern int QueryDisplayConfig(
        uint flags,
        ref uint numPathArrayElements,
        [In, Out] DISPLAYCONFIG_PATH_INFO[] pathInfoArray,
        ref uint numModeInfoArrayElements,
        [In, Out] DISPLAYCONFIG_MODE_INFO[] modeInfoArray,
        IntPtr currentTopologyId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_SOURCE_DEVICE_NAME requestPacket);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_TARGET_DEVICE_NAME requestPacket);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int left, top, right, bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MONITORINFOEX
    {
        public uint cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CCHDEVICENAME)]
        public string szDevice;
    }

    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdcMonitor, ref RECT lprcMonitor, IntPtr dwData);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX lpmi);

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

    private sealed record Win32Device(
        string Name,
        string FriendlyName,
        string HardwareId,
        string MonitorId,
        int X,
        int Y,
        int Width,
        int Height,
        PhysicalMonitorIdentity? PhysicalIdentity = null);

    private sealed record WmiDetails(string FriendlyName, string ProductCode, string? SerialNumber);

    private static Dictionary<string, WmiDetails>? cachedWmiDetails;
    private static DateTime lastWmiDetailQuery = DateTime.MinValue;

#pragma warning disable CA1416
    private static Dictionary<string, WmiDetails> GetWmiMonitorDetails()
    {
        if (cachedWmiDetails != null && (DateTime.UtcNow - lastWmiDetailQuery).TotalSeconds < 30)
            return cachedWmiDetails;

        var dict = new Dictionary<string, WmiDetails>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var searcher = new System.Management.ManagementObjectSearcher(@"root\wmi", "SELECT * FROM WmiMonitorID");
            foreach (var obj in searcher.Get())
            {
                var instance = obj["InstanceName"]?.ToString() ?? "";
                var nameCodes = obj["UserFriendlyName"] as ushort[];
                var productCodes = obj["ProductCodeID"] as ushort[];
                var serialCodes = obj["SerialNumberID"] as ushort[];

                string friendly = nameCodes != null ? new string(nameCodes.Where(c => c != 0).Select(c => (char)c).ToArray()).Trim() : "";
                string product = productCodes != null ? new string(productCodes.Where(c => c != 0).Select(c => (char)c).ToArray()).Trim() : "";
                string rawSerial = serialCodes != null ? new string(serialCodes.Where(c => c != 0).Select(c => (char)c).ToArray()).Trim() : "";
                string? serial = PhysicalMonitorIdentity.IsValidSerialNumber(rawSerial) ? rawSerial : null;

                var details = new WmiDetails(friendly, product, serial);

                // Map by normalized PnP ID (strip trailing _0, _1 if present)
                var pnpId = PhysicalMonitorIdentity.NormalizeDeviceInstanceId(instance);
                var lastUnderscore = pnpId.LastIndexOf('_');
                if (lastUnderscore > 0)
                    pnpId = pnpId.Substring(0, lastUnderscore);

                if (!string.IsNullOrEmpty(pnpId))
                    dict[pnpId] = details;

                // Also map by HardwareId (ProductCode)
                var parts = instance.Split('\\');
                if (parts.Length > 1)
                    dict[parts[1]] = details;
            }
        }
        catch
        {
            // Ignore WMI query failures
        }

        lastWmiDetailQuery = DateTime.UtcNow;
        return cachedWmiDetails = dict;
    }
#pragma warning restore CA1416

    private static string DecodeEdidManufacturer(ushort id)
    {
        var swapped = (ushort)(((id & 0xFF) << 8) | ((id >> 8) & 0xFF));
        var c1 = (char)('@' + ((swapped >> 10) & 0x1F));
        var c2 = (char)('@' + ((swapped >> 5) & 0x1F));
        var c3 = (char)('@' + (swapped & 0x1F));
        return $"{c1}{c2}{c3}";
    }

    private static string ExtractPnpInstanceId(string deviceInterfacePath)
    {
        if (string.IsNullOrEmpty(deviceInterfacePath)) return string.Empty;
        var s = deviceInterfacePath;
        if (s.StartsWith(@"\\?\", StringComparison.OrdinalIgnoreCase))
            s = s.Substring(4);

        var lastHash = s.LastIndexOf('#');
        if (lastHash > 0 && s.IndexOf('{', lastHash) > 0)
        {
            s = s.Substring(0, lastHash);
        }

        return s.Replace('#', '\\');
    }

    private static List<Win32Device> EnumerateDevices()
    {
        var ccd = EnumerateCcdDevices();
        if (ccd != null && ccd.Count > 0)
            return ccd;

        return EnumerateGdiDevices();
    }

    private static List<Win32Device>? EnumerateCcdDevices()
    {
        // 1. Get GDI desktop monitors via EnumDisplayMonitors
        var gdiMonitors = new List<MONITORINFOEX>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr hMon, IntPtr hdc, ref RECT rc, IntPtr data) =>
        {
            var mi = new MONITORINFOEX { cbSize = (uint)Marshal.SizeOf<MONITORINFOEX>() };
            if (GetMonitorInfo(hMon, ref mi))
                gdiMonitors.Add(mi);
            return true;
        }, IntPtr.Zero);

        if (gdiMonitors.Count == 0) return null;

        // 2. Query CCD active paths
        uint pathCount = 0, modeCount = 0;
        int status = GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS, out pathCount, out modeCount);
        if (status != ERROR_SUCCESS || pathCount == 0) return null;

        DISPLAYCONFIG_PATH_INFO[] paths = [];
        DISPLAYCONFIG_MODE_INFO[] modes = [];
        for (int retry = 0; retry < 3; retry++)
        {
            paths = new DISPLAYCONFIG_PATH_INFO[pathCount];
            modes = new DISPLAYCONFIG_MODE_INFO[modeCount];
            status = QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS, ref pathCount, paths, ref modeCount, modes, IntPtr.Zero);
            if (status == ERROR_SUCCESS) break;
            if (status == ERROR_INSUFFICIENT_BUFFER)
            {
                GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS, out pathCount, out modeCount);
                continue;
            }
            return null;
        }

        var wmiDetails = GetWmiMonitorDetails();
        var pathsByGdiName = new Dictionary<string, List<(DISPLAYCONFIG_PATH_INFO Path, PhysicalMonitorIdentity Identity)>>(StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < pathCount; i++)
        {
            var path = paths[i];
            if ((path.flags & 1) == 0 || path.targetInfo.targetAvailable == 0)
                continue;

            // Query Source GDI Device Name
            var sourceName = new DISPLAYCONFIG_SOURCE_DEVICE_NAME
            {
                header = new DISPLAYCONFIG_DEVICE_INFO_HEADER
                {
                    type = 1,
                    size = (uint)Marshal.SizeOf<DISPLAYCONFIG_SOURCE_DEVICE_NAME>(),
                    adapterId = path.sourceInfo.adapterId,
                    id = path.sourceInfo.id
                }
            };
            if (DisplayConfigGetDeviceInfo(ref sourceName) != ERROR_SUCCESS) continue;
            var gdiName = sourceName.viewGdiDeviceName;
            if (string.IsNullOrEmpty(gdiName)) continue;

            // Query Target Device Name
            var targetName = new DISPLAYCONFIG_TARGET_DEVICE_NAME
            {
                header = new DISPLAYCONFIG_DEVICE_INFO_HEADER
                {
                    type = 2,
                    size = (uint)Marshal.SizeOf<DISPLAYCONFIG_TARGET_DEVICE_NAME>(),
                    adapterId = path.targetInfo.adapterId,
                    id = path.targetInfo.id
                }
            };
            if (DisplayConfigGetDeviceInfo(ref targetName) != ERROR_SUCCESS) continue;

            var mfg = DecodeEdidManufacturer(targetName.edidManufactureId);
            var prod = targetName.edidProductCodeId.ToString("X4");
            var hardwareId = $"{mfg}{prod}";
            var devPath = targetName.monitorDevicePath ?? string.Empty;
            var pnpId = ExtractPnpInstanceId(devPath);
            var isInternal = path.targetInfo.outputTechnology is 11 /* EMBEDDED_DP */ or 7 /* LVDS */ or 0xFFFFFFFF;

            string friendly = targetName.monitorFriendlyDeviceName;
            string? serial = null;

            if (wmiDetails.TryGetValue(pnpId, out var details) || (!string.IsNullOrEmpty(hardwareId) && wmiDetails.TryGetValue(hardwareId, out details)))
            {
                if (string.IsNullOrEmpty(friendly) && !string.IsNullOrEmpty(details.FriendlyName))
                    friendly = details.FriendlyName;
                if (!string.IsNullOrEmpty(details.SerialNumber))
                    serial = details.SerialNumber;
            }

            if (string.IsNullOrEmpty(friendly))
                friendly = !string.IsNullOrEmpty(prod) ? prod : hardwareId;

            var physical = new PhysicalMonitorIdentity(
                pnpId,
                devPath,
                mfg,
                prod,
                hardwareId,
                serial,
                friendly,
                isInternal,
                path.targetInfo.outputTechnology,
                targetName.connectorInstance);

            if (!pathsByGdiName.TryGetValue(gdiName, out var list))
            {
                list = [];
                pathsByGdiName[gdiName] = list;
            }
            list.Add((path, physical));
        }

        var results = new List<Win32Device>();
        foreach (var mi in gdiMonitors)
        {
            var gdiName = mi.szDevice;
            var width = mi.rcMonitor.right - mi.rcMonitor.left;
            var height = mi.rcMonitor.bottom - mi.rcMonitor.top;

            PhysicalMonitorIdentity? physical = null;
            if (pathsByGdiName.TryGetValue(gdiName, out var pathList) && pathList.Count > 0)
            {
                physical = pathList[0].Identity;
            }

            var friendly = physical?.FriendlyName ?? "Screen";
            var hardwareId = physical?.HardwareId ?? string.Empty;
            var monitorId = physical?.DeviceInterfacePath ?? string.Empty;

            results.Add(new Win32Device(
                gdiName,
                friendly,
                hardwareId,
                monitorId,
                mi.rcMonitor.left,
                mi.rcMonitor.top,
                width,
                height,
                physical));
        }

        return results;
    }

    private static List<Win32Device> EnumerateGdiDevices()
    {
        var result = new List<Win32Device>();
        var wmiDetails = GetWmiMonitorDetails();

        for (uint index = 0; ; index++)
        {
            var device = new DISPLAY_DEVICE { cb = (uint)Marshal.SizeOf<DISPLAY_DEVICE>() };
            if (!EnumDisplayDevices(null, index, ref device, 0)) break;
            if ((device.StateFlags & DISPLAY_DEVICE_ATTACHED_TO_DESKTOP) == 0) continue;

            var mode = new DEVMODE { dmSize = (short)Marshal.SizeOf<DEVMODE>() };
            if (!EnumDisplaySettings(device.DeviceName, ENUM_CURRENT_SETTINGS, ref mode)) continue;

            string friendlyName = string.Empty;
            string hardwareId = string.Empty;
            string monitorId = string.Empty;

            for (uint monIndex = 0; ; monIndex++)
            {
                var monitor = new DISPLAY_DEVICE { cb = (uint)Marshal.SizeOf<DISPLAY_DEVICE>() };
                if (!EnumDisplayDevices(device.DeviceName, monIndex, ref monitor, 1 /* EDD_GET_DEVICE_INTERFACE_NAME */))
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

            if (!string.IsNullOrEmpty(hardwareId) && wmiDetails.TryGetValue(hardwareId, out var details) && !string.IsNullOrEmpty(details.FriendlyName))
            {
                friendlyName = details.FriendlyName;
            }

            if (string.IsNullOrEmpty(friendlyName))
            {
                if (!string.IsNullOrEmpty(hardwareId)) friendlyName = hardwareId;
                else if (!string.IsNullOrEmpty(device.DeviceString) && device.DeviceString.Contains("Virtual", StringComparison.OrdinalIgnoreCase))
                    friendlyName = device.DeviceString;
                else
                    friendlyName = !string.IsNullOrWhiteSpace(device.DeviceString) ? device.DeviceString : "Screen";
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
        var matches = devices.Where(d => !used.Contains(d.Name)
            && d.X == screen.Bounds.X
            && d.Y == screen.Bounds.Y
            && d.Width == screen.Bounds.Width
            && d.Height == screen.Bounds.Height).ToList();

        var chosenName = DisplayTopology.SelectDevice(matches.Select(d => d.Name), previousDevice);
        var exactPos = matches.FirstOrDefault(d => d.Name == chosenName);
        if (exactPos != null)
        {
            used.Add(exactPos.Name);
            return exactPos;
        }

        return null;
    }
}
