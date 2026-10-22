using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using DeskCanvas.Core.Services;

namespace DeskCanvas.Services;

/// <summary>
/// A Spotlight-style file query: what the user typed, parsed once into something both backends can
/// execute — Everything's search syntax, and a local matcher for the fallback index.
/// <para>
/// <b>Syntax</b> (all optional, all case-insensitive):
/// </para>
/// <list type="bullet">
/// <item><c>foo bar</c> — every word must appear in the name or the path.</item>
/// <item><c>"annual report"</c> — a phrase; quotes keep the space.</item>
/// <item><c>ext:cs,ts</c> — extension filter (leading dots and spaces are ignored).</item>
/// <item><c>path:src</c> — the folder must contain this text.</item>
/// <item><c>-draft</c> or <c>!draft</c> — exclude anything containing it.</item>
/// <item><c>/pattern/</c> or <c>re:pattern</c> — regular expression, matched against the name and
/// then the path. <c>/pattern/i</c> is accepted (i = ignore case, the default anyway).</item>
/// </list>
/// <para>
/// <b>Why a parser instead of passing the raw text through:</b> Everything understands its own
/// syntax, the fallback index understands none of it. Parsing once means "ext:cs" means the same
/// thing whichever backend answers, and a broken regex is reported instead of silently returning
/// nothing.
/// </para>
/// </summary>
public sealed class FileSearchQuery
{
    private readonly List<string> terms = [];
    private readonly List<string> pathTerms = [];
    private readonly List<string> excludes = [];
    private readonly List<string> extensions = [];
    private Regex? pattern;

    private FileSearchQuery(string raw) => Raw = raw;

    /// <summary>The text exactly as typed.</summary>
    public string Raw { get; }

    /// <summary>Free-text terms, all of which must match (Everything's implicit AND).</summary>
    public IReadOnlyList<string> Terms => terms;

    /// <summary>Folder filters (<c>path:</c>).</summary>
    public IReadOnlyList<string> PathTerms => pathTerms;

    /// <summary>Exclusions (<c>-term</c> / <c>!term</c>).</summary>
    public IReadOnlyList<string> Excludes => excludes;

    /// <summary>Extension filters without the dot.</summary>
    public IReadOnlyList<string> Extensions => extensions;

    /// <summary>True when the query is a regular expression.</summary>
    public bool IsRegex => pattern != null || RegexError != null;

    /// <summary>Non-null when a regular expression was requested but does not compile.</summary>
    public string? RegexError { get; private set; }

    /// <summary>True when there is nothing to search for.</summary>
    public bool IsEmpty => pattern == null && RegexError == null
                           && terms.Count == 0 && pathTerms.Count == 0 && extensions.Count == 0;

    /// <summary>The query translated into Everything's own syntax.</summary>
    public string EverythingSyntax
    {
        get
        {
            if (RegexError != null) return string.Empty;

            var builder = new StringBuilder();
            if (pattern != null)
            {
                // Everything takes the whole search as one regex; the structured filters are
                // still allowed next to it because Everything evaluates those separately.
                Append(builder, "regex:" + pattern);
            }
            else
            {
                foreach (var term in terms) Append(builder, term);
            }

            foreach (var term in pathTerms) Append(builder, "path:" + term);
            if (extensions.Count > 0) Append(builder, "ext:" + string.Join(";", extensions));
            foreach (var term in excludes) Append(builder, "!" + term);
            return builder.ToString().Trim();
        }
    }

    private static void Append(StringBuilder builder, string token)
    {
        if (builder.Length > 0) builder.Append(' ');
        // Quote anything with a space so Everything sees one token, not two.
        builder.Append(token.Contains(' ') ? $"\"{token}\"" : token);
    }

    /// <summary>
    /// Parse <paramref name="raw"/>. Never throws: an invalid regex lands in
    /// <see cref="RegexError"/> so the UI can say so instead of showing "no results".
    /// </summary>
    public static FileSearchQuery Parse(string? raw)
    {
        var query = new FileSearchQuery(raw ?? string.Empty);
        var text = (raw ?? string.Empty).Trim();
        if (text.Length == 0) return query;

        if (TryReadRegex(text, out var expression, out var remainder))
        {
            query.Compile(expression);
            text = remainder;
            if (text.Length == 0) return query;
        }

        foreach (var token in Tokenize(text))
        {
            if (token.Length == 0) continue;

            if (token.StartsWith("ext:", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var extension in token[4..].Split([',', ';', '|'], StringSplitOptions.RemoveEmptyEntries))
                {
                    var cleaned = extension.Trim().TrimStart('*', '.');
                    if (cleaned.Length > 0 && !query.extensions.Contains(cleaned, StringComparer.OrdinalIgnoreCase))
                        query.extensions.Add(cleaned);
                }

                continue;
            }

            if (token.StartsWith("path:", StringComparison.OrdinalIgnoreCase))
            {
                var value = token[5..].Trim();
                if (value.Length > 0) query.pathTerms.Add(value);
                continue;
            }

            if (token.StartsWith("name:", StringComparison.OrdinalIgnoreCase))
            {
                var value = token[5..].Trim();
                if (value.Length > 0) query.terms.Add(value);
                continue;
            }

            if (token.Length > 1 && (token[0] == '-' || token[0] == '!'))
            {
                query.excludes.Add(token[1..]);
                continue;
            }

            query.terms.Add(token);
        }

        return query;
    }

