namespace Notes.Models;

/// <summary>
/// What stays of the note card's colored top bar (顶栏). The historic two-state
/// "show title bar" switch expanded into this: the bar and its title text are
/// separately disposable, but they are one setting, not two.
/// </summary>
public enum NoteHeaderMode
{
    /// <summary>顶栏与标题: the colored bar with its editable title text — the factory look.</summary>
    BarAndTitle = 0,

    /// <summary>仅顶栏: the colored bar stays (expand button included) but shows no title text.</summary>
    BarOnly = 1,

    /// <summary>隐藏顶栏: the bar and the divider below it collapse and the body owns the whole card.</summary>
    Hidden = 2,
}

/// <summary>
/// Where the note's body comes from.
/// </summary>
public enum NoteSource
{
    /// <summary>The text stored inside the widget's own settings.</summary>
    Internal,

    /// <summary>A single markdown file on disk.</summary>
    File,

    /// <summary>All (or the most recent / explicitly picked) .md files of a folder.</summary>
    Folder,
}

/// <summary>
/// One palette of markdown text colors. A <c>null</c> color keeps the theme
/// default for that format (accent for links, theme text otherwise).
/// </summary>
/// <param name="BodyColor">Plain paragraphs, list items and table cells.</param>
/// <param name="HeadingColor">Headings (all levels).</param>
/// <param name="BoldColor">Bold (and bold-italic) text.</param>
/// <param name="ItalicColor">Italic-only text.</param>
/// <param name="StrikeColor">Strikethrough-only text.</param>
/// <param name="LinkColor">Links (defaults to the accent color).</param>
/// <param name="CodeColor">Inline code and fenced code blocks text.</param>
/// <param name="QuoteColor">Blockquote text (the left bar follows it too).</param>
public record MarkdownPalette(
    string? BodyColor = null,
    string? HeadingColor = null,
    string? BoldColor = null,
    string? ItalicColor = null,
    string? StrikeColor = null,
    string? LinkColor = null,
    string? CodeColor = null,
    string? QuoteColor = null);

/// <summary>
/// Per-widget markdown typography overrides (colors per light/dark mode).
/// </summary>
/// <param name="Enabled">Master switch: when off every field is ignored.</param>
/// <param name="Font">Body font family; <c>null</c>/empty follows the app font.</param>
/// <param name="Light">Colors used while the app is in the light theme.</param>
/// <param name="Dark">Colors used while the app is in the dark theme.</param>
public record MarkdownTypography(
    bool Enabled = false,
    string? Font = null,
    MarkdownPalette? Light = null,
    MarkdownPalette? Dark = null);

/// <param name="Title">Widget title (internal/folder modes).</param>
/// <param name="Content">Note body (internal mode).</param>
/// <param name="Updated">Internal-mode "last updated" stamp.</param>
/// <param name="Markdown">Render the body as markdown (double-click to edit the source).</param>
/// <param name="FollowAccentHeader">The title bar follows the app accent color.</param>
/// <param name="HeaderColor">Custom title bar color (hex, e.g. #3376CD) when not following the accent.</param>
/// <param name="HeaderOpacity">Title bar opacity (0–1), applied in both color modes.</param>
/// <param name="Source">Body source: internal text, one file, or a folder of files.</param>
/// <param name="Path">Target file or folder for <see cref="Source"/>.</param>
/// <param name="RecentFiles">Folder mode: take the most recently modified documents.</param>
/// <param name="DocumentCount">Folder mode: how many documents to show.</param>
/// <param name="SelectedFiles">Folder mode: explicit file names (when <see cref="RecentFiles"/> is off).</param>
/// <param name="BodyPadding">Left/right inner padding of the note body text (DIPs).</param>
/// <param name="MarkdownStyle">Markdown typography overrides (see <see cref="MarkdownTypography"/>).</param>
/// <param name="AllowInlineEdit">
/// Allow editing the content by double-clicking the widget card. Default
/// <c>false</c>: the card is read-only and a double-click (or the header's
/// expand button) opens the secondary panel, where the note is edited. Can be
/// turned on in the widget settings.
/// </param>
/// <param name="HeaderMode">
/// 顶栏 mode: bar with title text, bare bar, or no bar at all (see
/// <see cref="NoteHeaderMode"/>). Default <see cref="NoteHeaderMode.BarAndTitle"/>; when the bar
/// is hidden the divider below it collapses too and the body owns the whole card —
/// the secondary panel stays reachable via double-click.
/// </param>
/// <param name="ShowTitle">
/// Legacy two-state switch from before the 顶栏 became three-way, kept so old configs
/// load with their original look. <c>false</c> still means <see cref="NoteHeaderMode.Hidden"/>,
/// with precedence over <see cref="HeaderMode"/>; the settings UI keeps it in step
/// (<c>false</c> exactly when the mode is <see cref="NoteHeaderMode.Hidden"/>), so only
/// configs predating this field ever rely on it.
/// </param>
public record NoteModel(
    string? Title = null,
    string? Content = null,
    DateTime? Updated = null,
    bool Markdown = true,
    bool FollowAccentHeader = true,
    string? HeaderColor = null,
    double HeaderOpacity = 1.0,
    NoteSource Source = NoteSource.Internal,
    string? Path = null,
    bool RecentFiles = true,
    int DocumentCount = 3,
    List<string>? SelectedFiles = null,
    int BodyPadding = 4,
    MarkdownTypography? MarkdownStyle = null,
    bool AllowInlineEdit = false,
    NoteHeaderMode HeaderMode = NoteHeaderMode.BarAndTitle,
    bool ShowTitle = true)
{
    /// <summary>The mode the card actually renders with, honouring the legacy switch.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public NoteHeaderMode EffectiveHeaderMode => ShowTitle
        ? HeaderMode
        : NoteHeaderMode.Hidden;
}
