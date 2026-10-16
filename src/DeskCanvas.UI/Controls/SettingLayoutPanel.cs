using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

namespace DeskCanvas.UI.Controls;

/// <summary>Explicit layout request of a settings row; Auto decides from the measured width.</summary>
public enum SettingLayoutMode
{
    /// <summary>Stack when the row is too narrow for the label minimum plus the editor.</summary>
    Auto,

    /// <summary>Keep the single-line arrangement (short labels / small switches); the label still wraps.</summary>
    Inline,

    /// <summary>Always stacked: label group on the first row, editor on the second.</summary>
    Stacked,
}

/// <summary>The measured result of <see cref="SettingLayoutMode.Auto"/>.</summary>
public enum SettingEffectiveLayout
{
    Horizontal,
    Stacked,
}

/// <summary>Slots of the three (at most) children a <see cref="SettingLayoutPanel"/> arranges.</summary>
public enum SettingLayoutSlot
{
    Icon,
    Label,
    Editor,
}

/// <summary>
/// Three-slot layout for a settings row: icon (optional), the label group (title / subtitle /
/// requirement hints) and the SINGLE editor presenter. The panel decides horizontal vs stacked
/// during measurement from the row's actual width:
/// <code>T = iconWidthWithGap + LabelMinWidth + RowGap + editorRequiredWidth</code>
/// with a hysteresis band (needs <c>T + 16</c> to return to horizontal) so dragging a window
/// edge near the threshold does not flap the arrangement. The editor requirement is computed
/// from explicit sizes only and measured with an unconstrained width, independent of the
/// current arrangement — deriving it from the already-stacked (narrow) width would shrink the
/// threshold and flip back.
/// </summary>
public class SettingLayoutPanel : Panel
{
    /// <summary>Gap added after the threshold before flipping back to horizontal (anti-flap).</summary>
    public const double Hysteresis = 16;

    /// <summary>Where a child belongs; children without a slot are treated as the label group.</summary>
    public static readonly AttachedProperty<SettingLayoutSlot> SlotProperty =
        AvaloniaProperty.RegisterAttached<SettingLayoutPanel, Control, SettingLayoutSlot>("Slot");

    public static SettingLayoutSlot GetSlot(Control element) => element.GetValue(SlotProperty);
    public static void SetSlot(Control element, SettingLayoutSlot value) => element.SetValue(SlotProperty, value);

    public static readonly StyledProperty<SettingLayoutMode> LayoutModeProperty =
        AvaloniaProperty.Register<SettingLayoutPanel, SettingLayoutMode>(nameof(LayoutMode));

    /// <summary>Guaranteed width reserved for the label group in the horizontal arrangement.</summary>
    public static readonly StyledProperty<double> LabelMinWidthProperty =
        AvaloniaProperty.Register<SettingLayoutPanel, double>(nameof(LabelMinWidth), 160.0);

    /// <summary>Editor width the page declares as its reasonable minimum (0 = measure only).</summary>
    public static readonly StyledProperty<double> EditorMinWidthProperty =
        AvaloniaProperty.Register<SettingLayoutPanel, double>(nameof(EditorMinWidth));

    /// <summary>Placement of the editor: Right (default) in a row, Stretch for stacked wide editors.</summary>
    public static readonly StyledProperty<HorizontalAlignment> EditorAlignmentProperty =
        AvaloniaProperty.Register<SettingLayoutPanel, HorizontalAlignment>(nameof(EditorAlignment), HorizontalAlignment.Right);

    /// <summary>Vertical gap between the label row and the editor row when stacked.</summary>
    public static readonly StyledProperty<double> RowGapProperty =
        AvaloniaProperty.Register<SettingLayoutPanel, double>(nameof(RowGap), 12.0);

    /// <summary>Horizontal gap after the icon (the icon only accompanies the label group).</summary>
    public static readonly StyledProperty<double> IconGapProperty =
        AvaloniaProperty.Register<SettingLayoutPanel, double>(nameof(IconGap), 14.0);

    public static readonly DirectProperty<SettingLayoutPanel, SettingEffectiveLayout> EffectiveLayoutProperty =
        AvaloniaProperty.RegisterDirect<SettingLayoutPanel, SettingEffectiveLayout>(
            nameof(EffectiveLayout), panel => panel.EffectiveLayout);

    public SettingLayoutMode LayoutMode
    {
        get => GetValue(LayoutModeProperty);
        set => SetValue(LayoutModeProperty, value);
    }

    public double LabelMinWidth
    {
        get => GetValue(LabelMinWidthProperty);
        set => SetValue(LabelMinWidthProperty, value);
    }

    public double EditorMinWidth
    {
        get => GetValue(EditorMinWidthProperty);
        set => SetValue(EditorMinWidthProperty, value);
    }

    public HorizontalAlignment EditorAlignment
    {
        get => GetValue(EditorAlignmentProperty);
        set => SetValue(EditorAlignmentProperty, value);
    }

