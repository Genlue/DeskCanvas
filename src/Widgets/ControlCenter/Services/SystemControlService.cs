using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Management;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using Windows.Devices.Radios;
using Windows.Networking.Connectivity;

namespace ControlCenter.Services;

public record AudioDeviceInfo(string Id, string FullName, string ShortName, bool IsDefault);

public class SystemControlSnapshot
{
    public double Brightness { get; init; } = 75;
    public bool BrightnessSupported { get; init; } = true;
    public double Volume { get; init; } = 50;
    public bool IsMuted { get; init; }
    public double MicVolume { get; init; } = 100;
    public bool IsMicMuted { get; init; }
    public bool HasMicrophone { get; init; } = true;
    public IReadOnlyList<AudioDeviceInfo> AudioDevices { get; init; } = [];
    public string CurrentAudioName { get; init; } = "扬声器";
    public bool IsWifiEnabled { get; init; } = true;
    public bool IsWifiConnected { get; init; } = true;
    public string WifiSubtitle { get; init; } = "已连接";
    public bool IsBluetoothEnabled { get; init; }
    public string BluetoothSubtitle { get; init; } = "已关闭";
    public bool IsSystemDarkMode { get; init; } = true;
    public bool AreDesktopIconsVisible { get; init; } = true;
}

public sealed class SystemControlService : IDisposable
{
    private static readonly Lazy<SystemControlService> SharedInstance = new(() => new SystemControlService());
    public static SystemControlService Shared => SharedInstance.Value;

    private double cachedBrightness = 80;
    private bool brightnessSupported = true;
    private int pendingBrightness = -1;
    private int brightnessWorkerRunning;
    private DateTime lastBrightnessPollUtc = DateTime.MinValue;

    private bool cachedWifiEnabled = true;
    private bool cachedWifiConnected = true;
    private string cachedWifiSubtitle = "Wi-Fi";
    private bool cachedBluetoothEnabled;
    private string cachedBluetoothSubtitle = "关闭";
    private DateTime lastRadioPollUtc = DateTime.MinValue;
    private int radioPollRunning;

    public SystemControlSnapshot GetSnapshot(bool forceSlowQueries = false)
    {
        var (vol, muted) = GetMasterVolumeAndMute();
        var (micVol, micMuted, hasMic) = GetMicVolumeAndMute();
        var audioDevices = GetAudioOutputDevices();
        var defaultAudio = audioDevices.FirstOrDefault(d => d.IsDefault) ?? audioDevices.FirstOrDefault();
        var isDark = GetSystemDarkMode();
        var iconsVisible = GetDesktopIconsVisible();

        var now = DateTime.UtcNow;
        if (forceSlowQueries || (now - lastBrightnessPollUtc).TotalSeconds >= 4)
        {
            lastBrightnessPollUtc = now;
            _ = Task.Run(RefreshBrightnessCache);
        }

        if (forceSlowQueries || (now - lastRadioPollUtc).TotalSeconds >= 3)
        {
            lastRadioPollUtc = now;
            _ = Task.Run(RefreshRadioAndNetworkCacheAsync);
        }

        return new SystemControlSnapshot
        {
            Brightness = cachedBrightness,
            BrightnessSupported = brightnessSupported,
            Volume = vol,
            IsMuted = muted,
            MicVolume = micVol,
            IsMicMuted = micMuted,
            HasMicrophone = hasMic,
            AudioDevices = audioDevices,
            CurrentAudioName = defaultAudio?.ShortName ?? "默认输出",
            IsWifiEnabled = cachedWifiEnabled,
            IsWifiConnected = cachedWifiConnected,
            WifiSubtitle = cachedWifiSubtitle,
            IsBluetoothEnabled = cachedBluetoothEnabled,
            BluetoothSubtitle = cachedBluetoothSubtitle,
            IsSystemDarkMode = isDark,
            AreDesktopIconsVisible = iconsVisible
        };
    }

    #region 1. Master Volume & Microphone (Core Audio COM)

    public (double VolumePercent, bool IsMuted) GetMasterVolumeAndMute()
    {
        try
        {
            var ep = GetAudioEndpointVolume(EDataFlow.eRender, ERole.eMultimedia);
            if (ep == null) return (50, false);
            try
            {
                ep.GetMasterVolumeLevelScalar(out float level);
                ep.GetMute(out bool mute);
                return (Math.Round(Math.Clamp(level * 100.0, 0, 100)), mute);
            }
            finally
            {
                Marshal.ReleaseComObject(ep);
            }
        }
        catch
        {
            return (50, false);
        }
    }

