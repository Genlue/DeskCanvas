using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Shortcuts.Models;

namespace Shortcuts.Services.Executors;

/// <summary>Master volume via the Core Audio endpoint (the same interop shape as ControlCenter).</summary>
internal static class CoreAudio
{
    private enum EDataFlow { eRender = 0 }

    private enum ERole { eMultimedia = 1 }

    public static void SetMasterVolume(double percent)
    {
        float scalar = (float)Math.Clamp(percent / 100.0, 0.0, 1.0);
        var endpoint = GetEndpointVolume();
        if (endpoint == null)
            throw new ActionFailedException(Loc.Get("Shortcuts_Error_NoService"));

        try
        {
            var guid = Guid.Empty;
            endpoint.SetMasterVolumeLevelScalar(scalar, ref guid);
            if (scalar > 0.005f)
            {
                endpoint.GetMute(out var muted);
                if (muted) endpoint.SetMute(false, ref guid);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(endpoint);
        }
    }

    /// <summary>Toggle mute; returns the resulting muted state.</summary>
    public static bool ToggleMute()
    {
        var endpoint = GetEndpointVolume();
        if (endpoint == null)
            throw new ActionFailedException(Loc.Get("Shortcuts_Error_NoService"));

        try
        {
            endpoint.GetMute(out var muted);
            var next = !muted;
            var guid = Guid.Empty;
            endpoint.SetMute(next, ref guid);
            return next;
        }
        finally
        {
            Marshal.ReleaseComObject(endpoint);
        }
    }

    private static IAudioEndpointVolume? GetEndpointVolume()
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

            if (enumerator.GetDefaultAudioEndpoint(EDataFlow.eRender, ERole.eMultimedia, out device) != 0 || device == null)
                return null;

            var iid = typeof(IAudioEndpointVolume).GUID;
            if (device.Activate(ref iid, 0x17 /* CLSCTX_ALL */, IntPtr.Zero, out var obj) == 0 && obj is IAudioEndpointVolume endpoint)
                return endpoint;
        }
        catch
        {
            return null;
        }
        finally
        {
            if (device != null) Marshal.ReleaseComObject(device);
            if (enumerator != null) Marshal.ReleaseComObject(enumerator);
        }

        return null;
    }

    // ---- COM declarations (vtable order must match the SDK interfaces) -----------------

    [ComImport]
    [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig]
        int EnumAudioEndpoints(EDataFlow dataFlow, uint dwStateMask, out IntPtr ppDevices);

        [PreserveSig]
        int GetDefaultAudioEndpoint(EDataFlow dataFlow, ERole role, out IMMDevice? ppEndpoint);
    }

    [ComImport]
    [Guid("D666063F-1587-4E43-81F1-B948E807363F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig]
        int Activate(ref Guid iid, uint dwClsCtx, IntPtr pActivationParams, [MarshalAs(UnmanagedType.IUnknown)] out object? ppInterface);
    }

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
}

internal sealed class SetVolumeExecutor : IActionExecutor
{
    public Task<string?> ExecuteAsync(ShortcutAction action, RunContext context)
    {
        var level = Math.Round(context.ResolveNumberClamped(action, "level", 50));
        CoreAudio.SetMasterVolume(level);
        return Task.FromResult<string?>(Loc.Format("Shortcuts_Output_Volume", (int)level));
    }
}

internal sealed class MuteToggleExecutor : IActionExecutor
{
    public Task<string?> ExecuteAsync(ShortcutAction action, RunContext context)
    {
        var muted = CoreAudio.ToggleMute();
        return Task.FromResult<string?>(Loc.Get(muted ? "Shortcuts_Output_Muted" : "Shortcuts_Output_Unmuted"));
    }
}

internal sealed class LockScreenExecutor : IActionExecutor
{
    [DllImport("user32.dll")]
    private static extern bool LockWorkStation();

    public Task<string?> ExecuteAsync(ShortcutAction action, RunContext context)
    {
        if (!LockWorkStation())
            throw new ActionFailedException(Loc.Get("Shortcuts_Error_NoService"));
        return Task.FromResult<string?>(Loc.Get("Shortcuts_Output_Locked"));
    }
}

internal sealed class SleepExecutor : IActionExecutor
{
    [DllImport("powrprof.dll", SetLastError = true)]
    private static extern bool SetSuspendState(bool hibernate, bool forceCritical, bool disableWakeEvent);

    public Task<string?> ExecuteAsync(ShortcutAction action, RunContext context)
    {
        // Give the output one beat to surface in the UI before the machine suspends.
        _ = Task.Run(async () =>
        {
            await Task.Delay(400).ConfigureAwait(false);
            try { SetSuspendState(false, false, false); } catch { }
        });
        return Task.FromResult<string?>(Loc.Get("Shortcuts_Output_Sleeping"));
    }
}

