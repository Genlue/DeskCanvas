using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using DeskCanvas.Views;
using Hitokoto.Locales;
using Hitokoto.Models;
using Hitokoto.Services;

namespace Hitokoto.Views;

/// <summary>
/// Secondary panel for the Hitokoto widget: displays full quote details, Core API like actions,
/// local favorites, and recent quote history so users never lose a quote they accidentally skipped.
/// <para>
/// W8 migration: the card, the glass surface, the title chrome and the close button all live in
/// the shared <see cref="SecondaryPanelShell"/> now — the window declares only business content
/// and keeps only business behaviour (the store subscription, favorites/tabs/search, the toast
/// and the dynamic category badge). The five-branch material ApplyTheme, the root drag handler
/// and the per-open AppSettingsProvider are gone: <see cref="SecondaryPanelWindow"/> plus
/// <see cref="DeskCanvas.Services.PanelAppearanceResolver"/> own the material and the transitions.
/// </para>
/// </summary>
public partial class HitokotoPopupWindow : SecondaryPanelWindow
{
    private readonly Point? spawnScreenCenter;
    private readonly HitokotoModel model;
    private readonly Func<Task>? onRequestNextQuote;
    private HitokotoItem currentItem;
    private string activeTab = "Favorites";
    private string searchQuery = string.Empty;
    private string lastListSignature = string.Empty;
    private DispatcherTimer? toastTimer;

    protected override Point? SpawnScreenCenter => spawnScreenCenter;

    public HitokotoPopupWindow()
        : this(new HitokotoModel(), HitokotoStore.Instance.GetCurrentOrFallback(new HitokotoModel()), null, null, null)
    {
    }

    private static bool IsChinese =>
        System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.Equals("zh", StringComparison.OrdinalIgnoreCase);

    public HitokotoPopupWindow(
        HitokotoModel model,
        HitokotoItem initialItem,
        Point? screenCenter = null,
        Func<Task>? onRequestNextQuote = null,
        double? cornerRadius = null)
    {
        this.model = model.Normalize();
        currentItem = initialItem;
        spawnScreenCenter = screenCenter;
        this.onRequestNextQuote = onRequestNextQuote;

        InitializeComponent();
        InitializePanel(cornerRadius);

        // Every teardown path runs through Closed — hook it before anything else can fail, and
        // subscribe to the process-wide store last, so a half-constructed window can neither
        // linger in the active-panel map nor leave a dangling store subscription behind.
        Closed += OnWindowClosed;

        try
        {
            ApplyLocalization();
            PopulateSpotlight();
            RefreshList();

            HitokotoStore.Instance.Changed += OnStoreChanged;
        }
        catch
        {
            HitokotoStore.Instance.Changed -= OnStoreChanged;
            AbandonPanel(this);
            throw;
        }
    }

    private void ApplyLocalization()
    {
        if (IsChinese) return;

        Title = "Hitokoto · Quote Collection";
        HeaderTitleText.Text = "Hitokoto · Quote Collection";
        CurrentCategoryBadge.Text = "Quotes";
        OpenWebBtn.Content = "🌐 Web Details";
        SpotlightNextBtn.Content = "🎲 Next";
        CopyPlainBtn.Content = "📋 Copy Text";
        CopyMarkdownBtn.Content = "📝 Copy Markdown";
        SearchBox.Watermark = "Search bookmarked or recent quotes, sources, authors...";
        ClearHistoryBtn.Content = "Clear History";
    }

    public static void ShowPopup(
        HitokotoModel model,
        HitokotoItem currentItem,
        Point? screenCenter,
        Window? owner = null,
        Func<Task>? onRequestNextQuote = null,
        double? cornerRadius = null)
    {
        if (PanelCoolingDown<HitokotoPopupWindow>())
            return;

        if (TryCloseActivePanel<HitokotoPopupWindow>())
            return;

        var popup = new HitokotoPopupWindow(model, currentItem, screenCenter, onRequestNextQuote, cornerRadius);
        popup.ShowAsSecondaryPanel(owner);
    }

