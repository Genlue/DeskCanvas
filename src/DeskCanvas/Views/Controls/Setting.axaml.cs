using System;
using Avalonia;
using Avalonia.Layout;
using Avalonia.Media;
using DeskCanvas.Locales;
using SettingLayoutMode = DeskCanvas.UI.Controls.SettingLayoutMode;

namespace DeskCanvas.Views.Controls;

/// <summary>
/// Compatibility facade for existing settings pages and widget binaries.
/// The row layout, property storage and the template belong to DeskCanvas.UI; the facade keeps
/// the host type surface (styled-property fields, legacy shape getters) and feeds the localized
/// requirement hints from the host Locale.
/// </summary>
public partial class Setting : UI.Controls.Setting
{
    // Keep the host's public static fields available to old callers (one identity with the base).
    public new static readonly StyledProperty<StreamGeometry?> IconProperty = UI.Controls.Setting.IconProperty;
    public new static readonly StyledProperty<string> TitleProperty = UI.Controls.Setting.TitleProperty;
    public new static readonly StyledProperty<string> SubtitleProperty = UI.Controls.Setting.SubtitleProperty;
    public new static readonly StyledProperty<bool> AdminRequiredProperty = UI.Controls.Setting.AdminRequiredProperty;
    public new static readonly StyledProperty<bool> RestartRequiredProperty = UI.Controls.Setting.RestartRequiredProperty;
    public new static readonly StyledProperty<SettingLayoutMode> LayoutModeProperty = UI.Controls.Setting.LayoutModeProperty;
    public new static readonly StyledProperty<double> LabelMinWidthProperty = UI.Controls.Setting.LabelMinWidthProperty;
    public new static readonly StyledProperty<double> EditorMinWidthProperty = UI.Controls.Setting.EditorMinWidthProperty;
    public new static readonly StyledProperty<HorizontalAlignment> EditorHorizontalAlignmentProperty = UI.Controls.Setting.EditorHorizontalAlignmentProperty;

    // Preserve the existing diagnostic fixture's shape-getter surface.
    public bool ShowIcon => Icon != null;
    public double? SubtitleHeight => SubtitleVisible ? null : 0;
    public double? AdminRequiredHeight => AdminRequired ? null : 0;
    public double? RestartRequiredHeight => RestartRequired ? null : 0;
    public bool SubtitleVisible => !string.IsNullOrEmpty(Subtitle);
    public int TitleRowSpan => 1 + (SubtitleVisible ? 0 : 1) + (AdminRequired ? 0 : 1) + (RestartRequired ? 0 : 1);

    public Setting()
    {
        // Localized hint texts come from the host Locale so the shared template keeps rendering
        // exactly the old copy; the shared control carries neutral English defaults standalone.
        AdminRequiredText = Locale.Settings_AdminRequired;
        RestartRequiredText = Locale.Settings_RestartRequired;
    }
}
