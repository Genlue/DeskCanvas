using System.Collections.Generic;
using System.Globalization;

namespace DeskCanvas.ViewModels;

/// <summary>
/// One gallery section: every widget preview sharing a <c>WidgetInfoAttribute.Category</c>.
/// Built once per gallery load and treated as immutable afterwards.
/// </summary>
public sealed record WidgetCategoryGroup(
    string CategoryKey,
    string Icon,
    string Title,
    IReadOnlyList<WidgetPreviewViewModel> Widgets)
{
    private static bool IsZh => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.Equals(
        "zh", StringComparison.OrdinalIgnoreCase);

    /// <summary>Count badge next to the section header, e.g. "8 个组件" / "8 widgets".</summary>
    public string CountText => Widgets.Count == 1
        ? IsZh ? "1 个组件" : "1 widget"
        : IsZh ? $"{Widgets.Count} 个组件" : $"{Widgets.Count} widgets";
}
