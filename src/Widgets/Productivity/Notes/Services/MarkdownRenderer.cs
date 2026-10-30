using System.Text;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Markdig;
using Markdig.Extensions.Tables;
using Markdig.Extensions.TaskLists;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using DeskCanvas.Services;
using Notes.Models;
using Path = Avalonia.Controls.Shapes.Path;

namespace Notes.Services;

/// <summary>
/// Renders a markdown document as Avalonia controls (macOS-Notes-like typography:
/// bold headings, quiet body, accent links, soft code and quote surfaces).
/// Covers the widget-relevant subset: headings, paragraphs, inline styling,
/// links, code, lists (incl. task lists), quotes, rules and simple tables.
/// <para>
/// An optional <see cref="MarkdownTypography"/> overrides the colors (picked per
/// light/dark theme at render time) and the body font of the formats above;
/// colors left <c>null</c> keep the theme default.
/// </para>
/// </summary>
public static class MarkdownRenderer
{
    private static readonly MarkdownPipeline Pipeline =
        new MarkdownPipelineBuilder()
            .UseAdvancedExtensions()
            .UseSoftlineBreakAsHardlineBreak()
            .Build();

    private static readonly FontFamily MonoFamily = new("Cascadia Mono, Consolas, Courier New");

    // Chinese-note-taking tolerances: markers without the trailing space, full-width
    // markers (＃ ＞ ～～ ｜), ''italic'' and U+3000 spaces — all normalized to the
    // CommonMark forms Markdig understands before parsing.
    private static readonly Regex HeadingNoSpace = new(@"^(\s{0,3})(#{1,6})(?=[^\s#])", RegexOptions.Compiled);
    private static readonly Regex BulletNoSpace = new(@"^(\s*)([-+])(?=[^\s\-])", RegexOptions.Compiled);
    private static readonly Regex StarBulletNoSpace = new(@"^(\s*)\*(?!\*)(?=[^\s*])", RegexOptions.Compiled);
    private static readonly Regex QuoteNoSpace = new(@"^(\s*)>(?=[^\s>])", RegexOptions.Compiled);
    private static readonly Regex QuoteFullWidth = new(@"^(\s*)＞", RegexOptions.Compiled);
    private static readonly Regex QuoteItalic = new(@"''(?=\S)([^'\r\n]*?\S)''", RegexOptions.Compiled);
    private static readonly Regex FenceStart = new(@"^\s*(```|~~~)", RegexOptions.Compiled);

    /// <summary>
    /// Normalize common non-standard markdown spellings (see remarks) line-wise,
    /// leaving fenced code blocks untouched.
    /// </summary>
    public static string Preprocess(string? markdown)
    {
        if (string.IsNullOrEmpty(markdown)) return markdown ?? "";
        if (markdown.IndexOfAny(['#', '*', '~', '\'', '＃', '＞', '～', '｜', '　']) < 0) return markdown;

        var lines = markdown.Replace("\r\n", "\n").Split('\n');
        // The fence opener that is currently unclosed (``` or ~~~); fences only
        // close with their own marker, matching CommonMark.
        string? fence = null;
        var builder = new StringBuilder(markdown.Length + 16);

        foreach (var raw in lines)
        {
            var line = raw;

            var open = FenceStart.Match(line);
            if (open.Success)
            {
                if (fence == null) fence = open.Groups[1].Value;
                else if (fence == open.Groups[1].Value) fence = null;
            }

            if (fence == null)
            {
                line = line.Replace('＃', '#').Replace('｜', '|').Replace('　', ' ');
                line = QuoteFullWidth.Replace(line, "$1>");
                line = line.Replace("～～", "~~");
                line = HeadingNoSpace.Replace(line, "$1$2 ");
                line = BulletNoSpace.Replace(line, "$1$2 ");
                // "*text*" with a later asterisk on the line is (Chinese-style)
                // emphasis, not a space-less bullet — only convert lone stars.
                line = StarBulletNoSpace.Replace(line, m =>
                    line.IndexOf('*', m.Groups[1].Length + 1) < 0
                        ? $"{m.Groups[1].Value}* "
                        : m.Value);
                line = QuoteNoSpace.Replace(line, "$1> ");
                line = QuoteItalic.Replace(line, "*$1*");
            }

            builder.Append(line).Append('\n');
        }

        return builder.ToString();
    }

