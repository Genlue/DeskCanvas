using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using DeskCanvas.Locales;
using DeskCanvas.Services;

namespace DeskCanvas.ViewModels;

/// <summary>What a launcher row represents.</summary>
public enum SpotlightItemKind
{
    /// <summary>A shortcut from the launcher folder (the grid's tiles).</summary>
    App,

    /// <summary>A file or folder returned by file search (the list only).</summary>
    File,

    /// <summary>A non-selectable group caption ("文件").</summary>
    Header,

    /// <summary>A clipboard history entry (text or link).</summary>
    Clipboard,

    /// <summary>A DeskCanvas shortcut workflow offered by the Shortcuts plugin.</summary>
    Workflow,

    /// <summary>A row of data owned by a widget (a checklist item, a course, …).</summary>
    Widget,

    /// <summary>A 在浏览器中搜索 row: the query, an engine and a browser, ready to launch.</summary>
    Web,

    /// <summary>
    /// One real result the engine returned (title + link + snippet). It lives in the same 网页
    /// group as the action row, but it opens a specific page instead of the engine's result list.
    /// </summary>
    WebResult,
}

/// <summary>
/// The result categories the category bar can filter by. Index order is the UI order, and it is
/// also the F-key order: F1 selects <see cref="All"/>, F2 <see cref="Apps"/>, … by position.
/// <para>
/// Images / Files / Folders are mutually exclusive views of the file source: an image is never
/// listed under 文件, and a folder is never listed under either. 快捷指令 is not a category of its
/// own — workflows live under <see cref="Widgets"/> together with the widget data rows.
/// </para>
/// <para>
/// 网页 is appended <b>last</b> on purpose: the chip's position is its F-key, so inserting it in
/// the middle would renumber every key the user already knows.
/// </para>
/// </summary>
public enum SpotlightCategoryKind
{
    All,
    Apps,
    Images,
    Files,
    Folders,
    Widgets,
    Clipboard,
    Web,
}

/// <summary>One chip in the category bar. The label is localized by the view model.</summary>
public sealed record SpotlightCategory(SpotlightCategoryKind Kind, string Label);

/// <summary>One label/value line in the preview pane.</summary>
public sealed record SpotlightPreviewRow(string Label, string Value);

/// <summary>
/// One row in the launcher: an app tile, a file hit, a group header, an action, a clipboard entry
/// or a workflow.
/// <para>
/// Implements <see cref="INotifyPropertyChanged"/> on purpose: the icon (and usage metadata) arrive
/// asynchronously after the row exists, and the row must repaint itself — replacing
/// <c>ItemsSource</c> to force a refresh crashes Avalonia 12.1.4's container generator (see
/// <c>docs/聚焦搜索可行性研究.md</c> §3.3).
/// </para>
/// </summary>
public sealed class SpotlightItem : INotifyPropertyChanged
{
    private Bitmap? icon;
    private bool showName = true;
    private string subtitle;
    private int launchCount;
    private DateTime? lastLaunchedUtc;
    private bool isPinned;

    public SpotlightItem(LauncherEntry entry)
    {
        Kind = SpotlightItemKind.App;
        Entry = entry;
        Id = entry.Key;
        Name = entry.Name;
        subtitle = entry.ShortcutPath;
        IsBroken = entry.IsBroken;
    }

    public SpotlightItem(FileSearchHit hit)
    {
        Kind = SpotlightItemKind.File;
        Id = "file:" + hit.FullPath.ToLowerInvariant();
        Name = hit.Name;
        subtitle = System.IO.Path.GetDirectoryName(hit.FullPath) ?? string.Empty;
        FilePath = hit.FullPath;
        IsFolder = hit.IsFolder;
        Size = hit.Size;
    }

    private SpotlightItem(string header)
    {
        Kind = SpotlightItemKind.Header;
        Id = "header:" + header;
        Name = header;
        subtitle = string.Empty;
    }

    private SpotlightItem(
        SpotlightItemKind kind, string id, string name, string? subtitle,
        string? badge = null, string? clipboardText = null, bool isLink = false,
        string? sourceProcess = null, DateTime? capturedUtc = null,
        Func<Task<bool>>? run = null, string? groupTitle = null,
        SpotlightWebQuery? web = null, string? linkUrl = null)
    {
        Kind = kind;
        Id = id;
        Name = name;
        this.subtitle = subtitle ?? string.Empty;
        Badge = badge;
        ClipboardText = clipboardText;
        IsLink = isLink;
        SourceProcess = sourceProcess;
        CapturedUtc = capturedUtc;
        Run = run;
        GroupTitle = groupTitle;
        Web = web;
        LinkUrl = linkUrl;
    }

