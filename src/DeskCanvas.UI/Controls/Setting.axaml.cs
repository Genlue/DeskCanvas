using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;

namespace DeskCanvas.UI.Controls;

/// <summary>
/// Shared settings row: icon, label group (title / subtitle / requirement hints) and the SINGLE
/// editor presenter, arranged by <see cref="SettingLayoutPanel"/>. The control owns the row
/// contract only — pages keep binding their own editors and models.
///
/// The requirement hints are user-visible copy the EMBEDDING APPLICATION localizes:
/// <see cref="AdminRequiredText"/> and <see cref="RestartRequiredText"/> carry neutral English
/// defaults, and the host facade feeds its Locale resources so existing pages keep their
/// localized rendering without any page change. The shared template never hardcodes copy.
/// </summary>
public partial class Setting : UserControl
{
    public static readonly StyledProperty<StreamGeometry?> IconProperty =
        AvaloniaProperty.Register<Setting, StreamGeometry?>(nameof(Icon));

    public static readonly StyledProperty<string> TitleProperty =
        AvaloniaProperty.Register<Setting, string>(nameof(Title));

    public static readonly StyledProperty<string> SubtitleProperty =
        AvaloniaProperty.Register<Setting, string>(nameof(Subtitle));

    public static readonly StyledProperty<bool> AdminRequiredProperty =
        AvaloniaProperty.Register<Setting, bool>(nameof(AdminRequired));

    public static readonly StyledProperty<bool> RestartRequiredProperty =
        AvaloniaProperty.Register<Setting, bool>(nameof(RestartRequired));

    // ---- adaptive layout contract (W3) ---------------------------------------------------

    /// <summary>Auto (default) stacks when the row is too narrow; Inline forces one line; Stacked always splits.</summary>
    public static readonly StyledProperty<SettingLayoutMode> LayoutModeProperty =
        AvaloniaProperty.Register<Setting, SettingLayoutMode>(nameof(LayoutMode));

    /// <summary>Width the label group keeps in the horizontal arrangement (keep in sync with SettingsRowLabelMinWidth).</summary>
    public static readonly StyledProperty<double> LabelMinWidthProperty =
        AvaloniaProperty.Register<Setting, double>(nameof(LabelMinWidth), 160.0);

    /// <summary>Page-declared reasonable editor minimum; the editor is asked for its own width first.</summary>
    public static readonly StyledProperty<double> EditorMinWidthProperty =
        AvaloniaProperty.Register<Setting, double>(nameof(EditorMinWidth));

    /// <summary>Editor placement; Stacked wide editors use Stretch, compact switches keep the default Right.</summary>
    public static readonly StyledProperty<HorizontalAlignment> EditorHorizontalAlignmentProperty =
        AvaloniaProperty.Register<Setting, HorizontalAlignment>(nameof(EditorHorizontalAlignment), HorizontalAlignment.Right);

    // ---- localized hint texts ------------------------------------------------------------

    /// <summary>Copy shown while <see cref="AdminRequired"/> is set (localized by the embedding app).</summary>
    public static readonly StyledProperty<string> AdminRequiredTextProperty =
        AvaloniaProperty.Register<Setting, string>(nameof(AdminRequiredText),
            "Run the application as Administrator to change this setting");

    /// <summary>Copy shown while <see cref="RestartRequired"/> is set (localized by the embedding app).</summary>
    public static readonly StyledProperty<string> RestartRequiredTextProperty =
        AvaloniaProperty.Register<Setting, string>(nameof(RestartRequiredText),
            "Restart the app to apply changes");

    public StreamGeometry? Icon
    {
        get => GetValue(IconProperty);
        set => SetValue(IconProperty, value);
    }

    public string Title
    {
        get => GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public string Subtitle
    {
        get => GetValue(SubtitleProperty);
        set => SetValue(SubtitleProperty, value);
    }

    public bool AdminRequired
    {
        get => GetValue(AdminRequiredProperty);
        set => SetValue(AdminRequiredProperty, value);
    }

    public bool RestartRequired
    {
        get => GetValue(RestartRequiredProperty);
        set => SetValue(RestartRequiredProperty, value);
    }

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

    public HorizontalAlignment EditorHorizontalAlignment
    {
        get => GetValue(EditorHorizontalAlignmentProperty);
        set => SetValue(EditorHorizontalAlignmentProperty, value);
    }

    public string AdminRequiredText
    {
        get => GetValue(AdminRequiredTextProperty);
        set => SetValue(AdminRequiredTextProperty, value);
    }

    public string RestartRequiredText
    {
        get => GetValue(RestartRequiredTextProperty);
        set => SetValue(RestartRequiredTextProperty, value);
    }

    /// <summary>The arrangement the panel measured for this row (Stacked when unmeasured and Stacked was requested).</summary>
    public SettingEffectiveLayout EffectiveLayout =>
        this.GetVisualDescendants().OfType<SettingLayoutPanel>().FirstOrDefault()?.EffectiveLayout
        ?? (LayoutMode == SettingLayoutMode.Stacked ? SettingEffectiveLayout.Stacked : SettingEffectiveLayout.Horizontal);

    public Setting()
    {
        InitializeComponent();
    }
}
