using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;

namespace DeskCanvas.UI.Controls;

/// <summary>How the control composes the alpha channel of a committed colour.</summary>
public enum ColorInputAlphaPolicy
{
    /// <summary>Keep the previous value's alpha unless the user edited it explicitly (default).</summary>
    Preserve,

    /// <summary>Alpha is not this row's business: every commit is written opaque (alpha = FF).</summary>
    ForceOpaque,
}

/// <summary>String format of the model value written on commit.</summary>
public enum ColorInputFormat
{
    /// <summary>"#RRGGBB" — alpha is carried by a separate business field (e.g. an opacity slider).</summary>
    Rgb,

    /// <summary>"#AARRGGBB" (Avalonia convention, NOT CSS RRGGBBAA) — default.</summary>
    Argb,
}

/// <summary>
/// Unified ColorPicker + Hex draft + inline validation (W6). The business model value is a
/// string (as stored in the settings JSON); the control owns the draft state machine so an
/// intermediate text like "#" or "#FF" is never written back, an invalid text keeps the last
/// valid model value on screen, and a picker change and a Hex commit go through the same
/// normalisation path.
///
/// Rules (see the UI convergence report §6.2):
/// <list type="bullet">
/// <item>Hex accepts #RGB / #RRGGBB / #AARRGGBB (plus #ARGB, the 4-digit Avalonia form);
/// 8/4-digit input carries an explicit alpha.</item>
/// <item>Enter / focus loss commit a valid draft (only when the user actually edited the text);
/// an invalid draft stays in the box with the localized error and never touches the model.</item>
/// <item>AlphaPolicy=Preserve (default) never discards the previous alpha on an RGB edit;
/// ForceOpaque writes FF regardless. The 8-digit hex and the picker with visible alpha are the
/// explicit ways to change alpha.</item>
/// <item>ValueCommitted fires only for user-originated changes; loading a value from the model
/// (or an external update) never fires it and never causes an extra save.</item>
/// <item>An empty value is only valid with AllowEmpty=true (null = the business "automatic /
/// follow system" state); otherwise committing an empty draft shows the error.</item>
/// <item><see cref="InvalidFormatText"/> is user-visible copy the EMBEDDING APPLICATION
/// localizes (the host facade feeds its Locale resource); the English default keeps the
/// control readable standalone.</item>
/// </list>
/// </summary>
public partial class ColorInput : UserControl
{
    public static readonly StyledProperty<string?> ValueProperty =
        AvaloniaProperty.Register<ColorInput, string?>(nameof(Value), defaultBindingMode: BindingMode.TwoWay);

    /// <summary>Show the picker's alpha editing UI; does NOT make the control drop alpha.</summary>
    public static readonly StyledProperty<bool> IsAlphaVisibleProperty =
        AvaloniaProperty.Register<ColorInput, bool>(nameof(IsAlphaVisible));

    public static readonly StyledProperty<ColorInputAlphaPolicy> AlphaPolicyProperty =
        AvaloniaProperty.Register<ColorInput, ColorInputAlphaPolicy>(nameof(AlphaPolicy));

    public static readonly StyledProperty<ColorInputFormat> ColorFormatProperty =
        AvaloniaProperty.Register<ColorInput, ColorInputFormat>(nameof(ColorFormat), ColorInputFormat.Argb);

    /// <summary>true → an empty value is a legal model state (automatic / follow system).</summary>
    public static readonly StyledProperty<bool> AllowEmptyProperty =
        AvaloniaProperty.Register<ColorInput, bool>(nameof(AllowEmpty));

    /// <summary>Read-only keeps the colour visible, selectable and copyable but not editable.</summary>
    public static readonly StyledProperty<bool> IsReadOnlyProperty =
        AvaloniaProperty.Register<ColorInput, bool>(nameof(IsReadOnly));

    /// <summary>Copy shown for an invalid draft; localized by the embedding application.</summary>
    public static readonly StyledProperty<string> InvalidFormatTextProperty =
        AvaloniaProperty.Register<ColorInput, string>(nameof(InvalidFormatText),
            "Invalid color format — enter #RGB, #RRGGBB or #AARRGGBB");

    public static readonly DirectProperty<ColorInput, bool> HasValidationErrorProperty =
        AvaloniaProperty.RegisterDirect<ColorInput, bool>(nameof(HasValidationError), o => o.hasValidationError);

    public static readonly DirectProperty<ColorInput, string?> ValidationMessageProperty =
        AvaloniaProperty.RegisterDirect<ColorInput, string?>(nameof(ValidationMessage), o => o.validationMessage);

    private bool hasValidationError;
    private string? validationMessage;

    /// <summary>Raised once per accepted user edit (valid Hex commit, picker change, empty commit).</summary>
    public event EventHandler<ColorInputValueCommittedEventArgs>? ValueCommitted;

