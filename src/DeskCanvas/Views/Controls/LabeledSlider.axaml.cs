using System;
using Avalonia;
using Avalonia.Controls;

namespace DeskCanvas.Views.Controls;

/// <summary>
/// Compatibility facade for existing settings pages and widget binaries.
/// The editor, property storage and commit policy belong to DeskCanvas.UI.
/// </summary>
public partial class LabeledSlider : UI.Controls.LabeledSlider
{
    // Keep the host's public static fields and event signature available to old callers.
    public new static readonly StyledProperty<double> ValueProperty = UI.Controls.LabeledSlider.ValueProperty;
    public new static readonly StyledProperty<double> MinimumProperty = UI.Controls.LabeledSlider.MinimumProperty;
    public new static readonly StyledProperty<double> MaximumProperty = UI.Controls.LabeledSlider.MaximumProperty;
    public new static readonly StyledProperty<double> StepProperty = UI.Controls.LabeledSlider.StepProperty;
    public new static readonly StyledProperty<string> DisplayFormatProperty = UI.Controls.LabeledSlider.DisplayFormatProperty;
    public new static readonly StyledProperty<string> UnitProperty = UI.Controls.LabeledSlider.UnitProperty;
    public new static readonly StyledProperty<bool> ShowEditableValueProperty = UI.Controls.LabeledSlider.ShowEditableValueProperty;

    public new event EventHandler<LabeledSliderValueCommittedEventArgs>? ValueCommitted;

    // Preserve the existing diagnostic fixture's named-part reflection surface.
    private readonly Slider SliderPart;
    private readonly NumericUpDown NumericPart;
    private readonly TextBlock ValueTextPart;

    public LabeledSlider()
    {
        SliderPart = this.FindControl<Slider>("SliderPart")!;
        NumericPart = this.FindControl<NumericUpDown>("NumericPart")!;
        ValueTextPart = this.FindControl<TextBlock>("ValueTextPart")!;
        base.ValueCommitted += (_, e) => ValueCommitted?.Invoke(this, new LabeledSliderValueCommittedEventArgs(e.Value));
    }
}

/// <summary>Retains the original host event contract for existing widgets.</summary>
public sealed class LabeledSliderValueCommittedEventArgs(double value) : EventArgs
{
    public double Value { get; } = value;
}