    /// <summary>
    /// <c>/pattern/</c> or <c>re:pattern</c> — and, for convenience, a bare pattern that the user
    /// wrapped in slashes anywhere in the string (<c>ext:cs /^main/</c>).
    /// </summary>
    private static bool TryReadRegex(string text, out string expression, out string remainder)
    {
        expression = string.Empty;
        remainder = string.Empty;

        if (text.StartsWith("re:", StringComparison.OrdinalIgnoreCase))
        {
            expression = text[3..].Trim();
            return true;
        }

        if (text.StartsWith("regex:", StringComparison.OrdinalIgnoreCase))
        {
            expression = text[6..].Trim();
            return true;
        }

        if (text[0] != '/') return false;

        var end = text.LastIndexOf('/');
        if (end <= 0) return false;

        expression = text[1..end];
        remainder = text[(end + 1)..].Trim();
        return true;
    }

    private void Compile(string expression)
    {
        try
        {
            pattern = new Regex(expression, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                TimeSpan.FromMilliseconds(120));
        }
        catch (ArgumentException)
        {
            RegexError = expression;
        }
    }

    /// <summary>Split on whitespace, keeping <c>"quoted phrases"</c> together.</summary>
    private static IEnumerable<string> Tokenize(string text)
    {
        var current = new StringBuilder();
        var quoted = false;

        foreach (var ch in text)
        {
            if (ch == '"')
            {
                quoted = !quoted;
                continue;
            }

            if (!quoted && char.IsWhiteSpace(ch))
            {
                if (current.Length > 0)
                {
                    yield return current.ToString();
                    current.Clear();
                }

                continue;
            }

            current.Append(ch);
        }

        if (current.Length > 0) yield return current.ToString();
    }

    /// <summary>
    /// Does this file match? Used by the fallback index; Everything does its own matching, so this
    /// is the authority for what "the same query" means on a machine without Everything.
    /// </summary>
    public bool Matches(string name, string fullPath)
    {
        if (RegexError != null) return false;

        if (extensions.Count > 0)
        {
            var matchesExtension = false;
            foreach (var extension in extensions)
            {
                if (!name.EndsWith("." + extension, StringComparison.OrdinalIgnoreCase)) continue;
                matchesExtension = true;
                break;
            }

            // Several extensions are alternatives ("ext:cs,ts"), not a chain of requirements.
            if (!matchesExtension) return false;
        }

        foreach (var excluded in excludes)
        {
            if (Contains(name, excluded) || Contains(fullPath, excluded)) return false;
        }

        foreach (var term in pathTerms)
        {
            if (!Contains(fullPath, term)) return false;
        }

        if (pattern != null)
        {
            try
            {
                if (!pattern.IsMatch(name) && !pattern.IsMatch(fullPath)) return false;
            }
            catch (RegexMatchTimeoutException)
            {
                return false;
            }

            // A regex owns the text part; only the structured filters still apply.
            return true;
        }

        foreach (var term in terms)
        {
            if (!Contains(name, term) && !Contains(fullPath, term)) return false;
        }

        return true;
    }

    /// <summary>
    /// Rank a match: a name hit always beats a path-only hit, and the shared
    /// <see cref="LauncherScoring"/> keeps app and file results on one scale.
    /// </summary>
    public int Score(string name, string fullPath)
    {
        if (pattern != null)
        {
            try
            {
                if (pattern.IsMatch(name)) return 820 - Math.Min(200, name.Length);
                if (pattern.IsMatch(fullPath)) return 480;
            }
            catch (RegexMatchTimeoutException)
            {
                return 0;
            }

            return 0;
        }

        var text = string.Join(' ', terms);
        var score = text.Length == 0 ? 1 : LauncherScoring.Score(name, text);
        if (score > 0) return score;

        // A path-only hit is still a hit — Everything would return it too.
        foreach (var term in terms)
        {
            if (Contains(fullPath, term)) return 300;
        }

        return 0;
    }

    private static bool Contains(string haystack, string needle) =>
        haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);
}