    public void SetMasterVolume(double percent)
    {
        try
        {
            float scalar = (float)Math.Clamp(percent / 100.0, 0.0, 1.0);
            var ep = GetAudioEndpointVolume(EDataFlow.eRender, ERole.eMultimedia);
            if (ep == null) return;
            try
            {
                var guid = Guid.Empty;
                ep.SetMasterVolumeLevelScalar(scalar, ref guid);
                if (scalar > 0.005f)
                {
                    ep.GetMute(out bool muted);
                    if (muted) ep.SetMute(false, ref guid);
                }
            }
            finally
            {
                Marshal.ReleaseComObject(ep);
            }
        }
        catch { }
    }

    public bool ToggleMasterMute()
    {
        try
        {
            var ep = GetAudioEndpointVolume(EDataFlow.eRender, ERole.eMultimedia);
            if (ep == null) return false;
            try
            {
                ep.GetMute(out bool muted);
                bool next = !muted;
                var guid = Guid.Empty;
                ep.SetMute(next, ref guid);
                return next;
            }
            finally
            {
                Marshal.ReleaseComObject(ep);
            }
        }
        catch
        {
            return false;
        }
    }

    public (double VolumePercent, bool IsMuted, bool HasMic) GetMicVolumeAndMute()
    {
        try
        {
            var ep = GetAudioEndpointVolume(EDataFlow.eCapture, ERole.eCommunications)
                     ?? GetAudioEndpointVolume(EDataFlow.eCapture, ERole.eConsole);
            if (ep == null) return (0, true, false);
            try
            {
                ep.GetMasterVolumeLevelScalar(out float level);
                ep.GetMute(out bool mute);
                return (Math.Round(Math.Clamp(level * 100.0, 0, 100)), mute, true);
            }
            finally
            {
                Marshal.ReleaseComObject(ep);
            }
        }
        catch
        {
            return (0, true, false);
        }
    }

    public void SetMicVolume(double percent)
    {
        try
        {
            float scalar = (float)Math.Clamp(percent / 100.0, 0.0, 1.0);
            var ep = GetAudioEndpointVolume(EDataFlow.eCapture, ERole.eCommunications)
                     ?? GetAudioEndpointVolume(EDataFlow.eCapture, ERole.eConsole);
            if (ep == null) return;
            try
            {
                var guid = Guid.Empty;
                ep.SetMasterVolumeLevelScalar(scalar, ref guid);
                if (scalar > 0.01f)
                {
                    ep.GetMute(out bool muted);
                    if (muted) ep.SetMute(false, ref guid);
                }
            }
            finally
            {
                Marshal.ReleaseComObject(ep);
            }
        }
        catch { }
    }

    public bool ToggleMicMute()
    {
        try
        {
            var ep = GetAudioEndpointVolume(EDataFlow.eCapture, ERole.eCommunications)
                     ?? GetAudioEndpointVolume(EDataFlow.eCapture, ERole.eConsole);
            if (ep == null) return true;
            try
            {
                ep.GetMute(out bool muted);
                bool next = !muted;
                var guid = Guid.Empty;
                ep.SetMute(next, ref guid);
                return next;
            }
            finally
            {
                Marshal.ReleaseComObject(ep);
            }
        }
        catch
        {
            return true;
        }
    }

    #endregion

    #region 2. Audio Output Devices Enumeration & Switching