    /// <summary>A group caption. Headers are skipped by keyboard navigation and cannot be opened.</summary>
    public static SpotlightItem CreateHeader(string title) => new(title);

    /// <summary>A shortcut workflow offered by the Shortcuts plugin.</summary>
    public static SpotlightItem ForWorkflow(
        string id, string title, string? subtitle, string? badge, Func<Task<bool>> run)
        => new(SpotlightItemKind.Workflow, "wf:" + id, title, subtitle, badge: badge, run: run);

    /// <summary>A clipboard history entry. <paramref name="copyBack"/> puts the text back on the clipboard.</summary>
    public static SpotlightItem ForClipboard(
        string id, string text, bool isLink, DateTime capturedUtc, string? sourceProcess,
        Func<Task<bool>> copyBack)
        => new(SpotlightItemKind.Clipboard, "clip:" + id, text, null,
            clipboardText: text, isLink: isLink, sourceProcess: sourceProcess,
            capturedUtc: capturedUtc, run: copyBack);

    /// <summary>
    /// A widget-owned data row (a checklist item, a course, …). <paramref name="groupTitle"/> is
    /// the widget's own group label ("清单" / "课程") and doubles as the preview pane's type line.
    /// </summary>
    public static SpotlightItem ForWidget(
        string id, string title, string? subtitle, string? groupTitle, Func<Task<bool>> run)
        => new(SpotlightItemKind.Widget, "wd:" + id, title, subtitle, run: run, groupTitle: groupTitle);

    /// <summary>
    /// The 在浏览器中搜索 row. Its two lines are composed here, not by the pipeline: the router
    /// only says "this query, this engine, this browser", and the wording is the view's business.
    /// <para>
    /// The icon is the browser artwork rasterised once — a search action has no file behind it for
    /// the shell icon service to extract, and the "first character of the name" placeholder would
    /// read as a broken icon on the one row that never has a real one.
    /// </para>
    /// </summary>
    public static SpotlightItem ForWeb(SpotlightWebQuery target, Func<Task<bool>> run)
    {
        var item = new SpotlightItem(
            SpotlightItemKind.Web,
            "web:" + target.Url,
            string.Format(Locale.Spotlight_Web_Row_Title, target.EngineName, target.Query),
            string.Format(Locale.Spotlight_Web_Row_Subtitle, target.EngineHost, target.BrowserName),
            run: run,
            web: target);

        try
        {
            item.Icon = SpotlightGlyphImages.Get(SpotlightGlyphKind.Web, 72);
        }
        catch
        {
            // No rendering platform (a headless host): the letter placeholder is a fine fallback.
        }

        return item;
    }

    /// <summary>
    /// One page the engine returned for the current query. The row's two lines are the page's own
    /// title and the engine's excerpt, and its icon is the link artwork — a hit has no file for the
    /// shell icon service to extract, so the letter placeholder would read as a broken icon.
    /// </summary>
    public static SpotlightItem ForWebHit(SpotlightWebHit hit, Func<Task<bool>> run)
    {
        var subtitle = string.IsNullOrWhiteSpace(hit.Snippet) ? HostOf(hit.Url) : hit.Snippet;
        var item = new SpotlightItem(
            SpotlightItemKind.WebResult,
            SpotlightResultIds.WebHit(hit.Url),
            hit.Title,
            subtitle,
            run: run,
            linkUrl: hit.Url);

        try
        {
            item.Icon = SpotlightGlyphImages.Get(SpotlightGlyphKind.Web, 72);
        }
        catch
        {
            // No rendering platform (a headless host): the letter placeholder is a fine fallback.
        }

        return item;
    }

    /// <summary>"example.com" — the row's subtitle when the engine sent no excerpt.</summary>
    private static string HostOf(string url)
        => Uri.TryCreate(url, UriKind.Absolute, out var parsed) ? parsed.Host : url;

    /// <summary>Stable identity (usage store key / invocation id). Never empty.</summary>
    public string Id { get; }

    public SpotlightItemKind Kind { get; }

    public LauncherEntry? Entry { get; }

    /// <summary>The widget's own group label (Kind == Widget), e.g. 清单 / 课程.</summary>
    public string? GroupTitle { get; }

    /// <summary>Executor for Clipboard / Workflow / Widget / Web rows; null for the other kinds.</summary>
    public Func<Task<bool>>? Run { get; }

    /// <summary>The resolved web target (Kind == Web): engine, browser and the final address.</summary>
    public SpotlightWebQuery? Web { get; }

    /// <summary>The page address of one engine hit (Kind == WebResult); null for the action row.</summary>
    public string? LinkUrl { get; }

    /// <summary>Absolute path of a file hit (null for apps and headers).</summary>
    public string? FilePath { get; }

