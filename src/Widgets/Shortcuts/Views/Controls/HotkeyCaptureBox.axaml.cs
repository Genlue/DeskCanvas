using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Shortcuts.Models;
using Shortcuts.Services;

namespace Shortcuts.Views.Controls;

/// <summary>Value payload for <see cref="HotkeyCaptureBox.ValueChanged"/> (canonical or null).</summary>
public sealed class HotkeyCaptureValueChangedEventArgs(string? value) : EventArgs
{
    /// <summary>Canonical gesture text, or <c>null</c> when cleared.</summary>
    public string? Value { get; } = value;
}

/// <summary>
/// Shared capture field used both by the shortcut editor (global trigger hotkey) and by the
/// <c>sendHotkey</c> action parameter. Supports recording the next real key combination and
/// typing one by hand; both paths write the same canonical string. While recording it suspends
/// the process-wide hotkey table (so the captured key cannot run an old action).
/// </summary>
public sealed partial class HotkeyCaptureBox : UserControl
{
    public static readonly StyledProperty<string?> ValueProperty =
        AvaloniaProperty.Register<HotkeyCaptureBox, string?>(nameof(Value));

    public static readonly StyledProperty<bool> IsReadOnlyProperty =
        AvaloniaProperty.Register<HotkeyCaptureBox, bool>(nameof(IsReadOnly));

    public static readonly StyledProperty<bool> RequireModifierProperty =
        AvaloniaProperty.Register<HotkeyCaptureBox, bool>(nameof(RequireModifier));

    public static readonly StyledProperty<bool> ShowStatusProperty =
        AvaloniaProperty.Register<HotkeyCaptureBox, bool>(nameof(ShowStatus), defaultValue: true);

    private bool updatingText;
    private TopLevel? captureTopLevel;

    public HotkeyCaptureBox()
    {
        InitializeComponent();
        UpdateDisplay();
    }