    public IReadOnlyList<AudioDeviceInfo> GetAudioOutputDevices()
    {
        var list = new List<AudioDeviceInfo>();
        IMMDeviceEnumerator? enumerator = null;
        IMMDeviceCollection? collection = null;
        IMMDevice? defaultDevice = null;

        try
        {
            var clsid = new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E");
            var type = Type.GetTypeFromCLSID(clsid);
            if (type == null) return list;

            enumerator = (IMMDeviceEnumerator?)Activator.CreateInstance(type);
            if (enumerator == null) return list;

            string? defaultId = null;
            if (enumerator.GetDefaultAudioEndpoint(EDataFlow.eRender, ERole.eMultimedia, out defaultDevice) == 0 && defaultDevice != null)
            {
                defaultDevice.GetId(out defaultId);
            }

            const uint DEVICE_STATE_ACTIVE = 0x00000001;
            if (enumerator.EnumAudioEndpoints(EDataFlow.eRender, DEVICE_STATE_ACTIVE, out collection) == 0 && collection != null)
            {
                collection.GetCount(out uint count);
                var pkeyFriendlyName = new PROPERTYKEY(new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), 14);

                for (uint i = 0; i < count; i++)
                {
                    if (collection.Item(i, out var dev) == 0 && dev != null)
                    {
                        try
                        {
                            dev.GetId(out string id);
                            string fullName = "音频输出设备";
                            if (dev.OpenPropertyStore(0, out var store) == 0 && store != null)
                            {
                                try
                                {
                                    if (store.GetValue(ref pkeyFriendlyName, out var prop) == 0)
                                    {
                                        var str = prop.GetString();
                                        if (!string.IsNullOrWhiteSpace(str))
                                            fullName = str.Trim();
                                        PropVariantClear(ref prop);
                                    }
                                }
                                finally
                                {
                                    Marshal.ReleaseComObject(store);
                                }
                            }

                            string shortName = SimplifyAudioDeviceName(fullName);
                            bool isDef = !string.IsNullOrEmpty(defaultId) && string.Equals(id, defaultId, StringComparison.OrdinalIgnoreCase);
                            list.Add(new AudioDeviceInfo(id, fullName, shortName, isDef));
                        }
                        finally
                        {
                            Marshal.ReleaseComObject(dev);
                        }
                    }
                }
            }
        }
        catch { }
        finally
        {
            if (defaultDevice != null) Marshal.ReleaseComObject(defaultDevice);
            if (collection != null) Marshal.ReleaseComObject(collection);
            if (enumerator != null) Marshal.ReleaseComObject(enumerator);
        }

