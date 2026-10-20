using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Threading;

namespace Shortcuts.Services;

/// <summary>Which screen area a screenshot covers.</summary>
public enum ScreenshotScope
{
    /// <summary>Every monitor, as one image.</summary>
    Virtual,

    /// <summary>The primary monitor only.</summary>
    Primary,
}

/// <summary>How a window title is matched.</summary>
public enum WindowMatch
{
    Contains,
    Equals,
    StartsWith,
}

public enum MediaAction
{
    PlayPause,
    Next,
    Previous,
}

/// <summary>
/// Every operating-system effect a shortcut can have, behind one interface. Two reasons:
/// the runner stays testable (checks inject a fake and assert what was requested without
/// touching the real machine), and there is exactly one place where "success" is decided —
/// "the launch request was submitted" instead of three different localized sentences.
/// </summary>
public interface ISystemAdapter
{
    // ---- launching ---------------------------------------------------------------
    void OpenTarget(string target, string? arguments);

    void OpenFolder(string path);

    void OpenUrl(string url);

    // ---- screen ------------------------------------------------------------------
    string CaptureScreenshot(string? directory, ScreenshotScope scope);

    // ---- audio -------------------------------------------------------------------
    double GetVolume();

    void SetVolume(double percent);

    bool GetMute();

    void SetMute(bool muted);

    // ---- session -----------------------------------------------------------------
    void LockWorkStation();

    void Suspend();

    void EmptyRecycleBin();

    void MinimizeAll();

    void ShowNotification(string message, string? title);

    // ---- input -------------------------------------------------------------------
    void TypeText(string text);

    void SendKeySequence(IReadOnlyList<string> combinations);

    void SendHotkey(string gesture, int holdMs);

    // ---- windows -----------------------------------------------------------------
    long FindWindow(string title, WindowMatch match);

    bool WaitForWindow(string title, WindowMatch match, TimeSpan timeout, CancellationToken token, out long handle);

    void ActivateWindow(long handle);

    // ---- media -------------------------------------------------------------------
    Task<bool> MediaTransportAsync(MediaAction action);

    // ---- clipboard ---------------------------------------------------------------
    bool HasClipboard { get; }

    Task<string?> GetClipboardTextAsync();

    Task SetClipboardTextAsync(string text);
}

/// <summary>
/// The real Windows implementation. Keyboard input goes through <see cref="KeyboardInput"/> so
/// the existing stroke-level seam still applies.
/// </summary>
public sealed class HostSystemAdapter(Func<TopLevel?>? topLevelAccessor) : ISystemAdapter
{
    private const int SmXVirtualScreen = 76;
    private const int SmYVirtualScreen = 77;
    private const int SmCxVirtualScreen = 78;
    private const int SmCyVirtualScreen = 79;
    private const int SmCxScreen = 0;
    private const int SmCyScreen = 1;

    private const uint SherbNoConfirmation = 0x00000001;
    private const uint SherbNoProgressUi = 0x00000002;
    private const uint SherbNoSound = 0x00000004;
    private const int E_UNEXPECTED = unchecked((int)0x8000FFFF);

    private const ushort VkControl = 0x11;
    private const ushort VkMenu = 0x12;
    private const ushort VkShift = 0x10;
    private const ushort VkLWin = 0x5B;

    // ---- launching ---------------------------------------------------------------

    public void OpenTarget(string target, string? arguments)
    {
        var startInfo = new ProcessStartInfo { FileName = target, UseShellExecute = true };
        if (!string.IsNullOrWhiteSpace(arguments)) startInfo.Arguments = arguments;
        Process.Start(startInfo);
    }

