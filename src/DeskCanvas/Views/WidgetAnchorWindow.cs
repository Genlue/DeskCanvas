using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using DeskCanvas.Services;

namespace DeskCanvas.Views;

/// <summary>
/// Invisible 1×1 helper window that stays alive for the whole app lifetime and
/// serves as the <see cref="Screens"/> anchor for the display monitor (Avalonia
/// needs a TopLevel to enumerate screens; none may exist before the first widget
/// or the settings window is shown). Placed far off-screen, removed from Alt-Tab
/// and transparent, so it never disturbs the desktop.
/// </summary>
public sealed class WidgetAnchorWindow : Window
{
    public event EventHandler? DisplaySettingsChanged;
    public event EventHandler? DisplayDevicesChanged;

    public WidgetAnchorWindow()
    {
        Width = 1;
        Height = 1;
        ShowInTaskbar = false;
        SystemDecorations = SystemDecorations.None;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        Opacity = 0;

        if (OperatingSystem.IsWindows())
            Win32Properties.AddWndProcHookCallback(this, OnWindowMessage);
    }

    private IntPtr OnWindowMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        // Display resolution/attachment, taskbar work area, and device changes can arrive
        // before the polling interval. Freeze placement persistence synchronously, while
        // the monitor service queues the read until Avalonia has invalidated its cache.
        if (message is 0x007E or 0x02E0 || message == 0x001A && wParam.ToInt64() == 0x002F)
            DisplaySettingsChanged?.Invoke(this, EventArgs.Empty);
        else if (message == 0x0219 && wParam.ToInt64() == 0x0007)
            // Generic device-tree changes include USB devices. Only a changed display
            // signature warrants hiding the desktop; the service decides after this read.
            Dispatcher.UIThread.Post(() => DisplayDevicesChanged?.Invoke(this, EventArgs.Empty));
        return IntPtr.Zero;
    }

    /// <summary>Create the native window at an off-screen position (invisible) and keep it alive.</summary>
    public void ShowAnchored()
    {
        Position = new PixelPoint(-32000, -32000);
        Show();
        Position = new PixelPoint(-32000, -32000);
        InteropService.RemoveWindowFromAltTab(this);
    }
}