    public bool IsFolder { get; }

    public long Size { get; }

    public string Name { get; }

    /// <summary>
    /// The shortcut path for an app, the parent folder for a file, the source/time hint for a
    /// clipboard entry, the description for an action, empty for a header. Updates raise
    /// <see cref="PropertyChanged"/> so the visible row repaints.
    /// </summary>
    public string Subtitle
    {
        get => subtitle;
        set
        {
            if (subtitle == value) return;
            subtitle = value;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// The row's trailing badge text (a workflow's own hotkey; null otherwise). It is display-only:
    /// the badge never takes part in ranking or keyboard handling.
    /// </summary>
    public string? Badge { get; }

    /// <summary>The clipboard text (Kind == Clipboard).</summary>
    public string? ClipboardText { get; }

    /// <summary>True when the clipboard entry looks like a link (http/https/www).</summary>
    public bool IsLink { get; }

    /// <summary>The process that placed the clipboard entry, when it could be read.</summary>
    public string? SourceProcess { get; }

    /// <summary>When the clipboard entry was captured (Kind == Clipboard).</summary>
    public DateTime? CapturedUtc { get; }

    /// <summary>
    /// True when the shortcut's resolved target no longer exists. The tile stays visible with a
    /// placeholder icon and 目标不可用 — broken shortcuts are never silently deleted.
    /// </summary>
    public bool IsBroken { get; }

    /// <summary>First character, drawn in the tile while the real icon is still loading.</summary>
    public string Initial => Name.Length > 0 ? Name[..1].ToUpperInvariant() : "?";

    public bool IsSelectable => Kind != SpotlightItemKind.Header;

    /// <summary>Whether the row can be executed with Enter (apps, files, clipboard, workflows, widget rows, web rows: yes).</summary>
    public bool CanActivate =>
        Kind is SpotlightItemKind.App or SpotlightItemKind.File
            or SpotlightItemKind.Clipboard or SpotlightItemKind.Workflow
            or SpotlightItemKind.Widget or SpotlightItemKind.Web
            or SpotlightItemKind.WebResult;

    public Bitmap? Icon
    {
        get => icon;
        set
        {
            if (ReferenceEquals(icon, value)) return;
            icon = value;
            OnPropertyChanged(nameof(Icon));
            OnPropertyChanged(nameof(HasIcon));
        }
    }

    public bool HasIcon => icon != null;

    /// <summary>
    /// Whether the tile draws its name. Driven by the launcher settings (显示名称); the filtered
    /// result list always shows names, so this only affects the grid.
    /// </summary>
    public bool ShowName
    {
        get => showName;
        set
        {
            if (showName == value) return;
            showName = value;
            OnPropertyChanged();
        }
    }

    /// <summary>How many times this app was launched from the launcher (usage store).</summary>
    public int LaunchCount
    {
        get => launchCount;
        set
        {
            if (launchCount == value) return;
            launchCount = value;
            OnPropertyChanged();
        }
    }

    /// <summary>When this app was last launched from the launcher, UTC (usage store).</summary>
    public DateTime? LastLaunchedUtc
    {
        get => lastLaunchedUtc;
        set
        {
            if (Equals(lastLaunchedUtc, value)) return;
            lastLaunchedUtc = value;
            OnPropertyChanged();
        }
    }

    /// <summary>Whether the user pinned this app (usage store; pinned apps float in the grid).</summary>
    public bool IsPinned
    {
        get => isPinned;
        set
        {
            if (isPinned == value) return;
            isPinned = value;
            OnPropertyChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

/// <summary>
/// The preview pane's state. A data-only contract on purpose: the window's buttons call back into
/// <see cref="SpotlightViewModel"/> (PreviewActivate / PreviewReveal / PreviewRun), so this class
/// never touches Process.Start itself and can be asserted headlessly.
/// </summary>
public sealed class SpotlightPreviewViewModel : INotifyPropertyChanged
{
    private SpotlightItem? item;
    private string title = string.Empty;
    private string subtitle = string.Empty;
    private string kindLabel = string.Empty;
    private string contentText = string.Empty;
    private string excerpt = string.Empty;
    private Bitmap? media;
    private Bitmap? icon;
    private IReadOnlyList<SpotlightPreviewRow> rows = [];
    private bool hasOpen;
    private bool hasReveal;
    private bool hasRun;
    private bool hasCopy;
    private bool hasMediaSlot;

    /// <summary>The row the pane is showing (null = the empty placeholder).</summary>
    public SpotlightItem? Item
    {
        get => item;
        private set
        {
            if (ReferenceEquals(item, value)) return;
            item = value;
            OnPropertyChanged();
        }
    }

    public string Title
    {
        get => title;
        private set
        {
            if (title == value) return;
            title = value;
            OnPropertyChanged();
        }
    }

    public string Subtitle
    {
        get => subtitle;
        private set
        {
            if (subtitle == value) return;
            subtitle = value;
            OnPropertyChanged();
        }
    }

    /// <summary>The localized type line (应用程序 / 文件 / 动作 / …).</summary>
    public string KindLabel
    {
        get => kindLabel;
        private set
        {
            if (kindLabel == value) return;
            kindLabel = value;
            OnPropertyChanged();
        }
    }

    /// <summary>Clipboard text / action description, wrapped by the pane. Empty for the other kinds.</summary>
    public string ContentText
    {
        get => contentText;
        private set
        {
            if (contentText == value) return;
            contentText = value;
            OnPropertyChanged();
        }
    }

    public Bitmap? Icon
    {
        get => icon;
        private set
        {
            if (ReferenceEquals(icon, value)) return;
            icon = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasIcon));
        }
    }

    public bool HasIcon => icon != null;

    /// <summary>
    /// The hero image: the file's real thumbnail (a photo, a PDF page, a slide). Null when the
    /// type has no thumbnail handler — the pane then draws <see cref="Icon"/> instead.
    /// </summary>
    public Bitmap? Media
    {
        get => media;
        private set
        {
            if (ReferenceEquals(media, value)) return;
            media = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasMedia));
        }
    }

    public bool HasMedia => media != null;

    /// <summary>
    /// Whether the pane reserves the hero slot at all. Set once per selection (files only), never
    /// when the thumbnail arrives — a slot that appears together with its image would force a
    /// layout pass mid-update, and a missed invalidation leaves the image with a zero rect.
    /// </summary>
    public bool HasMediaSlot
    {
        get => hasMediaSlot;
        private set
        {
            if (hasMediaSlot == value) return;
            hasMediaSlot = value;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// The head of a text/document file, shown under the detail rows. Empty for everything else.
    /// </summary>
    public string Excerpt
    {
        get => excerpt;
        private set
        {
            if (excerpt == value) return;
            excerpt = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasExcerpt));
        }
    }

    public bool HasExcerpt => !string.IsNullOrEmpty(excerpt);

    /// <summary>Label/value lines (目标 / 位置 / 最近使用 / …). Replaced wholesale on every Bind.</summary>
    public IReadOnlyList<SpotlightPreviewRow> Rows
    {
        get => rows;
        private set
        {
            rows = value;
            OnPropertyChanged();
        }
    }

    public bool HasOpen
    {
        get => hasOpen;
        private set
        {
            if (hasOpen == value) return;
            hasOpen = value;
            OnPropertyChanged();
        }
    }

    public bool HasReveal
    {
        get => hasReveal;
        private set
        {
            if (hasReveal == value) return;
            hasReveal = value;
            OnPropertyChanged();
        }
    }

    public bool HasRun
    {
        get => hasRun;
        private set
        {
            if (hasRun == value) return;
            hasRun = value;
            OnPropertyChanged();
        }
    }

    public bool HasCopy
    {
        get => hasCopy;
        private set
        {
            if (hasCopy == value) return;
            hasCopy = value;
            OnPropertyChanged();
        }
    }

    /// <summary>Whether the pane currently shows a real row (false = empty placeholder).</summary>
    public bool HasItem => Item != null;

    /// <summary>Rebind the pane to a row. Null shows the empty placeholder.</summary>
    public void Bind(SpotlightItem? newItem)
    {
        Item = newItem;
        // A file always gets the hero slot, even before its thumbnail exists: the slot's size is
        // fixed, so the image can appear inside it without moving a single other element.
        HasMediaSlot = newItem?.Kind == SpotlightItemKind.File;
    }

    // The view model writes these through the same property setters; internal setters would split
    // the contract, so the VM uses the public ones via a friend-like extension. Kept simple: the
    // VM constructs and mutates the pane through these methods.
    internal void Set(
        string title, string subtitle, string kindLabel, string contentText, string excerpt, Bitmap? media,
        Bitmap? icon, IReadOnlyList<SpotlightPreviewRow> rows, bool hasOpen, bool hasReveal, bool hasRun, bool hasCopy)
    {
        Title = title;
        Subtitle = subtitle;
        KindLabel = kindLabel;
        ContentText = contentText;
        Excerpt = excerpt;
        Media = media;
        Icon = icon;
        Rows = rows;
        HasOpen = hasOpen;
        HasReveal = hasReveal;
        HasRun = hasRun;
        HasCopy = hasCopy;
        OnPropertyChanged(nameof(HasItem));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