    /// <summary>
    /// Build the visual tree for a markdown string.
    /// </summary>
    /// <param name="scope">Element used to resolve theme resources (accent, base colors).</param>
    /// <param name="markdown">The markdown text.</param>
    /// <param name="fontSize">Body font size; headings scale around it.</param>
    /// <param name="typography">Per-widget style overrides (enabled flag, font and the
    /// palette matching the current light/dark theme); <c>null</c> renders with theme defaults.</param>
    public static Control Render(StyledElement scope, string? markdown, double fontSize = 14, MarkdownTypography? typography = null)
    {
        var palette = Palette.From(typography, scope);
        var panel = new StackPanel { Spacing = Math.Round(fontSize * 0.38) };

        if (string.IsNullOrWhiteSpace(markdown))
            return panel;

        foreach (var block in Markdown.Parse(Preprocess(markdown), Pipeline))
            AppendBlock(scope, panel, block, fontSize, palette, null);

        return panel;
    }

    /// <summary>
    /// Resolved typography for one render: colors and font of the active
    /// light/dark palette. Every member is null when the theme default applies.
    /// </summary>
    private sealed class Palette
    {
        public FontFamily? Font { get; init; }
        public IBrush? Body { get; init; }
        public IBrush? Heading { get; init; }
        public IBrush? Bold { get; init; }
        public IBrush? Italic { get; init; }
        public IBrush? Strike { get; init; }
        public IBrush? Link { get; init; }
        public IBrush? Code { get; init; }
        public IBrush? Quote { get; init; }

        public bool HasQuote => Quote != null;

        /// <summary>Pick the palette of the theme currently active, if styling is enabled.
        /// The variant is resolved per screen: the scope element's effective variant (each
        /// window can run light/dark independently) first, falling back to the app-wide
        /// variant when the scope has not resolved one yet (the historic behaviour).</summary>
        public static Palette From(MarkdownTypography? typography, StyledElement? scope)
        {
            if (typography is not { Enabled: true }) return new Palette();

            var scopeVariant = scope?.ActualThemeVariant;
            var variant = scopeVariant == ThemeVariant.Dark || scopeVariant == ThemeVariant.Light
                ? scopeVariant
                : Application.Current?.ActualThemeVariant;
            var isDark = variant == ThemeVariant.Dark;
            var colors = isDark ? typography.Dark : typography.Light;
            if (colors == null) return new Palette { Font = ResolveFont(typography.Font) };

            return new Palette
            {
                Font = ResolveFont(typography.Font),
                Body = Parse(colors.BodyColor),
                Heading = Parse(colors.HeadingColor),
                Bold = Parse(colors.BoldColor),
                Italic = Parse(colors.ItalicColor),
                Strike = Parse(colors.StrikeColor),
                Link = Parse(colors.LinkColor),
                Code = Parse(colors.CodeColor),
                Quote = Parse(colors.QuoteColor),
            };
        }

        private static IBrush? Parse(string? hex) =>
            hex != null && Color.TryParse(hex, out var color) ? new SolidColorBrush(color) : null;

        private static FontFamily? ResolveFont(string? family) =>
            string.IsNullOrWhiteSpace(family) ? null : new FontFamily(family);
    }

