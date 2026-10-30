using Avalonia.Threading;
using Shortcuts.Models;
using Shortcuts.Services;
using Shortcuts.Views.Controls;

namespace Shortcuts.Views;

/// <summary>
/// Global-trigger hotkey row: the shared <see cref="HotkeyCaptureBox"/> owns recording/typing,
/// while this partial maps a committed value into the document and renders registration state.
/// </summary>
public partial class ShortcutsEditorWindow
{
    /// <summary>Push the current shortcut's binding and registration state into the row.</summary>
    private void UpdateHotkeyUi()
    {
        if (HotkeyCapture.IsRecording)
        {
            HotkeyStatusText.Text = Loc.Get("Shortcuts_Hotkey_RecordingStatus");
            return;
        }

        HotkeyCapture.Value = shortcut.Hotkey;
        HotkeyStatusText.Text = DescribeHotkeyStatus(shortcut.Hotkey ?? string.Empty);
    }

    private void OnHotkeyCaptureValueChanged(object? sender, HotkeyCaptureValueChangedEventArgs e)
    {
        // The capture box fires this before it ends the capture, so the store write happens
        // while the global table is suspended; EndCapture then registers the new document.
        shortcut.Hotkey = e.Value;
        CommitChange();
        UpdateHotkeyUi();
    }

    private string DescribeHotkeyStatus(string hotkey)
    {
        if (hotkey.Length == 0)
            return Loc.Get("Shortcuts_Hotkey_None");

        if (!HotkeyGesture.TryParse(hotkey, requireModifier: true, out _, out _))
            return Loc.Get("Shortcuts_Hotkey_Invalid");

        if (!ShortcutHotkeyService.TryGetBinding(shortcut.Id, out var binding))
            return Loc.Get("Shortcuts_Hotkey_Pending");

        return binding.Status switch
        {
            HotkeyRegistrationStatus.Registered => Loc.Get("Shortcuts_Hotkey_Registered"),
            HotkeyRegistrationStatus.Conflict => Loc.Get("Shortcuts_Hotkey_Conflict"),
            HotkeyRegistrationStatus.Duplicate => Loc.Format("Shortcuts_Hotkey_Duplicate",
                binding.Detail ?? string.Empty),
            HotkeyRegistrationStatus.Invalid => Loc.Get("Shortcuts_Hotkey_Invalid"),
            HotkeyRegistrationStatus.Suspended => Loc.Get("Shortcuts_Hotkey_Suspended"),
            HotkeyRegistrationStatus.Inactive => Loc.Get("Shortcuts_Hotkey_Pending"),
            _ => Loc.Get("Shortcuts_Hotkey_None"),
        };
    }

    private void OnHotkeyStateChanged()
    {
        if (Dispatcher.UIThread.CheckAccess()) UpdateHotkeyUi();
        else Dispatcher.UIThread.Post(UpdateHotkeyUi);
    }
}
