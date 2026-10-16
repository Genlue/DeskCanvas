using System;
using Avalonia;
using DeskCanvas.Locales;
using ColorInputAlphaPolicy = DeskCanvas.UI.Controls.ColorInputAlphaPolicy;
using ColorInputFormat = DeskCanvas.UI.Controls.ColorInputFormat;

namespace DeskCanvas.Views.Controls;

/// <summary>
/// Compatibility facade for existing settings pages and widget binaries.
/// The draft/commit engine, property storage and validation belong to DeskCanvas.UI; the
/// facade keeps the host type surface (styled-property fields, the old event-argument type)
/// and feeds the localized invalid-format copy from the host Locale.
/// </summary>
public partial class ColorInput : UI.Controls.ColorInput
{
    // Keep the host's public static fields available to old callers (one identity with the base).
    public new static readonly StyledProperty<string?> ValueProperty = UI.Controls.ColorInput.ValueProperty;
    public new static readonly StyledProperty<bool> IsAlphaVisibleProperty = UI.Controls.ColorInput.IsAlphaVisibleProperty;
    public new static readonly StyledProperty<ColorInputAlphaPolicy> AlphaPolicyProperty = UI.Controls.ColorInput.AlphaPolicyProperty;
    public new static readonly StyledProperty<ColorInputFormat> ColorFormatProperty = UI.Controls.ColorInput.ColorFormatProperty;
    public new static readonly StyledProperty<bool> AllowEmptyProperty = UI.Controls.ColorInput.AllowEmptyProperty;
    public new static readonly StyledProperty<bool> IsReadOnlyProperty = UI.Controls.ColorInput.IsReadOnlyProperty;

    public new event EventHandler<ColorInputValueCommittedEventArgs>? ValueCommitted;

    public ColorInput()
    {
        // Localized error copy comes from the host Locale; the shared control keeps a neutral
        // English default standalone.
        InvalidFormatText = Locale.Settings_ColorInput_InvalidFormat;
        base.ValueCommitted += (_, e) => ValueCommitted?.Invoke(this, new ColorInputValueCommittedEventArgs(e.Value));
    }
}

/// <summary>Retains the original host event contract for existing widgets.</summary>
public sealed class ColorInputValueCommittedEventArgs(string? value) : EventArgs
{
    public string? Value { get; } = value;
}