    public double RowGap
    {
        get => GetValue(RowGapProperty);
        set => SetValue(RowGapProperty, value);
    }

    public double IconGap
    {
        get => GetValue(IconGapProperty);
        set => SetValue(IconGapProperty, value);
    }

    private SettingEffectiveLayout effectiveLayout;
    public SettingEffectiveLayout EffectiveLayout
    {
        get => effectiveLayout;
        private set => SetAndRaise(EffectiveLayoutProperty, ref effectiveLayout, value);
    }

    /// <summary>True while the current arrangement is stacked (state for the hysteresis band).</summary>
    private bool stacked;

    /// <summary>The threshold from the last measurement; exposed for the foundation checks.</summary>
    public double LastHorizontalThreshold { get; private set; }

    static SettingLayoutPanel()
    {
        AffectsMeasure<SettingLayoutPanel>(
            LayoutModeProperty, LabelMinWidthProperty, EditorMinWidthProperty,
            EditorAlignmentProperty, RowGapProperty, IconGapProperty);
        SlotProperty.Changed.AddClassHandler<Control>((control, _) =>
        {
            if (control.Parent is SettingLayoutPanel panel) panel.InvalidateMeasure();
        });
    }

    private Control? Child(SettingLayoutSlot slot) => FindChild(slot);

