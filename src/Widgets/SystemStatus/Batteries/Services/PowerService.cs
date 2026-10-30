using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Text.RegularExpressions;
using Batteries.Models;

namespace Batteries.Services;

public static class PowerService
{
    [StructLayout(LayoutKind.Sequential)]
    public struct SYSTEM_POWER_STATUS
    {
        public byte ACLineStatus;         // 0: Offline, 1: Online, 255: Unknown
        public byte BatteryFlag;          // 1: High, 2: Low, 4: Critical, 8: Charging, 128: No system battery, 255: Unknown
        public byte BatteryLifePercent;   // 0-100, 255: Unknown
        public byte SystemStatusFlag;
        public int BatteryLifeTime;       // Seconds remaining, -1 if unknown
        public int BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll")]
    private static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS lpSystemPowerStatus);

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_DEVINFO_DATA
    {
        public uint cbSize;
        public Guid ClassGuid;
        public uint DevInst;
        public IntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DEVPROPKEY
    {
        public Guid fmtid;
        public uint pid;

        public DEVPROPKEY(Guid fmtid, uint pid)
        {
            this.fmtid = fmtid;
            this.pid = pid;
        }
    }

    private const uint DIGCF_PRESENT = 0x00000002;
    private const uint DIGCF_ALLCLASSES = 0x00000004;

    private static readonly DEVPROPKEY PKEY_Device_BatteryPercentage = 
        new(new Guid("104ea319-6ee2-4701-bd47-8ddbf425bbe5"), 2);
    private static readonly DEVPROPKEY PKEY_Device_FriendlyName = 
        new(new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), 14);
    private static readonly DEVPROPKEY PKEY_NAME = 
        new(new Guid("b725f130-47ef-101a-a5f1-02608c9eebac"), 10);
    private static readonly DEVPROPKEY PKEY_Device_DeviceDesc = 
        new(new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), 2);
    private static readonly DEVPROPKEY PKEY_Device_InstanceId = 
        new(new Guid("78c34fc8-104a-4aca-9ea4-524d52996e57"), 256);
    private static readonly DEVPROPKEY PKEY_Device_Class = 
        new(new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), 9);
    private static readonly DEVPROPKEY PKEY_Device_DevNodeStatus = 
        new(new Guid("4340a6c5-93fa-4706-972c-7b648008a5a7"), 2);
    private static readonly DEVPROPKEY PKEY_Device_ProblemCode = 
        new(new Guid("4340a6c5-93fa-4706-972c-7b648008a5a7"), 3);
    private static readonly DEVPROPKEY PKEY_DeviceContainer_Category = 
        new(new Guid("78c34fc8-104a-4aca-9ea4-524d52996e57"), 91);

    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr SetupDiGetClassDevsW(
        IntPtr ClassGuid,
        string? Enumerator,
        IntPtr hwndParent,
        uint Flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiEnumDeviceInfo(
        IntPtr DeviceInfoSet,
        uint MemberIndex,
        ref SP_DEVINFO_DATA DeviceInfoData);

    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool SetupDiGetDevicePropertyW(
        IntPtr DeviceInfoSet,
        ref SP_DEVINFO_DATA DeviceInfoData,
        ref DEVPROPKEY PropertyKey,
        out uint PropertyType,
        byte[]? PropertyBuffer,
        uint PropertyBufferSize,
        out uint RequiredSize,
        uint Flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr DeviceInfoSet);

    public record PowerInfo(
        bool HasBattery,
        bool IsCharging,
        bool IsAcConnected,
        int Percentage,
        int RemainingMinutes
    );

    public record PeripheralInfo(
        string Name,
        DeviceKind Kind,
        int Percentage,
        string InstanceId = "",
        bool IsConnected = true
    );

    public record BluetoothDeviceInfo(
        string Name,
        string CleanName,
        string InstanceId,
        DeviceKind Kind,
        bool IsConnected,
        int? BatteryPercentage
    );

    public static PowerInfo GetCurrentPowerInfo()
    {
        if (OperatingSystem.IsWindows() && GetSystemPowerStatus(out var status))
        {
            bool hasBattery = status.BatteryFlag != 128 && status.BatteryLifePercent != 255;
            bool isAc = status.ACLineStatus == 1;
            bool isCharging = (status.BatteryFlag & 8) != 0 || (isAc && hasBattery && status.BatteryLifePercent < 100);
            int percent = hasBattery ? Math.Clamp((int)status.BatteryLifePercent, 0, 100) : 100;
            int minutes = status.BatteryLifeTime > 0 ? status.BatteryLifeTime / 60 : -1;

            return new PowerInfo(hasBattery, isCharging, isAc, percent, minutes);
        }

        return new PowerInfo(false, true, true, 100, -1);
    }

    /// <summary>
    /// Strips Windows protocol suffixes (Hands-Free, Avrcp, etc.) and registry resource tags.
    /// </summary>
    public static string CleanDeviceName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return string.Empty;

        var cleaned = name.Trim();
        if (cleaned.StartsWith("@"))
        {
            var semi = cleaned.IndexOf(';');
            if (semi >= 0 && semi < cleaned.Length - 1)
                cleaned = cleaned[(semi + 1)..].Trim();
        }

        cleaned = Regex.Replace(
            cleaned,
            @"\s+(Hands-Free(\s+AG|\s+HF)?|Avrcp(\s+传输)?|A2DP\s+SNK|Audio)$",
            "",
            RegexOptions.IgnoreCase
        ).Trim();

        return string.IsNullOrEmpty(cleaned) ? name : cleaned;
    }

    public static List<PeripheralInfo> GetConnectedPeripherals(IReadOnlyDictionary<string, DeviceKind>? overrides = null)
    {
        var list = new List<PeripheralInfo>();
        if (!OperatingSystem.IsWindows()) return list;

        try
        {
            // 1. Primary path: Native SetupAPI querying DEVPKEY_Device_BatteryPercentage on present devices
            IntPtr hDevInfo = SetupDiGetClassDevsW(IntPtr.Zero, null, IntPtr.Zero, DIGCF_PRESENT | DIGCF_ALLCLASSES);
            if (hDevInfo != IntPtr.Zero && hDevInfo != new IntPtr(-1))
            {
                try
                {
                    SP_DEVINFO_DATA devData = new() { cbSize = (uint)Marshal.SizeOf<SP_DEVINFO_DATA>() };
                    uint index = 0;
                    var foundMap = new Dictionary<string, PeripheralInfo>(StringComparer.OrdinalIgnoreCase);

                    while (SetupDiEnumDeviceInfo(hDevInfo, index++, ref devData))
                    {
                        var key = PKEY_Device_BatteryPercentage;
                        if (SetupDiGetDevicePropertyW(hDevInfo, ref devData, ref key, out uint propType, null, 0, out uint requiredSize, 0) || requiredSize > 0)
                        {
                            byte[] buffer = new byte[requiredSize];
                            if (SetupDiGetDevicePropertyW(hDevInfo, ref devData, ref key, out propType, buffer, requiredSize, out _, 0))
                            {
                                int battery = -1;
                                if (propType is 17 or 3 && buffer.Length >= 1) // DEVPROP_TYPE_BYTE / SBYTE
                                {
                                    battery = buffer[0];
                                }
                                else if (propType == 7 && buffer.Length >= 4) // DEVPROP_TYPE_UINT32
                                {
                                    battery = BitConverter.ToInt32(buffer, 0);
                                }
                                else if (buffer.Length > 0)
                                {
                                    battery = buffer[0];
                                }

                                if (battery is < 0 or > 100) continue;

                                uint problemCode = GetUInt32Property(hDevInfo, ref devData, PKEY_Device_ProblemCode);
                                if (problemCode != 0) continue;

                                string rawName = GetStringProperty(hDevInfo, ref devData, PKEY_Device_FriendlyName)
                                    ?? GetStringProperty(hDevInfo, ref devData, PKEY_NAME)
                                    ?? GetStringProperty(hDevInfo, ref devData, PKEY_Device_DeviceDesc)
                                    ?? "Unknown Device";

                                string cleanName = CleanDeviceName(rawName);
                                string instanceId = GetStringProperty(hDevInfo, ref devData, PKEY_Device_InstanceId) ?? "";
                                string category = GetStringProperty(hDevInfo, ref devData, PKEY_DeviceContainer_Category) ?? "";

                                DeviceKind kind = ResolveDeviceKind(cleanName, rawName, category, overrides);

                                // Avoid duplicates; prefer the one with highest battery percentage or cleaner name
                                if (!foundMap.TryGetValue(cleanName, out var existing) || battery > existing.Percentage)
                                {
                                    foundMap[cleanName] = new PeripheralInfo(cleanName, kind, battery, instanceId, true);
                                }
                            }
                        }
                    }

                    list.AddRange(foundMap.Values);
                }
                finally
                {
                    SetupDiDestroyDeviceInfoList(hDevInfo);
                }
            }

            // 2. Fallback path for devices reporting via classic registry keys
            if (list.Count == 0)
            {
                list.AddRange(GetPeripheralsFromRegistry(overrides));
            }
        }
        catch { }

        return list;
    }

    /// <summary>
    /// Discovers all paired or connected Bluetooth devices in the system.
    /// </summary>
    public static List<BluetoothDeviceInfo> GetDiscoveredBluetoothDevices(IReadOnlyDictionary<string, DeviceKind>? overrides = null)
    {
        var results = new List<BluetoothDeviceInfo>();
        if (!OperatingSystem.IsWindows()) return results;

        try
        {
            var connectedPeripherals = GetConnectedPeripherals(overrides);
            var connectedMap = new Dictionary<string, PeripheralInfo>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in connectedPeripherals)
            {
                connectedMap[p.Name] = p;
            }

            var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var enumerator in new[] { "BTHLE", "BTHENUM" })
            {
                IntPtr hDevs = SetupDiGetClassDevsW(IntPtr.Zero, enumerator, IntPtr.Zero, DIGCF_PRESENT | DIGCF_ALLCLASSES);
                if (hDevs == IntPtr.Zero || hDevs == new IntPtr(-1)) continue;

                try
                {
                    SP_DEVINFO_DATA dData = new() { cbSize = (uint)Marshal.SizeOf<SP_DEVINFO_DATA>() };
                    uint idx = 0;
                    while (SetupDiEnumDeviceInfo(hDevs, idx++, ref dData))
                    {
                        string rawName = GetStringProperty(hDevs, ref dData, PKEY_Device_FriendlyName)
                            ?? GetStringProperty(hDevs, ref dData, PKEY_NAME)
                            ?? GetStringProperty(hDevs, ref dData, PKEY_Device_DeviceDesc)
                            ?? "";

                        string id = GetStringProperty(hDevs, ref dData, PKEY_Device_InstanceId) ?? "";
                        if (string.IsNullOrWhiteSpace(rawName)) continue;

                        // Filter out virtual/protocol services
                        if (IsVirtualBluetoothService(rawName, id)) continue;

                        string clean = CleanDeviceName(rawName);
                        if (!seenNames.Add(clean)) continue;

                        bool isConnected = connectedMap.TryGetValue(clean, out var conn);
                        int? battery = isConnected ? conn?.Percentage : null;
                        DeviceKind kind = ResolveDeviceKind(clean, rawName, null, overrides);

                        results.Add(new BluetoothDeviceInfo(rawName, clean, id, kind, isConnected, battery));
                    }
                }
                finally
                {
                    SetupDiDestroyDeviceInfoList(hDevs);
                }
            }
        }
        catch { }

        return results;
    }

    private static bool IsVirtualBluetoothService(string name, string instanceId)
    {
        var lower = name.ToLowerInvariant();
        if (lower.Contains("服务") || lower.Contains("service") || lower.Contains("外围设备") ||
            lower.Contains("串行") || lower.Contains("rfcomm") || lower.Contains("sms") ||
            lower.Contains("mms") || lower.Contains("lyra") || lower.Contains("chat") ||
            lower.Contains("adapter") || lower.Contains("适配器"))
        {
            return true;
        }

        // Parent enumerator devices like MS_BTHLE or protocol GUIDs without friendly names
        if (instanceId.StartsWith(@"BTH\") || instanceId.StartsWith(@"ROOT\")) return true;

        return false;
    }

    public static DeviceKind ResolveDeviceKind(
        string cleanName,
        string rawName,
        string? category = null,
        IReadOnlyDictionary<string, DeviceKind>? overrides = null)
    {
        if (overrides != null)
        {
            if (overrides.TryGetValue(cleanName, out var k1)) return k1;
            if (overrides.TryGetValue(rawName, out var k2)) return k2;
        }

        return InferDeviceKind(cleanName, category);
    }

    public static DeviceKind InferDeviceKind(string name, string? category = null)
    {
        if (!string.IsNullOrEmpty(category) && category.Contains("Gamepad", StringComparison.OrdinalIgnoreCase))
        {
            return DeviceKind.Gamepad;
        }

        var lower = name.ToLowerInvariant();

        // Gamepad / Controllers
        if (lower.Contains("controller") || lower.Contains("gamepad") || lower.Contains("xbox") ||
            lower.Contains("dualsense") || lower.Contains("dualshock") || lower.Contains("switch") ||
            lower.Contains("joy-con") || lower.Contains("flydigi") || lower.Contains("betop") ||
            lower.Contains("手柄") || lower.Contains("摇杆") || lower.Contains("八位堂") ||
            lower.Contains("8bitdo") || lower.Contains("gamesir"))
        {
            return DeviceKind.Gamepad;
        }

        // Headphones / Earphones
        if (lower.Contains("headset") || lower.Contains("headphone") || lower.Contains("earphone") ||
            lower.Contains("earbuds") || lower.Contains("airpod") || lower.Contains("buds") ||
            lower.Contains("audio") || lower.Contains("soundcore") || lower.Contains("qcy") ||
            lower.Contains("melo") || lower.Contains("edifier") || lower.Contains("漫步者") ||
            lower.Contains("耳机") || lower.Contains("耳麦") || lower.Contains("hands-free"))
        {
            return DeviceKind.Headphones;
        }

        // Mice
        if (lower.Contains("mouse") || lower.Contains("鼠标") || lower.Contains("sc580") ||
            lower.Contains("g304") || lower.Contains("g502") || lower.Contains("master") ||
            lower.Contains("trackball") || lower.Contains("touchpad"))
        {
            return DeviceKind.Mouse;
        }

        // Keyboards
        if (lower.Contains("keyboard") || lower.Contains("键盘") || lower.Contains("kbd") ||
            lower.Contains("keychron") || lower.Contains("nuphy") || lower.Contains("filco") ||
            lower.Contains("ikbc"))
        {
            return DeviceKind.Keyboard;
        }

        // Phones
        if (lower.Contains("phone") || lower.Contains("手机") || lower.Contains("redmi") ||
            lower.Contains("iphone") || lower.Contains("galaxy") || lower.Contains("huawei") ||
            lower.Contains("xiaomi") || lower.Contains("honor") || lower.Contains("oppo") ||
            lower.Contains("vivo"))
        {
            return DeviceKind.Phone;
        }

        return DeviceKind.Other;
    }

    // Only ever reached after the OperatingSystem.IsWindows() guard in GetConnectedPeripherals,
    // so declaring the Windows requirement here (instead of on the public entry points) is exact.
    [SupportedOSPlatform("windows")]
    private static List<PeripheralInfo> GetPeripheralsFromRegistry(IReadOnlyDictionary<string, DeviceKind>? overrides)
    {
        var list = new List<PeripheralInfo>();
        string[] rootPaths = [
            @"SYSTEM\CurrentControlSet\Enum\BTHLEDevice",
            @"SYSTEM\CurrentControlSet\Enum\BTHLE",
            @"SYSTEM\CurrentControlSet\Enum\BTHENUM",
            @"SYSTEM\CurrentControlSet\Enum\HID"
        ];

        foreach (var rootPath in rootPaths)
        {
            using var root = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(rootPath);
            if (root == null) continue;

            foreach (var subKeyName in root.GetSubKeyNames())
            {
                using var subKey = root.OpenSubKey(subKeyName);
                if (subKey == null) continue;

                foreach (var instanceName in subKey.GetSubKeyNames())
                {
                    using var instanceKey = subKey.OpenSubKey(instanceName);
                    if (instanceKey == null) continue;

                    using var devParams = instanceKey.OpenSubKey("Device Parameters");
                    if (devParams == null) continue;

                    var batteryObj = devParams.GetValue("BatteryPercentage") ?? devParams.GetValue("BatteryLifePercent");
                    if (batteryObj is int or byte or short or long)
                    {
                        int percent = Convert.ToInt32(batteryObj);
                        if (percent is >= 0 and <= 100)
                        {
                            var rawName = instanceKey.GetValue("FriendlyName") as string
                                ?? instanceKey.GetValue("DeviceDesc") as string
                                ?? subKeyName;

                            string clean = CleanDeviceName(rawName);
                            var kind = ResolveDeviceKind(clean, rawName, null, overrides);
                            list.Add(new PeripheralInfo(clean, kind, percent, $"{rootPath}\\{subKeyName}\\{instanceName}"));
                        }
                    }
                }
            }
        }

        return list;
    }

    private static string? GetStringProperty(IntPtr hDevInfo, ref SP_DEVINFO_DATA devData, DEVPROPKEY key)
    {
        if (SetupDiGetDevicePropertyW(hDevInfo, ref devData, ref key, out _, null, 0, out uint reqSize, 0) || reqSize > 0)
        {
            byte[] buf = new byte[reqSize];
            if (SetupDiGetDevicePropertyW(hDevInfo, ref devData, ref key, out uint type, buf, reqSize, out _, 0))
            {
                if (type == 18) // DEVPROP_TYPE_STRING
                {
                    return Encoding.Unicode.GetString(buf).TrimEnd('\0');
                }
            }
        }
        return null;
    }

    private static uint GetUInt32Property(IntPtr hDevInfo, ref SP_DEVINFO_DATA devData, DEVPROPKEY key)
    {
        if (SetupDiGetDevicePropertyW(hDevInfo, ref devData, ref key, out _, null, 0, out uint reqSize, 0) || reqSize > 0)
        {
            byte[] buf = new byte[reqSize];
            if (SetupDiGetDevicePropertyW(hDevInfo, ref devData, ref key, out uint type, buf, reqSize, out _, 0))
            {
                if (type == 7 && buf.Length >= 4) return BitConverter.ToUInt32(buf, 0);
            }
        }
        return 0;
    }
}