    private static void AppendBlock(StyledElement scope, Panel parent, Block block, double fontSize, Palette palette, IBrush? quoteBrush)
    {
        switch (block)
        {
            case HeadingBlock heading:
            {
                var host = NewTextBlock(fontSize * heading.Level switch
                {
                    1 => 1.5,
                    2 => 1.3,
                    3 => 1.15,
                    4 => 1.05,
                    _ => 0.95,
                }, palette);
                host.FontWeight = heading.Level <= 2 ? FontWeight.Bold : FontWeight.SemiBold;
                SetForeground(host, palette.Heading);
                AppendInline(host, heading.Inline, InlineStyle.Plain, palette);
                parent.Children.Add(host);
                break;
            }
            case ParagraphBlock paragraph:
            {
                var host = NewTextBlock(fontSize, palette);
                // Inside a quote the quote color is the plain-text base, otherwise
                // the body color; null keeps the theme default.
                SetForeground(host, quoteBrush ?? palette.Body);
                AppendInline(host, paragraph.Inline, InlineStyle.Plain, palette);
                parent.Children.Add(host);
                break;
            }
            case ListBlock list:
            {
                parent.Children.Add(BuildList(scope, list, fontSize, palette, quoteBrush));
                break;
            }
            case QuoteBlock quote:
            {
                var inner = new StackPanel { Spacing = Math.Round(fontSize * 0.3) };
                foreach (var child in quote)
                    AppendBlock(scope, inner, child, fontSize, palette, palette.Quote);
                // The quote surface is normally softened via opacity; a custom
                // quote color already is the author's choice, so keep it crisp.
                inner.Opacity = palette.HasQuote ? 1 : 0.78;
                parent.Children.Add(new Border
                {
                    Margin = new Thickness(0, 0, 0, 2),
                    Padding = new Thickness(10, 2, 2, 2),
                    BorderThickness = new Thickness(3, 0, 0, 0),
                    BorderBrush = palette.Quote ?? AccentBrush(scope, 0.9),
                    Child = inner,
                });
                break;
            }
            case ThematicBreakBlock:
            {
                parent.Children.Add(BuildRule());
                break;
            }
            case CodeBlock code:
            {
                parent.Children.Add(BuildCode(scope, code.Lines.ToString(), fontSize, palette));
                break;
            }
            case Table table:
            {
                parent.Children.Add(BuildTable(scope, table, fontSize, palette, quoteBrush));
                break;
            }
            case ContainerBlock container:
            {
                foreach (var child in container)
                    AppendBlock(scope, parent, child, fontSize, palette, quoteBrush);
                break;
            }
        }
    }

    private static Control BuildList(StyledElement scope, ListBlock list, double fontSize, Palette palette, IBrush? quoteBrush)
    {
        var panel = new StackPanel { Spacing = Math.Round(fontSize * 0.18) };
        var number = int.TryParse(list.OrderedStart, out var start) ? start : 1;

        foreach (var item in list.OfType<ListItemBlock>())
        {
            var (marker, body) = BuildItem(scope, item, list, number, fontSize, palette, quoteBrush);
            if (list.IsOrdered) number++;

            var columns = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto, *") };
            Grid.SetColumn(marker, 0);
            Grid.SetColumn(body, 1);
            columns.Children.Add(marker);
            columns.Children.Add(body);
            panel.Children.Add(columns);
        }

        return panel;
    }

    private static (TextBlock Marker, StackPanel Body) BuildItem(
        StyledElement scope, ListItemBlock item, ListBlock list, int number, double fontSize, Palette palette, IBrush? quoteBrush)
    {
        var marker = new TextBlock
        {
            Text = list.IsOrdered ? $"{number}." : "•",
            FontSize = fontSize,
            Opacity = 0.65,
            Margin = new Thickness(0, 0, 6, 0),
            MinWidth = 12,
        };
        ApplyFont(marker, palette);
        SetForeground(marker, quoteBrush ?? palette.Body);

        var body = new StackPanel { Spacing = list.IsLoose ? 4 : 2 };
        var first = true;
        var isTask = false;
        var taskChecked = false;

        foreach (var block in item)
        {
            if (first && block is ParagraphBlock paragraph && TryExtractTask(paragraph, out var checkedState))
            {
                isTask = true;
                taskChecked = checkedState;
                var host = NewTextBlock(fontSize, palette);
                SetForeground(host, quoteBrush ?? palette.Body);
                AppendInline(host, paragraph.Inline, InlineStyle.Plain, palette);
                body.Children.Add(host);
                first = false;
                continue;
            }

            first = false;
            AppendBlock(scope, body, block, fontSize, palette, quoteBrush);
        }

        if (isTask)
        {
            marker.Text = taskChecked ? "✓" : "○";
            if (taskChecked)
            {
                marker.Foreground = AccentBrush(scope, 1);
                marker.Opacity = 1;
                body.Opacity = 0.55;
            }
        }

        return (marker, body);
    }

    /// <summary>
    /// Detect Markdig's task-list marker at the start of an item's first paragraph.
    /// </summary>
    private static bool TryExtractTask(ParagraphBlock paragraph, out bool isChecked)
    {
        isChecked = false;
        if (paragraph.Inline == null) return false;

        foreach (var inline in paragraph.Inline)
        {
            if (inline is TaskList task)
            {
                isChecked = task.Checked;
                return true;
            }

            break;
        }

        return false;
    }