    /// <summary>Last successfully parsed colour (model load or commit); source of Preserve alpha.</summary>
    private Color? lastValidColor;

    /// <summary>Guards every internal write so handlers ignore our own echo (no event loops).</summary>
    private bool isSynchronizing;

    /// <summary>Guards programmatic text writes from being mistaken for user typing.</summary>
    private bool updatingText;

    /// <summary>True once the user edited the Hex draft since the last successful apply.</summary>
    private bool draftEdited;

    public ColorInput()
    {
        InitializeComponent();

        PickerPart.ColorChanged += OnPickerColorChanged;
        HexBoxPart.KeyDown += OnHexKeyDown;
        HexBoxPart.LostFocus += OnHexLostFocus;
        HexBoxPart.PropertyChanged += OnHexBoxPropertyChanged;

        ApplyFormatToParts();
        UpdateReadOnlyState();
    }

    public string? Value
    {
        get => GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public bool IsAlphaVisible
    {
        get => GetValue(IsAlphaVisibleProperty);
        set => SetValue(IsAlphaVisibleProperty, value);
    }

    public ColorInputAlphaPolicy AlphaPolicy
    {
        get => GetValue(AlphaPolicyProperty);
        set => SetValue(AlphaPolicyProperty, value);
    }

    public ColorInputFormat ColorFormat
    {
        get => GetValue(ColorFormatProperty);
        set => SetValue(ColorFormatProperty, value);
    }

    public bool AllowEmpty
    {
        get => GetValue(AllowEmptyProperty);
        set => SetValue(AllowEmptyProperty, value);
    }

    public bool IsReadOnly
    {
        get => GetValue(IsReadOnlyProperty);
        set => SetValue(IsReadOnlyProperty, value);
    }

    public string InvalidFormatText
    {
        get => GetValue(InvalidFormatTextProperty);
        set => SetValue(InvalidFormatTextProperty, value);
    }

    public bool HasValidationError
    {
        get => hasValidationError;
        private set => SetAndRaise(HasValidationErrorProperty, ref hasValidationError, value);
    }

    public string? ValidationMessage
    {
        get => validationMessage;
        private set => SetAndRaise(ValidationMessageProperty, ref validationMessage, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == ValueProperty)
        {
            // External model load / update: mirror it, never commit, never save.
            UpdateFromValue(change.GetNewValue<string?>());
        }
        else if (change.Property == IsAlphaVisibleProperty)
        {
            PickerPart.IsAlphaVisible = IsAlphaVisible;
        }
        else if (change.Property == ColorFormatProperty)
        {
            ApplyFormatToParts();
        }
        else if (change.Property == IsReadOnlyProperty)
        {
            UpdateReadOnlyState();
        }
    }

    // ---- external load -----------------------------------------------------------------------

    private void UpdateFromValue(string? value)
    {
        if (isSynchronizing) return;

        if (string.IsNullOrWhiteSpace(value))
        {
            lastValidColor = null;
            PushDraftText(string.Empty);
            SetValidationError(false, null);
            return;
        }

        if (Color.TryParse(value, out var color))
        {
            lastValidColor = color;
            PushPickerColor(color);
            PushDraftText(Format(color));
            SetValidationError(false, null);
        }
        else
        {
            // Surface broken stored data instead of silently hiding it: keep the raw text as
            // the draft so the user can repair it; the model is untouched either way.
            PushDraftText(value);
            SetValidationError(true, InvalidFormatText);
        }
    }

    // ---- user edits --------------------------------------------------------------------------

    private void OnHexBoxPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs change)
    {
        if (change.Property != TextBox.TextProperty || updatingText || isSynchronizing) return;
        // Typing invalidates the previous verdict; the next commit attempt re-validates. This
        // also keeps "#" / "#FF" intermediate states quiet while the user is mid-entry.
        draftEdited = true;
        SetValidationError(false, null);
    }