    public void OpenFolder(string path) =>
        Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });

    public void OpenUrl(string url) =>
        Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });

    // ---- screen ------------------------------------------------------------------

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    public string CaptureScreenshot(string? directory, ScreenshotScope scope)
    {
        int x, y, width, height;
        if (scope == ScreenshotScope.Primary)
        {
            x = 0;
            y = 0;
            width = GetSystemMetrics(SmCxScreen);
            height = GetSystemMetrics(SmCyScreen);
        }
        else
        {
            x = GetSystemMetrics(SmXVirtualScreen);
            y = GetSystemMetrics(SmYVirtualScreen);
            width = GetSystemMetrics(SmCxVirtualScreen);
            height = GetSystemMetrics(SmCyVirtualScreen);
        }

        if (width <= 0 || height <= 0)
            throw new InvalidOperationException("No screen geometry available");

        var folder = string.IsNullOrWhiteSpace(directory)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Screenshots")
            : Environment.ExpandEnvironmentVariables(directory);

        Directory.CreateDirectory(folder);

        // Milliseconds in the name: two screenshots in the same second must not overwrite
        // each other (a workflow can take several in a row).
        var file = Path.Combine(folder, $"Shortcut_{DateTime.Now:yyyyMMdd_HHmmss_fff}.png");
        var suffix = 1;
        while (File.Exists(file))
        {
            file = Path.Combine(folder,
                $"Shortcut_{DateTime.Now:yyyyMMdd_HHmmss_fff}_{suffix++}.png");
        }

        using var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.CopyFromScreen(x, y, 0, 0, new Size(width, height), CopyPixelOperation.SourceCopy);
        }

        bitmap.Save(file, ImageFormat.Png);
        return file;
    }

    // ---- audio -------------------------------------------------------------------

    public double GetVolume()
    {
        var endpoint = AudioEndpoint.Open();
        try
        {
            endpoint.GetMasterVolumeLevelScalar(out var scalar);
            return Math.Round(scalar * 100.0);
        }
        finally
        {
            Marshal.ReleaseComObject(endpoint);
        }
    }

    public void SetVolume(double percent)
    {
        var endpoint = AudioEndpoint.Open();
        try
        {
            var guid = Guid.Empty;
            endpoint.SetMasterVolumeLevelScalar((float)Math.Clamp(percent / 100.0, 0.0, 1.0), ref guid);
        }
        finally
        {
            Marshal.ReleaseComObject(endpoint);
        }
    }

    public bool GetMute()
    {
        var endpoint = AudioEndpoint.Open();
        try
        {
            endpoint.GetMute(out var muted);
            return muted;
        }
        finally
        {
            Marshal.ReleaseComObject(endpoint);
        }
    }

    public void SetMute(bool muted)
    {
        var endpoint = AudioEndpoint.Open();
        try
        {
            var guid = Guid.Empty;
            endpoint.SetMute(muted, ref guid);
        }
        finally
        {
            Marshal.ReleaseComObject(endpoint);
        }
    }

    // ---- session -----------------------------------------------------------------

    [DllImport("powrprof.dll", SetLastError = true)]
    private static extern bool SetSuspendState(bool hibernate, bool forceCritical, bool disableWakeEvent);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHEmptyRecycleBin(IntPtr hwnd, string? pszRootPath, uint dwFlags);

    public void LockWorkStation()
    {
        if (!LockWorkStationNative()) throw new InvalidOperationException("LockWorkStation failed");
    }

    [DllImport("user32.dll", EntryPoint = "LockWorkStation")]
    private static extern bool LockWorkStationNative();

    public void Suspend()
    {
        if (!SetSuspendState(false, false, false))
            throw new InvalidOperationException("SetSuspendState failed");
    }

    public void EmptyRecycleBin()
    {
        var result = SHEmptyRecycleBin(IntPtr.Zero, null,
            SherbNoConfirmation | SherbNoProgressUi | SherbNoSound);
        if (result != 0 && result != E_UNEXPECTED)
            throw new InvalidOperationException("SHEmptyRecycleBin: 0x" + result.ToString("X8"));
    }

    public void MinimizeAll()
    {
        object? shell = null;
        try
        {
            var type = Type.GetTypeFromProgID("Shell.Application")
                       ?? throw new InvalidOperationException("Shell.Application unavailable");
            shell = Activator.CreateInstance(type)
                    ?? throw new InvalidOperationException("Shell.Application unavailable");
            type.InvokeMember("MinimizeAll", System.Reflection.BindingFlags.InvokeMethod, null, shell, null);
        }
        finally
        {
            if (shell != null && Marshal.IsComObject(shell)) Marshal.ReleaseComObject(shell);
        }
    }

    private const string PowerShellAumid =
        "{1AC14E77-02E7-4E5D-B744-2EB1AE5198B7}\\WindowsPowerShell\\v1.0\\powershell.exe";

    public void ShowNotification(string message, string? title)
    {
        try
        {
            ShowToast(message, title, null);
        }
        catch
        {
            ShowToast(message, title, PowerShellAumid);
        }
    }

    private static void ShowToast(string message, string? title, string? appUserModelId)
    {
        var body = System.Security.SecurityElement.Escape(message) ?? string.Empty;
        var heading = string.IsNullOrWhiteSpace(title)
            ? string.Empty
            : "<text>" + (System.Security.SecurityElement.Escape(title) ?? string.Empty) + "</text>";

        var xml = new Windows.Data.Xml.Dom.XmlDocument();
        xml.LoadXml("<toast><visual><binding template=\"ToastGeneric\">" + heading +
                    "<text>" + body + "</text></binding></visual></toast>");

        var toast = new Windows.UI.Notifications.ToastNotification(xml);
        var notifier = appUserModelId == null
            ? Windows.UI.Notifications.ToastNotificationManager.CreateToastNotifier()
            : Windows.UI.Notifications.ToastNotificationManager.CreateToastNotifier(appUserModelId);
        notifier.Show(toast);
    }

    // ---- input -------------------------------------------------------------------

    public void TypeText(string text)
    {
        var strokes = new List<KeyStroke>(text.Length * 2);
        foreach (var ch in text)
        {
            if (ch == '\r') continue;

            if (ch is '\n' or '\t')
            {
                var vk = (ushort)(ch == '\n' ? 0x0D : 0x09);
                strokes.Add(KeyStroke.KeyDown(vk));
                strokes.Add(KeyStroke.KeyUp(vk));
                continue;
            }

            // Surrogate pairs: each UTF-16 code unit is sent as its own unicode key event.
            strokes.Add(KeyStroke.UnicodeDown(ch));
            strokes.Add(KeyStroke.UnicodeUp(ch));
        }

        KeyboardInput.Send(strokes);
    }

    public void SendKeySequence(IReadOnlyList<string> combinations)
    {
        foreach (var combination in combinations)
        {
            if (!Models.HotkeyGesture.TryParse(combination, requireModifier: false, out var gesture, out _))
                throw new FormatException(combination);

            SendStrokes(gesture, holdMs: 0);
        }
    }

    public void SendHotkey(string gesture, int holdMs)
    {
        if (!Models.HotkeyGesture.TryParse(gesture, requireModifier: false, out var parsed, out _))
            throw new FormatException(gesture);

        SendStrokes(parsed, holdMs);
    }

    private static void SendStrokes(Models.HotkeyGesture gesture, int holdMs)
    {
        var downs = new List<KeyStroke>(5);
        if (gesture.Modifiers.HasFlag(Models.HotkeyModifiers.Control)) downs.Add(KeyStroke.KeyDown(VkControl));
        if (gesture.Modifiers.HasFlag(Models.HotkeyModifiers.Alt)) downs.Add(KeyStroke.KeyDown(VkMenu));
        if (gesture.Modifiers.HasFlag(Models.HotkeyModifiers.Shift)) downs.Add(KeyStroke.KeyDown(VkShift));
        if (gesture.Modifiers.HasFlag(Models.HotkeyModifiers.Win)) downs.Add(KeyStroke.KeyDown(VkLWin));
        downs.Add(KeyStroke.KeyDown((ushort)gesture.VirtualKey));

        var ups = new List<KeyStroke>(5);
        ups.Add(KeyStroke.KeyUp((ushort)gesture.VirtualKey));
        if (gesture.Modifiers.HasFlag(Models.HotkeyModifiers.Win)) ups.Add(KeyStroke.KeyUp(VkLWin));
        if (gesture.Modifiers.HasFlag(Models.HotkeyModifiers.Shift)) ups.Add(KeyStroke.KeyUp(VkShift));
        if (gesture.Modifiers.HasFlag(Models.HotkeyModifiers.Alt)) ups.Add(KeyStroke.KeyUp(VkMenu));
        if (gesture.Modifiers.HasFlag(Models.HotkeyModifiers.Control)) ups.Add(KeyStroke.KeyUp(VkControl));

        try
        {
            KeyboardInput.Send(downs);
            if (holdMs > 0) Thread.Sleep(holdMs);
        }
        finally
        {
            // Release even when a press failed halfway: leaving a modifier down would break
            // the user's keyboard for the rest of the session.
            try
            {
                KeyboardInput.Send(ups);
            }
            catch
            {
                foreach (var up in ups)
                {
                    try { KeyboardInput.Send([up]); } catch { }
                }
            }
        }
    }

    // ---- windows -----------------------------------------------------------------

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder text, int count);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hWnd);

    private const int SwRestore = 9;

    public long FindWindow(string title, WindowMatch match)
    {
        long found = 0;
        EnumWindows((handle, _) =>
        {
            if (!IsWindowVisible(handle)) return true;
            var text = WindowTitle(handle);
            if (text.Length == 0) return true;
            if (!Matches(text, title, match)) return true;
            found = handle.ToInt64();
            return false;
        }, IntPtr.Zero);

        return found;
    }

    public bool WaitForWindow(string title, WindowMatch match, TimeSpan timeout, CancellationToken token,
        out long handle)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            token.ThrowIfCancellationRequested();
            handle = FindWindow(title, match);
            if (handle != 0) return true;
            Thread.Sleep(200);
        }

        handle = 0;
        return false;
    }

    public void ActivateWindow(long handle)
    {
        var ptr = new IntPtr(handle);
        if (!IsWindow(ptr)) throw new InvalidOperationException("Window no longer exists");
        ShowWindow(ptr, SwRestore);
        if (!SetForegroundWindow(ptr))
            throw new InvalidOperationException("Windows refused to bring the window forward");
    }

    private static string WindowTitle(IntPtr handle)
    {
        var length = GetWindowTextLength(handle);
        if (length <= 0) return string.Empty;
        var builder = new System.Text.StringBuilder(length + 1);
        GetWindowText(handle, builder, builder.Capacity);
        return builder.ToString();
    }

    private static bool Matches(string candidate, string expected, WindowMatch match) => match switch
    {
        WindowMatch.Equals => string.Equals(candidate, expected, StringComparison.OrdinalIgnoreCase),
        WindowMatch.StartsWith => candidate.StartsWith(expected, StringComparison.OrdinalIgnoreCase),
        _ => candidate.Contains(expected, StringComparison.OrdinalIgnoreCase),
    };

    // ---- media -------------------------------------------------------------------

    public async Task<bool> MediaTransportAsync(MediaAction action)
    {
        Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager? manager;
        try
        {
            manager = await Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager
                .RequestAsync();
        }
        catch
        {
            return false;
        }

        var session = manager?.GetCurrentSession();
        if (session == null) return false;

        return action switch
        {
            MediaAction.PlayPause => await session.TryTogglePlayPauseAsync(),
            MediaAction.Next => await session.TrySkipNextAsync(),
            MediaAction.Previous => await session.TrySkipPreviousAsync(),
            _ => false,
        };
    }

    // ---- clipboard ---------------------------------------------------------------

    private IClipboard? Clipboard => topLevelAccessor?.Invoke()?.Clipboard;

    public bool HasClipboard => Clipboard != null;

    /// <summary>
    /// 剪贴板对象属于 UI 线程（读取 TopLevel.Clipboard 属性本身就有线程检查），任何线程
    /// 调用都必须先落到 UI 线程。正常路径由运行器的 UI 亲和门保证已在 UI 线程上；这里
    /// 再兜一层，防止未来有绕过运行器的调用方。
    /// </summary>
    private static Task<T> OnUiAsync<T>(Func<Task<T>> action) =>
        Dispatcher.UIThread.CheckAccess() ? action() : Dispatcher.UIThread.InvokeAsync(action);

    private static Task OnUiAsync(Func<Task> action) =>
        Dispatcher.UIThread.CheckAccess() ? action() : Dispatcher.UIThread.InvokeAsync(action);

    public Task<string?> GetClipboardTextAsync() => OnUiAsync(async () =>
    {
        var clipboard = Clipboard ?? throw new InvalidOperationException("No clipboard available");
        return await clipboard.TryGetTextAsync().ConfigureAwait(true);
    });

    public Task SetClipboardTextAsync(string text) => OnUiAsync(async () =>
    {
        var clipboard = Clipboard ?? throw new InvalidOperationException("No clipboard available");
        await clipboard.SetTextAsync(text).ConfigureAwait(true);
    });

    // ---- Core Audio --------------------------------------------------------------

    private static class AudioEndpoint
    {
        internal enum EDataFlow { eRender = 0 }

        internal enum ERole { eMultimedia = 1 }

        public static IAudioEndpointVolume Open()
        {
            IMMDeviceEnumerator? enumerator = null;
            IMMDevice? device = null;
            try
            {
                var type = Type.GetTypeFromCLSID(new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E"));
                if (type == null) throw new InvalidOperationException("No audio endpoint");

                enumerator = (IMMDeviceEnumerator?)Activator.CreateInstance(type);
                if (enumerator == null) throw new InvalidOperationException("No audio endpoint");

                if (enumerator.GetDefaultAudioEndpoint((int)EDataFlow.eRender, (int)ERole.eMultimedia, out device) != 0 ||
                    device == null)
                {
                    throw new InvalidOperationException("No audio endpoint");
                }

                var iid = typeof(IAudioEndpointVolume).GUID;
                if (device.Activate(ref iid, 0x17 /* CLSCTX_ALL */, IntPtr.Zero, out var obj) == 0 &&
                    obj is IAudioEndpointVolume endpoint)
                {
                    return endpoint;
                }

                throw new InvalidOperationException("No audio endpoint");
            }
            finally
            {
                if (device != null) Marshal.ReleaseComObject(device);
                if (enumerator != null) Marshal.ReleaseComObject(enumerator);
            }
        }
    }

    // ---- COM declarations (vtable order must match the SDK interfaces) ------------

    [ComImport]
    [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig]
        int EnumAudioEndpoints(int dataFlow, uint dwStateMask, out IntPtr ppDevices);

        [PreserveSig]
        int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice? ppEndpoint);
    }

    [ComImport]
    [Guid("D666063F-1587-4E43-81F1-B948E807363F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig]
        int Activate(ref Guid iid, uint dwClsCtx, IntPtr pActivationParams,
            [MarshalAs(UnmanagedType.IUnknown)] out object? ppInterface);
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
