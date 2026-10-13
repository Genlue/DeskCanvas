using System;
using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace DeskCanvas.Views.Controls;

/// <summary>
/// "Slider + current value" compound editor (W6 of the UI convergence plan). It carries
/// ONLY the editor — pages keep wrapping it in a <see cref="Setting"/> row; the control has
/// no idea which model it edits.
///
/// Contract:
/// <list type="bullet">
/// <item><b>Value</b> is the single source of truth (TwoWay by default). Slider and value box
/// are pushed FROM it; user edits flow back through <c>SetCurrentValue</c>, so an external
/// binding is never replaced.</item>
/// <item><b>Step</b> (default 1) snaps every <i>user edit</i> to the grid anchored at
/// <c>Minimum</c> and clamps it to the range; a runtime range/step change re-snaps the current
/// value the same way. A value <i>loaded</i> from the model is mirrored as-is and never written
/// back, so merely opening a settings page cannot touch the settings file.</item>
/// <item>NaN / Infinity never reach the parts (they are displayed as the range start), and an
/// inverted or non-finite range falls back to 0–100 — the row always shows something sane.</item>
/// <item><b>ValueCommitted</b> fires only when a *user interaction completes* (slider drag
/// release / pointer release / adjust keys, numeric Enter / focus loss / spinner) and the
/// value actually changed since the last commit or external load. Loading a value from the
/// model never fires it — pages may therefore save on commit without extra disk writes, while
/// pages that keep live-saving on the TwoWay binding see exactly the old semantics.</item>
/// </list>
/// </summary>
public partial class LabeledSlider : UserControl
{
    private const double Epsilon = 1e-9;

    public static readonly StyledProperty<double> ValueProperty =
        AvaloniaProperty.Register<LabeledSlider, double>(nameof(Value), defaultBindingMode: BindingMode.TwoWay);

    public static readonly StyledProperty<double> MinimumProperty =
        AvaloniaProperty.Register<LabeledSlider, double>(nameof(Minimum));

    public static readonly StyledProperty<double> MaximumProperty =
        AvaloniaProperty.Register<LabeledSlider, double>(nameof(Maximum), 100.0);

    /// <summary>Snap grid (positive, default 1); invalid values fall back to 1.</summary>
    public static readonly StyledProperty<double> StepProperty =
        AvaloniaProperty.Register<LabeledSlider, double>(nameof(Step), 1.0);

    /// <summary>Numeric format for the displayed value (current culture). Default "0".</summary>
    public static readonly StyledProperty<string> DisplayFormatProperty =
        AvaloniaProperty.Register<LabeledSlider, string>(nameof(DisplayFormat), "0");

    /// <summary>Suffix rendered next to the value text (e.g. "%"); numeric mode shows no unit.</summary>
    public static readonly StyledProperty<string> UnitProperty =
        AvaloniaProperty.Register<LabeledSlider, string>(nameof(Unit), string.Empty);

    /// <summary>false → formatted text; true → editable NumericUpDown in the value column.</summary>
    public static readonly StyledProperty<bool> ShowEditableValueProperty =
        AvaloniaProperty.Register<LabeledSlider, bool>(nameof(ShowEditableValue));

    /// <summary>Raised once per completed user edit; never on load or external value updates.</summary>
    public event EventHandler<LabeledSliderValueCommittedEventArgs>? ValueCommitted;

    /// <summary>Guards every internal write to the parts / Value so handlers ignore our own echo.</summary>
    private bool isSynchronizing;

    /// <summary>Last value the outside world knew about (external load or last commit).</summary>
    private double lastCommittedValue;

    public LabeledSlider()
    {
        InitializeComponent();

        SliderPart.PropertyChanged += OnSliderPropertyChanged;
        SliderPart.AddHandler(Thumb.DragCompletedEvent, OnSliderDragCompleted, RoutingStrategies.Bubble);
        SliderPart.AddHandler(PointerReleasedEvent, OnSliderPointerReleased, RoutingStrategies.Bubble);
        SliderPart.AddHandler(KeyUpEvent, OnSliderKeyUp, RoutingStrategies.Bubble);

        NumericPart.ValueChanged += OnNumericValueChanged;
        NumericPart.AddHandler(KeyDownEvent, OnNumericKeyDown, RoutingStrategies.Bubble);
        NumericPart.AddHandler(LostFocusEvent, OnNumericLostFocus, RoutingStrategies.Bubble);
        NumericPart.Spinned += OnNumericSpinned;

        ApplyRangeToParts();
        UpdateEditorVisibility();
        UpdateDisplayText();
        PropagateAutomationName();
    }

