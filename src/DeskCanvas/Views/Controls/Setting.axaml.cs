using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;

namespace DeskCanvas.Views.Controls;

public partial class Setting : UserControl
{
    /// <summary>Legacy shape getters (kept for old plugins; the template no longer uses them —
    /// it binds the styled properties directly through converters, so they carry no implicit
    /// change notifications).</summary>
    public bool ShowIcon => Icon != null;
    public double? SubtitleHeight => SubtitleVisible ? null : 0;
    public double? AdminRequiredHeight => AdminRequired ? null : 0;
    public double? RestartRequiredHeight => RestartRequired ? null : 0;
    public bool SubtitleVisible => !string.IsNullOrEmpty(Subtitle);
    public int TitleRowSpan => 1 + (SubtitleVisible ? 0 : 1) + (AdminRequired ? 0 : 1) + (RestartRequired ? 0 : 1);
    
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

    /// <summary>The arrangement the panel measured for this row (Stacked when unmeasured and Stacked was requested).</summary>
    public SettingEffectiveLayout EffectiveLayout =>
        this.GetVisualDescendants().OfType<SettingLayoutPanel>().FirstOrDefault()?.EffectiveLayout
        ?? (LayoutMode == SettingLayoutMode.Stacked ? SettingEffectiveLayout.Stacked : SettingEffectiveLayout.Horizontal);

    public Setting()
    {
        InitializeComponent();
    }
}
