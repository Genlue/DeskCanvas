using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using DeskCanvas.Core.Interfaces;
using DeskCanvas.Core.Models.Settings;
using DeskCanvas.Core.Services;
using DeskCanvas.Locales;
using DeskCanvas.Services;

namespace DeskCanvas.ViewModels;

public sealed class SpotlightViewModel : INotifyPropertyChanged, IDisposable
{
	private readonly record struct PreviewSnapshot(string Title, string Subtitle, string KindLabel, string ContentText, string Excerpt, Bitmap? Media, IReadOnlyList<SpotlightPreviewRow> Rows, bool HasOpen, bool HasReveal, bool HasRun, bool HasCopy);

	private const int SearchDelayMs = 110;

	/// <summary>
	/// Quiet period before a file's thumbnail is asked for.
	/// <para>
	/// Arrow-key navigation moves the selection faster than the shell can render a thumbnail, and
	/// every request that is superseded still costs a handler round-trip. One pause = one preview;
	/// the pane's icon and detail rows are already painted, so the delay is invisible.
	/// </para>
	/// </summary>
	private const int PreviewDelayMs = 120;

	/// <summary>Hard cap of 最近使用 tiles; the live cap is the grid's column count (see RefreshRecentsAsync).</summary>
	private const int RecentLimit = 8;

	private const int RouteLimit = 40;

	private readonly SpotlightQueryRouter router;

	private readonly LauncherCatalog catalog;

	private readonly ShellIconService icons;

	private readonly SpotlightPreviewService previews;

	/// <summary>
	/// The network half of 网页搜索. It is queried <b>after</b> the local sources are on screen:
	/// the inline hits are an enhancement, and a slow DNS must never delay the app/file rows.
	/// </summary>
	private readonly SpotlightWebResults webResults;

	private readonly SpotlightUsageStore usage;

	private readonly SpotlightRecentLaunchStore recentLaunches;

	private readonly SpotlightClipboardService clipboard;

	private readonly FileSearchService files;

	private readonly IAppSettingsProvider settings;

	private string query = string.Empty;

	private int activeCategory;

	private bool prefixOverrodeCategory;

	private int categoryBeforePrefix;

	private bool isSettingCategoryInternal;

	private int iconGeneration;

	private int searchGeneration;

	private int previewGeneration;

	private CancellationTokenSource? previewCancellation;

	private CancellationTokenSource? searchCancellation;

	private bool searching;

	private string? searchHint;

	private IReadOnlyList<SpotlightGroup> lastGroups = Array.Empty<SpotlightGroup>();

	private SpotlightItem? selected;

	private bool disposed;

	private string? calculation;

	private string? calculationValue;

	private SpotlightGlyphKind queryGlyph = SpotlightGlyphKind.Apps;

	/// <summary>
	/// Whether the empty-query surface is the app grid. False in 搜索栏式唤出, where the empty
	/// query is a bare search field: the App Store artwork belongs to the grid, not to the field.
	/// </summary>
	private bool appGridVisible = true;

	private PreviewSnapshot previewSnapshot;

	public ObservableCollection<SpotlightItem> Items { get; } = new ObservableCollection<SpotlightItem>();

	public ObservableCollection<SpotlightItem> RecentItems { get; } = new ObservableCollection<SpotlightItem>();

	public ObservableCollection<SpotlightItem> Results { get; } = new ObservableCollection<SpotlightItem>();

	/// <summary>
	/// The category bar, in the order the chips are drawn. Each position is also the chip's
	/// F-key slot only until the user rearranges them (设置 › 类别快捷键): a gesture belongs to a
	/// category, never to a position. 快捷指令 is deliberately absent — its rows live under
	/// 组件 (see <see cref="MatchesCategory"/>).
	/// </summary>
	public IReadOnlyList<SpotlightCategory> Categories => categories;

	private List<SpotlightCategory> categories = [];

	public int ActiveCategory => activeCategory;

	/// <summary>
	/// What the active chip filters by. The chip's <b>position</b> changes when the user rearranges
	/// the bar, so every filter decision reads this, never the index.
	/// </summary>
	public SpotlightCategoryKind ActiveKind =>
		activeCategory >= 0 && activeCategory < categories.Count
			? categories[activeCategory].Kind
			: SpotlightCategoryKind.All;

	public string Query
	{
		get
		{
			return query;
		}
		private set
		{
			if (!(query == value))
			{
				query = value;
				OnPropertyChanged("Query");
				OnPropertyChanged("IsFiltering");
				OnPropertyChanged("IsEmptyFolder");
				UpdateCalculation();
			}
		}
	}

	public string? Calculation => calculation;

	public bool HasCalculation => calculation != null;

	public string? CalculationValue => calculationValue;

	public SpotlightGlyphKind QueryGlyph => queryGlyph;

	public bool GlyphIsFilled => SpotlightGlyphs.IsFilled(queryGlyph);

	public bool IsFiltering => query.Length > 0;

	public bool IsEmptyFolder => !IsFiltering && Items.Count == 0;

	public bool IsEmptyResult => IsFiltering && Results.Count == 0 && !IsSearching && !HasCalculation;

	public bool IsSearching
	{
		get
		{
			return searching;
		}
		private set
		{
			if (searching != value)
			{
				searching = value;
				OnPropertyChanged("IsSearching");
				OnPropertyChanged("IsEmptyResult");
			}
		}
	}

	public string? SearchHint
	{
		get
		{
			return searchHint;
		}
		private set
		{
			if (!(searchHint == value))
			{
				searchHint = value;
				OnPropertyChanged("SearchHint");
				OnPropertyChanged("HasSearchHint");
			}
		}
	}

	public bool HasSearchHint => !string.IsNullOrEmpty(searchHint);

	public FileSearchBackend FileBackend => files.Backend;

	public string Folder => settings.Get().EffectiveLauncher.EffectiveFolder;

	public SpotlightPreviewViewModel Preview { get; } = new SpotlightPreviewViewModel();

	public SpotlightItem? Selected => selected;

	public event Action? OpenSettingsRequested;

	public event PropertyChangedEventHandler? PropertyChanged;

	public SpotlightViewModel(SpotlightQueryRouter router, LauncherCatalog catalog, ShellIconService icons, SpotlightUsageStore usage, SpotlightClipboardService clipboard, FileSearchService files, IAppSettingsProvider settings, SpotlightPreviewService previews, SpotlightWebResults webResults, SpotlightRecentLaunchStore? recentLaunches = null)
	{
		this.router = router;
		this.catalog = catalog;
		this.icons = icons;
		this.previews = previews;
		this.usage = usage;
		this.clipboard = clipboard;
		this.files = files;
		this.settings = settings;
		this.webResults = webResults;
		this.recentLaunches = recentLaunches ?? new SpotlightRecentLaunchStore();
		categories = BuildCategories(SavedCategoryOrder());
		catalog.Changed += OnCatalogChanged;
		settings.DataChanged += OnSettingsChanged;
		usage.Changed += OnUsageChanged;
		clipboard.Changed += OnClipboardChanged;
		BindPreview(null);
	}

	/// <summary>The chip order the settings ask for; a settings file that cannot be read falls back to the default.</summary>
	private IReadOnlyList<string> SavedCategoryOrder()
	{
		try
		{
			return settings.Get().EffectiveLauncher.EffectiveCategoryOrder;
		}
		catch
		{
			return LauncherCategoryKeys.Order;
		}
	}

	/// <summary>Map the stored order onto the localized chips, skipping anything unrecognisable.</summary>
	private static List<SpotlightCategory> BuildCategories(IReadOnlyList<string> order)
	{
		var result = new List<SpotlightCategory>(order.Count);
		foreach (var key in order)
		{
			var kind = KindForKey(key);
			if (kind == null) continue;
			result.Add(new SpotlightCategory(kind.Value, LabelFor(kind.Value)));
		}

		// A settings file can never leave the bar empty: without chips there is no way back.
		if (result.Count == 0)
			result.Add(new SpotlightCategory(SpotlightCategoryKind.All, Locale.Spotlight_Category_All));

		return result;
	}

