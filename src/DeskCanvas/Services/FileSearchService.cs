using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DeskCanvas.Core.Interfaces;

namespace DeskCanvas.Services;

/// <summary>One file row the launcher can show and open.</summary>
/// <param name="Name">File name with extension.</param>
/// <param name="FullPath">Absolute path.</param>
/// <param name="IsFolder">True for a directory.</param>
/// <param name="Size">Bytes; 0 for folders.</param>
/// <param name="Score">Ranking score (higher first).</param>
public sealed record FileSearchHit(string Name, string FullPath, bool IsFolder, long Size, int Score);

/// <summary>Which engine answered the last query.</summary>
public enum FileSearchBackend
{
    /// <summary>Everything's index (instant, whole disk).</summary>
    Everything,

    /// <summary>The launcher's own folder index (only the user's own folders).</summary>
    LocalIndex,

    /// <summary>Nothing answered — see <see cref="FileSearchService.Detail"/>.</summary>
    None,
}

/// <summary>
/// File search for the launcher: Everything when it is there, the launcher's own folder index when
/// it is not.
/// <para>
/// <b>Why two engines:</b> Everything answers a whole-disk query in a millisecond but is a separate
/// product the user may not run (and, if it runs elevated, a normal-integrity process cannot talk
/// to it at all — UIPI). A launcher whose file search silently returns nothing in those cases is
/// worse than one with a smaller index, so the fallback covers the folders a Spotlight user
/// actually searches: desktop, documents, downloads, pictures, music, videos.
/// </para>
/// <para>
/// Both engines run behind one parsed <see cref="FileSearchQuery"/>, so a query means the same
/// thing whichever one answers.
/// </para>
/// </summary>
public sealed class FileSearchService : IDisposable
{
    /// <summary>
    /// Hard cap on what one query may return. This is a ceiling, not a page size: the list
    /// renders every hit and scrolls, so the cap only exists to keep a one-letter query from
    /// turning into a five-figure list.
    /// </summary>
    public const int MaxResults = 200;

    /// <summary>
    /// How many candidates Everything is asked for before the local re-rank. Everything answers
    /// in its own order (name/path), not by relevance, so a narrow pool silently drops the file
    /// the user meant before it is ever scored — the pool has to be an order of magnitude wider
    /// than what is shown.
    /// </summary>
    private const int EverythingCandidatePool = 2000;

    private readonly IAppSettingsProvider settings;
    private readonly LocalFileIndex index = new();

    private long lastStartAttempt = long.MinValue / 2;

    public FileSearchService(IAppSettingsProvider settings) => this.settings = settings;

    /// <summary>Which engine answered the most recent query.</summary>
    public FileSearchBackend Backend { get; private set; } = FileSearchBackend.None;

    /// <summary>Human-readable explanation of <see cref="Backend"/> (null when it just worked).</summary>
    public string? Detail { get; private set; }

    /// <summary>Everything's own status, for the settings page.</summary>
    public EverythingStatus EverythingStatus => EverythingIpc.Shared.Status;

    /// <summary>The Everything client executable, or null when it is not installed.</summary>
    public string? EverythingPath => EverythingIpc.Shared.ClientPath ?? EverythingIpc.FindClientExecutable();

    /// <summary>True while Everything's IPC window exists.</summary>
    public bool IsEverythingRunning => EverythingIpc.Shared.IsClientRunning;