    public double Value
    {
        get => GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public double Minimum
    {
        get => GetValue(MinimumProperty);
        set => SetValue(MinimumProperty, value);
    }

    public double Maximum
    {
        get => GetValue(MaximumProperty);
        set => SetValue(MaximumProperty, value);
    }

    public double Step
    {
        get => GetValue(StepProperty);
        set => SetValue(StepProperty, value);
    }

    public string DisplayFormat
    {
        get => GetValue(DisplayFormatProperty);
        set => SetValue(DisplayFormatProperty, value);
    }

    public string Unit
    {
        get => GetValue(UnitProperty);
        set => SetValue(UnitProperty, value);
    }

    public bool ShowEditableValue
    {
        get => GetValue(ShowEditableValueProperty);
        set => SetValue(ShowEditableValueProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == ValueProperty)
        {
            var value = change.GetNewValue<double>();
            // An external write (model load / another editor) resets the commit baseline: the
            // page already knows this value, a later interaction must compare against it.
            if (!isSynchronizing) lastCommittedValue = value;
            PushValueToParts(value);
            UpdateDisplayText();
        }
        else if (change.Property == ShowEditableValueProperty)
        {
            UpdateEditorVisibility();
        }
        else if (change.Property == MinimumProperty || change.Property == MaximumProperty
              || change.Property == StepProperty || change.Property == DisplayFormatProperty)
        {
            ApplyRangeToParts();
            // Re-snap the current value against the new range/step so the row never shows an
            // off-grid value after a runtime reconfiguration.
            var snapped = Snap(Value);
            if (Math.Abs(snapped - Value) > Epsilon) SetCurrentValue(ValueProperty, snapped);
            else { PushValueToParts(snapped); UpdateDisplayText(); }
        }
        else if (change.Property == UnitProperty)
        {
            UpdateDisplayText();
        }
        else if (change.Property == AutomationProperties.NameProperty)
        {
            PropagateAutomationName();
        }
    }

    // ---- value normalisation -----------------------------------------------------------------

    /// <summary>Invalid ranges normalise to 0–100 (the contract: "normalise or report a configuration error").</summary>
    private (double Min, double Max) EffectiveRange()
    {
        var min = Minimum;
        var max = Maximum;
        if (!double.IsFinite(min)) min = 0;
        if (!double.IsFinite(max)) max = 100;
        if (max <= min) { min = 0; max = 100; }
        return (min, max);
    }

    private double EffectiveStep()
    {
        var step = Step;
        return double.IsFinite(step) && step > 0 ? step : 1.0;
    }

    /// <summary>Clamp to the range and align to the step grid anchored at Minimum.</summary>
    private double Snap(double value)
    {
        var (min, max) = EffectiveRange();
        var step = EffectiveStep();
        if (!double.IsFinite(value)) value = min;
        value = Math.Clamp(value, min, max);
        var ticks = Math.Round((value - min) / step, MidpointRounding.AwayFromZero);
        var snapped = Math.Round(min + ticks * step, 10); // damp 0.30000000000000004-style residue
        return Math.Clamp(snapped, min, max);
    }

    // ---- part synchronisation ----------------------------------------------------------------

    private void PushValueToParts(double value)
    {
        // A non-finite value must never reach the parts (it would poison the slider's own
        // coercion): it is displayed as the range start instead of crashing the row.
        if (!double.IsFinite(value)) value = EffectiveRange().Min;

        var previous = isSynchronizing;
        isSynchronizing = true;
        try
        {
            if (Math.Abs(SliderPart.Value - value) > Epsilon) SliderPart.Value = value;
            var rounded = (decimal)value;
            if (NumericPart.Value != rounded) NumericPart.Value = rounded;
        }
        finally { isSynchronizing = previous; }
    }

    private void ApplyRangeToParts()
    {
        var (min, max) = EffectiveRange();
        var step = EffectiveStep();
        var previous = isSynchronizing;
        isSynchronizing = true;
        try
        {
            SliderPart.Minimum = min;
            SliderPart.Maximum = max;
            SliderPart.TickFrequency = step;
            SliderPart.IsSnapToTickEnabled = true;
            NumericPart.Minimum = (decimal)min;
            NumericPart.Maximum = (decimal)max;
            NumericPart.Increment = (decimal)step;
            NumericPart.FormatString = string.IsNullOrEmpty(DisplayFormat) ? "0" : DisplayFormat;
        }
        finally { isSynchronizing = previous; }
    }

    /// <summary>A user-originated edit from one of the parts: snap, publish through Value, mirror to the sibling part.</summary>
    private void SetValueFromParts(double raw)
    {
        var snapped = Snap(raw);
        if (Math.Abs(snapped - Value) > Epsilon)
        {
            var previous = isSynchronizing;
            isSynchronizing = true;
            try { SetCurrentValue(ValueProperty, snapped); }
            finally { isSynchronizing = previous; }
        }
        else
        {
            // Same effective value: the parts may still hold a drifted raw input (e.g. a drag
            // past the range end) — normalise them without touching Value.
            PushValueToParts(snapped);
            UpdateDisplayText();
        }
    }

    private void UpdateEditorVisibility()
    {
        ValueTextPart.IsVisible = !ShowEditableValue;
        NumericPart.IsVisible = ShowEditableValue;
    }

    private void UpdateDisplayText()
    {
        var format = string.IsNullOrEmpty(DisplayFormat) ? "0" : DisplayFormat;
        var value = double.IsFinite(Value) ? Value : EffectiveRange().Min;
        string text;
        try
        {
            text = value.ToString(format, CultureInfo.CurrentCulture);
        }
        catch (FormatException)
        {
            text = value.ToString(CultureInfo.CurrentCulture);
        }

        ValueTextPart.Text = text + Unit;
    }

    /// <summary>The page-level accessible name reaches the interactive parts (the row itself is decorative).</summary>
    private void PropagateAutomationName()
    {
        var name = AutomationProperties.GetName(this);
        if (string.IsNullOrEmpty(name)) return;
        AutomationProperties.SetName(SliderPart, name);
        AutomationProperties.SetName(NumericPart, name);
        AutomationProperties.SetName(ValueTextPart, name);
    }

    // ---- part event handlers -----------------------------------------------------------------

    private void OnSliderPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs change)
    {
        if (change.Property != RangeBase.ValueProperty || isSynchronizing) return;
        SetValueFromParts(SliderPart.Value);
    }