        return list;
    }

    public bool SetDefaultAudioOutputDevice(string deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId)) return false;
        try
        {
            var clsid = new Guid("870AF99C-171D-4F9E-AF0D-E63DF40C2BC9");
            var type = Type.GetTypeFromCLSID(clsid);
            if (type == null) return false;

            var instance = Activator.CreateInstance(type);
            if (instance == null) return false;

            try
            {
                if (instance is IPolicyConfig policyConfig)
                {
                    policyConfig.SetDefaultEndpoint(deviceId, ERole.eConsole);
                    policyConfig.SetDefaultEndpoint(deviceId, ERole.eMultimedia);
                    policyConfig.SetDefaultEndpoint(deviceId, ERole.eCommunications);
                    return true;
                }
                if (instance is IPolicyConfigVista policyVista)
                {
                    policyVista.SetDefaultEndpoint(deviceId, ERole.eConsole);
                    policyVista.SetDefaultEndpoint(deviceId, ERole.eMultimedia);
                    policyVista.SetDefaultEndpoint(deviceId, ERole.eCommunications);
                    return true;
                }
            }
            finally
            {
                Marshal.ReleaseComObject(instance);
            }
        }
        catch { }
        return false;
    }

    public string CycleNextAudioOutputDevice()
    {
        var devices = GetAudioOutputDevices();
        if (devices.Count == 0) return "无设备";
        if (devices.Count == 1) return devices[0].ShortName;

        int currentIndex = -1;
        for (int i = 0; i < devices.Count; i++)
        {
            if (devices[i].IsDefault)
            {
                currentIndex = i;
                break;
            }
        }

        int nextIndex = (currentIndex + 1) % devices.Count;
        var nextDevice = devices[nextIndex];
        SetDefaultAudioOutputDevice(nextDevice.Id);
        return nextDevice.ShortName;
    }

    private static string SimplifyAudioDeviceName(string fullName)
    {
        int openParen = fullName.IndexOf('(');
        int closeParen = fullName.LastIndexOf(')');
        if (openParen > 0 && closeParen > openParen + 1)
        {
            string prefix = fullName[..openParen].Trim();
            string inside = fullName[(openParen + 1)..closeParen].Trim();
            if (prefix.Length <= 8 && !string.IsNullOrEmpty(inside))
                return $"{prefix} · {inside}";
        }
        return fullName;
    }

    #endregion

    #region 3. Screen Brightness (WMI + DDC/CI Dual Channel)

    public void SetBrightness(double percent)
    {
        int clamped = (int)Math.Round(Math.Clamp(percent, 0, 100));
        cachedBrightness = clamped;
        Interlocked.Exchange(ref pendingBrightness, clamped);

        if (Interlocked.CompareExchange(ref brightnessWorkerRunning, 1, 0) == 0)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    while (true)
                    {
                        int target = Interlocked.Exchange(ref pendingBrightness, -1);
                        if (target < 0) break;

                        ApplyBrightnessHardware(target);
                        lastBrightnessPollUtc = DateTime.UtcNow;
                        await Task.Delay(75);
                    }
                }
                finally
                {
                    Interlocked.Exchange(ref brightnessWorkerRunning, 0);
                }
            });
        }
    }

    private void RefreshBrightnessCache()
    {
        if (pendingBrightness >= 0 || brightnessWorkerRunning != 0) return;

        // 1. Try WMI (Internal Laptop / AIO Display)
        try
        {
            using var searcher = new ManagementObjectSearcher(@"root\WMI", "SELECT CurrentBrightness FROM WmiMonitorBrightness");
            using var collection = searcher.Get();
            foreach (ManagementObject obj in collection)
            {
                using (obj)
                {
                    if (obj["CurrentBrightness"] is byte b)
                    {
                        cachedBrightness = b;
                        brightnessSupported = true;
                        return;
                    }
                }
            }
        }
        catch { }

        // 2. Fallback: Try DDC/CI (External Desktop Monitors)
        try
        {
            int ddcValue = -1;
            EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr hMonitor, IntPtr hdc, ref RECT rect, IntPtr data) =>
            {
                if (GetNumberOfPhysicalMonitorsFromHMONITOR(hMonitor, out uint count) && count > 0)
                {
                    var monitors = new PHYSICAL_MONITOR[count];
                    if (GetPhysicalMonitorsFromHMONITOR(hMonitor, count, monitors))
                    {
                        try
                        {
                            for (int i = 0; i < count; i++)
                            {
                                if (GetMonitorBrightness(monitors[i].hPhysicalMonitor, out uint min, out uint cur, out uint max) && max > min)
                                {
                                    ddcValue = (int)Math.Round((cur - min) * 100.0 / (max - min));
                                    return false; // Found first controllable monitor
                                }
                            }
                        }
                        finally
                        {
                            DestroyPhysicalMonitors(count, monitors);
                        }
                    }
                }
                return true;
            }, IntPtr.Zero);

            if (ddcValue >= 0)
            {
                cachedBrightness = Math.Clamp(ddcValue, 0, 100);
                brightnessSupported = true;
                return;
            }
        }
        catch { }
    }

    private static void ApplyBrightnessHardware(int targetPercent)
    {
        bool appliedViaWmi = false;
        try
        {
            using var searcher = new ManagementObjectSearcher(@"root\WMI", "SELECT * FROM WmiMonitorBrightnessMethods");
            using var collection = searcher.Get();
            foreach (ManagementObject obj in collection)
            {
                using (obj)
                {
                    obj.InvokeMethod("WmiSetBrightness", [1u, (byte)targetPercent]);
                    appliedViaWmi = true;
                }
            }
        }
        catch { }

        if (appliedViaWmi) return;

        // Fallback to DDC/CI for external monitors
        try
        {
            EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr hMonitor, IntPtr hdc, ref RECT rect, IntPtr data) =>
            {
                if (GetNumberOfPhysicalMonitorsFromHMONITOR(hMonitor, out uint count) && count > 0)
                {
                    var monitors = new PHYSICAL_MONITOR[count];
                    if (GetPhysicalMonitorsFromHMONITOR(hMonitor, count, monitors))
                    {
                        try
                        {
                            for (int i = 0; i < count; i++)
                            {
                                if (GetMonitorBrightness(monitors[i].hPhysicalMonitor, out uint min, out _, out uint max) && max > min)
                                {
                                    uint rawTarget = (uint)Math.Round(min + (max - min) * (targetPercent / 100.0));
                                    SetMonitorBrightness(monitors[i].hPhysicalMonitor, rawTarget);
                                }
                            }
                        }
                        finally
                        {
                            DestroyPhysicalMonitors(count, monitors);
                        }
                    }
                }
                return true;
            }, IntPtr.Zero);
        }
        catch { }
    }

    #endregion

    #region 4. Wi-Fi & Bluetooth Radios (WinRT Radios + NetworkInformation)

    private async Task RefreshRadioAndNetworkCacheAsync()
    {
        if (Interlocked.CompareExchange(ref radioPollRunning, 1, 0) != 0)
            return;

        try
        {
            bool wifiRadioOn = false;
            bool foundWifiRadio = false;
            bool btRadioOn = false;
            bool foundBtRadio = false;

            try
            {
                var radios = await Radio.GetRadiosAsync();
                foreach (var radio in radios)
                {
                    if (radio.Kind == RadioKind.WiFi)
                    {
                        foundWifiRadio = true;
                        wifiRadioOn = radio.State == RadioState.On;
                    }
                    else if (radio.Kind == RadioKind.Bluetooth)
                    {
                        foundBtRadio = true;
                        btRadioOn = radio.State == RadioState.On;
                    }
                }
            }
            catch { }

            // Check active network interfaces
            bool wifiUp = false;
            bool ethernetUp = false;
            string? wifiName = null;

            try
            {
                var profile = NetworkInformation.GetInternetConnectionProfile();
                if (profile != null && profile.IsWlanConnectionProfile)
                {
                    wifiUp = true;
                    wifiName = profile.WlanConnectionProfileDetails?.GetConnectedSsid();
                    if (string.IsNullOrWhiteSpace(wifiName))
                        wifiName = profile.ProfileName;
                }
            }
            catch { }

            try
            {
                foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (nic.OperationalStatus != OperationalStatus.Up) continue;
                    if (nic.NetworkInterfaceType == NetworkInterfaceType.Wireless80211)
                    {
                        wifiUp = true;
                        if (string.IsNullOrWhiteSpace(wifiName))
                            wifiName = nic.Name;
                    }
                    else if (nic.NetworkInterfaceType == NetworkInterfaceType.Ethernet)
                    {
                        ethernetUp = true;
                    }
                }
            }
            catch { }

            if (!foundWifiRadio)
            {
                cachedWifiEnabled = wifiUp || ethernetUp;
                cachedWifiConnected = wifiUp || ethernetUp;
                cachedWifiSubtitle = wifiUp
                    ? (string.IsNullOrWhiteSpace(wifiName) ? "已连接" : wifiName)
                    : (ethernetUp ? "以太网已连接" : "未连接");
            }
            else
            {
                cachedWifiEnabled = wifiRadioOn;
                cachedWifiConnected = wifiRadioOn && wifiUp;
                cachedWifiSubtitle = !wifiRadioOn
                    ? "已关闭"
                    : wifiUp
                        ? (string.IsNullOrWhiteSpace(wifiName) ? "已连接" : wifiName)
                        : (ethernetUp ? "以太网" : "未连接");
            }

            cachedBluetoothEnabled = foundBtRadio && btRadioOn;
            cachedBluetoothSubtitle = !foundBtRadio
                ? "未检测到适配器"
                : btRadioOn ? "已开启" : "已关闭";
        }
        finally
        {
            Interlocked.Exchange(ref radioPollRunning, 0);
        }
    }

    public async Task<bool> ToggleWifiAsync()
    {
        try
        {
            var radios = await Radio.GetRadiosAsync();
            var wifiRadios = radios.Where(r => r.Kind == RadioKind.WiFi).ToList();
            if (wifiRadios.Count == 0)
            {
                OpenSystemSetting("ms-settings:network-wifi");
                return cachedWifiEnabled;
            }

            bool targetOn = !cachedWifiEnabled;
            foreach (var radio in wifiRadios)
            {
                await radio.SetStateAsync(targetOn ? RadioState.On : RadioState.Off);
            }

            cachedWifiEnabled = targetOn;
            cachedWifiSubtitle = targetOn ? "正在开启…" : "已关闭";
            lastRadioPollUtc = DateTime.MinValue;
            return targetOn;
        }
        catch
        {
            OpenSystemSetting("ms-settings:network-wifi");
            return cachedWifiEnabled;
        }
    }

    public async Task<bool> ToggleBluetoothAsync()
    {
        try
        {
            var radios = await Radio.GetRadiosAsync();
            var btRadios = radios.Where(r => r.Kind == RadioKind.Bluetooth).ToList();
            if (btRadios.Count == 0)
            {
                OpenSystemSetting("ms-settings:bluetooth");
                return cachedBluetoothEnabled;
            }

            bool targetOn = !cachedBluetoothEnabled;
            foreach (var radio in btRadios)
            {
                await radio.SetStateAsync(targetOn ? RadioState.On : RadioState.Off);
            }

            cachedBluetoothEnabled = targetOn;
            cachedBluetoothSubtitle = targetOn ? "已开启" : "已关闭";
            lastRadioPollUtc = DateTime.MinValue;
            return targetOn;
        }
        catch
        {
            OpenSystemSetting("ms-settings:bluetooth");
            return cachedBluetoothEnabled;
        }
    }

    #endregion

    #region 5. Windows System Dark / Light Mode (Registry + Broadcast)

    private const string PersonalizeRegPath = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    public bool GetSystemDarkMode()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PersonalizeRegPath, false);
            if (key?.GetValue("AppsUseLightTheme") is int val)
                return val == 0;
        }
        catch { }
        return true;
    }

    public bool ToggleSystemDarkMode()
    {
        bool nextDark = !GetSystemDarkMode();
        int lightValue = nextDark ? 0 : 1;

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PersonalizeRegPath, true);
            if (key != null)
            {
                key.SetValue("AppsUseLightTheme", lightValue, RegistryValueKind.DWord);
                key.SetValue("SystemUsesLightTheme", lightValue, RegistryValueKind.DWord);
            }

            // Broadcast WM_SETTINGCHANGE so Explorer, Taskbar, and apps following system theme update immediately
            const int HWND_BROADCAST = 0xFFFF;
            const uint WM_SETTINGCHANGE = 0x001A;
            const uint SMTO_ABORTIFHUNG = 0x0002;
            SendMessageTimeout(
                new IntPtr(HWND_BROADCAST),
                WM_SETTINGCHANGE,
                IntPtr.Zero,
                "ImmersiveColorSet",
                SMTO_ABORTIFHUNG,
                200,
                out _);
        }
        catch { }

        return nextDark;
    }

    #endregion

    #region 6. Windows Desktop Icons Show / Hide (SHELLDLL_DefView 0x7402)

    public bool GetDesktopIconsVisible()
    {
        try
        {
            var defView = FindShellDefView(out var listView);
            if (listView != IntPtr.Zero)
                return IsWindowVisible(listView);
            if (defView != IntPtr.Zero)
                return IsWindowVisible(defView);
        }
        catch { }
        return true;
    }

    public bool ToggleDesktopIcons()
    {
        try
        {
            var defView = FindShellDefView(out _);
            if (defView != IntPtr.Zero)
            {
                const uint WM_COMMAND = 0x0111;
                var toggleIconsCmd = new IntPtr(0x7402);
                PostMessage(defView, WM_COMMAND, toggleIconsCmd, IntPtr.Zero);
            }
        }
        catch { }

        return !GetDesktopIconsVisible();
    }

    private static IntPtr FindShellDefView(out IntPtr listViewHwnd)
    {
        listViewHwnd = IntPtr.Zero;
        IntPtr defView = IntPtr.Zero;

        IntPtr progman = FindWindow("Progman", null);
        if (progman != IntPtr.Zero)
        {
            defView = FindWindowEx(progman, IntPtr.Zero, "SHELLDLL_DefView", null);
        }

        if (defView == IntPtr.Zero)
        {
            IntPtr found = IntPtr.Zero;
            EnumWindows((hwnd, _) =>
            {
                IntPtr child = FindWindowEx(hwnd, IntPtr.Zero, "SHELLDLL_DefView", null);
                if (child != IntPtr.Zero)
                {
                    found = child;
                    return false;
                }
                return true;
            }, IntPtr.Zero);
            defView = found;
        }

        if (defView != IntPtr.Zero)
        {
            listViewHwnd = FindWindowEx(defView, IntPtr.Zero, "SysListView32", null);
        }

        return defView;
    }

    #endregion

    #region 7. System Settings Launcher

    public static void OpenSystemSetting(string msSettingsUri)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = msSettingsUri,
                UseShellExecute = true
            });
        }
        catch { }
    }

    #endregion

    #region Native Win32 & Core Audio COM Declarations

    private static IAudioEndpointVolume? GetAudioEndpointVolume(EDataFlow dataFlow, ERole role)
    {
        IMMDeviceEnumerator? enumerator = null;
        IMMDevice? device = null;
        try
        {
            var clsid = new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E");
            var type = Type.GetTypeFromCLSID(clsid);
            if (type == null) return null;

            enumerator = (IMMDeviceEnumerator?)Activator.CreateInstance(type);
            if (enumerator == null) return null;

            if (enumerator.GetDefaultAudioEndpoint(dataFlow, role, out device) != 0 || device == null)
                return null;

            var iid = typeof(IAudioEndpointVolume).GUID;
            if (device.Activate(ref iid, 0x17 /* CLSCTX_ALL */, IntPtr.Zero, out var obj) == 0 && obj is IAudioEndpointVolume ep)
                return ep;
        }
        catch { }
        finally
        {
            if (device != null) Marshal.ReleaseComObject(device);
            if (enumerator != null) Marshal.ReleaseComObject(enumerator);
        }
        return null;
    }

    private enum EDataFlow
    {
        eRender = 0,
        eCapture = 1,
        eAll = 2
    }

    private enum ERole
    {
        eConsole = 0,
        eMultimedia = 1,
        eCommunications = 2
    }

    [ComImport]
    [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig]
        int EnumAudioEndpoints(EDataFlow dataFlow, uint dwStateMask, out IMMDeviceCollection? ppDevices);

        [PreserveSig]
        int GetDefaultAudioEndpoint(EDataFlow dataFlow, ERole role, out IMMDevice? ppEndpoint);
    }

    [ComImport]
    [Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceCollection
    {
        [PreserveSig]
        int GetCount(out uint pcDevices);

        [PreserveSig]
        int Item(uint nDevice, out IMMDevice? ppDevice);
    }

    [ComImport]
    [Guid("D666063F-1587-4E43-81F1-B948E807363F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig]
        int Activate(ref Guid iid, uint dwClsCtx, IntPtr pActivationParams, [MarshalAs(UnmanagedType.IUnknown)] out object? ppInterface);

        [PreserveSig]
        int OpenPropertyStore(uint stgmAccess, out IPropertyStore? ppProperties);

        [PreserveSig]
        int GetId([MarshalAs(UnmanagedType.LPWStr)] out string ppstrId);
    }

    [ComImport]
    [Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        [PreserveSig]
        int GetCount(out uint cProps);

        [PreserveSig]
        int GetAt(uint iProp, out PROPERTYKEY pkey);

        [PreserveSig]
        int GetValue(ref PROPERTYKEY key, out PROPVARIANT pv);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROPERTYKEY(Guid fmtid, uint pid)
    {
        public Guid fmtid = fmtid;
        public uint pid = pid;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROPVARIANT
    {
        public ushort vt;
        public ushort wReserved1;
        public ushort wReserved2;
        public ushort wReserved3;
        public IntPtr p;
        public int p2;

        public readonly string? GetString()
        {
            if (vt == 31 && p != IntPtr.Zero) // VT_LPWSTR
                return Marshal.PtrToStringUni(p);
            return null;
        }
    }

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PROPVARIANT pvar);

    [ComImport]
    [Guid("5CDF2C82-841E-4546-9722-0CF74078229A")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioEndpointVolume
    {
        [PreserveSig] int RegisterControlChangeNotify(IntPtr pNotify);
        [PreserveSig] int UnregisterControlChangeNotify(IntPtr pNotify);
        [PreserveSig] int GetChannelCount(out uint pnChannelCount);
        [PreserveSig] int SetMasterVolumeLevel(float fLevelDB, ref Guid pguidEventContext);
        [PreserveSig] int SetMasterVolumeLevelScalar(float fLevel, ref Guid pguidEventContext);
        [PreserveSig] int GetMasterVolumeLevel(out float pfLevelDB);
        [PreserveSig] int GetMasterVolumeLevelScalar(out float pfLevel);
        [PreserveSig] int SetChannelVolumeLevel(uint nChannel, float fLevelDB, ref Guid pguidEventContext);
        [PreserveSig] int SetChannelVolumeLevelScalar(uint nChannel, float fLevel, ref Guid pguidEventContext);
        [PreserveSig] int GetChannelVolumeLevel(uint nChannel, out float pfLevelDB);
        [PreserveSig] int GetChannelVolumeLevelScalar(uint nChannel, out float pfLevel);
        [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool bMute, ref Guid pguidEventContext);
        [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool pbMute);
    }

    [ComImport]
    [Guid("F8679F50-850A-41CF-9C72-430F290290C8")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPolicyConfig
    {
        [PreserveSig] int GetMixFormat([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, IntPtr ppFormat);
        [PreserveSig] int GetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, bool bDefault, IntPtr ppFormat);
        [PreserveSig] int ResetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName);
        [PreserveSig] int SetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, IntPtr pEndpointFormat, IntPtr mixFormat);
        [PreserveSig] int GetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, bool bDefault, IntPtr pmftDefaultPeriod, IntPtr pmftMinimumPeriod);
        [PreserveSig] int SetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, IntPtr pmftPeriod);
        [PreserveSig] int GetShareMode([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, IntPtr pMode);
        [PreserveSig] int SetShareMode([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, IntPtr mode);
        [PreserveSig] int GetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, bool bFxStore, IntPtr key, IntPtr pv);
        [PreserveSig] int SetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, bool bFxStore, IntPtr key, IntPtr pv);
        [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, ERole role);
        [PreserveSig] int SetEndpointVisibility([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, bool bVisible);
    }

    [ComImport]
    [Guid("568B9108-44BF-40B4-9006-86AFE5B5A620")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPolicyConfigVista
    {
        [PreserveSig] int GetMixFormat([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, IntPtr ppFormat);
        [PreserveSig] int GetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, int bDefault, IntPtr ppFormat);
        [PreserveSig] int SetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, IntPtr pEndpointFormat, IntPtr mixFormat);
        [PreserveSig] int GetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, int bDefault, IntPtr pmftDefaultPeriod, IntPtr pmftMinimumPeriod);
        [PreserveSig] int SetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, IntPtr pmftPeriod);
        [PreserveSig] int GetShareMode([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, IntPtr pMode);
        [PreserveSig] int SetShareMode([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, IntPtr mode);
        [PreserveSig] int GetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, int bFxStore, IntPtr key, IntPtr pv);
        [PreserveSig] int SetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, int bFxStore, IntPtr key, IntPtr pv);
        [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, ERole role);
        [PreserveSig] int SetEndpointVisibility([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, int bVisible);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PHYSICAL_MONITOR
    {
        public IntPtr hPhysicalMonitor;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szPhysicalMonitorDescription;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdcMonitor, ref RECT lprcMonitor, IntPtr dwData);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);

    [DllImport("dxva2.dll", SetLastError = true)]
    private static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(IntPtr hMonitor, out uint pdwNumberOfPhysicalMonitors);

    [DllImport("dxva2.dll", SetLastError = true)]
    private static extern bool GetPhysicalMonitorsFromHMONITOR(IntPtr hMonitor, uint dwPhysicalMonitorArraySize, [Out] PHYSICAL_MONITOR[] pPhysicalMonitorArray);

    [DllImport("dxva2.dll", SetLastError = true)]
    private static extern bool DestroyPhysicalMonitors(uint dwPhysicalMonitorArraySize, [In] PHYSICAL_MONITOR[] pPhysicalMonitorArray);

    [DllImport("dxva2.dll", SetLastError = true)]
    private static extern bool GetMonitorBrightness(IntPtr hMonitor, out uint pdwMinimumBrightness, out uint pdwCurrentBrightness, out uint pdwMaximumBrightness);

    [DllImport("dxva2.dll", SetLastError = true)]
    private static extern bool SetMonitorBrightness(IntPtr hMonitor, uint dwNewBrightness);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr FindWindow(string? lpClassName, string? lpWindowName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr FindWindowEx(IntPtr hwndParent, IntPtr hwndChildAfter, string? lpszClass, string? lpszWindow);

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SendMessageTimeout(
        IntPtr hWnd,
        uint Msg,
        IntPtr wParam,
        string lParam,
        uint fuFlags,
        uint uTimeout,
        out IntPtr lpdwResult);

    #endregion

    public void Dispose()
    {
    }
}