    private static Control BuildCode(StyledElement scope, string code, double fontSize, Palette palette)
    {
        var text = new TextBlock
        {
            Text = code.TrimEnd('\n'),
            FontFamily = MonoFamily,
            FontSize = Math.Round(fontSize * 0.9),
            TextWrapping = TextWrapping.Wrap,
        };
        SetForeground(text, palette.Code);
        return new Border
        {
            Background = ResolveBrush(scope, "SystemControlBackgroundBaseLowBrush", "#22808080"),
            CornerRadius = new CornerRadius(5),
            Padding = new Thickness(10, 7, 10, 7),
            Child = text,
        };
    }

    private static Control BuildTable(StyledElement scope, Table table, double fontSize, Palette palette, IBrush? quoteBrush)
    {
        var grid = new Grid { Margin = new Thickness(0, 2, 0, 2) };
        var rows = table.OfType<TableRow>().ToList();
        var columnCount = rows.Count == 0 ? 0 : rows.Max(r => r.OfType<TableCell>().Count());
        for (var c = 0; c < columnCount; c++)
            grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        for (var r = 0; r < rows.Count; r++)
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));

        for (var r = 0; r < rows.Count; r++)
        {
            var cells = rows[r].OfType<TableCell>().ToList();
            for (var c = 0; c < cells.Count && c < columnCount; c++)
            {
                var content = string.Join(" ", cells[c]
                    .OfType<ParagraphBlock>()
                    .Select(p => InlineToText(p.Inline)));
                var text = NewTextBlock(fontSize * 0.92, palette);
                text.Text = content;
                SetForeground(text, quoteBrush ?? palette.Body);
                text.Margin = new Thickness(0, 1, 14, 1);
                if (r == 0) text.FontWeight = FontWeight.SemiBold;
                Grid.SetRow(text, r);
                Grid.SetColumn(text, c);
                grid.Children.Add(text);
            }
        }

        return grid;
    }

    private static string InlineToText(Markdig.Syntax.Inlines.Inline? inline) => inline switch
    {
        null => "",
        LiteralInline literal => literal.Content.ToString(),
        CodeInline code => code.Content,
        AutolinkInline link => link.Url,
        ContainerInline container => string.Concat(container.Select(InlineToText)),
        _ => "",
    };

    private static Control BuildRule()
    {
        return new Border
        {
            Height = 12,
            Child = new Path
            {
                Height = 1,
                Stretch = Stretch.Fill,
                Opacity = 0.25,
                StrokeThickness = 1.5,
                StrokeDashArray = new Avalonia.Collections.AvaloniaList<double> { 1, 1 },
                StrokeDashOffset = 1,
                VerticalAlignment = VerticalAlignment.Center,
                Data = new LineGeometry(new Point(0, 0), new Point(1, 0)),
            },
        };
    }

    private static TextBlock NewTextBlock(double fontSize, Palette palette)
    {
        var block = new TextBlock
        {
            FontSize = Math.Round(fontSize * 10) / 10,
            TextWrapping = TextWrapping.Wrap,
        };
        // A custom markdown font applies to every text host (blocks that create
        // their own TextBlocks outside this helper — list markers — call
        // ApplyFont; code stays monospaced on purpose).
        ApplyFont(block, palette);
        return block;
    }

    private static void ApplyFont(TextBlock block, Palette palette)
    {
        if (palette.Font != null) block.FontFamily = palette.Font;
    }

    /// <summary>
    /// Set a palette color only when one is defined: an explicit <c>null</c>
    /// local value would override the theme/inherited foreground and render
    /// the text invisible.
    /// </summary>
    private static void SetForeground(TextBlock block, IBrush? brush)
    {
        if (brush != null) block.Foreground = brush;
    }

    private static void SetForeground(Avalonia.Controls.Documents.TextElement element, IBrush? brush)
    {
        if (brush != null) element.Foreground = brush;
    }

    private record InlineStyle
    {
        public static readonly InlineStyle Plain = new();

        public bool Bold { get; init; }
        public bool Italic { get; init; }
        public bool Strike { get; init; }
        public bool Link { get; init; }
        public bool Code { get; init; }
    }

    private static void AppendInline(TextBlock host, Markdig.Syntax.Inlines.Inline? inline, InlineStyle style, Palette palette)
    {
        switch (inline)
        {
            case null:
                break;
            case EmphasisInline emphasis:
            {
                var next = emphasis.DelimiterChar switch
                {
                    '~' => style with { Strike = true },
                    _ when emphasis.DelimiterCount >= 3 => style with { Bold = true, Italic = true },
                    _ when emphasis.DelimiterCount == 2 => style with { Bold = true },
                    _ => style with { Italic = true },
                };
                foreach (var child in emphasis)
                    AppendInline(host, child, next, palette);
                break;
            }
            case LinkInline link:
            {
                var next = link.IsImage ? style : style with { Link = true };
                foreach (var child in link)
                    AppendInline(host, child, next, palette);
                break;
            }
            case ContainerInline container:
            {
                foreach (var child in container)
                    AppendInline(host, child, style, palette);
                break;
            }
            case LiteralInline literal:
            {
                host.Inlines!.Add(BuildRun(literal.Content.ToString(), style, host, palette));
                break;
            }
            case CodeInline code:
            {
                host.Inlines!.Add(BuildRun(code.Content, style with { Code = true }, host, palette));
                break;
            }
            case AutolinkInline link:
            {
                host.Inlines!.Add(BuildRun(link.Url, style with { Link = true }, host, palette));
                break;
            }
            case HtmlEntityInline entity:
            {
                var transcoded = entity.Transcoded;
                host.Inlines!.Add(BuildRun(
                    transcoded.Length > 0 ? transcoded.ToString() : entity.Original.ToString(),
                    style, host, palette));
                break;
            }
            case LineBreakInline:
            {
                host.Inlines!.Add(new Run("\n"));
                break;
            }
            case TaskList:
                break; // surfaced as the item marker
        }
    }

    private static Run BuildRun(string text, InlineStyle style, TextBlock host, Palette palette)
    {
        var run = new Run(text);

        if (style.Bold) run.FontWeight = FontWeight.Bold;
        if (style.Italic) run.FontStyle = FontStyle.Italic;

        if (style.Strike && style.Link)
            run.TextDecorations = new TextDecorationCollection
            {
                new TextDecoration { Location = TextDecorationLocation.Underline },
                new TextDecoration { Location = TextDecorationLocation.Strikethrough },
            };
        else if (style.Strike)
            run.TextDecorations = TextDecorations.Strikethrough;
        else if (style.Link)
            run.TextDecorations = TextDecorations.Underline;

        // Color precedence: code > link > bold > italic > strikethrough. Runs
        // without a color here inherit the host's foreground (block color or
        // the theme text color). Links always take their color (accent by
        // default) so they stay recognizable. NEVER assign an explicit null:
        // a local null would override the theme/inherited foreground and make
        // the text render invisible.
        if (style.Code)
            SetForeground(run, palette.Code);
        else if (style.Link)
            run.Foreground = palette.Link ?? AccentBrush(host, 1);
        else if (style.Bold)
            SetForeground(run, palette.Bold);
        else if (style.Italic)
            SetForeground(run, palette.Italic);
        else if (style.Strike)
            SetForeground(run, palette.Strike);

        if (style.Code)
        {
            run.FontFamily = MonoFamily;
            run.FontSize = Math.Round(host.FontSize * 0.92 * 10) / 10;
            run.Background = ResolveBrush(host, "SystemControlBackgroundBaseLowBrush", "#33808080");
        }

        return run;
    }

    private static IBrush? AccentBrush(StyledElement scope, double opacity)
    {
        var color = ResolveThemed(scope, "SystemAccentColor") is Color c
            ? c
            : Color.Parse("#0078D4");
        return opacity >= 1 ? new SolidColorBrush(color) : new SolidColorBrush(color, opacity);
    }

    private static IBrush ResolveBrush(StyledElement scope, string key, string fallback) =>
        ResolveThemed(scope, key) as IBrush ?? new SolidColorBrush(Color.Parse(fallback));

    /// <summary>
    /// Resolve a theme resource with the element's ACTIVE variant made explicit via ThemeScopeResolver.
    /// Walks element -> Window -> Application so window-scoped colorful or normal theme isolation
    /// takes precedence over Application.Current.
    /// </summary>
    private static object? ResolveThemed(StyledElement scope, string key)
    {
        if (ThemeScopeResolver.TryFindResource(scope as Visual, key, scope.ActualThemeVariant, out var res))
            return res;
        return null;
    }
}
