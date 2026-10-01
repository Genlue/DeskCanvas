namespace Notes.Models;

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
/// <param name="ShowTitle">
/// Show the colored title bar on the card. Default <c>true</c>; when off the
/// header (and the divider below it) collapse and the body owns the whole card —
/// the secondary panel stays reachable via double-click.
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
    bool ShowTitle = true);