    /// <summary>Canonical gesture text; setting it programmatically never raises <see cref="ValueChanged"/>.</summary>
    public string? Value
    {
        get => GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    /// <summary>The editor's global-hotkey row uses a read-only field; the action editor allows typing.</summary>
    public bool IsReadOnly
    {
        get => GetValue(IsReadOnlyProperty);
        set => SetValue(IsReadOnlyProperty, value);
    }

    /// <summary>Global trigger hotkeys need a modifier (F-keys may stand alone); actions do not.</summary>
    public bool RequireModifier
    {
        get => GetValue(RequireModifierProperty);
        set => SetValue(RequireModifierProperty, value);
    }

    /// <summary>Hide the inline status when the host renders its own registration status.</summary>
    public bool ShowStatus
    {
        get => GetValue(ShowStatusProperty);
        set => SetValue(ShowStatusProperty, value);
    }

    public bool IsRecording { get; private set; }

    /// <summary>Raised only for a user-committed, valid value (or a clear).</summary>
    public event EventHandler<HotkeyCaptureValueChangedEventArgs>? ValueChanged;

    /// <summary>Focus the text box after a host flyout opens.</summary>
    public void FocusInput()
    {
        InputBox.Focus();
    }

    /// <summary>Cancel recording (e.g. the owner window closes or switches shortcut).</summary>
    public void CancelRecording() => StopRecording(resume: true);

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        // Named children are not attached until InitializeComponent has finished.
        if (InputBox is null) return;

        if (change.Property == ValueProperty)
        {
            UpdateDisplay();
        }
        else if (change.Property == IsReadOnlyProperty)
        {
            InputBox.IsReadOnly = IsReadOnly;
            UpdateDisplay();
        }
        else if (change.Property == ShowStatusProperty)
        {
            StatusText.IsVisible = ShowStatus;
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        StopRecording(resume: true);
        base.OnDetachedFromVisualTree(e);
    }

    // ---- display -----------------------------------------------------------------

    private void UpdateDisplay()
    {
        if (IsRecording) return;

        updatingText = true;
        InputBox.Text = DisplayText(Value);
        updatingText = false;

        InputBox.IsReadOnly = IsReadOnly;

        RecordButton.Content = Loc.Get("Shortcuts_Hotkey_Record");
        AutomationProperties.SetName(RecordButton, Loc.Get("Shortcuts_Hotkey_Record"));
        ClearButton.IsEnabled = !string.IsNullOrEmpty(Value);

        if (!ShowStatus) return;

        if (string.IsNullOrEmpty(Value))
        {
            StatusText.Text = Loc.Get("Shortcuts_Hotkey_None");
        }
        else if (!HotkeyGesture.TryParse(Value, RequireModifier, out _, out _))
        {
            StatusText.Text = Loc.Get("Shortcuts_Hotkey_Invalid");
        }
        else
        {
            StatusText.Text = string.Empty;
        }
    }

    // ---- recording ---------------------------------------------------------------

    private void OnRecordClicked(object? sender, RoutedEventArgs e)
    {
        if (IsRecording)
        {
            StopRecording(resume: true);
            return;
        }

        StartRecording();
    }

    private void StartRecording()
    {
        if (IsRecording) return;

        IsRecording = true;

        // The global hotkey table must be gone before the user presses the new combination.
        ShortcutHotkeyService.BeginCapture();

        updatingText = true;
        InputBox.Text = Loc.Get("Shortcuts_Hotkey_Recording");
        updatingText = false;

        RecordButton.Content = Loc.Get("Shortcuts_Hotkey_Cancel");
        AutomationProperties.SetName(RecordButton, Loc.Get("Shortcuts_Hotkey_Cancel"));
        ClearButton.IsEnabled = true;

        if (ShowStatus)
            StatusText.Text = Loc.Get("Shortcuts_Hotkey_RecordingStatus");

        captureTopLevel = TopLevel.GetTopLevel(this);
        captureTopLevel?.AddHandler(KeyDownEvent, OnCaptureKeyDown,
            RoutingStrategies.Tunnel, handledEventsToo: true);

        FocusInput();
    }

    private void StopRecording(bool resume)
    {
        if (!IsRecording) return;

        IsRecording = false;

        if (captureTopLevel != null)
        {
            captureTopLevel.RemoveHandler(KeyDownEvent, OnCaptureKeyDown);
            captureTopLevel = null;
        }

        if (resume)
            ShortcutHotkeyService.EndCapture();

        updatingText = false;
        UpdateDisplay();
    }

    private void OnCaptureKeyDown(object? sender, KeyEventArgs e)
    {
        if (!IsRecording) return;

        e.Handled = true;

        if (e.Key == Key.Escape)
        {
            StopRecording(resume: true);
            return;
        }

        if (e.Key is Key.Back or Key.Delete)
        {
            CommitValue(null);
            return;
        }

        if (HotkeyGesture.IsModifierKey(e.Key))
        {
            if (ShowStatus)
                StatusText.Text = Loc.Get("Shortcuts_Hotkey_HintModifier");
            return;
        }

        if (!HotkeyGesture.TryFromKey(e.Key, e.KeyModifiers, RequireModifier,
                out var gesture, out var error))
        {
            if (ShowStatus)
            {
                StatusText.Text = Loc.Get(error == HotkeyParseError.ModifierRequired
                    ? "Shortcuts_Hotkey_HintModifier"
                    : "Shortcuts_Hotkey_HintUnsupported");
            }
            return;
        }

        CommitValue(gesture.Text);
    }

    // ---- typing ------------------------------------------------------------------

    private void OnClearClicked(object? sender, RoutedEventArgs e) => CommitValue(null);

    private void OnTextChanged(object? sender, TextChangedEventArgs e)
    {
        if (updatingText || IsRecording) return;

        // Programmatic display writes can raise TextChanged after updatingText is cleared;
        // don't turn the canonical echo into a "typing" hint.
        if (string.Equals(InputBox.Text, DisplayText(Value), StringComparison.Ordinal)) return;

        // An empty field is a clear; non-empty text is committed on Enter / focus loss so the
        // user can finish typing "ctrl + alt + k" without every keystroke being invalid.
        if (string.IsNullOrWhiteSpace(InputBox.Text))
        {
            if (!string.IsNullOrEmpty(Value))
                CommitValue(null);
            return;
        }

        if (ShowStatus)
            StatusText.Text = Loc.Get("Shortcuts_Hotkey_TypingHint");
    }

    private string DisplayText(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        return HotkeyGesture.TryParse(value, RequireModifier, out var gesture, out _)
            ? gesture.Text
            : value;
    }

    private void OnInputLostFocus(object? sender, RoutedEventArgs e)
    {
        if (IsRecording) return;
        ApplyTypedText();
    }

    private void OnInputKeyDown(object? sender, KeyEventArgs e)
    {
        if (IsRecording) return;

        if (e.Key == Key.Enter)
        {
            ApplyTypedText();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            UpdateDisplay();
            e.Handled = true;
        }
    }

    private void ApplyTypedText()
    {
        var text = (InputBox.Text ?? string.Empty).Trim();
        if (text.Length == 0)
        {
            CommitValue(null);
            return;
        }

        if (!HotkeyGesture.TryParse(text, RequireModifier, out var gesture, out var error))
        {
            if (ShowStatus)
            {
                StatusText.Text = Loc.Get(error == HotkeyParseError.ModifierRequired
                    ? "Shortcuts_Hotkey_HintModifier"
                    : "Shortcuts_Hotkey_Invalid");
            }
            return;
        }

        CommitValue(gesture.Text);
    }

    /// <summary>
    /// Publish a validated value. The event fires BEFORE recording teardown, so the editor's
    /// handler can write the store while the global table is still suspended; EndCapture then
    /// re-registers the new document.
    /// </summary>
    private void CommitValue(string? value)
    {
        var normalized = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        var changed = !string.Equals(Value, normalized, StringComparison.Ordinal);

        updatingText = true;
        SetCurrentValue(ValueProperty, normalized);
        updatingText = false;

        try
        {
            if (changed)
                ValueChanged?.Invoke(this, new HotkeyCaptureValueChangedEventArgs(normalized));
        }
        finally
        {
            StopRecording(resume: true);
            UpdateDisplay();
        }
    }
}
