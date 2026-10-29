using System;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using DeskCanvas.Services;
using DeskCanvas.UI.Controls;

namespace DeskCanvas.Views;

/// <summary>
/// The base window for every <see cref="WindowRole.Dialog"/> surface: it owns the window chrome
/// that used to be copied into each dialog's AXAML, and nothing else.
/// <list type="bullet">
/// <item><b>One chrome.</b> A dialog is a single card floating over the desktop:
/// <c>WindowDecorations=None</c> + <c>Background=Transparent</c> +
/// <c>TransparencyLevelHint=Transparent</c>, with the card drawn by <see cref="DialogShell"/> inside
/// a transparent shadow gutter. The old pair of dialogs used <c>AcrylicBlur</c> + <c>BorderOnly</c>
/// with an 8 DIP margin, which put the card inside a blurred window — the reported "外围多一圈
/// 毛玻璃" — and clipped nothing at the card's rounded corners.</item>
/// <item><b>Movable.</b> The shell's header carries the shared drag handle
/// (<see cref="PanelDragBehavior"/>). The extended-client-area caption the old dialogs asked for is
/// deliberately NOT used: the OS claims that strip, so a press on the title never reached the
/// dialog's own handler and the window could not be moved at all.</item>
/// <item><b>Escape closes.</b> One rule for every dialog, replacing three different answers
/// (some closed, some ignored it). A window that gives Escape a narrower meaning of its own turns it
/// off with <see cref="ClosesOnEscape"/> — the base handler is subscribed from this constructor, so
/// it runs BEFORE any handler a derived window adds.</item>
/// <item><b>Role.</b> Registered once, here. Per-dialog <c>WindowRoles.Register</c> calls would
/// re-register (the registry is not idempotent) and stack duplicate Opened/Closed subscriptions.</item>
/// </list>
/// </summary>
public abstract class HostDialogWindow : Window
{
    protected HostDialogWindow()
    {
        // Enforced here rather than per-file: a dialog that declares its own material is exactly
        // how the acrylic ring and the square corners came back. AXAML may still override these
        // (it is applied after the constructor), which is why the convention guard rejects the
        // two attributes that used to do it.
        WindowDecorations = WindowDecorations.None;
        Background = Brushes.Transparent;
        TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent };

        WindowRoles.Register(this, WindowRole.Dialog);
        KeyDown += OnDialogKeyDown;
    }

    /// <summary>The shared chrome, when the window's content root is a <see cref="DialogShell"/>.</summary>
    protected DialogShell? Shell => Content as DialogShell;

    /// <summary>
    /// Escape closes the dialog. Because this handler is subscribed from the constructor, it runs
    /// before anything a derived window adds — so a dialog with its own Escape meaning must use
    /// <see cref="ClosesOnEscape"/> (or overwrite this key earlier), and a dialog that must close
    /// with a specific result overrides <see cref="OnDialogEscape"/> rather than adding a handler.
    /// A control that consumes Escape (a text field cancelling an IME composition) sets
    /// <c>Handled</c> on the way up, and that is honoured here.
    /// </summary>
    private void OnDialogKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || e.Handled || !ClosesOnEscape) return;
        if (OnDialogEscape()) e.Handled = true;
    }

    /// <summary>
    /// Whether Escape belongs to the dialog at all. True for a normal dialog; a window that gives
    /// Escape a narrower meaning of its own (the shortcuts editor closes its drawers with it, and
    /// never closes itself) overrides this to false. The base is subscribed from its constructor,
    /// i.e. BEFORE any derived handler, so without this flag the base would win the key.
    /// </summary>
    protected virtual bool ClosesOnEscape => true;

    /// <summary>
    /// What Escape does. The default closes the dialog with no result; a modal that returns a value
    /// from <c>ShowDialog&lt;T&gt;</c> overrides this to close with its cancel value. Returning
    /// false leaves the key unhandled for the content to consume.
    /// </summary>
    protected virtual bool OnDialogEscape()
    {
        Close();
        return true;
    }
}
