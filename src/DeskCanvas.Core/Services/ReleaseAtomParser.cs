using System.Net;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace DeskCanvas.Core.Services;

/// <summary>
/// The latest release parsed from a GitHub <c>releases.atom</c> feed.
/// </summary>
/// <param name="TagName">Release tag exactly as published (e.g. <c>v3.8.0</c>).</param>
/// <param name="Title">Entry title (e.g. <c>DeskCanvas v3.8.0</c>).</param>
/// <param name="Version">Parsed semantic version for comparison against the running build.</param>
/// <param name="PageUrl">Release page URL (…/releases/tag/&lt;tag&gt;).</param>
/// <param name="ChangelogText">Release body as readable plain text (GitHub renders the markdown body to HTML in the feed; tags are stripped and entities decoded).</param>
public record AtomReleaseInfo(string TagName, string Title, Version Version, string PageUrl, string ChangelogText);

/// <summary>
/// Pure parser for GitHub <c>releases.atom</c> feeds — the update check's quota-free fallback
/// source. The feed lives on <c>github.com</c> (not <c>api.github.com</c>) and is NOT subject
/// to the unauthenticated REST API budget (60 req/h per shared exit IP) whose exhaustion turns
/// the primary check into HTTP 403 on popular proxy exit nodes. Parsing is intentionally
/// side-effect-free so the check suites can pin it against frozen feed samples.
/// </summary>
public static partial class ReleaseAtomParser
{
    private static readonly Regex VersionRegex = new(@"\d+(\.\d+)+", RegexOptions.Compiled);
    private static readonly Regex TagStripRegex = new(@"<[^>]+>", RegexOptions.Compiled);
    private static readonly Regex ApiUrlRegex = new(@"^https?://api\.github\.com/repos/([^/\s]+)/([^/\s?#]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Derive the <c>releases.atom</c> feed URL from the resolved GitHub API URL
    /// (<c>api.github.com/repos/{owner}/{repo}/…</c> → <c>github.com/{owner}/{repo}/releases.atom</c>).
    /// Returns <c>null</c> when the URL is not a GitHub API endpoint — a custom update URL pointing
    /// elsewhere has no known feed location, so the fallback must not guess.
    /// </summary>
    public static string? DeriveAtomUrl(string? apiUrl)
    {
        if (string.IsNullOrWhiteSpace(apiUrl)) return null;
        var match = ApiUrlRegex.Match(apiUrl);
        return match.Success
            ? $"https://github.com/{match.Groups[1].Value}/{match.Groups[2].Value}/releases.atom"
            : null;
    }

    /// <summary>
    /// Parse the FIRST (latest) entry of a <c>releases.atom</c> feed. Returns <c>null</c> when the
    /// XML is malformed or carries no entry — callers keep their primary-source error reason.
    /// The tag prefers the entry link's last path segment (…/releases/tag/v3.8.0) and falls back
    /// to the id tail (tag:github.com,2008:Repository/&lt;id&gt;/v3.8.0), tolerating URL-escaped tags.
    /// </summary>
    public static AtomReleaseInfo? TryParse(string? xml)
    {
        if (string.IsNullOrWhiteSpace(xml)) return null;

        XDocument document;
        try { document = XDocument.Parse(xml); }
        catch { return null; }

        var ns = document.Root?.Name.Namespace ?? XNamespace.None;
        var entry = document.Root?.Element(ns + "entry");
        if (entry == null) return null;

        var tag = ReadTag(entry, ns);
        if (string.IsNullOrWhiteSpace(tag)) return null;

        var versionText = tag.TrimStart('v', 'V').Trim();
        if (!Version.TryParse(versionText, out var version))
        {
            var versionMatch = VersionRegex.Match(versionText);
            if (!versionMatch.Success || !Version.TryParse(versionMatch.Value, out version)) return null;
        }

        var title = entry.Element(ns + "title")?.Value.Trim();
        if (string.IsNullOrWhiteSpace(title)) title = tag;

        var pageUrl = entry.Elements(ns + "link")
            .Select(link => link.Attribute("href")?.Value)
            .FirstOrDefault(href => !string.IsNullOrWhiteSpace(href) && href.Contains("/tag/", StringComparison.OrdinalIgnoreCase));
        if (string.IsNullOrWhiteSpace(pageUrl))
            pageUrl = $"https://github.com/releases/tag/{tag}";

        var changelog = HtmlToText(entry.Element(ns + "content")?.Value);

        return new AtomReleaseInfo(tag, title, version, pageUrl, changelog);
    }

    /// <summary>
    /// Tag from the entry link's last path segment (…/releases/tag/v3.8.0), falling back to the
    /// id tail (tag:github.com,2008:Repository/&lt;id&gt;/v3.8.0) when the link is missing.
    /// </summary>
    private static string ReadTag(XElement entry, XNamespace ns)
    {
        var fromLink = entry.Elements(ns + "link")
            .Select(link => link.Attribute("href")?.Value)
            .FirstOrDefault(href => !string.IsNullOrWhiteSpace(href) && href.Contains("/tag/", StringComparison.OrdinalIgnoreCase));
        var source = !string.IsNullOrWhiteSpace(fromLink) ? fromLink : entry.Element(ns + "id")?.Value;
        if (string.IsNullOrWhiteSpace(source)) return "";
        var segments = source.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length == 0 ? "" : Uri.UnescapeDataString(segments[^1]).Trim();
    }

    /// <summary>
    /// GitHub renders the release markdown body into HTML for the feed's content element
    /// (XML-escaped). Decode, strip tags, decode again (the body itself may carry HTML entities),
    /// and collapse the whitespace — enough for a readable plain-text changelog in the popup.
    /// </summary>
    private static string HtmlToText(string? html)
    {
        if (string.IsNullOrWhiteSpace(html)) return "";
        var decoded = WebUtility.HtmlDecode(html);
        var stripped = TagStripRegex.Replace(decoded, " ");
        var text = WebUtility.HtmlDecode(stripped);
        var lines = text.Split('\n').Select(line => line.Trim()).Where(line => line.Length > 0);
        return string.Join('\n', lines);
    }
}