    private void OnStoreChanged(object? sender, EventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            currentItem = HitokotoStore.Instance.GetCurrentOrFallback(model);
            PopulateSpotlight();
            RefreshList();
        });
    }

    private void PopulateSpotlight()
    {
        bool isZh = IsChinese;
        CurrentCategoryBadge.Text = $"{currentItem.CategoryName} · #{currentItem.Id}";
        var creatorInfo = string.IsNullOrWhiteSpace(currentItem.Creator)
            ? ""
            : (isZh ? $" · 提交者: {currentItem.Creator}" : $" · By: {currentItem.Creator}");
        var shortUuid = string.IsNullOrWhiteSpace(currentItem.Uuid)
            ? (isZh ? "本地句子" : "Local quote")
            : (currentItem.Uuid.Length > 13 ? currentItem.Uuid[..13] + "…" : currentItem.Uuid);
        SpotlightMetaText.Text = $"UUID: {shortUuid}{creatorInfo}";
        SpotlightQuoteText.Text = $"“{currentItem.Hitokoto}”";
        SpotlightFromText.Text = currentItem.FormattedAttribution;

        var isLiked = HitokotoStore.Instance.IsLiked(currentItem.Uuid);
        SpotlightLikeBtn.Content = isLiked
            ? (isZh ? $"❤️ 已赞 ({currentItem.Likes})" : $"❤️ Liked ({currentItem.Likes})")
            : (isZh ? $"♡ 点赞 ({currentItem.Likes})" : $"♡ Like ({currentItem.Likes})");

        var isFav = HitokotoStore.Instance.IsFavorite(currentItem.Uuid);
        SpotlightFavBtn.Content = isFav
            ? (isZh ? "⭐ 已收藏" : "⭐ Bookmarked")
            : (isZh ? "☆ 收藏" : "☆ Bookmark");
    }

    private void RefreshList()
    {
        bool isZh = IsChinese;
        var favs = HitokotoStore.Instance.GetFavorites();
        var hist = HitokotoStore.Instance.GetHistory();

        TabFavorites.Content = isZh ? $"⭐ 本地收藏 ({favs.Count})" : $"⭐ Bookmarks ({favs.Count})";
        TabHistory.Content = isZh ? $"🕒 最近偶遇 ({hist.Count})" : $"🕒 History ({hist.Count})";
        ClearHistoryBtn.IsVisible = activeTab == "History" && hist.Count > 0;

        IEnumerable<HitokotoItem> source = activeTab == "Favorites" ? favs : hist;
        if (!string.IsNullOrWhiteSpace(searchQuery))
        {
            var q = searchQuery.Trim();
            source = source.Where(item =>
                item.Hitokoto.Contains(q, StringComparison.OrdinalIgnoreCase)
                || (item.From?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false)
                || (item.FromWho?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false)
                || item.CategoryName.Contains(q, StringComparison.OrdinalIgnoreCase));
        }

        var list = source.ToList();

        // The card list is only rebuilt when what it shows actually changed: a like toggled from
        // the desktop card (or a quote arriving in the background) used to tear down and recreate
        // every card while the popup was open, even though the list body was identical.
        var signature = BuildListSignature(list);
        if (signature == lastListSignature)
            return;
        lastListSignature = signature;

        ItemsStack.Children.Clear();

        if (list.Count == 0)
        {
            EmptyStateText.Text = activeTab == "Favorites"
                ? (isZh ? "暂无收藏句子，点击上方「☆ 收藏」即可收入摘抄本" : "No bookmarked quotes yet. Click '☆ Bookmark' above to save quotes.")
                : (isZh ? "暂无历史记录，双击桌面一言卡片即可偶遇新句子" : "No history yet. Double-click the quote widget on desktop to discover more.");
            EmptyStateText.IsVisible = true;
            return;
        }

        EmptyStateText.IsVisible = false;
        foreach (var item in list)
        {
            ItemsStack.Children.Add(BuildItemCard(item));
        }
    }

    /// <summary>
    /// Identity of everything the card list renders (tab, search text, and each item's key plus its
    /// bookmark state — likes are not shown on the cards). Used to skip a full rebuild.
    /// </summary>
    private string BuildListSignature(List<HitokotoItem> list)
    {
        var builder = new StringBuilder(activeTab.Length + searchQuery.Length + list.Count * 40);
        builder.Append(activeTab).Append('\u0001').Append(searchQuery).Append('\u0001').Append(list.Count);
        foreach (var item in list)
        {
            builder.Append('\u0002')
                   .Append(item.Uuid.Length > 0 ? item.Uuid : item.Hitokoto)
                   .Append(HitokotoStore.Instance.IsFavorite(item.Uuid) ? '+' : '-');
        }
        return builder.ToString();
    }

    private Control BuildItemCard(HitokotoItem item)
    {
        bool isZh = IsChinese;
        var card = new Border { Classes = { "quote-item-card" } };
        var grid = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,Auto"),
            ColumnDefinitions = new ColumnDefinitions("*,Auto")
        };

        var quoteText = new TextBlock
        {
            Text = item.Hitokoto,
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 19,
            Margin = new Thickness(0, 0, 8, 4)
        };
        Grid.SetRow(quoteText, 0);
        Grid.SetColumn(quoteText, 0);
        Grid.SetColumnSpan(quoteText, 2);

        var metaPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalAlignment = VerticalAlignment.Center
        };
        metaPanel.Children.Add(new Border
        {
            Background = new SolidColorBrush(Color.Parse("#20808080")),
            CornerRadius = new CornerRadius(5),
            Padding = new Thickness(5, 1),
            Child = new TextBlock
            {
                Text = item.CategoryName,
                FontSize = 10,
                Opacity = 0.75
            }
        });
        metaPanel.Children.Add(new TextBlock
        {
            Text = item.CompactAttribution,
            FontSize = 11,
            Opacity = 0.65,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 220
        });
        Grid.SetRow(metaPanel, 1);
        Grid.SetColumn(metaPanel, 0);

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            VerticalAlignment = VerticalAlignment.Center
        };

        var applyBtn = new Button
        {
            Classes = { "action-pill" },
            Content = isZh ? "展示" : "Show",
            FontSize = 11,
            Padding = new Thickness(7, 2)
        };
        ToolTip.SetTip(applyBtn, isZh ? "在桌面组件上展示此句" : "Display this quote on the desktop widget");
        applyBtn.Click += (_, _) =>
        {
            HitokotoStore.Instance.SelectQuote(item);
            ShowToast(isZh ? "已在桌面组件展示该句" : "Displayed on desktop widget");
        };

        var copyBtn = new Button
        {
            Classes = { "action-pill" },
            Content = isZh ? "复制" : "Copy",
            FontSize = 11,
            Padding = new Thickness(7, 2)
        };
        copyBtn.Click += async (_, _) =>
        {
            if (Clipboard != null)
            {
                await Clipboard.SetTextAsync(item.CopyPlainText);
                ShowToast(Locale.Hitokoto_Copied);
            }
        };

        var isFav = HitokotoStore.Instance.IsFavorite(item.Uuid);
        var favBtn = new Button
        {
            Classes = { "action-pill" },
            Content = isFav ? "⭐" : "☆",
            FontSize = 11,
            Padding = new Thickness(7, 2)
        };
        ToolTip.SetTip(favBtn, isFav
            ? (isZh ? "移出本地收藏" : "Remove from bookmarks")
            : (isZh ? "加入本地收藏" : "Add to bookmarks"));
        favBtn.Click += (_, _) =>
        {
            var nowFav = HitokotoStore.Instance.ToggleFavorite(item);
            ShowToast(nowFav ? Locale.Hitokoto_FavoritedToast : Locale.Hitokoto_UnfavoritedToast);
        };

        actions.Children.Add(applyBtn);
        actions.Children.Add(copyBtn);
        actions.Children.Add(favBtn);

        Grid.SetRow(actions, 1);
        Grid.SetColumn(actions, 1);

        grid.Children.Add(quoteText);
        grid.Children.Add(metaPanel);
        grid.Children.Add(actions);
        card.Child = grid;
        return card;
    }

    private async void OnNextQuoteClicked(object? sender, RoutedEventArgs e)
    {
        if (onRequestNextQuote != null)
        {
            await onRequestNextQuote();
        }
        else
        {
            var next = await HitokotoService.FetchQuoteAsync(model, currentItem.Uuid);
            HitokotoStore.Instance.SelectQuote(next);
        }
    }

    private async void OnLikeCurrentClicked(object? sender, RoutedEventArgs e)
    {
        if (HitokotoStore.Instance.IsLiked(currentItem.Uuid))
        {
            ShowToast(IsChinese ? "你已经为这句话点过赞啦 ❤️" : "Already liked ❤️");
            return;
        }

        currentItem = HitokotoStore.Instance.MarkLiked(currentItem);
        PopulateSpotlight();
        ShowToast(Locale.Hitokoto_LikedToast);
        await HitokotoService.SubmitLikeAsync(currentItem.Uuid);
    }

    private void OnToggleFavCurrentClicked(object? sender, RoutedEventArgs e)
    {
        var nowFav = HitokotoStore.Instance.ToggleFavorite(currentItem);
        PopulateSpotlight();
        RefreshList();
        ShowToast(nowFav ? Locale.Hitokoto_FavoritedToast : Locale.Hitokoto_UnfavoritedToast);
    }

    private async void OnCopyPlainClicked(object? sender, RoutedEventArgs e)
    {
        if (Clipboard != null)
        {
            await Clipboard.SetTextAsync(currentItem.CopyPlainText);
            ShowToast(Locale.Hitokoto_Copied);
        }
    }

    private async void OnCopyMarkdownClicked(object? sender, RoutedEventArgs e)
    {
        if (Clipboard != null)
        {
            await Clipboard.SetTextAsync(currentItem.CopyMarkdown);
            ShowToast(IsChinese ? "已复制 Markdown 引用格式" : "Copied as Markdown quote");
        }
    }

    private void OnOpenWebClicked(object? sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = currentItem.WebUrl,
                UseShellExecute = true
            });
        }
        catch
        {
            // Ignore shell open error
        }
    }

    private void OnTabClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string tab) return;
        activeTab = tab;
        TabFavorites.Classes.Set("active", activeTab == "Favorites");
        TabHistory.Classes.Set("active", activeTab == "History");
        RefreshList();
    }

    private void OnSearchTextChanged(object? sender, TextChangedEventArgs e)
    {
        searchQuery = SearchBox.Text ?? string.Empty;
        RefreshList();
    }

    private void OnClearHistoryClicked(object? sender, RoutedEventArgs e)
    {
        HitokotoStore.Instance.ClearHistory();
        ShowToast(IsChinese ? "已清空偶遇历史记录" : "History cleared");
    }

    private void ShowToast(string message)
    {
        ToastText.Text = message;
        ToastBanner.IsVisible = true;
        toastTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1800) };
        toastTimer.Stop();
        toastTimer.Tick -= OnToastTick;
        toastTimer.Tick += OnToastTick;
        toastTimer.Start();
    }

    private void OnToastTick(object? sender, EventArgs e)
    {
        toastTimer?.Stop();
        ToastBanner.IsVisible = false;
    }

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        HitokotoStore.Instance.Changed -= OnStoreChanged;
        toastTimer?.Stop();
    }
}