internal sealed class ScreenshotExecutor : IActionExecutor
{
    private const int SmXVirtualScreen = 76;
    private const int SmYVirtualScreen = 77;
    private const int SmCxVirtualScreen = 78;
    private const int SmCyVirtualScreen = 79;

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    public async Task<string?> ExecuteAsync(ShortcutAction action, RunContext context)
    {
        await Task.Run(() =>
        {
            var x = GetSystemMetrics(SmXVirtualScreen);
            var y = GetSystemMetrics(SmYVirtualScreen);
            var width = GetSystemMetrics(SmCxVirtualScreen);
            var height = GetSystemMetrics(SmCyVirtualScreen);
            if (width <= 0 || height <= 0)
                throw new ActionFailedException(Loc.Get("Shortcuts_Error_NoService"));

            var directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Screenshots");
            Directory.CreateDirectory(directory);
            var file = Path.Combine(directory, $"Shortcut_{DateTime.Now:yyyyMMdd_HHmmss}.png");

            using var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            using (var graphics = Graphics.FromImage(bitmap))
            {
                graphics.CopyFromScreen(x, y, 0, 0, new Size(width, height), CopyPixelOperation.SourceCopy);
            }
            bitmap.Save(file, ImageFormat.Png);
        }).ConfigureAwait(true);

        return Loc.Get("Shortcuts_Output_Screenshot");
    }
}

internal sealed class EmptyRecycleBinExecutor : IActionExecutor
{
    private const uint SherbNoConfirmation = 0x00000001;
    private const uint SherbNoProgressUi = 0x00000002;
    private const uint SherbNoSound = 0x00000004;
    private const int E_UNEXPECTED = unchecked((int)0x8000FFFF);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHEmptyRecycleBin(IntPtr hwnd, string? pszRootPath, uint dwFlags);

    public async Task<string?> ExecuteAsync(ShortcutAction action, RunContext context)
    {
        if (!await context.ConfirmDestructiveAsync().ConfigureAwait(true))
            throw new ActionFailedException(Loc.Get("Shortcuts_Error_ConfirmReject"));

        var result = await Task.Run(() =>
            SHEmptyRecycleBin(IntPtr.Zero, null, SherbNoConfirmation | SherbNoProgressUi | SherbNoSound))
            .ConfigureAwait(true);

        // S_OK = emptied; E_UNEXPECTED = already empty (treated as success).
        if (result != 0 && result != E_UNEXPECTED)
            throw new ActionFailedException("SHEmptyRecycleBin: 0x" + result.ToString("X8"));

        return Loc.Get(result == E_UNEXPECTED
            ? "Shortcuts_Output_BinAlreadyEmpty"
            : "Shortcuts_Output_BinEmptied");
    }
}

internal sealed class WaitExecutor : IActionExecutor
{
    public async Task<string?> ExecuteAsync(ShortcutAction action, RunContext context)
    {
        var seconds = Math.Round(context.ResolveNumberClamped(action, "seconds", 1));
        if (seconds > 0)
            await Task.Delay(TimeSpan.FromSeconds(seconds), context.Token);

        return Loc.Format("Shortcuts_Param_SecondsUnit", (int)seconds);
    }
}

/// <summary>Minimize every top-level window through the shell's own verb (no key injection).</summary>
internal sealed class MinimizeAllExecutor : IActionExecutor
{
    public Task<string?> ExecuteAsync(ShortcutAction action, RunContext context)
    {
        object? shell = null;
        try
        {
            var type = Type.GetTypeFromProgID("Shell.Application");
            if (type == null)
                throw new ActionFailedException(Loc.Get("Shortcuts_Error_NoService"));

            shell = Activator.CreateInstance(type);
            if (shell == null)
                throw new ActionFailedException(Loc.Get("Shortcuts_Error_NoService"));

            type.InvokeMember("MinimizeAll", System.Reflection.BindingFlags.InvokeMethod,
                null, shell, null);
        }
        catch (ActionFailedException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new ActionFailedException(Loc.Get("Shortcuts_Error_NoService") + " - " + ex.Message);
        }
        finally
        {
            if (shell != null && Marshal.IsComObject(shell))
                Marshal.ReleaseComObject(shell);
        }

        return Task.FromResult<string?>(Loc.Get("Shortcuts_Output_Minimized"));
    }
}

/// <summary>Windows toast notification; falls back to the PowerShell host AUMID for unpackaged apps.</summary>
internal sealed class NotifyExecutor : IActionExecutor
{
    private const string PowerShellAumid = "{1AC14E77-02E7-4E5D-B744-2EB1AE5198B7}\\WindowsPowerShell\\v1.0\\powershell.exe";

    public async Task<string?> ExecuteAsync(ShortcutAction action, RunContext context)
    {
        var message = context.Resolve(action, "message").Trim();
        if (message.Length == 0)
            throw new ActionFailedException(Loc.Format("Shortcuts_Error_EmptyParam", Loc.Get("Shortcuts_Param_message")));

        await Task.Run(() =>
        {
            try
            {
                Show(message, null);
            }
            catch
            {
                try
                {
                    Show(message, PowerShellAumid);
                }
                catch
                {
                    throw new ActionFailedException(Loc.Get("Shortcuts_Error_Notification"));
                }
            }
        }).ConfigureAwait(true);

        return Loc.Get("Shortcuts_Output_NotificationSent");
    }

    private static void Show(string message, string? appUserModelId)
    {
        var escaped = System.Security.SecurityElement.Escape(message) ?? string.Empty;
        var xml = new Windows.Data.Xml.Dom.XmlDocument();
        xml.LoadXml(
            "<toast><visual><binding template=\"ToastGeneric\"><text>" + escaped +
            "</text></binding></visual></toast>");
        var toast = new Windows.UI.Notifications.ToastNotification(xml);
        var notifier = appUserModelId == null
            ? Windows.UI.Notifications.ToastNotificationManager.CreateToastNotifier()
            : Windows.UI.Notifications.ToastNotificationManager.CreateToastNotifier(appUserModelId);
        notifier.Show(toast);
    }
}