    /// <summary>
    /// Search files for <paramref name="raw"/>. Never throws and never returns null: a failed
    /// backend degrades to the next one, and a bad query degrades to an empty list.
    /// </summary>
    public async Task<IReadOnlyList<FileSearchHit>> SearchAsync(string? raw, CancellationToken cancellationToken)
    {
        var launcher = settings.Get().EffectiveLauncher;
        if (!launcher.FileSearch) return [];

        var query = FileSearchQuery.Parse(raw);
        if (query.IsEmpty || query.RegexError != null)
        {
            Backend = FileSearchBackend.None;
            Detail = query.RegexError != null ? "invalid-regex" : null;
            return [];
        }

        // Everything first: it is the only engine that covers the whole disk.
        if (await TryEverythingAsync(query, launcher, cancellationToken).ConfigureAwait(false) is { } hits)
        {
            return hits;
        }

        cancellationToken.ThrowIfCancellationRequested();
        return await SearchLocalAsync(query, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Start the Everything client (settings page button). Returns false when it cannot be found.</summary>
    public bool StartEverything() => EverythingIpc.Shared.TryStartClient();

    /// <summary>Drop the fallback index so the next query re-reads the folders.</summary>
    public void InvalidateIndex() => index.Invalidate();

    /// <summary>
    /// Whether this query has to be answered against the whole path instead of the file name.
    /// <para>
    /// Everything's default is name-only; MATCHPATH is the exception, not the rule. Leaving it on
    /// for every query is what made a common word unfindable: "图标" then also matches every file
    /// under every folder whose name contains 图标, the candidate pool is thousands of rows, and
    /// Everything hands back its first <see cref="EverythingCandidatePool"/> rows in its own
    /// order — the file the user wanted is usually past that cut.
    /// </para>
    /// </summary>
    public static bool NeedsMatchPath(FileSearchQuery query) =>
        query.PathTerms.Count > 0
        || query.Terms.Any(t => t.Contains('\\') || t.Contains('/'))
        || query.Raw.Contains("path:", StringComparison.OrdinalIgnoreCase);

    private async Task<IReadOnlyList<FileSearchHit>?> TryEverythingAsync(
        FileSearchQuery query, Core.Models.Settings.LauncherSettings launcher, CancellationToken cancellationToken)
    {
        var ipc = EverythingIpc.Shared;

        if (!ipc.IsClientRunning)
        {
            // Starting a client is a user-visible side effect, so it is a setting, and it is
            // attempted at most once every 30 s — a missing install must not spawn a process per
            // keystroke.
            if (!launcher.AutoStartEverything) return null;
            if (Environment.TickCount64 - Volatile.Read(ref lastStartAttempt) < 30_000) return null;
            Volatile.Write(ref lastStartAttempt, Environment.TickCount64);

            if (!await Task.Run(ipc.TryStartClient, cancellationToken).ConfigureAwait(false)) return null;
        }

        var matchPath = NeedsMatchPath(query);

        var raw = await Task.Run(
            () => ipc.Query(query.EverythingSyntax, query.IsRegex, matchCase: false, EverythingCandidatePool, matchPath),
            cancellationToken).ConfigureAwait(false);

        if (ipc.Status != EverythingStatus.Connected) return null;

        var ranked = raw
            .Select(hit => new FileSearchHit(hit.Name, hit.FullPath, hit.IsFolder, hit.Size,
                query.Score(hit.Name, hit.FullPath)))
            .Where(hit => hit.Score > 0)
            .OrderByDescending(hit => hit.Score)
            .ThenBy(hit => hit.Name, StringComparer.CurrentCultureIgnoreCase)
            .Take(MaxResults)
            .ToList();

        Backend = FileSearchBackend.Everything;
        Detail = null;
        return ranked;
    }

    private async Task<IReadOnlyList<FileSearchHit>> SearchLocalAsync(FileSearchQuery query, CancellationToken cancellationToken)
    {
        var entries = await index.GetEntriesAsync(cancellationToken).ConfigureAwait(false);
        if (entries.Count == 0)
        {
            Backend = FileSearchBackend.None;
            Detail = EverythingIpc.Shared.Status switch
            {
                EverythingStatus.AccessDenied => "everything-elevated",
                EverythingStatus.NotInstalled => "everything-missing",
                _ => "index-empty",
            };
            return [];
        }

        var hits = new List<FileSearchHit>();
        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!query.Matches(entry.Name, entry.FullPath)) continue;

            var score = query.Score(entry.Name, entry.FullPath);
            if (score <= 0) continue;
            hits.Add(new FileSearchHit(entry.Name, entry.FullPath, entry.IsFolder, entry.Size, score));
        }

        Backend = FileSearchBackend.LocalIndex;
        Detail = EverythingIpc.Shared.Status switch
        {
            EverythingStatus.AccessDenied => "everything-elevated",
            EverythingStatus.NotRunning => "everything-not-running",
            EverythingStatus.NotInstalled => "everything-missing",
            // The window exists but the query got no answer (IPC disabled in the running client,
            // a client that is still loading its index, a hung UI thread). Reporting "not running"
            // here would tell the user to start a program that is already on screen.
            EverythingStatus.Failed when EverythingIpc.Shared.IsClientRunning => "everything-unresponsive",
            _ => null,
        };

        return hits
            .OrderByDescending(hit => hit.Score)
            .ThenBy(hit => hit.Name, StringComparer.CurrentCultureIgnoreCase)
            .Take(MaxResults)
            .ToList();
    }

    public void Dispose() => index.Dispose();
}

/// <summary>One indexed file or folder.</summary>
internal readonly record struct IndexEntry(string Name, string FullPath, bool IsFolder, long Size);