    private void OnSliderDragCompleted(object? sender, VectorEventArgs e) => TryCommit();

    // A pointer release covers track clicks / repeat buttons, which change the value without a
    // thumb drag. The baseline check keeps press-without-change releases silent.
    private void OnSliderPointerReleased(object? sender, PointerReleasedEventArgs e) => TryCommit();

    private void OnSliderKeyUp(object? sender, KeyEventArgs e)
    {
        if (e.Key is Key.Left or Key.Right or Key.Up or Key.Down or Key.Home or Key.End or Key.PageUp or Key.PageDown)
            TryCommit();
    }

    private void OnNumericValueChanged(object? sender, NumericUpDownValueChangedEventArgs e)
    {
        if (isSynchronizing) return;
        // null appears while the text box holds a transient unparseable draft — never write it back.
        if (NumericPart.Value is { } value) SetValueFromParts((double)value);
    }

    private void OnNumericKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) TryCommit();
    }

    private void OnNumericLostFocus(object? sender, RoutedEventArgs e) => TryCommit();

    private void OnNumericSpinned(object? sender, SpinEventArgs e) => TryCommit();

    // ---- commit ------------------------------------------------------------------------------

    private void TryCommit()
    {
        if (isSynchronizing) return;
        var value = Value;
        if (Math.Abs(lastCommittedValue - value) <= Epsilon) return;
        lastCommittedValue = value;
        ValueCommitted?.Invoke(this, new LabeledSliderValueCommittedEventArgs(value));
    }
}

/// <summary>New value of a completed user edit; pages may keep reading <c>sender.Value</c> instead.</summary>
public sealed class LabeledSliderValueCommittedEventArgs(double value) : EventArgs
{
    public double Value { get; } = value;
}