    private Control? FindChild(SettingLayoutSlot slot)
    {
        foreach (var child in Children)
        {
            if (child is Control control && GetSlot(control) == slot) return control;
        }
        return null;
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var icon = FindChild(SettingLayoutSlot.Icon);
        var label = FindChild(SettingLayoutSlot.Label);
        var editor = FindChild(SettingLayoutSlot.Editor);

        var iconVisible = icon is { IsVisible: true };
        double iconWidth = 0;
        if (iconVisible && icon is not null)
        {
            icon.Measure(Size.Infinity);
            iconWidth = icon.DesiredSize.Width + IconGap;
        }

        // Stable editor requirement: explicit sizes first, then the unconstrained natural width
        // (guarded against Infinity, which a stretchy editor measured without bounds may report).
        double editorRequired = Math.Max(0, EditorMinWidth);
        double editorNaturalWidth = 0, editorNaturalHeight = 0;
        if (editor is not null)
        {
            if (!double.IsNaN(editor.Width) && editor.Width > 0)
                editorRequired = Math.Max(editorRequired, editor.Width);
            if (editor.MinWidth > 0)
                editorRequired = Math.Max(editorRequired, editor.MinWidth);

            editor.Measure(Size.Infinity);
            editorNaturalWidth = editor.DesiredSize.Width;
            editorNaturalHeight = editor.DesiredSize.Height;
            if (double.IsFinite(editorNaturalWidth))
                editorRequired = Math.Max(editorRequired, editorNaturalWidth);
        }

        var rowGap = Math.Max(0, RowGap);
        var labelMin = Math.Max(0, LabelMinWidth);
        var threshold = iconWidth + labelMin + rowGap + editorRequired;
        LastHorizontalThreshold = threshold;

        var width = availableSize.Width;
        stacked = LayoutMode switch
        {
            SettingLayoutMode.Stacked => true,
            SettingLayoutMode.Inline => false,
            _ => double.IsFinite(width) && (stacked ? width < threshold + Hysteresis : width < threshold),
        };
        EffectiveLayout = stacked ? SettingEffectiveLayout.Stacked : SettingEffectiveLayout.Horizontal;

        if (stacked)
        {
            double labelHeight = 0;
            if (label is { IsVisible: true })
            {
                label.Measure(new Size(FiniteOr(Subtract(width, iconWidth), iconWidth), double.PositiveInfinity));
                labelHeight = label.DesiredSize.Height;
            }

            double editorHeight = 0;
            if (editor is { IsVisible: true })
            {
                editor.Measure(new Size(FiniteOr(width, editorNaturalWidth), double.PositiveInfinity));
                editorHeight = editor.DesiredSize.Height;
            }

            var iconHeight = iconVisible && icon is not null ? icon.DesiredSize.Height : 0;
            var firstRow = Math.Max(iconHeight, labelHeight);
            var height = firstRow + (editorHeight > 0 ? rowGap + editorHeight : 0);
            return new Size(FiniteOr(width, Math.Max(firstRow, editorNaturalWidth)), height);
        }
        else
        {
            double editorWidth = editorNaturalWidth, editorHeight = editorNaturalHeight;
            if (editor is { IsVisible: true })
            {
                // The editor keeps its natural width; the label group keeps at least labelMin.
                var editorAvailable = FiniteOr(Subtract(width, iconWidth + labelMin + rowGap), double.PositiveInfinity);
                editor.Measure(new Size(editorAvailable, double.PositiveInfinity));
                editorWidth = editor.DesiredSize.Width;
                editorHeight = editor.DesiredSize.Height;
            }
            else
            {
                editorWidth = 0;
                editorHeight = 0;
            }

            double labelHeight = 0;
            if (label is { IsVisible: true })
            {
                var labelAvailable = FiniteOr(Subtract(width, iconWidth + editorWidth + rowGap), double.PositiveInfinity);
                label.Measure(new Size(labelAvailable, double.PositiveInfinity));
                labelHeight = label.DesiredSize.Height;
            }

            var iconHeight = iconVisible && icon is not null ? icon.DesiredSize.Height : 0;
            var height = Math.Max(Math.Max(iconHeight, labelHeight), editorHeight);
            var naturalWidth = iconWidth + Math.Max(label?.DesiredSize.Width ?? 0, labelMin) + rowGap + editorWidth;
            return new Size(FiniteOr(width, naturalWidth), height);
        }
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var icon = FindChild(SettingLayoutSlot.Icon);
        var label = FindChild(SettingLayoutSlot.Label);
        var editor = FindChild(SettingLayoutSlot.Editor);
        var iconVisible = icon is { IsVisible: true };

        var rowGap = Math.Max(0, RowGap);
        var iconWidth = iconVisible && icon is not null ? icon.DesiredSize.Width + IconGap : 0;
        var iconHeight = iconVisible && icon is not null ? icon.DesiredSize.Height : 0;

        if (stacked)
        {
            var labelHeight = label is { IsVisible: true } ? label.DesiredSize.Height : 0;
            var firstRow = Math.Max(iconHeight, labelHeight);

            // The icon accompanies the LABEL group only — it is centered on the first row,
            // never spanning the editor below it.
            icon?.Arrange(new Rect(0, Math.Max(0, (firstRow - iconHeight) / 2),
                iconVisible ? icon.DesiredSize.Width : 0, iconHeight));
            label?.Arrange(new Rect(iconWidth, 0, Math.Max(0, finalSize.Width - iconWidth), labelHeight));

            if (editor is { IsVisible: true })
            {
                var y = firstRow + rowGap;
                var editorHeight = editor.DesiredSize.Height;
                switch (EditorAlignment)
                {
                    case HorizontalAlignment.Stretch:
                        editor.Arrange(new Rect(0, y, finalSize.Width, editorHeight));
                        break;
                    case HorizontalAlignment.Left:
                        editor.Arrange(new Rect(0, y, editor.DesiredSize.Width, editorHeight));
                        break;
                    case HorizontalAlignment.Center:
                        editor.Arrange(new Rect(Math.Max(0, (finalSize.Width - editor.DesiredSize.Width) / 2), y,
                            editor.DesiredSize.Width, editorHeight));
                        break;
                    default: // Right
                        editor.Arrange(new Rect(Math.Max(0, finalSize.Width - editor.DesiredSize.Width), y,
                            editor.DesiredSize.Width, editorHeight));
                        break;
                }
            }
        }
        else
        {
            var editorWidth = editor is { IsVisible: true } ? editor.DesiredSize.Width : 0;
            var editorHeight = editor is { IsVisible: true } ? editor.DesiredSize.Height : 0;
            var labelWidth = Math.Max(0, finalSize.Width - iconWidth - editorWidth - rowGap);
            var labelHeight = label is { IsVisible: true } ? label.DesiredSize.Height : 0;

            icon?.Arrange(new Rect(0, Math.Max(0, (finalSize.Height - iconHeight) / 2),
                iconVisible ? icon.DesiredSize.Width : 0, iconHeight));
            label?.Arrange(new Rect(iconWidth, Math.Max(0, (finalSize.Height - labelHeight) / 2),
                labelWidth, labelHeight));

            if (editor is { IsVisible: true })
            {
                var y = Math.Max(0, (finalSize.Height - editorHeight) / 2);
                switch (EditorAlignment)
                {
                    case HorizontalAlignment.Stretch:
                        var stretchX = iconWidth + Math.Max(0, LabelMinWidth) + rowGap;
                        editor.Arrange(new Rect(stretchX, y, Math.Max(0, finalSize.Width - stretchX), editorHeight));
                        break;
                    case HorizontalAlignment.Left:
                        editor.Arrange(new Rect(iconWidth, y, editorWidth, editorHeight));
                        break;
                    case HorizontalAlignment.Center:
                        editor.Arrange(new Rect(Math.Max(0, (finalSize.Width - editorWidth) / 2), y, editorWidth, editorHeight));
                        break;
                    default: // Right
                        editor.Arrange(new Rect(Math.Max(0, finalSize.Width - editorWidth), y, editorWidth, editorHeight));
                        break;
                }
            }
        }

        return finalSize;
    }

    private static double Subtract(double value, double amount)
        => double.IsFinite(value) ? Math.Max(0, value - amount) : value;

    private static double FiniteOr(double value, double fallback)
        => double.IsFinite(value) ? value : fallback;
}