	private static SpotlightCategoryKind? KindForKey(string key) => LauncherCategoryKeys.Match(key) switch
	{
		LauncherCategoryKeys.All => SpotlightCategoryKind.All,
		LauncherCategoryKeys.Apps => SpotlightCategoryKind.Apps,
		LauncherCategoryKeys.Images => SpotlightCategoryKind.Images,
		LauncherCategoryKeys.Files => SpotlightCategoryKind.Files,
		LauncherCategoryKeys.Folders => SpotlightCategoryKind.Folders,
		LauncherCategoryKeys.Widgets => SpotlightCategoryKind.Widgets,
		LauncherCategoryKeys.Clipboard => SpotlightCategoryKind.Clipboard,
		LauncherCategoryKeys.Web => SpotlightCategoryKind.Web,
		_ => null,
	};

	private static string LabelFor(SpotlightCategoryKind kind) => kind switch
	{
		SpotlightCategoryKind.Apps => Locale.Spotlight_Category_Apps,
		SpotlightCategoryKind.Images => Locale.Spotlight_Category_Images,
		SpotlightCategoryKind.Files => Locale.Spotlight_Category_Files,
		SpotlightCategoryKind.Folders => Locale.Spotlight_Category_Folders,
		SpotlightCategoryKind.Widgets => Locale.Spotlight_Category_Widgets,
		SpotlightCategoryKind.Clipboard => Locale.Spotlight_Category_Clipboard,
		SpotlightCategoryKind.Web => Locale.Spotlight_Category_Web,
		_ => Locale.Spotlight_Category_All,
	};

	/// <summary>
	/// Re-read the saved chip order (设置 › 类别快捷键 rearranges it). The <b>filter</b> the user
	/// is on survives the move: it is the same category at a new position, not a new category.
	/// </summary>
	private void RebuildCategories()
	{
		var kind = ActiveKind;
		var next = BuildCategories(SavedCategoryOrder());

		var same = next.Count == categories.Count;
		for (var i = 0; same && i < next.Count; i++)
			same = next[i].Kind == categories[i].Kind && next[i].Label == categories[i].Label;
		if (same) return;

		categories = next;
		var index = categories.FindIndex(category => category.Kind == kind);
		activeCategory = index >= 0 ? index : 0;

		OnPropertyChanged("Categories");
		OnPropertyChanged("ActiveCategory");
		OnPropertyChanged("PlaceholderText");
		ApplyResults();
	}

	public void SetCategory(int index)
	{
		int num = Math.Clamp(index, 0, Categories.Count - 1);
		if (activeCategory != num)
		{
			if (!isSettingCategoryInternal)
			{
				prefixOverrodeCategory = false;
				categoryBeforePrefix = num;
			}
			activeCategory = num;
			OnPropertyChanged("ActiveCategory");
			OnPropertyChanged("PlaceholderText");
			ApplyResults();
		}
	}

	public void SetCategoryByKind(SpotlightCategoryKind kind)
	{
		var index = categories.FindIndex(c => c.Kind == kind);
		if (index >= 0)
		{
			var prevInternal = isSettingCategoryInternal;
			isSettingCategoryInternal = true;
			try
			{
				SetCategory(index);
			}
			finally
			{
				isSettingCategoryInternal = prevInternal;
			}
		}
	}

