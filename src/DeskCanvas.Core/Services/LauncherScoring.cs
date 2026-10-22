using System;

namespace DeskCanvas.Core.Services;

/// <summary>
/// The launcher's ranking rule: exact &gt; prefix &gt; word-start &gt; substring &gt; ordered
/// subsequence. Kept pure and static so it can be asserted without a window or a folder.
/// <para>
/// Lives in Core because two callers share it: the app grid's filter
/// (<c>SpotlightViewModel</c>) and the file search's fallback index
/// (<c>FileSearchQuery.Score</c>). Ranking a file and ranking an app must feel the same — the user
/// types one query and reads one list.
/// </para>
/// </summary>
public static class LauncherScoring
{
    /// <summary>
    /// Computes a match score between a candidate name and a search query.
    /// Higher values indicate closer matches; returns 0 if the query does not match.
    /// </summary>
    /// <param name="name">Candidate name to evaluate.</param>
    /// <param name="query">Search query string.</param>
    /// <returns>Rank score (higher is better, 0 for no match).</returns>
    public static int Score(string name, string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return 1;
        if (name.Equals(query, StringComparison.CurrentCultureIgnoreCase)) return 1000;
        if (name.StartsWith(query, StringComparison.CurrentCultureIgnoreCase)) return 900 - name.Length;

        var index = name.IndexOf(query, StringComparison.CurrentCultureIgnoreCase);
        if (index >= 0)
        {
            // A hit that starts a word ("chrome" in "Google Chrome") outranks a mid-word hit.
            var wordStart = index > 0 && (name[index - 1] is ' ' or '-' or '_' or '·');
            return (wordStart ? 800 : 700) - index - name.Length;
        }

        var queryIndex = 0;
        var gaps = 0;
        var lastHit = -1;
        for (var i = 0; i < name.Length && queryIndex < query.Length; i++)
        {
            if (char.ToLowerInvariant(name[i]) != char.ToLowerInvariant(query[queryIndex])) continue;
            if (lastHit >= 0) gaps += i - lastHit - 1;
            lastHit = i;
            queryIndex++;
        }

        return queryIndex == query.Length ? Math.Max(1, 400 - gaps * 5 - name.Length) : 0;
    }
}
