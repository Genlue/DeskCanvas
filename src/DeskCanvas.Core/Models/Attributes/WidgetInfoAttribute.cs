using System.Text.RegularExpressions;

namespace DeskCanvas.Core.Models.Attributes;

/// <summary>
/// Attribute to register a widget inside an assembly.
/// </summary>
/// <param name="viewType">Type of UserControl to render.</param>
/// <param name="modelType">Type of Model, that will be stored in <c>layout.json</c> and provided via DI.</param>
/// <param name="editModelViewType">Type of UserControl for editing the model.</param>
/// <param name="title">Resource key of your widget's name</param>
/// <param name="subtitle">Resource key of your widget's description</param>
/// <param name="defaultColumns">Default width in grid columns (defaults to 2).</param>
/// <param name="defaultRows">Default height in grid rows (defaults to 2).</param>
/// <param name="presetSpans">Preset spans offered in the gallery's size panel, formatted
/// as "WxH" pairs separated by commas (e.g. "2x2,4x2,4x4"). Null/empty means the widget
/// is placed at its default span only and the gallery shows no size panel.</param>
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
public class WidgetInfoAttribute(
    Type viewType,
    Type? modelType = null,
    Type? editModelViewType = null,
    string? title = null,
    string? subtitle = null,
    int defaultColumns = 2,
    int defaultRows = 2,
    string? presetSpans = null)
    : Attribute
{
    /// <summary>
    /// Pre-presetSpans constructor signature. Widget assemblies compiled against the
    /// old attribute carry custom-attribute blobs that bind this exact 7-parameter
    /// constructor — keep it so a stale widget DLL (hot-updated out of step with the
    /// host) still loads instead of failing reflection activation.
    /// </summary>
    public WidgetInfoAttribute(
        Type viewType,
        Type? modelType,
        Type? editModelViewType,
        string? title,
        string? subtitle,
        int defaultColumns,
        int defaultRows)
        : this(viewType, modelType, editModelViewType, title, subtitle, defaultColumns, defaultRows, null)
    {
    }
    /// <summary>
    /// Type of UserControl to render.
    /// </summary>
    public Type ViewType { get; } = viewType;
    
    /// <summary>
    /// Type of Model, that will be stored in <c>layout.json</c> and provided via DI.
    /// </summary>
    public Type? ModelType { get; } = modelType;
    
    /// <summary>
    /// Type of UserControl for editing the model.
    /// <para>Will be rendered in a separate window on Right-click, "Edit Widget"</para>
    /// </summary>
    public Type? EditModelViewType { get; } = editModelViewType;
    
    /// <summary>
    /// Resource key of your widget's name.
    /// </summary>
    public string? Title { get; } = title;
    
    /// <summary>
    /// Resource key of your widget's description.
    /// </summary>
    public string? Subtitle { get; } = subtitle;

    /// <summary>
    /// Default width in grid columns.
    /// </summary>
    public int DefaultColumns { get; } = defaultColumns;

    /// <summary>
    /// Default height in grid rows.
    /// </summary>
    public int DefaultRows { get; } = defaultRows;

    /// <summary>
    /// Raw preset span list as declared (e.g. "2x2,4x2,4x4").
    /// </summary>
    public string? PresetSpanList { get; } = presetSpans;

    private IReadOnlyList<(int Columns, int Rows)>? presetSpansCache;

    /// <summary>
    /// Parsed <see cref="PresetSpanList"/>; empty when no presets are declared.
    /// Malformed entries are skipped rather than throwing — a widget must stay loadable.
    /// </summary>
    public IReadOnlyList<(int Columns, int Rows)> PresetSpans
    {
        get
        {
            if (presetSpansCache != null) return presetSpansCache;
            var spans = new List<(int Columns, int Rows)>();
            if (!string.IsNullOrWhiteSpace(PresetSpanList))
            {
                foreach (var part in PresetSpanList.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    var match = Regex.Match(part, @"^(\d+)x(\d+)$", RegexOptions.IgnoreCase);
                    if (!match.Success) continue;
                    var columns = int.Parse(match.Groups[1].Value);
                    var rows = int.Parse(match.Groups[2].Value);
                    if (columns >= 1 && rows >= 1)
                        spans.Add((columns, rows));
                }
            }
            presetSpansCache = spans;
            return spans;
        }
    }
}