	public static bool TryMatchPrefix(string text, out SpotlightCategoryKind kind, out string strippedQuery)
	{
		kind = SpotlightCategoryKind.All;
		strippedQuery = string.Empty;
		if (string.IsNullOrEmpty(text)) return false;

		(string Prefix, SpotlightCategoryKind Kind)[] wordPrefixes =
		[
			("clipboard:", SpotlightCategoryKind.Clipboard),
			("folder:", SpotlightCategoryKind.Folders),
			("widget:", SpotlightCategoryKind.Widgets),
			("image:", SpotlightCategoryKind.Images),
			("file:", SpotlightCategoryKind.Files),
			("app:", SpotlightCategoryKind.Apps),
			("img:", SpotlightCategoryKind.Images),
			("dir:", SpotlightCategoryKind.Folders),
			("web:", SpotlightCategoryKind.Web),
			("cb:", SpotlightCategoryKind.Clipboard),
			("sc:", SpotlightCategoryKind.Widgets),
		];

		foreach (var (prefix, catKind) in wordPrefixes)
		{
			if (text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
			{
				kind = catKind;
				strippedQuery = text[prefix.Length..].TrimStart();
				return true;
			}
		}

		char first = text[0];
		SpotlightCategoryKind? symbolKind = first switch
		{
			'>' => SpotlightCategoryKind.Apps,
			'#' => SpotlightCategoryKind.Files,
			'%' => SpotlightCategoryKind.Images,
			'/' => SpotlightCategoryKind.Folders,
			'@' => SpotlightCategoryKind.Widgets,
			'!' => SpotlightCategoryKind.Web,
			'?' => SpotlightCategoryKind.Web,
			'$' => SpotlightCategoryKind.Clipboard,
			_ => null
		};

		if (symbolKind.HasValue)
		{
			kind = symbolKind.Value;
			strippedQuery = text[1..].TrimStart();
			return true;
		}

		return false;
	}

	/// <summary>
	/// The field's placeholder. A locked category says so ("搜索图片…") — the only immediate
	/// feedback a category key can give while the query is still empty.
	/// </summary>
	public string PlaceholderText => ActiveKind == SpotlightCategoryKind.All
		? (!string.IsNullOrWhiteSpace(settings.Get().EffectiveLauncher.SearchPlaceholder)
			? settings.Get().EffectiveLauncher.SearchPlaceholder!
			: Locale.Spotlight_Placeholder)
		: string.Format(Locale.Spotlight_Placeholder_Category, Categories[activeCategory].Label);

	/// <summary>
	/// Told by the window which surface the empty query is: the app grid (应用列表式唤出) or a
	/// bare search field (搜索栏式唤出). Only the grid may wear the App Store artwork.
	/// </summary>
	public void SetAppGridVisible(bool visible)
	{
		if (appGridVisible == visible) return;
		appGridVisible = visible;
		UpdateGlyph();
	}

	private void UpdateGlyph()
	{
		// The App Store artwork belongs to the app GRID only. 全部 is the search itself, so it wears
		// the magnifier the moment there is a query — whatever the first group happens to be — and a
		// locked category says what it searches even before anything is typed.
		SpotlightGlyphKind spotlightGlyphKind;
		if (HasCalculation)
		{
			spotlightGlyphKind = SpotlightGlyphKind.Calculator;
		}
		else if (ActiveKind != SpotlightCategoryKind.All)
		{
			spotlightGlyphKind = CategoryGlyph(ActiveKind);
		}
		else if (!IsFiltering)
		{
			// Only the app grid wears the App Store artwork; the 搜索栏式 empty field is a search.
			spotlightGlyphKind = appGridVisible ? SpotlightGlyphKind.Apps : SpotlightGlyphKind.Search;
		}
		else
		{
			spotlightGlyphKind = SpotlightGlyphKind.Search;
		}
		if (spotlightGlyphKind != queryGlyph)
		{
			queryGlyph = spotlightGlyphKind;
			OnPropertyChanged("QueryGlyph");
			OnPropertyChanged("GlyphIsFilled");
		}
	}

	/// <summary>The mark a locked category wears: every chip has its own artwork, so the field
	/// says which surface it is searching before the first keystroke.</summary>
	private static SpotlightGlyphKind CategoryGlyph(SpotlightCategoryKind kind) => kind switch
	{
		SpotlightCategoryKind.Apps => SpotlightGlyphKind.Apps,
		SpotlightCategoryKind.Images => SpotlightGlyphKind.Image,
		SpotlightCategoryKind.Files => SpotlightGlyphKind.Files,
		SpotlightCategoryKind.Folders => SpotlightGlyphKind.Folder,
		SpotlightCategoryKind.Widgets => SpotlightGlyphKind.Widget,
		SpotlightCategoryKind.Clipboard => SpotlightGlyphKind.Clipboard,
		SpotlightCategoryKind.Web => SpotlightGlyphKind.Web,
		_ => SpotlightGlyphKind.Search,
	};

	private void UpdateCalculation()
	{
		string text = ExpressionCalculator.TryFormat(query);
		string text2 = ((text == null) ? null : ("= " + text));
		if (!(text2 == calculation))
		{
			calculation = text2;
			calculationValue = text;
			OnPropertyChanged("Calculation");
			OnPropertyChanged("HasCalculation");
			OnPropertyChanged("CalculationValue");
			OnPropertyChanged("IsEmptyResult");
			UpdateGlyph();
		}
	}

	public void RequestOpenSettings()
	{
		OpenSettingsRequested?.Invoke();
	}

	public void SetSelected(SpotlightItem? item)
	{
		if (selected != item)
		{
			selected = item;
			OnPropertyChanged("Selected");
			BindPreview(item);
		}
	}

	public async Task<bool> ActivateAsync(SpotlightItem item, bool reveal = false)
	{
		try
		{
			bool launched = false;
			switch (item.Kind)
			{
			case SpotlightItemKind.App:
			{
				LauncherEntry entry = item.Entry;
				if ((object)entry == null)
				{
					return false;
				}
				if (reveal)
				{
					LauncherCatalog.RevealInExplorer(entry.ShortcutPath);
					return true;
				}
				LauncherCatalog.Launch(entry);
				RecordLaunch(item);
				launched = true;
				break;
			}
			case SpotlightItemKind.File:
			{
				string path = item.FilePath;
				if (path == null)
				{
					return false;
				}
				if (reveal)
				{
					LauncherCatalog.RevealInExplorer(path);
					return true;
				}
				LauncherCatalog.OpenPath(path);
				launched = true;
				break;
			}
			case SpotlightItemKind.Clipboard:
			case SpotlightItemKind.Workflow:
			case SpotlightItemKind.Widget:
			case SpotlightItemKind.Web:
			case SpotlightItemKind.WebResult:
			{
				Func<Task<bool>> run = item.Run;
				if (run == null)
				{
					return false;
				}
				launched = await run().ConfigureAwait(continueOnCapturedContext: true);
				break;
			}
			default:
				return false;
			}

			if (launched && !reveal && IsFiltering)
			{
				recentLaunches.RecordLaunch(item);
			}

			return launched;
		}
		catch
		{
			return false;
		}
	}

	public async Task<bool> OpenItem(SpotlightItem item) => await ActivateAsync(item);

	public async Task<bool> LaunchSelected() => selected != null ? await ActivateAsync(selected) : false;

	public void RecordLaunch(SpotlightItem item)
	{
		usage.RecordLaunch(item.Id);
		ApplyUsage(item);
		RefreshRecentsAsync();
		if (IsFiltering)
		{
			recentLaunches.RecordLaunch(item);
		}
	}

	public void Start()
	{
		catalog.Start(Folder);
		Reload();
	}

	public void Reload()
	{
		if (disposed)
		{
			return;
		}
		Dictionary<string, Bitmap> dictionary = Items.Where((SpotlightItem item) => item.Entry != null).ToDictionary((SpotlightItem item) => item.Id, (SpotlightItem item) => item.Icon, StringComparer.Ordinal);
		bool showNames = settings.Get().EffectiveLauncher.ShowNames;
		List<SpotlightItem> list = new List<SpotlightItem>(catalog.Entries.Count);
		foreach (LauncherEntry entry in catalog.Entries)
		{
			SpotlightItem spotlightItem = new SpotlightItem(entry)
			{
				ShowName = showNames
			};
			if (entry.IsBroken)
			{
				spotlightItem.Subtitle = Locale.Spotlight_Broken;
			}
			ApplyUsage(spotlightItem);
			if (dictionary.TryGetValue(entry.Key, out var value))
			{
				spotlightItem.Icon = value;
			}
			list.Add(spotlightItem);
		}
		Items.Clear();
		foreach (SpotlightItem item in OrderTiles(list, settings.Get().EffectiveLauncher.SortMode))
		{
			Items.Add(item);
		}
		OnPropertyChanged("IsEmptyFolder");
		RefreshRecentsAsync();
		StartQuery();
		StartIconLoad();
	}

	/// <summary>
	/// The grid's display order. Pinned tiles are floated first in every mode — a pin is a
	/// placement, not a sort key — and each mode keeps the name order as its tie-breaker so the
	/// grid never reshuffles between two apps that score the same.
	/// </summary>
	private static IEnumerable<SpotlightItem> OrderTiles(
		List<SpotlightItem> tiles, LauncherSortMode sortMode)
	{
		IEnumerable<SpotlightItem> ordered = sortMode switch
		{
			LauncherSortMode.LastUsed => tiles
				.OrderByDescending(item => item.LastLaunchedUtc ?? DateTime.MinValue)
				.ThenBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase),
			LauncherSortMode.MostUsed => tiles
				.OrderByDescending(item => item.LaunchCount)
				.ThenBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase),
			// Name (default): the catalogue already sorted by display name, but ordering here
			// keeps the rule independent of the catalogue's own implementation.
			_ => tiles.OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase),
		};

		return ordered.Where(item => item.IsPinned).Concat(ordered.Where(item => !item.IsPinned));
	}

	public void SetQuery(string? text)
	{
		var raw = text ?? string.Empty;
		if (string.IsNullOrWhiteSpace(raw) && raw.Length > 0 && settings.Get().EffectiveLauncher.SpaceShowsRecentLaunches)
		{
			if (prefixOverrodeCategory)
			{
				prefixOverrodeCategory = false;
				SetCategory(categoryBeforePrefix);
			}
			Query = raw;
			StartQuery();
			return;
		}

		var trimmed = raw.Trim();
		if (TryMatchPrefix(trimmed, out var targetKind, out var stripped))
		{
			if (!prefixOverrodeCategory)
			{
				categoryBeforePrefix = activeCategory;
				prefixOverrodeCategory = true;
			}
			SetCategoryByKind(targetKind);
			Query = trimmed;
			StartQuery(targetKind, stripped);
			return;
		}

		if (prefixOverrodeCategory)
		{
			prefixOverrodeCategory = false;
			SetCategory(categoryBeforePrefix);
		}

		Query = trimmed;
		StartQuery();
	}

	public void ResetIcons()
	{
		Interlocked.Increment(ref iconGeneration);
		icons.ClearMemoryCache();
		foreach (SpotlightItem item in Items)
		{
			item.Icon = null;
		}
	}

	public void RefreshFileIndex()
	{
		files.InvalidateIndex();
	}

	private void StartQuery(SpotlightCategoryKind? targetKind = null, string? strippedQuery = null)
	{
		if (disposed) return;

		int generation = Interlocked.Increment(ref searchGeneration);
		searchCancellation?.Cancel();
		searchCancellation?.Dispose();
		searchCancellation = null;

		// Single space or only whitespace query with SpaceShowsRecentLaunches enabled:
		// Display recently opened items from search results.
		if (string.IsNullOrWhiteSpace(query) && query.Length > 0 && settings.Get().EffectiveLauncher.SpaceShowsRecentLaunches)
		{
			IsSearching = false;
			SearchHint = null;
			lastGroups = Array.Empty<SpotlightGroup>();
			var maxCount = settings.Get().EffectiveLauncher.SpaceRecentLaunchesCount;
			var recents = recentLaunches.GetRecent(maxCount);
			Results.Clear();
			if (recents.Count > 0)
			{
				Results.Add(SpotlightItem.CreateHeader(Locale.Spotlight_RecentLaunches_Header));
				int iconGen = Volatile.Read(in iconGeneration);
				foreach (var recent in recents)
				{
					var item = MapRecentLaunch(recent, iconGen);
					if (item != null)
					{
						Results.Add(item);
					}
				}
			}
			OnPropertyChanged("IsEmptyResult");
			OnPropertyChanged("Results");
			UpdateGlyph();
			return;
		}

		if (!IsFiltering)
		{
			IsSearching = false;
			SearchHint = null;
			lastGroups = Array.Empty<SpotlightGroup>();
			ApplyResults();
			RefreshRecentsAsync();
			return;
		}

		if (!targetKind.HasValue && TryMatchPrefix(query, out var matchedKind, out var stripped))
		{
			targetKind = matchedKind;
			strippedQuery = stripped;
		}

		// When query is just the prefix (stripped query is empty):
		// Switch category chip and show category default list / app list.
		if (targetKind.HasValue && string.IsNullOrEmpty(strippedQuery))
		{
			IsSearching = false;
			SearchHint = null;
			lastGroups = Array.Empty<SpotlightGroup>();
			Results.Clear();

			if (targetKind.Value == SpotlightCategoryKind.Apps)
			{
				Results.Add(SpotlightItem.CreateHeader(Locale.Spotlight_Category_Apps));
				foreach (var item in Items)
				{
					Results.Add(item);
				}
			}
			else if (targetKind.Value == SpotlightCategoryKind.Clipboard)
			{
				var entries = clipboard.Entries;
				if (entries.Count > 0)
				{
					Results.Add(SpotlightItem.CreateHeader(Locale.Spotlight_Category_Clipboard));
					foreach (var entry in entries)
					{
						var captured = entry;
						Results.Add(SpotlightItem.ForClipboard(
							entry.Id, entry.Text, entry.IsLink, entry.CapturedUtc, entry.SourceProcess,
							() => Task.FromResult(clipboard.CopyBack(captured.Id) is not null)));
					}
				}
			}

			OnPropertyChanged("IsEmptyResult");
			OnPropertyChanged("Results");
			UpdateGlyph();
			return;
		}

		string searchText = strippedQuery ?? query;
		CancellationTokenSource cancellationTokenSource = (searchCancellation = new CancellationTokenSource());
		IsSearching = true;
		_ = RouteFastAppsAsync(searchText, generation, cancellationTokenSource.Token);
		RouteAsync(searchText, generation, cancellationTokenSource.Token);
	}

	private SpotlightItem? MapRecentLaunch(SpotlightRecentLaunch recent, int generation)
	{
		switch (recent.Kind)
		{
		case SpotlightItemKind.App:
		{
			var entry = catalog.Entries.FirstOrDefault(e => e.Key == recent.Id || e.Target == recent.Target)
				?? new LauncherEntry(recent.Display, recent.Target ?? recent.Id, recent.Target, IsBroken: false);
			var item = new SpotlightItem(entry)
			{
				Subtitle = recent.Sub ?? entry.ShortcutPath
			};
			ApplyUsage(item);
			if (!string.IsNullOrEmpty(entry.ShortcutPath))
			{
				LoadResultIconAsync(item, entry.ShortcutPath, generation);
			}
			return item;
		}
		case SpotlightItemKind.File:
		{
			var path = recent.Target ?? recent.Id;
			bool isFolder = Directory.Exists(path);
			long size = 0L;
			if (!isFolder && File.Exists(path))
			{
				try { size = new FileInfo(path).Length; } catch { }
			}
			var item = new SpotlightItem(new FileSearchHit(recent.Display, path, isFolder, size, 0));
			if (!string.IsNullOrEmpty(recent.Sub))
			{
				item.Subtitle = recent.Sub;
			}
			LoadResultIconAsync(item, path, generation);
			return item;
		}
		case SpotlightItemKind.Clipboard:
		{
			var capturedId = recent.Id.StartsWith("clip:", StringComparison.Ordinal)
				? recent.Id["clip:".Length..]
				: recent.Id;
			return SpotlightItem.ForClipboard(
				capturedId, recent.Display, isLink: false, recent.Timestamp, recent.Sub,
				() => Task.FromResult(clipboard.CopyBack(capturedId) is not null));
		}
		case SpotlightItemKind.Web:
		case SpotlightItemKind.WebResult:
		{
			var url = recent.Target ?? recent.Id;
			return SpotlightItem.ForWebHit(
				new SpotlightWebHit(recent.Display, url, recent.Sub ?? string.Empty),
				() =>
				{
					var browser = settings.Get().EffectiveLauncher.EffectiveSearchBrowser;
					if (!SpotlightWebProvider.Open(new SpotlightWebQuery(string.Empty, string.Empty, string.Empty, url, string.Empty, browser)))
					{
						LauncherCatalog.OpenPath(url);
					}
					return Task.FromResult(true);
				});
		}
		default:
		{
			return SpotlightItem.ForWorkflow(
				recent.Id, recent.Display, recent.Sub, null,
				() =>
				{
					if (!string.IsNullOrEmpty(recent.Target))
					{
						LauncherCatalog.OpenPath(recent.Target);
						return Task.FromResult(true);
					}
					return Task.FromResult(false);
				});
		}
		}
	}

	private async Task RouteFastAppsAsync(string text, int generation, CancellationToken cancellationToken)
	{
		try
		{
			var fastGroups = await router.RouteFastAppsAsync(text, 40, cancellationToken).ConfigureAwait(false);
			if (cancellationToken.IsCancellationRequested) return;

			await Dispatcher.UIThread.InvokeAsync(() =>
			{
				if (!disposed && generation == Volatile.Read(in searchGeneration))
				{
					if (lastGroups.Count == 0 || (lastGroups.Count == 1 && lastGroups[0].Kind == SpotlightResultKind.App))
					{
						lastGroups = fastGroups;
						ApplyResults();
					}
				}
			});
		}
		catch
		{
		}
	}

	private async Task RouteAsync(string text, int generation, CancellationToken cancellationToken)
	{
		IReadOnlyList<SpotlightGroup> groups = Array.Empty<SpotlightGroup>();
		bool stillSearching = false;
		string hint = null;
		try
		{
			await Task.Delay(110, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
			SpotlightQueryOutcome outcome = await router.RouteAsync(new SpotlightQueryRequest(text), cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
			cancellationToken.ThrowIfCancellationRequested();
			groups = outcome.Groups;
			stillSearching = outcome.SearchingFiles;
			hint = outcome.Hint ?? BuildFileHint();
		}
		catch (OperationCanceledException)
		{
			return;
		}
		catch
		{
		}
		await Dispatcher.UIThread.InvokeAsync(() =>
		{
			if (!disposed && generation == Volatile.Read(in searchGeneration))
			{
				lastGroups = groups;
				IsSearching = stillSearching;
				SearchHint = hint;
				ApplyResults();
			}
		});

		// Second phase: the local rows are on screen already, so the network round-trip cannot
		// delay them. The hits merge into the same 网页 group and arrive as their own repaint.
		_ = LoadWebResultsAsync(text, generation, cancellationToken);
	}

	/// <summary>
	/// Fetch the engine's own answers for <paramref name="text"/> and fold them into the group the
	/// action row lives in. Best effort: a cancelled, slow or refused request leaves the list
	/// exactly as the local sources left it.
	/// </summary>
	private async Task LoadWebResultsAsync(string text, int generation, CancellationToken cancellationToken)
	{
		// A formula already has its answer in the pill; searching it would spend a request on
		// something the list is explicitly hiding (see ApplyResults).
		if (HasCalculation) return;

		try
		{
			var hits = await webResults.QueryAsync(text, cancellationToken).ConfigureAwait(false);
			if (hits.Count == 0 || cancellationToken.IsCancellationRequested) return;

			await Dispatcher.UIThread.InvokeAsync(() =>
			{
				if (disposed || generation != Volatile.Read(in searchGeneration)) return;

				lastGroups = SpotlightQueryRouter.WithWebResults(lastGroups, hits);
				ApplyResults();
			});
		}
		catch
		{
			// The inline list is an enhancement; its failure is not the search's failure.
		}
	}

	private string? BuildFileHint()
	{
		string detail = files.Detail;
		if (1 == 0)
		{
		}
		string result;
		if (detail == "invalid-regex")
		{
			result = Locale.Spotlight_Search_InvalidRegex;
		}
		else if (detail == "everything-elevated")
		{
			result = Locale.Spotlight_Search_EverythingElevated;
		}
		else if (detail == "everything-unresponsive")
		{
			// Everything is on screen but did not answer this query. Saying "start Everything"
			// here is the confusing case users report: the program is already running.
			result = Locale.Spotlight_Search_EverythingUnresponsive;
		}
		else
		{
			result = ((files.Backend == FileSearchBackend.LocalIndex) ? Locale.Spotlight_Search_LocalIndex : null);
		}
		if (1 == 0)
		{
		}
		return result;
	}

	private void ApplyResults()
	{
		Results.Clear();
		var category = ActiveKind;
		HashSet<string> hashSet = new HashSet<string>(StringComparer.Ordinal);
		int generation = Volatile.Read(in iconGeneration);
		foreach (SpotlightGroup lastGroup in lastGroups)
		{
			// Group-level gate first: a category that only shows files never maps app rows, so
			// the icon extraction for those rows is not even started.
			if (!GroupMatchesCategory(lastGroup.Kind, category))
			{
				continue;
			}

			// A formula already has its answer in the field's pill; offering 在浏览器中搜索 the
			// same text would put two answers on screen, one of which is a detour.
			if (HasCalculation && lastGroup.Kind == SpotlightResultKind.Web)
			{
				continue;
			}

			List<SpotlightItem> mapped = new List<SpotlightItem>(lastGroup.Items.Count);
			foreach (SpotlightResult item in lastGroup.Items)
			{
				SpotlightItem spotlightItem = MapResult(item, generation);
				if (spotlightItem != null && MatchesCategory(spotlightItem, category))
				{
					mapped.Add(spotlightItem);
				}
			}

			if (mapped.Count == 0)
			{
				continue;
			}

			string text = HeaderFor(lastGroup, category);
			if (text != null && hashSet.Add(text))
			{
				Results.Add(SpotlightItem.CreateHeader(text));
			}
			foreach (SpotlightItem item in mapped)
			{
				Results.Add(item);
			}
		}
		OnPropertyChanged("IsEmptyResult");

		// The list was just rebuilt, so its selection is gone (the collection was cleared). The view
		// re-seats it — both when a category switch re-filters and when the inline web hits land
		// after the local rows.
		OnPropertyChanged("Results");
		UpdateGlyph();
	}

	/// <summary>
	/// Whether a whole result group can contribute to <paramref name="category"/>. 组件 owns both
	/// the widget data rows and the 快捷指令 workflows, so those two kinds pass together.
	/// <para>
	/// 网页 is the one kind that is <b>excluded</b> from every other locked category: "show me the
	/// apps that match" must not end with a row that opens a browser.
	/// </para>
	/// </summary>
	private static bool GroupMatchesCategory(SpotlightResultKind kind, SpotlightCategoryKind category) => category switch
	{
		SpotlightCategoryKind.All => true,
		SpotlightCategoryKind.Apps => kind == SpotlightResultKind.App,
		SpotlightCategoryKind.Images or SpotlightCategoryKind.Files or SpotlightCategoryKind.Folders
			=> kind == SpotlightResultKind.File,
		SpotlightCategoryKind.Widgets => kind is SpotlightResultKind.Workflow or SpotlightResultKind.Widget,
		SpotlightCategoryKind.Clipboard => kind == SpotlightResultKind.Clipboard,
		SpotlightCategoryKind.Web => kind == SpotlightResultKind.Web,
		_ => true,
	};

	/// <summary>
	/// Whether one row belongs to <paramref name="category"/>. The three file views are
	/// mutually exclusive — an image is 图片, a directory is 文件夹, everything else is 文件 — so
	/// the same hit never shows up under two chips.
	/// <para>
	/// Public because it is the category contract, not a rendering detail: the headless checks
	/// assert it without a window, a file index or a search backend.
	/// </para>
	/// </summary>
	public static bool MatchesCategory(SpotlightItem item, SpotlightCategoryKind category) => category switch
	{
		SpotlightCategoryKind.All => true,
		SpotlightCategoryKind.Apps => item.Kind == SpotlightItemKind.App,
		SpotlightCategoryKind.Images => item.Kind == SpotlightItemKind.File
			&& !item.IsFolder && SpotlightFileKinds.IsImage(item.FilePath),
		SpotlightCategoryKind.Files => item.Kind == SpotlightItemKind.File
			&& !item.IsFolder && !SpotlightFileKinds.IsImage(item.FilePath),
		SpotlightCategoryKind.Folders => item.Kind == SpotlightItemKind.File && item.IsFolder,
		SpotlightCategoryKind.Widgets => item.Kind is SpotlightItemKind.Workflow or SpotlightItemKind.Widget,
		SpotlightCategoryKind.Clipboard => item.Kind == SpotlightItemKind.Clipboard,
		SpotlightCategoryKind.Web => item.Kind is SpotlightItemKind.Web or SpotlightItemKind.WebResult,
		_ => true,
	};

	/// <summary>
	/// The caption above a group. A locked file view renames the single 文件 group to the chip's
	/// own label (图片 / 文件夹), while 组件 keeps its sub-captions (快捷指令 / 清单 / 课程) so the
	/// merged category still says where each row came from.
	/// </summary>
	private string? HeaderFor(SpotlightGroup group, SpotlightCategoryKind category)
	{
		if (category is SpotlightCategoryKind.Images or SpotlightCategoryKind.Files or SpotlightCategoryKind.Folders)
		{
			return Categories[activeCategory].Label;
		}
		return group.Title ?? HeaderLabel(group.Kind);
	}

	private static string? HeaderLabel(SpotlightResultKind kind)
	{
		return kind switch
		{
			SpotlightResultKind.App => Locale.Spotlight_Category_Apps,
			SpotlightResultKind.Workflow => Locale.Spotlight_Category_Shortcuts,
			SpotlightResultKind.File => Locale.Spotlight_Category_Files,
			SpotlightResultKind.Clipboard => Locale.Spotlight_Category_Clipboard,
			SpotlightResultKind.Widget => Locale.Spotlight_Category_Widgets,
			SpotlightResultKind.Web => Locale.Spotlight_Web_Group,
			_ => null,
		};
	}

	private SpotlightItem? MapResult(SpotlightResult result, int generation)
	{
		switch (result.Kind)
		{
		case SpotlightResultKind.App:
		{
			SpotlightItem spotlightItem = Items.FirstOrDefault((SpotlightItem candidate) => candidate.Id == result.Id);
			if (spotlightItem == null)
			{
				LauncherEntry launcherEntry = catalog.Entries.FirstOrDefault((LauncherEntry candidate) => candidate.Key == result.Id) ?? new LauncherEntry(result.Title, result.TargetPath ?? result.Id, result.TargetPath, result.IsBroken);
				spotlightItem = new SpotlightItem(launcherEntry);
				LoadResultIconAsync(spotlightItem, launcherEntry.ShortcutPath, generation);
			}
			else
			{
				LoadResultIconAsync(spotlightItem, spotlightItem.Entry.ShortcutPath, generation);
			}
			if (spotlightItem.IsBroken)
			{
				spotlightItem.Subtitle = Locale.Spotlight_Broken;
			}
			ApplyUsage(spotlightItem);
			return spotlightItem;
		}
		case SpotlightResultKind.File:
		{
			string targetPath = result.TargetPath;
			if (string.IsNullOrEmpty(targetPath))
			{
				return null;
			}
			bool flag = Directory.Exists(targetPath);
			long size = 0L;
			if (!flag)
			{
				try
				{
					size = new FileInfo(targetPath).Length;
				}
				catch
				{
				}
			}
			SpotlightItem spotlightItem2 = new SpotlightItem(new FileSearchHit(result.Title, targetPath, flag, size, (int)result.Score));
			if (result.IsBroken)
			{
				spotlightItem2.Subtitle = Locale.Spotlight_Broken;
			}
			LoadResultIconAsync(spotlightItem2, targetPath, generation);
			return spotlightItem2;
		}
		case SpotlightResultKind.Clipboard:
		{
			if (result.RunAsync == null)
			{
				return null;
			}
			string id2 = (result.Id.StartsWith("clip:", StringComparison.Ordinal) ? result.Id.Substring("clip:".Length) : result.Id);
			return SpotlightItem.ForClipboard(id2, result.ClipboardText ?? result.Title, result.IsLink, result.CapturedUtc ?? DateTime.MinValue, result.SourceProcess, result.RunAsync);
		}
		case SpotlightResultKind.Workflow:
		{
			if (result.RunAsync == null)
			{
				return null;
			}
			string id = (result.Id.StartsWith("wf:", StringComparison.Ordinal) ? result.Id.Substring("wf:".Length) : result.Id);
			return SpotlightItem.ForWorkflow(id, result.Title, result.Subtitle, result.Badge, result.RunAsync);
		}
		case SpotlightResultKind.Widget:
		{
			if (result.RunAsync == null)
			{
				return null;
			}
			string id3 = (result.Id.StartsWith("wd:", StringComparison.Ordinal) ? result.Id.Substring("wd:".Length) : result.Id);
			return SpotlightItem.ForWidget(id3, result.Title, result.Subtitle, result.GroupTitle, result.RunAsync);
		}
		case SpotlightResultKind.Web:
		{
			// The row's two lines and its icon are composed by the factory: the pipeline only
			// says which engine/browser/address the query resolved to.
			if (result.RunAsync == null)
			{
				return null;
			}

			// A hit (WebUrl set) is a specific page; the action row carries the prepared target.
			if (result.WebUrl is { Length: > 0 } url)
			{
				return SpotlightItem.ForWebHit(
					new SpotlightWebHit(result.Title, url, result.WebSnippet ?? string.Empty),
					result.RunAsync);
			}

			if (result.WebQuery == null)
			{
				return null;
			}
			return SpotlightItem.ForWeb(result.WebQuery, result.RunAsync);
		}
		default:
			return null;
		}
	}

	private async Task RefreshRecentsAsync()
	{
		var launcher = settings.Get().EffectiveLauncher;
		if (!launcher.RecentApps)
		{
			await Dispatcher.UIThread.InvokeAsync(() =>
			{
				if (!disposed)
				{
					RecentItems.Clear();
				}
			});
			return;
		}

		// 最近使用 is one row of the grid's own tiles, so it can only hold as many tiles as the
		// grid has columns: a ninth tile in a seven-column grid would wrap the strip into a second
		// row and break both "one row" and the alignment with the grid underneath.
		var limit = Math.Clamp(launcher.Columns, 1, RecentLimit);

		IReadOnlyList<SpotlightResult> recents;
		try
		{
			recents = (await router.RouteAsync(new SpotlightQueryRequest(string.Empty, limit), CancellationToken.None).ConfigureAwait(continueOnCapturedContext: false)).Recents;
		}
		catch
		{
			return;
		}
		await Dispatcher.UIThread.InvokeAsync(() =>
		{
			if (disposed)
			{
				return;
			}
			RecentItems.Clear();
			foreach (SpotlightResult item in recents)
			{
				SpotlightItem spotlightItem = MapRecent(item);
				if (spotlightItem != null)
				{
					RecentItems.Add(spotlightItem);
				}
			}
		});
	}

	private SpotlightItem? MapRecent(SpotlightResult result)
	{
		if (result.Kind != SpotlightResultKind.App)
		{
			return null;
		}
		SpotlightItem spotlightItem = Items.FirstOrDefault((SpotlightItem candidate) => candidate.Id == result.Id);
		if (spotlightItem == null)
		{
			LauncherEntry launcherEntry = catalog.Entries.FirstOrDefault((LauncherEntry candidate) => candidate.Key == result.Id) ?? new LauncherEntry(result.Title, result.TargetPath ?? result.Id, result.TargetPath, result.IsBroken);
			spotlightItem = new SpotlightItem(launcherEntry);
			if (spotlightItem.IsBroken)
			{
				spotlightItem.Subtitle = Locale.Spotlight_Broken;
			}
			LoadResultIconAsync(spotlightItem, launcherEntry.ShortcutPath, Volatile.Read(in iconGeneration));
		}
		ApplyUsage(spotlightItem);
		return spotlightItem;
	}

	private void ApplyUsage(SpotlightItem item)
	{
		LauncherUsage launcherUsage = usage.Get(item.Id);
		item.LaunchCount = launcherUsage.LaunchCount;
		item.LastLaunchedUtc = launcherUsage.LastLaunchedUtc;
		item.IsPinned = launcherUsage.Pinned;
	}

	public void StartIconLoad(int? pixels = null)
	{
		if (!disposed)
		{
			int pixels2 = pixels ?? 256;
			int generation = Interlocked.Increment(ref iconGeneration);
			SpotlightItem[] array = Items.Where((SpotlightItem item) => item.Icon == null).ToArray();
			if (array.Length != 0)
			{
				LoadIconsAsync(array, pixels2, generation);
			}
		}
	}

	private async Task LoadIconsAsync(SpotlightItem[] snapshot, int pixels, int generation)
	{
		try
		{
			foreach (SpotlightItem item in snapshot)
			{
				if (disposed || generation != Volatile.Read(in iconGeneration))
				{
					break;
				}
				Bitmap bitmap = await icons.GetIconAsync(item.Entry.ShortcutPath, pixels).ConfigureAwait(continueOnCapturedContext: false);
				if (bitmap == null)
				{
					continue;
				}
				if (disposed || generation != Volatile.Read(in iconGeneration))
				{
					break;
				}
				await Dispatcher.UIThread.InvokeAsync(() =>
				{
					if (generation == Volatile.Read(in iconGeneration))
					{
						item.Icon = bitmap;
					}
				});
			}
		}
		catch
		{
		}
	}

	private async Task LoadResultIconAsync(SpotlightItem item, string? path, int generation)
	{
		if (path == null)
		{
			return;
		}
		try
		{
			Bitmap bitmap = await icons.GetIconAsync(path, 256).ConfigureAwait(continueOnCapturedContext: false);
			if (bitmap == null || disposed)
			{
				return;
			}
			await Dispatcher.UIThread.InvokeAsync(() =>
			{
				if (!disposed && generation == Volatile.Read(in iconGeneration))
				{
					item.Icon = bitmap;
				}
			});
		}
		catch
		{
		}
	}

	private void BindPreview(SpotlightItem? item)
	{
		int generation = Interlocked.Increment(ref previewGeneration);

		// A new selection owns the pane: whatever the previous one was still extracting (a shell
		// thumbnail, a 30 MB deck's XML) is now wasted work.
		previewCancellation?.Cancel();
		previewCancellation?.Dispose();
		previewCancellation = null;

		Preview.Bind(item);
		switch (item?.Kind)
		{
		case SpotlightItemKind.App:
		{
			LauncherEntry entry = item.Entry;
			LauncherUsage launcherUsage = usage.Get(item.Id);
			ApplyPreview(item.Name, item.Subtitle, Locale.Spotlight_Preview_Kind_App, string.Empty, new SpotlightPreviewRow[4]
			{
				new SpotlightPreviewRow(Locale.Spotlight_Preview_Label_Target, entry.Target ?? entry.ShortcutPath),
				new SpotlightPreviewRow(Locale.Spotlight_Preview_Label_Location, Path.GetDirectoryName(entry.ShortcutPath) ?? string.Empty),
				new SpotlightPreviewRow(Locale.Spotlight_Preview_Label_Recent, FormatRelative(launcherUsage.LastLaunchedUtc)),
				new SpotlightPreviewRow(Locale.Spotlight_Preview_Label_Launches, launcherUsage.LaunchCount.ToString(CultureInfo.CurrentCulture))
			}, hasOpen: true, hasReveal: true, hasRun: false, hasCopy: false);
			LoadPreviewIconAsync(entry.ShortcutPath, generation);
			break;
		}
		case SpotlightItemKind.File:
		{
			string filePath = item.FilePath;

			// The type line is known before any IO — classification is extension-only, the same
			// rule the category bar uses. Waiting for the thumbnail to say "图片" would leave the
			// pane reading "文件" for every hit until the handler answered.
			SpotlightPreviewFlavor flavor = SpotlightPreviewService.Classify(filePath, item.IsFolder);
			ApplyPreview(item.Name, item.Subtitle, KindLabelFor(flavor, item.IsFolder), string.Empty, new SpotlightPreviewRow[2]
			{
				new SpotlightPreviewRow(Locale.Spotlight_Preview_Label_Location, Path.GetDirectoryName(filePath) ?? string.Empty),
				new SpotlightPreviewRow(Locale.Spotlight_Preview_Label_Size, FormatSize(item))
			}, hasOpen: true, hasReveal: true, hasRun: false, hasCopy: false);

			// The icon is the immediate fallback: a thumbnail can take a beat, and an empty frame
			// in the meantime is worse than the type's generic icon.
			LoadPreviewIconAsync(filePath, generation);

			CancellationTokenSource cancellation = new CancellationTokenSource();
			previewCancellation = cancellation;
			_ = LoadFilePreviewAsync(item, filePath, generation, cancellation.Token);
			break;
		}
		case SpotlightItemKind.Clipboard:
			ApplyPreview(item.Name, string.Empty, Locale.Spotlight_Preview_Kind_Clipboard, item.ClipboardText ?? string.Empty, new SpotlightPreviewRow[2]
			{
				new SpotlightPreviewRow(Locale.Spotlight_Preview_Label_Captured, FormatRelative(item.CapturedUtc)),
				new SpotlightPreviewRow(Locale.Spotlight_Preview_Label_Source, string.IsNullOrEmpty(item.SourceProcess) ? "—" : item.SourceProcess)
			}, item.IsLink, hasReveal: false, hasRun: false, hasCopy: true);
			break;
		case SpotlightItemKind.Workflow:
			// The badge is the workflow's own hotkey, not a launcher-assigned quick key; it is
			// read-only information, so the pane only shows it when the workflow has one.
			ApplyPreview(item.Name, item.Subtitle, Locale.Spotlight_Preview_Kind_Workflow, string.Empty,
				string.IsNullOrEmpty(item.Badge)
					? Array.Empty<SpotlightPreviewRow>()
					: new SpotlightPreviewRow[1]
					{
						new SpotlightPreviewRow(Locale.Spotlight_Preview_Label_Hotkey, item.Badge)
					},
				hasOpen: false, hasReveal: false, hasRun: true, hasCopy: false);
			break;
		case SpotlightItemKind.Widget:
			// The widget owns both lines: the subtitle is its own summary (list name + state,
			// teacher · room · slot) and the group title is its type line. Enter runs the row's
			// own action — toggle the checklist item / open the timetable panel.
			ApplyPreview(item.Name, item.Subtitle, item.GroupTitle ?? Locale.Spotlight_Preview_Kind_Widget, string.Empty, Array.Empty<SpotlightPreviewRow>(), hasOpen: false, hasReveal: false, hasRun: true, hasCopy: false);
			break;
		case SpotlightItemKind.Web:
			// The pane answers "where is this going?" — engine, browser and the exact address —
			// because that is the only thing the user cannot see from the row. 打开 repeats Enter.
			ApplyPreview(item.Name, string.Empty, Locale.Spotlight_Preview_Kind_Web, string.Empty,
				item.Web is { } target
					? new SpotlightPreviewRow[3]
					{
						new SpotlightPreviewRow(Locale.Spotlight_Preview_Label_Engine, target.EngineName),
						new SpotlightPreviewRow(Locale.Spotlight_Preview_Label_Browser, target.BrowserName),
						new SpotlightPreviewRow(Locale.Spotlight_Preview_Label_Address, target.Url)
					}
					: Array.Empty<SpotlightPreviewRow>(),
				hasOpen: true, hasReveal: false, hasRun: false, hasCopy: false, icon: item.Icon);
			break;
		case SpotlightItemKind.WebResult:
			// A hit has nowhere to "reveal": the pane answers "which page is this?" with the
			// snippet and the exact address, and 打开 repeats Enter.
			ApplyPreview(item.Name, item.Subtitle, Locale.Spotlight_Preview_Kind_Web,
				item.Subtitle,
				item.LinkUrl is { Length: > 0 } link
					? new SpotlightPreviewRow[1]
					{
						new SpotlightPreviewRow(Locale.Spotlight_Preview_Label_Address, link)
					}
					: Array.Empty<SpotlightPreviewRow>(),
				hasOpen: true, hasReveal: false, hasRun: false, hasCopy: false, icon: item.Icon);
			break;
		default:
			ApplyPreview(Locale.Spotlight_Preview_Empty, string.Empty, string.Empty, string.Empty, Array.Empty<SpotlightPreviewRow>(), hasOpen: false, hasReveal: false, hasRun: false, hasCopy: false);
			break;
		}
	}

	/// <summary>
	/// Fill the pane's rich half — hero thumbnail, text excerpt, extra facts — without ever
	/// touching the UI thread until the result is ready.
	/// <para>
	/// The generation check is the whole correctness argument: a slow thumbnail for row 3 must not
	/// land on row 7's pane. Cancellation covers the common case (the user kept typing), the
	/// generation covers the race (the task finished just as the selection moved).
	/// </para>
	/// </summary>
	private async Task LoadFilePreviewAsync(SpotlightItem item, string path, int generation, CancellationToken cancellationToken)
	{
		try
		{
			await Task.Delay(PreviewDelayMs, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);

			SpotlightPreviewContent content = await previews.LoadAsync(path, item.IsFolder, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
			cancellationToken.ThrowIfCancellationRequested();

			// One line per completed preview. "The pane is blank" has several very different
			// causes (no handler, an unreadable file, a layout that never ran), and this is the
			// only place that can tell them apart after the fact.
			GlassDiagnostics.Event(
				$"[spotlight] preview {content.Flavor} media={(content.Media != null ? "yes" : "no")} " +
				$"excerpt={content.Excerpt.Length} facts={content.Facts.Count} exists={File.Exists(path)} path='{path}'");

			await Dispatcher.UIThread.InvokeAsync(() =>
			{
				if (disposed || generation != Volatile.Read(in previewGeneration))
				{
					return;
				}

				PreviewSnapshot snapshot = previewSnapshot;
				IReadOnlyList<SpotlightPreviewRow> rows = MergeRows(snapshot.Rows, content.Facts);
				string kindLabel = KindLabelFor(content.Flavor, item.IsFolder);
				previewSnapshot = snapshot with
				{
					KindLabel = kindLabel,
					Excerpt = content.Excerpt,
					Media = content.Media,
					Rows = rows
				};
				Preview.Set(snapshot.Title, snapshot.Subtitle, kindLabel, snapshot.ContentText,
					content.Excerpt, content.Media, Preview.Icon, rows,
					snapshot.HasOpen, snapshot.HasReveal, snapshot.HasRun, snapshot.HasCopy);
			});
		}
		catch (OperationCanceledException)
		{
			// The selection moved on; the pane already belongs to another row.
		}
		catch
		{
			// A preview is an enhancement: any failure leaves the icon + rows baseline standing.
		}
	}

	private async Task LoadPreviewIconAsync(string path, int generation)
	{
		try
		{
			Bitmap bitmap = await icons.GetIconAsync(path, 256).ConfigureAwait(continueOnCapturedContext: false);
			if (bitmap == null || disposed)
			{
				return;
			}
			await Dispatcher.UIThread.InvokeAsync(() =>
			{
				if (!disposed && generation == Volatile.Read(in previewGeneration))
				{
					PreviewSnapshot snapshot = previewSnapshot;
					Preview.Set(snapshot.Title, snapshot.Subtitle, snapshot.KindLabel, snapshot.ContentText,
						snapshot.Excerpt, snapshot.Media, bitmap, snapshot.Rows,
						snapshot.HasOpen, snapshot.HasReveal, snapshot.HasRun, snapshot.HasCopy);
				}
			});
		}
		catch
		{
		}
	}

	private void ApplyPreview(string title, string subtitle, string kindLabel, string contentText, IReadOnlyList<SpotlightPreviewRow> rows, bool hasOpen, bool hasReveal, bool hasRun, bool hasCopy, string excerpt = "", Bitmap? media = null, Bitmap? icon = null)
	{
		previewSnapshot = new PreviewSnapshot(title, subtitle, kindLabel, contentText, excerpt, media, rows, hasOpen, hasReveal, hasRun, hasCopy);
		Preview.Set(title, subtitle, kindLabel, contentText, excerpt, media, icon, rows, hasOpen, hasReveal, hasRun, hasCopy);
	}

	/// <summary>The pane's type line, localized. The flavor itself is language-neutral.</summary>
	private static string KindLabelFor(SpotlightPreviewFlavor flavor, bool isFolder) => flavor switch
	{
		SpotlightPreviewFlavor.Image => Locale.Spotlight_Preview_Kind_Image,
		SpotlightPreviewFlavor.Pdf => Locale.Spotlight_Preview_Kind_Pdf,
		SpotlightPreviewFlavor.Document => Locale.Spotlight_Preview_Kind_Document,
		SpotlightPreviewFlavor.Presentation => Locale.Spotlight_Preview_Kind_Presentation,
		SpotlightPreviewFlavor.Spreadsheet => Locale.Spotlight_Preview_Kind_Spreadsheet,
		SpotlightPreviewFlavor.Text => Locale.Spotlight_Preview_Kind_Text,
		SpotlightPreviewFlavor.Video => Locale.Spotlight_Preview_Kind_Video,
		SpotlightPreviewFlavor.Audio => Locale.Spotlight_Preview_Kind_Audio,
		SpotlightPreviewFlavor.Archive => Locale.Spotlight_Preview_Kind_Archive,
		SpotlightPreviewFlavor.Folder => Locale.Spotlight_Preview_Kind_Folder,
		_ => isFolder ? Locale.Spotlight_Preview_Kind_Folder : Locale.Spotlight_Preview_Kind_File,
	};

    /// <summary>
    /// Append the facts the preview service discovered to the rows the view model already built.
    /// Dimensions, page count and item count belong above the path (they describe the thing);
    /// timestamps and sheet names belong below it (they describe its metadata).
    /// </summary>
    private static IReadOnlyList<SpotlightPreviewRow> MergeRows(IReadOnlyList<SpotlightPreviewRow> rows, IReadOnlyList<SpotlightPreviewFact> facts)
    {
        if (facts.Count == 0)
        {
            return rows;
        }

        List<SpotlightPreviewRow> leading = new List<SpotlightPreviewRow>(2);
        List<SpotlightPreviewRow> trailing = new List<SpotlightPreviewRow>(2);
        foreach (SpotlightPreviewFact fact in facts)
        {
            string label = FactLabel(fact.Kind);
            if (label.Length == 0)
            {
                continue;
            }

            SpotlightPreviewRow row = new SpotlightPreviewRow(label, fact.Value);
            if (fact.Kind is SpotlightPreviewFactKind.Dimensions or SpotlightPreviewFactKind.Items or SpotlightPreviewFactKind.Pages)
            {
                leading.Add(row);
            }
            else
            {
                trailing.Add(row);
            }
        }

        List<SpotlightPreviewRow> merged = new List<SpotlightPreviewRow>(leading.Count + rows.Count + trailing.Count);
        merged.AddRange(leading);
        merged.AddRange(rows);
        merged.AddRange(trailing);
        return merged;
    }

    private static string FactLabel(SpotlightPreviewFactKind kind) => kind switch
    {
        SpotlightPreviewFactKind.Dimensions => Locale.Spotlight_Preview_Label_Dimensions,
        SpotlightPreviewFactKind.Modified => Locale.Spotlight_Preview_Label_Modified,
        SpotlightPreviewFactKind.Items => Locale.Spotlight_Preview_Label_Items,
        SpotlightPreviewFactKind.Sheets => Locale.Spotlight_Preview_Label_Sheets,
        SpotlightPreviewFactKind.Pages => Locale.Spotlight_Preview_Label_Pages,
        _ => string.Empty,
    };

	private static string FormatRelative(DateTime? utc)
	{
		if (utc.HasValue)
		{
			DateTime valueOrDefault = utc.GetValueOrDefault();
			if (true)
			{
				TimeSpan timeSpan = DateTime.UtcNow - valueOrDefault;
				if (timeSpan < TimeSpan.Zero)
				{
					timeSpan = TimeSpan.Zero;
				}
				if (timeSpan < TimeSpan.FromMinutes(1L))
				{
					return "now";
				}
				if (!(timeSpan < TimeSpan.FromHours(1)))
				{
					if (!(timeSpan < TimeSpan.FromDays(1)))
					{
						return $"{(int)timeSpan.TotalDays}d";
					}
					return $"{(int)timeSpan.TotalHours}h";
				}
				return $"{(int)timeSpan.TotalMinutes}m";
			}
		}
		return "—";
	}

	private static string FormatSize(SpotlightItem item)
	{
		if (item.IsFolder || item.Size <= 0)
		{
			return "—";
		}
		string result;
		if (item.Size >= 1048576)
		{
			IFormatProvider currentCulture = CultureInfo.CurrentCulture;
			IFormatProvider provider = currentCulture;
			DefaultInterpolatedStringHandler handler = new DefaultInterpolatedStringHandler(3, 1, currentCulture);
			handler.AppendFormatted((double)item.Size / 1048576.0, "0.#");
			handler.AppendLiteral(" MB");
			result = string.Create(provider, ref handler);
		}
		else
		{
			IFormatProvider currentCulture = CultureInfo.CurrentCulture;
			IFormatProvider provider2 = currentCulture;
			DefaultInterpolatedStringHandler handler2 = new DefaultInterpolatedStringHandler(3, 1, currentCulture);
			handler2.AppendFormatted((double)item.Size / 1024.0, "0.#");
			handler2.AppendLiteral(" KB");
			result = string.Create(provider2, ref handler2);
		}
		return result;
	}

	public void Dispose()
	{
		if (!disposed)
		{
			disposed = true;
			catalog.Changed -= OnCatalogChanged;
			settings.DataChanged -= OnSettingsChanged;
			usage.Changed -= OnUsageChanged;
			clipboard.Changed -= OnClipboardChanged;
			Interlocked.Increment(ref iconGeneration);
			Interlocked.Increment(ref searchGeneration);
			Interlocked.Increment(ref previewGeneration);
			previewCancellation?.Cancel();
			previewCancellation?.Dispose();
			previewCancellation = null;
			searchCancellation?.Cancel();
			searchCancellation?.Dispose();
			searchCancellation = null;
		}
	}

	private void OnCatalogChanged()
	{
		Dispatcher.UIThread.Post(Reload);
	}

	private void OnUsageChanged()
	{
		Dispatcher.UIThread.Post(() =>
		{
			if (!disposed)
			{
				RefreshRecentsAsync();
			}
		});
	}

	private void OnClipboardChanged()
	{
		Dispatcher.UIThread.Post(() =>
		{
			if (!disposed && ActiveKind == SpotlightCategoryKind.Clipboard)
			{
				SetQuery(query);
			}
		});
	}

	private void OnSettingsChanged(object? sender, AppSettings? oldData, AppSettings newData)
	{
		if (oldData?.EffectiveLauncher.SearchPlaceholder != newData.EffectiveLauncher.SearchPlaceholder)
		{
			Dispatcher.UIThread.Post(() => OnPropertyChanged("PlaceholderText"));
		}
		if (oldData?.EffectiveLauncher.ShowNames != newData.EffectiveLauncher.ShowNames)
		{
			bool showNames = newData.EffectiveLauncher.ShowNames;
			Dispatcher.UIThread.Post(() =>
			{
				foreach (SpotlightItem item in Items)
				{
					item.ShowName = showNames;
				}
			});
		}
		if (oldData?.EffectiveLauncher.FileSearch != newData.EffectiveLauncher.FileSearch)
		{
			Dispatcher.UIThread.Post(() =>
			{
				if (IsFiltering)
				{
					StartQuery();
				}
			});
		}
		// 网页搜索: the row is re-prepared on every query, so a settings change has to re-run the
		// query that is on screen — otherwise the row keeps the engine the user just replaced.
		// The inline result count is part of the same answer: a new count means a new fetch.
		if (oldData?.EffectiveLauncher.WebSearch != newData.EffectiveLauncher.WebSearch
			|| oldData?.EffectiveLauncher.EffectiveSearchEngine != newData.EffectiveLauncher.EffectiveSearchEngine
			|| oldData?.EffectiveLauncher.EffectiveSearchEngineUrl != newData.EffectiveLauncher.EffectiveSearchEngineUrl
			|| oldData?.EffectiveLauncher.EffectiveSearchBrowser != newData.EffectiveLauncher.EffectiveSearchBrowser
			|| oldData?.EffectiveLauncher.EffectiveWebResultCount != newData.EffectiveLauncher.EffectiveWebResultCount)
		{
			Dispatcher.UIThread.Post(() =>
			{
				if (IsFiltering)
				{
					StartQuery();
				}
			});
		}
		// 类别排序: the chips are rebuilt in place; the filter the user is on follows its category.
		if (!(oldData?.EffectiveLauncher.EffectiveCategoryOrder ?? LauncherCategoryKeys.Order)
				.SequenceEqual(newData.EffectiveLauncher.EffectiveCategoryOrder, StringComparer.Ordinal))
		{
			Dispatcher.UIThread.Post(RebuildCategories);
		}
		// Columns decides how many tiles the strip may hold (one row of the grid's cells), so a
		// column change has to re-ask for the recents, not just re-flow the grid.
		if (oldData?.EffectiveLauncher.RecentApps != newData.EffectiveLauncher.RecentApps
			|| oldData?.EffectiveLauncher.Columns != newData.EffectiveLauncher.Columns)
		{
			_ = RefreshRecentsAsync();
		}
		if (oldData?.EffectiveLauncher.ClipboardHistory != newData.EffectiveLauncher.ClipboardHistory)
		{
			clipboard.RefreshSettings();
		}
		// 排序方式 changes the grid's order, not its content: a reload is the one path that
		// re-applies usage metadata and the mode in the same pass.
		if (oldData?.EffectiveLauncher.SortMode != newData.EffectiveLauncher.SortMode)
		{
			Dispatcher.UIThread.Post(Reload);
		}
		string a = oldData?.EffectiveLauncher.EffectiveFolder;
		string current = newData.EffectiveLauncher.EffectiveFolder;
		if (!string.Equals(a, current, StringComparison.OrdinalIgnoreCase))
		{
			Dispatcher.UIThread.Post(() =>
			{
				catalog.Start(current);
				Reload();
			});
		}
	}

	private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
	{
		PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
	}
}