/// <summary>
/// The launcher's own file index: the user's personal folders, enumerated once and reused for a
/// couple of minutes.
/// <para>
/// Deliberately small. A full-disk index is Everything's job; this exists so file search still
/// answers when Everything is missing or unreachable. The folders are the ones a Spotlight user
/// types a name from, the entry cap keeps a runaway profile (a node_modules farm on the desktop)
/// from eating the process, and the TTL keeps a freshly saved file findable without a manual
/// refresh.
/// </para>
/// </summary>
internal sealed class LocalFileIndex : IDisposable
{
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(2);

    /// <summary>Enough for a personal profile; beyond this the walk is not worth the latency.</summary>
    private const int MaxEntries = 80_000;

    /// <summary>Depth from a root; deep trees are the ones that cost seconds to walk.</summary>
    private const int MaxDepth = 8;

    /// <summary>Directories that are never interesting and are usually enormous.</summary>
    private static readonly HashSet<string> SkippedFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "node_modules", ".git", ".svn", ".vs", "obj", "bin", "AppData", "$Recycle.Bin",
        "System Volume Information", "Windows", "Program Files", "Program Files (x86)",
    };

    private readonly SemaphoreSlim buildGate = new(1, 1);
    private List<IndexEntry> entries = [];
    private long builtAt = long.MinValue / 2;
    private bool disposed;

    public async Task<IReadOnlyList<IndexEntry>> GetEntriesAsync(CancellationToken cancellationToken)
    {
        if (entries.Count > 0 && Environment.TickCount64 - Volatile.Read(ref builtAt) < (long)Ttl.TotalMilliseconds)
            return entries;

        await buildGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Another caller may have finished the build while this one waited.
            if (entries.Count > 0 && Environment.TickCount64 - Volatile.Read(ref builtAt) < (long)Ttl.TotalMilliseconds)
                return entries;

            var built = await Task.Run(() => Build(cancellationToken), cancellationToken).ConfigureAwait(false);
            entries = built;
            Volatile.Write(ref builtAt, Environment.TickCount64);
            return entries;
        }
        finally
        {
            buildGate.Release();
        }
    }

    public void Invalidate()
    {
        entries = [];
        Volatile.Write(ref builtAt, long.MinValue / 2);
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        buildGate.Dispose();
    }

    private static List<IndexEntry> Build(CancellationToken cancellationToken)
    {
        var result = new List<IndexEntry>(4096);
        var budget = Environment.TickCount64 + 2500;

        foreach (var root in Roots())
        {
            if (result.Count >= MaxEntries || Environment.TickCount64 > budget) break;
            Walk(root, result, budget, cancellationToken);
        }

        return result;
    }

    private static IEnumerable<string> Roots()
    {
        foreach (var folder in new[]
                 {
                     Environment.SpecialFolder.Desktop,
                     Environment.SpecialFolder.MyDocuments,
                     Environment.SpecialFolder.MyPictures,
                     Environment.SpecialFolder.MyMusic,
                     Environment.SpecialFolder.MyVideos,
                 })
        {
            var path = Environment.GetFolderPath(folder);
            if (!string.IsNullOrEmpty(path) && Directory.Exists(path)) yield return path;
        }

        var downloads = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        if (Directory.Exists(downloads)) yield return downloads;
    }

    private static void Walk(string root, List<IndexEntry> result, long budget, CancellationToken cancellationToken)
    {
        var pending = new Stack<(string Path, int Depth)>();
        pending.Push((root, 0));

        while (pending.Count > 0)
        {
            if (result.Count >= MaxEntries || Environment.TickCount64 > budget) return;
            if (cancellationToken.IsCancellationRequested) return;

            var (folder, depth) = pending.Pop();
            if (depth > MaxDepth) continue;

            IEnumerable<FileSystemInfo> children;
            try
            {
                children = new DirectoryInfo(folder).EnumerateFileSystemInfos();
            }
            catch
            {
                // Access denied / a folder removed mid-walk: skip it, keep the rest.
                continue;
            }

            try
            {
                foreach (var child in children)
                {
                    if (cancellationToken.IsCancellationRequested) return;

                    var isFolder = child is DirectoryInfo;
                    if (isFolder && SkippedFolders.Contains(child.Name)) continue;
                    if (!isFolder && child.Name.Length == 0) continue;

                    long size = 0;
                    if (!isFolder)
                    {
                        try { size = ((FileInfo)child).Length; } catch { }
                    }

                    result.Add(new IndexEntry(child.Name, child.FullName, isFolder, size));
                    if (isFolder) pending.Push((child.FullName, depth + 1));

                    if (result.Count >= MaxEntries) return;
                }
            }
            catch
            {
                // An enumeration that throws halfway keeps what it already produced.
            }
        }
    }
}