    private void OnHexKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            CommitDraft();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            RevertDraft();
            e.Handled = true;
        }
    }

    private void OnHexLostFocus(object? sender, RoutedEventArgs e) => CommitDraft();

    private void OnPickerColorChanged(object? sender, ColorChangedEventArgs e)
    {
        if (isSynchronizing) return;
        // A picker move supersedes any pending text draft (the draft is re-rendered below).
        draftEdited = false;
        ApplyColor(e.NewColor, fromHex: false, hexDigits: 0);
    }

    /// <summary>Commit the Hex draft on Enter / focus loss. No user edit → nothing to commit.</summary>
    private void CommitDraft()
    {
        if (isSynchronizing || !draftEdited) return;

        var text = (HexBoxPart.Text ?? string.Empty).Trim();
        if (text.Length == 0)
        {
            if (AllowEmpty)
            {
                draftEdited = false;
                lastValidColor = null;
                SetValidationError(false, null);
                var previous = Value;
                if (!string.IsNullOrEmpty(previous))
                {
                    var previousSync = isSynchronizing;
                    isSynchronizing = true;
                    try { SetCurrentValue(ValueProperty, null); }
                    finally { isSynchronizing = previousSync; }
                    ValueCommitted?.Invoke(this, new ColorInputValueCommittedEventArgs(null));
                }

                return;
            }

            SetValidationError(true, InvalidFormatText);
            return;
        }

        if (!Color.TryParse(text, out var color))
        {
            // Invalid input stays in the draft; the model and the last preview remain valid.
            SetValidationError(true, InvalidFormatText);
            return;
        }

        draftEdited = false;
        ApplyColor(color, fromHex: true, CountHexDigits(text));
    }

    /// <summary>Escape: restore this draft; committed model changes are never undone.</summary>
    private void RevertDraft()
    {
        draftEdited = false;
        SetValidationError(false, null);
        PushDraftText(lastValidColor is { } color ? Format(color) : (Value ?? string.Empty));
    }

    /// <summary>Normalise a valid colour, mirror it into every part and publish a changed value once.</summary>
    private void ApplyColor(Color incoming, bool fromHex, int hexDigits)
    {
        var alpha = ResolveAlpha(incoming, fromHex, hexDigits);
        var composed = Color.FromArgb(alpha, incoming.R, incoming.G, incoming.B);
        lastValidColor = composed;
        var formatted = Format(composed);
        var changed = !string.Equals(Value, formatted, StringComparison.Ordinal);

        var previousSync = isSynchronizing;
        isSynchronizing = true;
        try
        {
            draftEdited = false;
            PushPickerColor(composed);
            PushDraftText(formatted);
            SetValidationError(false, null);
            if (changed) SetCurrentValue(ValueProperty, formatted);
        }
        finally { isSynchronizing = previousSync; }

        if (changed) ValueCommitted?.Invoke(this, new ColorInputValueCommittedEventArgs(formatted));
    }

    // ---- alpha / formatting ------------------------------------------------------------------

    private byte ResolveAlpha(Color incoming, bool fromHex, int hexDigits)
    {
        // ForceOpaque is unconditional: the row declared alpha is not its business.
        if (AlphaPolicy == ColorInputAlphaPolicy.ForceOpaque) return 255;
        // An explicit #ARGB / #AARRGGBB is the user editing alpha directly.
        if (fromHex && (hexDigits == 4 || hexDigits == 8)) return incoming.A;
        // Picker with visible alpha is also an explicit edit.
        if (IsAlphaVisible) return incoming.A;
        // Otherwise: never discard the previous value's alpha ("IsAlphaVisible=false does not
        // mean alpha may be rewritten to FF").
        return lastValidColor?.A ?? incoming.A;
    }

    /// <summary>Hex digit count of the draft, or -1 when it is not a pure hex form.</summary>
    private static int CountHexDigits(string text)
    {
        var t = text.Trim();
        if (t.StartsWith('#')) t = t[1..];
        if (t.Length == 0) return -1;
        foreach (var ch in t)
        {
            if (!Uri.IsHexDigit(ch)) return -1;
        }

        return t.Length;
    }

    private string Format(Color color) => ColorFormat == ColorInputFormat.Rgb
        ? $"#{color.R:X2}{color.G:X2}{color.B:X2}"
        : $"#{color.A:X2}{color.R:X2}{color.G:X2}{color.B:X2}";

    // ---- part synchronisation ----------------------------------------------------------------

    private void PushPickerColor(Color color)
    {
        var previous = isSynchronizing;
        isSynchronizing = true;
        try
        {
            if (PickerPart.Color != color) PickerPart.Color = color;
        }
        finally { isSynchronizing = previous; }
    }

    private void PushDraftText(string text)
    {
        if (string.Equals(HexBoxPart.Text, text, StringComparison.Ordinal)) return;
        updatingText = true;
        try { HexBoxPart.Text = text; }
        finally { updatingText = false; }
    }

    private void SetValidationError(bool hasError, string? message)
    {
        HasValidationError = hasError;
        ValidationMessage = hasError ? message : null;
        ErrorTextPart.IsVisible = hasError;
        ErrorTextPart.Text = hasError ? message : null;
    }

    private void ApplyFormatToParts()
    {
        HexBoxPart.Watermark = ColorFormat == ColorInputFormat.Rgb ? "#RRGGBB" : "#AARRGGBB";
        PushDraftText(lastValidColor is { } color ? Format(color) : (Value ?? string.Empty));
    }

    private void UpdateReadOnlyState()
    {
        PickerPart.IsEnabled = !IsReadOnly;
        HexBoxPart.IsReadOnly = IsReadOnly;
    }
}

/// <summary>New value of a completed user edit; null only when an AllowEmpty field was cleared.</summary>
public sealed class ColorInputValueCommittedEventArgs(string? value) : EventArgs
{
    public string? Value { get; } = value;
}
