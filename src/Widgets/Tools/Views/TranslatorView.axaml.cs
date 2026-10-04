using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Tools.Models;
using Tools.Services;
using Tools.Services.Translation;
using DeskCanvas.Core.Interfaces;
using DeskCanvas.Core.Models;
using DeskCanvas.Services;

namespace Tools.Views;

public partial class TranslatorView : UserControl, IWidgetSelfRefreshing
{
    /// <summary>Panel combos render items through ToString (compiled bindings can't see the record's properties).</summary>
    internal record LangOption(string Name, string ShortName, string Code)
    {
        public override string ToString() => Name;
    }

    internal record EngineOption(string FullName, string ShortName, string Code)
    {
        public override string ToString() => FullName;
    }

    internal static readonly List<LangOption> Languages =
    [
        new("自动检测", "自动", "auto"),
        new("简体中文", "中文", "zh"),
        new("英语", "英语", "en"),
        new("日语", "日语", "ja"),
        new("韩语", "韩语", "ko"),
        new("法语", "法语", "fr"),
        new("德语", "德语", "de"),
        new("西班牙语", "西语", "es"),
        new("俄语", "俄语", "ru")
    ];

    internal static readonly List<EngineOption> Engines =
    [
        new("有道 (国内直连)", "有道", "Youdao"),
        new("MyMemory (免Key)", "MyMem", "MyMemory"),
        new("百度翻译 (开放平台)", "百度", "Baidu"),
        new("DeepLX / 自定义", "DeepLX", "DeepLX")
    ];

    private TranslatorModel model;
    private readonly DispatcherTimer debounceTimer;
    private readonly DispatcherTimer toastTimer;
    private CancellationTokenSource? cts;
    private WidgetTier currentTier = (WidgetTier)(-1);

    private string currentSourceLang = "auto";
    private string currentTargetLang = "zh";
    private string currentEngine = "Youdao";

    public TranslatorView() : this(new TranslatorModel()) { }

    public TranslatorView(TranslatorModel model)
    {
        this.model = model;
        currentSourceLang = model.SourceLanguage;
        currentTargetLang = model.TargetLanguage;
        currentEngine = model.Engine;

        TranslationService.Instance.Configure(model);

        InitializeComponent();

        debounceTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(600)
        };
        debounceTimer.Tick += OnDebounceTick;

        toastTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1.5)
        };
        toastTimer.Tick += (_, _) =>
        {
            ToastBanner.IsVisible = false;
            toastTimer.Stop();
        };

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        SizeChanged += OnSizeChanged;
        LayoutUpdated += OnViewLayoutUpdated;

        InputTextBox.AddHandler(PointerWheelChangedEvent, OnTextBoxPointerWheel, RoutingStrategies.Bubble, true);
        OutputTextBox.AddHandler(PointerWheelChangedEvent, OnTextBoxPointerWheel, RoutingStrategies.Bubble, true);

        InputTextBox.TemplateApplied += (_, _) => ConfigureScrollers();
        OutputTextBox.TemplateApplied += (_, _) => ConfigureScrollers();

        ApplyTier(WidgetTier.Medium, force: true);
        RebuildMenus();
        UpdatePillLabels();
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        // A cached page (settings window) or a Gallery preview can be unloaded and then re-added to
        // the visual tree, so the handlers dropped in OnUnloaded must come back — otherwise a
        // re-added view stops reacting to resize and to the mouse wheel.
        SizeChanged -= OnSizeChanged;
        SizeChanged += OnSizeChanged;
        LayoutUpdated -= OnViewLayoutUpdated;
        LayoutUpdated += OnViewLayoutUpdated;
        InputTextBox.RemoveHandler(PointerWheelChangedEvent, OnTextBoxPointerWheel);
        InputTextBox.AddHandler(PointerWheelChangedEvent, OnTextBoxPointerWheel, RoutingStrategies.Bubble, true);
        OutputTextBox.RemoveHandler(PointerWheelChangedEvent, OnTextBoxPointerWheel);
        OutputTextBox.AddHandler(PointerWheelChangedEvent, OnTextBoxPointerWheel, RoutingStrategies.Bubble, true);

        var size = Bounds.Size;
        var tier = (size.Width > 0 && size.Height > 0) ? ResolveTier(size) : ResolveTierFromSpan();
        ApplyTier(tier, force: true);

        Dispatcher.UIThread.Post(ConfigureScrollers);
        RebuildMenus();
        UpdatePillLabels();
        UpdateAdaptiveCornerRadii();
        if (size.Width > 0)
        {
            AdaptControlBar();
        }
    }

    /// <summary>
    /// Every relayout can change what fits (tier switches flip pill labels between
    /// full and short names, locale changes reflow the text, …) — re-evaluate the
    /// control bar on each pass, coalesced to one run per layout batch.
    /// </summary>
    private void OnViewLayoutUpdated(object? sender, EventArgs e) => QueueBarAdaptation();

    private void QueueBarAdaptation()
    {
        if (barAdaptQueued) return;
        barAdaptQueued = true;
        // Background priority: runs after the current layout batch settles, so the
        // measurement below sees post-tier-switch visuals; bursts of layout events
        // (drag-resize fires this dozens of times) collapse into one adaptation.
        Dispatcher.UIThread.Post(() =>
        {
            barAdaptQueued = false;
            AdaptControlBar();
        }, DispatcherPriority.Background);
    }

    private void OnUnloaded(object? sender, RoutedEventArgs e)
    {
        debounceTimer.Stop();
        toastTimer.Stop();

        // Cancel *and* dispose: the CTS is replaced on every translation attempt, so leaving it
        // alive would keep its registration handles (and the request they cancel) unreclaimable.
        cts?.Cancel();
        cts?.Dispose();
        cts = null;

        SizeChanged -= OnSizeChanged;
        LayoutUpdated -= OnViewLayoutUpdated;
        InputTextBox.RemoveHandler(PointerWheelChangedEvent, OnTextBoxPointerWheel);
        OutputTextBox.RemoveHandler(PointerWheelChangedEvent, OnTextBoxPointerWheel);
    }

    private void OnSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        var size = e.NewSize;
        if (size.Width <= 0 || size.Height <= 0) return;

        var tier = ResolveTier(size);
        ApplyTier(tier);
        UpdateAdaptiveCornerRadii();
        AdaptControlBar();
    }

    /// <summary>Guards against re-entrant adaptation passes (layout events firing during one).</summary>
    private bool barAdaptQueued;

    /// <summary>
    /// 各按钮"可见时"的自然宽度缓存。IsVisible=false 的控件不参与测量、DesiredSize
    /// 会被清零——若直接读它算"恢复某档会多宽"，刚藏起来的按钮总显得不占地方，
    /// 每一轮都得出"level 0 也放得下"再把它亮出来，布局在 0/1 档间无限打转。
    /// </summary>
    private readonly Dictionary<Layoutable, double> barNaturalWidths = new();

    /// <summary>
    /// 控件栏测量驱动的渐进降级（替代旧的单一像素阈值）：按各降级档位计算控件栏的
    /// 自然宽度，选出能放下的最小档位一次性应用。必须整档一次算好——先恢复再隐藏
    /// 的试错会让每次翻动都失效布局，LayoutUpdated 再触发适配，无限打转。
    /// 像素阈值法对 locale 文案长度、字体缩放全盲；测量法对一切都成立。
    /// </summary>
    private void AdaptControlBar()
    {
        var available = ContentGrid.Bounds.Width;
        if (available <= 0) available = Math.Max(0, Bounds.Width - 24);
        if (available <= 0) return;

        bool isSmall = currentTier == WidgetTier.Small || currentTier == WidgetTier.Cell;
        CaptureBarNaturalWidths();

        var best = 4;
        for (int level = 0; level <= 4; level++)
        {
            if (MeasureControlBarNaturalWidth(level, isSmall) <= available + 0.5)
            {
                best = level;
                break;
            }
        }
        ApplyControlBarLevel(best, isSmall);
    }

    private void CaptureBarNaturalWidths()
    {
        foreach (var child in ControlBarGrid.Children)
        {
            if (child is Layoutable { IsVisible: true } element && element.DesiredSize.Width > 0)
                barNaturalWidths[element] = element.DesiredSize.Width + element.Margin.Left + element.Margin.Right;
        }
    }

    /// <summary>
    /// 降级序列：0 全量 → 1 藏引擎（引擎选择在二级面板里也有）→ 2 藏交换 →
    /// 3 藏立即翻译 → 4 藏展开。小档下"立即翻译"由 tier 样式隐藏，代码不写它的
    /// 本地值——本地值会永久压过样式，档位切换后就再也回不来了。
    /// </summary>
    private void ApplyControlBarLevel(int level, bool isSmall)
    {
        if (isSmall)
        {
            // 交还样式控制（tier-small 样式会把它藏掉）；本地值残留会让它在小档复活。
            if (TranslateBtn.IsVisible) TranslateBtn.ClearValue(IsVisibleProperty);
        }
        else
        {
            SetBarVisibility(TranslateBtn, level < 3);
        }

        SetBarVisibility(EngineBtn, level < 1);
        SetBarVisibility(SwapBtn, level < 2);
        SetBarVisibility(ExpandBtn, level < 4);
    }

    /// <summary>该档位下控件栏的自然宽度：可见成员的缓存宽度求和（缺失时退回 DesiredSize）。</summary>
    private double MeasureControlBarNaturalWidth(int level, bool isSmall)
    {
        double natural = 0;
        foreach (var child in ControlBarGrid.Children)
        {
            if (child is not Layoutable element) continue;
            if (!IsVisibleAtLevel(element, level, isSmall)) continue;
            natural += barNaturalWidths.TryGetValue(element, out var width)
                ? width
                : element.DesiredSize.Width + element.Margin.Left + element.Margin.Right;
        }
        return natural;
    }

    private bool IsVisibleAtLevel(Layoutable element, int level, bool isSmall)
    {
        if (ReferenceEquals(element, EngineBtn)) return level < 1;
        if (ReferenceEquals(element, SwapBtn)) return level < 2;
        if (ReferenceEquals(element, TranslateBtn)) return !isSmall && level < 3;
        if (ReferenceEquals(element, ExpandBtn)) return level < 4;
        return true;
    }

    private static void SetBarVisibility(Control control, bool visible)
    {
        if (control.IsVisible != visible) control.IsVisible = visible;
    }

    private WidgetTier ResolveTier(Size size)
    {
        // 1. Grid host span resolution if available
        try
        {
            var spanTier = SizeTiers.ResolveTier(this, size);
            if (spanTier is WidgetTier.Small or WidgetTier.Medium or WidgetTier.Large)
            {
                return spanTier;
            }
        }
        catch { }

        // 2. Direct size / aspect ratio resolution:
        // 4x4 (Large): size > 220px in both width and height
        if (size.Width > 220 && size.Height > 220)
            return WidgetTier.Large;

        // 4x2 (Medium): wide card (width > 220 && height <= 220, or aspect ratio >= 1.35)
        if (size.Width > 220 || size.Width > size.Height * 1.35)
            return WidgetTier.Medium;

        // 2x2 (Small): compact card
        return WidgetTier.Small;
    }

    private WidgetTier ResolveTierFromSpan()
    {
        try
        {
            var spanTier = SizeTiers.ResolveTier(this, null);
            if (spanTier is WidgetTier.Small or WidgetTier.Medium or WidgetTier.Large)
                return spanTier;
        }
        catch { }
        return WidgetTier.Medium;
    }

    private void ApplyTier(WidgetTier tier, bool force = false)
    {
        if (!force && tier == currentTier) return;
        currentTier = tier;

        bool isSmall = tier == WidgetTier.Small || tier == WidgetTier.Cell;
        bool isLarge = tier == WidgetTier.Large;
        bool isMedium = tier == WidgetTier.Medium;

        Classes.Set("tier-small", isSmall);
        Classes.Set("tier-medium", isMedium);
        Classes.Set("tier-large", isLarge);

        if (isMedium)
        {
            // 4x2 Medium Tier: Left half Input, Right half Output (左右双栏模式)
            ContentGrid.RowDefinitions = RowDefinitions.Parse("Auto,*");
            ContentGrid.ColumnDefinitions = ColumnDefinitions.Parse("*,*");

            Grid.SetRow(ControlBarGrid, 0);
            Grid.SetColumn(ControlBarGrid, 0);
            Grid.SetRowSpan(ControlBarGrid, 1);
            Grid.SetColumnSpan(ControlBarGrid, 2);
            ControlBarGrid.Margin = new Thickness(0, 0, 0, 4);

            Grid.SetRow(InputBoxBorder, 1);
            Grid.SetColumn(InputBoxBorder, 0);
            Grid.SetRowSpan(InputBoxBorder, 1);
            Grid.SetColumnSpan(InputBoxBorder, 1);
            InputBoxBorder.Margin = new Thickness(0, 0, 3, 0);

            Grid.SetRow(OutputBoxBorder, 1);
            Grid.SetColumn(OutputBoxBorder, 1);
            Grid.SetRowSpan(OutputBoxBorder, 1);
            Grid.SetColumnSpan(OutputBoxBorder, 1);
            OutputBoxBorder.Margin = new Thickness(3, 0, 0, 0);

            InputTextBox.TextWrapping = TextWrapping.Wrap;
            OutputTextBox.TextWrapping = TextWrapping.Wrap;
            ScrollViewer.SetHorizontalScrollBarVisibility(InputTextBox, ScrollBarVisibility.Disabled);
            ScrollViewer.SetHorizontalScrollBarVisibility(OutputTextBox, ScrollBarVisibility.Disabled);
            ScrollViewer.SetVerticalScrollBarVisibility(InputTextBox, ScrollBarVisibility.Auto);
            ScrollViewer.SetVerticalScrollBarVisibility(OutputTextBox, ScrollBarVisibility.Auto);

            InputTextBox.FontSize = 12;
            OutputTextBox.FontSize = 12;
        }
        else if (isLarge)
        {
            // 4x4 Large Tier: Vertical multi-line scroll mode (垂直多行上下滚动模式)
            ContentGrid.RowDefinitions = RowDefinitions.Parse("*,Auto,*");
            ContentGrid.ColumnDefinitions = ColumnDefinitions.Parse("*,*");

            Grid.SetRow(InputBoxBorder, 0);
            Grid.SetColumn(InputBoxBorder, 0);
            Grid.SetRowSpan(InputBoxBorder, 1);
            Grid.SetColumnSpan(InputBoxBorder, 2);
            InputBoxBorder.Margin = new Thickness(0, 0, 0, 3);

            Grid.SetRow(ControlBarGrid, 1);
            Grid.SetColumn(ControlBarGrid, 0);
            Grid.SetRowSpan(ControlBarGrid, 1);
            Grid.SetColumnSpan(ControlBarGrid, 2);
            ControlBarGrid.Margin = new Thickness(0, 4, 0, 4);

            Grid.SetRow(OutputBoxBorder, 2);
            Grid.SetColumn(OutputBoxBorder, 0);
            Grid.SetRowSpan(OutputBoxBorder, 1);
            Grid.SetColumnSpan(OutputBoxBorder, 2);
            OutputBoxBorder.Margin = new Thickness(0, 3, 0, 0);

            InputTextBox.TextWrapping = TextWrapping.Wrap;
            OutputTextBox.TextWrapping = TextWrapping.Wrap;
            ScrollViewer.SetHorizontalScrollBarVisibility(InputTextBox, ScrollBarVisibility.Disabled);
            ScrollViewer.SetHorizontalScrollBarVisibility(OutputTextBox, ScrollBarVisibility.Disabled);
            ScrollViewer.SetVerticalScrollBarVisibility(InputTextBox, ScrollBarVisibility.Auto);
            ScrollViewer.SetVerticalScrollBarVisibility(OutputTextBox, ScrollBarVisibility.Auto);

            InputTextBox.FontSize = 13.5;
            OutputTextBox.FontSize = 13.5;
        }
        else
        {
            // 2x2 Small Tier: Compact vertical cards, horizontal single-line scroll mode
            ContentGrid.RowDefinitions = RowDefinitions.Parse("*,Auto,*");
            ContentGrid.ColumnDefinitions = ColumnDefinitions.Parse("*,*");

            Grid.SetRow(InputBoxBorder, 0);
            Grid.SetColumn(InputBoxBorder, 0);
            Grid.SetRowSpan(InputBoxBorder, 1);
            Grid.SetColumnSpan(InputBoxBorder, 2);
            InputBoxBorder.Margin = new Thickness(0, 0, 0, 1);

            Grid.SetRow(ControlBarGrid, 1);
            Grid.SetColumn(ControlBarGrid, 0);
            Grid.SetRowSpan(ControlBarGrid, 1);
            Grid.SetColumnSpan(ControlBarGrid, 2);
            ControlBarGrid.Margin = new Thickness(0, 1, 0, 1);

            Grid.SetRow(OutputBoxBorder, 2);
            Grid.SetColumn(OutputBoxBorder, 0);
            Grid.SetRowSpan(OutputBoxBorder, 1);
            Grid.SetColumnSpan(OutputBoxBorder, 2);
            OutputBoxBorder.Margin = new Thickness(0, 1, 0, 0);

            InputTextBox.TextWrapping = TextWrapping.NoWrap;
            OutputTextBox.TextWrapping = TextWrapping.NoWrap;
            ScrollViewer.SetHorizontalScrollBarVisibility(InputTextBox, ScrollBarVisibility.Auto);
            ScrollViewer.SetHorizontalScrollBarVisibility(OutputTextBox, ScrollBarVisibility.Auto);
            ScrollViewer.SetVerticalScrollBarVisibility(InputTextBox, ScrollBarVisibility.Auto);
            ScrollViewer.SetVerticalScrollBarVisibility(OutputTextBox, ScrollBarVisibility.Auto);

            InputTextBox.FontSize = 11;
            OutputTextBox.FontSize = 11;
        }

        ConfigureScrollers();
        UpdatePillLabels();
        UpdateAdaptiveCornerRadii();
    }

    /// <summary>
    /// Adaptively match the inner textboxes and buttons to the host widget's corner radius (Apple HIG concentric curvature).
    /// </summary>
    private void UpdateAdaptiveCornerRadii()
    {
        CornerRadius? targetInner = null;
        CornerRadius? targetPill = null;

        var widget = this.FindAncestorOfType<DeskCanvas.Views.Widget>();
        if (widget != null)
        {
            targetInner = widget.InnerRadius;
            targetPill = widget.PillRadius;
        }
        else if (this.TryFindResource("WidgetInnerCornerRadius", ActualThemeVariant, out var ir) && ir is CornerRadius innerCr)
        {
            targetInner = innerCr;
            if (this.TryFindResource("WidgetPillCornerRadius", ActualThemeVariant, out var pr) && pr is CornerRadius pillCr)
            {
                targetPill = pillCr;
            }
        }

        if (targetInner.HasValue)
        {
            InputBoxBorder.CornerRadius = targetInner.Value;
            OutputBoxBorder.CornerRadius = targetInner.Value;
        }

        if (targetPill.HasValue)
        {
            SourceLangBtn.CornerRadius = targetPill.Value;
            TargetLangBtn.CornerRadius = targetPill.Value;
            EngineBtn.CornerRadius = targetPill.Value;
        }
    }

    private void ConfigureScrollers()
    {
        bool isSmall = currentTier == WidgetTier.Small || currentTier == WidgetTier.Cell;

        var inScroller = GetScroller(InputTextBox);
        if (inScroller != null)
        {
            inScroller.HorizontalScrollBarVisibility = isSmall ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled;
            inScroller.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        }

        var outScroller = GetScroller(OutputTextBox);
        if (outScroller != null)
        {
            outScroller.HorizontalScrollBarVisibility = isSmall ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled;
            outScroller.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        }
    }

    private void UpdatePillLabels()
    {
        bool isSmall = currentTier == WidgetTier.Small || currentTier == WidgetTier.Cell;
        bool isMedium = currentTier == WidgetTier.Medium;

        var src = Languages.FirstOrDefault(l => l.Code.Equals(currentSourceLang, StringComparison.OrdinalIgnoreCase)) ?? Languages[0];
        var tgt = Languages.FirstOrDefault(l => l.Code.Equals(currentTargetLang, StringComparison.OrdinalIgnoreCase)) ?? Languages[1];
        var eng = Engines.FirstOrDefault(e => e.Code.Equals(currentEngine, StringComparison.OrdinalIgnoreCase)) ?? Engines[0];

        SourceLangText.Text = isSmall ? src.ShortName : src.Name;
        TargetLangText.Text = isSmall ? tgt.ShortName : tgt.Name;
        EngineText.Text = isSmall ? GetCompactEngineName(eng.Code) : (isMedium ? eng.ShortName : eng.FullName);

        // 标签换了自然宽度就变了，清掉让下一轮适配重新采集（可见时立即采集，
        // 隐藏时等恢复后再采——期间旧缓存只影响一轮的档位估计）。
        barNaturalWidths.Remove(SourceLangBtn);
        barNaturalWidths.Remove(TargetLangBtn);
        barNaturalWidths.Remove(EngineBtn);
    }

    private static string GetCompactEngineName(string engineCode) => engineCode switch
    {
        "Youdao" => "有道",
        "Baidu" => "百度",
        "MyMemory" => "MyM",
        "DeepLX" => "DLX",
        _ => "引擎"
    };

    private void RebuildMenus()
    {
        // 1. Source Lang Menu
        SourceLangMenu.ItemsSource = null;
        var srcItems = new List<MenuItem>();
        foreach (var lang in Languages)
        {
            bool isSelected = lang.Code.Equals(currentSourceLang, StringComparison.OrdinalIgnoreCase);
            var item = new MenuItem
            {
                Header = isSelected ? $"✓ {lang.Name}" : $"   {lang.Name}",
                Tag = lang.Code
            };
            item.Click += OnSelectSourceLang;
            srcItems.Add(item);
        }
        SourceLangMenu.ItemsSource = srcItems;

        // 2. Target Lang Menu (Target does not include "auto")
        TargetLangMenu.ItemsSource = null;
        var tgtItems = new List<MenuItem>();
        foreach (var lang in Languages.Where(l => l.Code != "auto"))
        {
            bool isSelected = lang.Code.Equals(currentTargetLang, StringComparison.OrdinalIgnoreCase);
            var item = new MenuItem
            {
                Header = isSelected ? $"✓ {lang.Name}" : $"   {lang.Name}",
                Tag = lang.Code
            };
            item.Click += OnSelectTargetLang;
            tgtItems.Add(item);
        }
        TargetLangMenu.ItemsSource = tgtItems;

        // 3. Engine Menu
        EngineMenu.ItemsSource = null;
        var engItems = new List<MenuItem>();
        foreach (var eng in Engines)
        {
            bool isSelected = eng.Code.Equals(currentEngine, StringComparison.OrdinalIgnoreCase);
            var item = new MenuItem
            {
                Header = isSelected ? $"✓ {eng.FullName}" : $"   {eng.FullName}",
                Tag = eng.Code
            };
            item.Click += OnSelectEngine;
            engItems.Add(item);
        }
        EngineMenu.ItemsSource = engItems;
    }

    private void OnMenuBtnClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.ContextMenu != null)
        {
            RebuildMenus();
            btn.ContextMenu.Open(btn);
        }
    }

    private void OnSelectSourceLang(object? sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: string code })
        {
            currentSourceLang = code;
            UpdatePillLabels();
            RebuildMenus();
            TriggerTranslationIfHasText();
        }
    }

    private void OnSelectTargetLang(object? sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: string code })
        {
            currentTargetLang = code;
            UpdatePillLabels();
            RebuildMenus();
            TriggerTranslationIfHasText();
        }
    }

    private void OnSelectEngine(object? sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: string code })
        {
            currentEngine = code;
            UpdatePillLabels();
            RebuildMenus();
            TriggerTranslationIfHasText();
        }
    }

    public void Refresh(WidgetLayout layout)
    {
        if (layout.Settings is not { } settings || settings.ValueKind != JsonValueKind.Object)
            return;

        try
        {
            var updated = settings.Deserialize<TranslatorModel>();
            if (updated != null)
            {
                model = updated;
                currentSourceLang = model.SourceLanguage;
                currentTargetLang = model.TargetLanguage;
                currentEngine = model.Engine;
                TranslationService.Instance.Configure(model);
                UpdatePillLabels();
                RebuildMenus();
            }
        }
        catch { }
    }

    private void OnInputTextChanged(object? sender, TextChangedEventArgs e)
    {
        var text = InputTextBox.Text ?? string.Empty;
        CharCountText.Text = $"{text.Length} 字符";

        if (string.IsNullOrWhiteSpace(text))
        {
            OutputTextBox.Text = string.Empty;
            StatusText.Text = string.Empty;
            debounceTimer.Stop();
            return;
        }

        if (model.AutoTranslate)
        {
            debounceTimer.Stop();
            debounceTimer.Start();
        }
    }

    private void OnDebounceTick(object? sender, EventArgs e)
    {
        debounceTimer.Stop();
        _ = ExecuteTranslateAsync();
    }

    private void OnInputKeyUp(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && !e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            e.Handled = true;
            debounceTimer.Stop();
            _ = ExecuteTranslateAsync();
        }
    }

    private void OnTranslateClicked(object? sender, RoutedEventArgs e)
    {
        debounceTimer.Stop();
        _ = ExecuteTranslateAsync();
    }

    private void TriggerTranslationIfHasText()
    {
        if (!string.IsNullOrWhiteSpace(InputTextBox.Text))
        {
            debounceTimer.Stop();
            _ = ExecuteTranslateAsync();
        }
    }

    private async Task ExecuteTranslateAsync()
    {
        var text = InputTextBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(text)) return;

        cts?.Cancel();
        // Every debounced attempt replaces the previous source; disposing it releases the timer/registration
        // handles it owns. A CancellationTokenSource is not reclaimable until it is disposed.
        cts?.Dispose();
        var requestCts = new CancellationTokenSource();
        cts = requestCts;
        var token = requestCts.Token;

        bool isSmall = currentTier == WidgetTier.Small || currentTier == WidgetTier.Cell;
        LoadingText.IsVisible = !isSmall;
        StatusText.Text = isSmall ? "翻译中" : "请求中...";

        try
        {
            var result = await TranslationService.Instance.TranslateAsync(text, currentSourceLang, currentTargetLang, currentEngine, token);
            if (!token.IsCancellationRequested)
            {
                OutputTextBox.Text = result;
                StatusText.Text = isSmall ? "完成" : $"完成 ({currentEngine})";
            }
        }
        catch (OperationCanceledException)
        {
            // Cancelled
        }
        catch (Exception ex)
        {
            if (!token.IsCancellationRequested)
            {
                StatusText.Text = "翻译失败";
                OutputTextBox.Text = $"[错误] {ex.Message}";
            }
        }
        finally
        {
            if (!token.IsCancellationRequested)
            {
                LoadingText.IsVisible = false;
            }

            // This attempt is over, so its source is released here as well — otherwise the last source
            // of a view that never unloads again would stay alive for the lifetime of the widget.
            requestCts.Dispose();
            if (ReferenceEquals(cts, requestCts)) cts = null;
        }
    }

    private void OnSwapClicked(object? sender, RoutedEventArgs e)
    {
        // Determine new languages
        string newSource;
        string newTarget;

        if (currentSourceLang == "auto")
        {
            newSource = currentTargetLang;
            newTarget = currentTargetLang == "zh" ? "en" : "zh";
        }
        else
        {
            newSource = currentTargetLang;
            newTarget = currentSourceLang;
        }

        currentSourceLang = newSource;
        currentTargetLang = newTarget;

        UpdatePillLabels();
        RebuildMenus();

        // Swap texts
        var oldInput = InputTextBox.Text;
        var oldOutput = OutputTextBox.Text;

        if (!string.IsNullOrWhiteSpace(oldOutput) && !oldOutput.StartsWith("[错误]"))
        {
            InputTextBox.Text = oldOutput;
            OutputTextBox.Text = oldInput;
            _ = ExecuteTranslateAsync();
        }
    }

    private void OnClearInputClicked(object? sender, RoutedEventArgs e)
    {
        InputTextBox.Text = string.Empty;
        OutputTextBox.Text = string.Empty;
        StatusText.Text = string.Empty;
    }

    private void OnCopyOutputClicked(object? sender, RoutedEventArgs e)
    {
        var result = OutputTextBox.Text;
        if (!string.IsNullOrWhiteSpace(result))
        {
            ClipboardNative.SetText(result);
            ShowToast("已复制译文到剪贴板");
        }
    }

    private void ShowToast(string message)
    {
        ToastText.Text = message;
        ToastBanner.IsVisible = true;
        toastTimer.Stop();
        toastTimer.Start();
    }

    // ---- 二级面板（大窗翻译）：与 ClipboardPopupWindow 相同的宿主复用路径 ----

    /// <summary>Live state handed to the secondary panel when it opens.</summary>
    internal TranslatorModel PanelModel => model;

    internal (string Source, string Target, string Engine) PanelPreferences =>
        (currentSourceLang, currentTargetLang, currentEngine);

    internal string PanelInput => InputTextBox.Text ?? string.Empty;

    internal string PanelOutput => OutputTextBox.Text ?? string.Empty;

    /// <summary>
    /// Pull the panel's final input/output back into the widget. Setting the input text
    /// re-arms the auto-translate debounce, so it is stopped right after — the panel
    /// already produced the result shown here.
    /// </summary>
    internal void ApplyPanelResult(string input, string output)
    {
        InputTextBox.Text = input;
        OutputTextBox.Text = output;
        debounceTimer.Stop();
    }

    internal void ApplyPanelPreferences(string source, string target, string engine)
    {
        currentSourceLang = source;
        currentTargetLang = target;
        currentEngine = engine;
        UpdatePillLabels();
        RebuildMenus();
    }

    /// <summary>
    /// The host widget's visual corner radius, so the panel's corners and its glass match
    /// the widget card exactly. Null keeps the panel's own default radius.
    /// </summary>
    private double? SpawnCornerRadius
    {
        get
        {
            var r = (VisualRoot as DeskCanvas.Views.Widget)?.Radius.TopLeft ?? 0;
            return r > 0 ? r : null;
        }
    }

    public void OnOpenPanelClicked(object? sender, RoutedEventArgs e)
    {
        // A pending auto-translate would fire under the open panel; the panel owns the
        // editing session now.
        debounceTimer.Stop();
        var (screenCenter, _) = GetScreenCenterAndTopLevel();
        var owner = VisualRoot as Window;
        TranslatorPopupWindow.ShowPopup(screenCenter, owner, SpawnCornerRadius, this);
    }

    private (Point? ScreenCenter, TopLevel? TopLevel) GetScreenCenterAndTopLevel()
    {
        if (VisualRoot is Visual rootVisual && VisualRoot is TopLevel topLevel)
        {
            var bounds = Bounds;
            var centerLocal = new Point(bounds.Width / 2, bounds.Height / 2);
            var rootPoint = this.TranslatePoint(centerLocal, rootVisual);
            if (rootPoint.HasValue)
            {
                var screenPoint = topLevel.PointToScreen(rootPoint.Value);
                return (new Point(screenPoint.X, screenPoint.Y), topLevel);
            }
        }
        return (null, null);
    }

    private ScrollViewer? inputScroller;
    private ScrollViewer? outputScroller;

    private ScrollViewer? GetScroller(TextBox textBox)
    {
        if (textBox == InputTextBox)
            return inputScroller ??= textBox.FindDescendantOfType<ScrollViewer>();
        if (textBox == OutputTextBox)
            return outputScroller ??= textBox.FindDescendantOfType<ScrollViewer>();
        return textBox.FindDescendantOfType<ScrollViewer>();
    }

    private void OnTextBoxPointerWheel(object? sender, PointerWheelEventArgs e)
    {
        if (sender is not TextBox textBox) return;

        var scroller = GetScroller(textBox);
        if (scroller == null) return;

        var maxX = Math.Max(0, scroller.Extent.Width - scroller.Viewport.Width);
        var maxY = Math.Max(0, scroller.Extent.Height - scroller.Viewport.Height);

        // In 4x4 (Large) and 4x2 (Medium): vertical scrolling mode (上下滚动模式)
        if (currentTier != WidgetTier.Small && currentTier != WidgetTier.Cell)
        {
            if (maxY > 0 && e.Delta.Y != 0)
            {
                var newY = Math.Clamp(scroller.Offset.Y - e.Delta.Y * 40.0, 0, maxY);
                scroller.Offset = new Vector(scroller.Offset.X, newY);
                e.Handled = true;
            }
            return;
        }

        // In 2x2 (Small): left-right horizontal scrolling mode (左右滚动模式)
        // If Shift is pressed and vertical scrolling is possible, allow vertical scrolling
        if (e.KeyModifiers.HasFlag(KeyModifiers.Shift) && maxY > 0)
        {
            var vDelta = e.Delta.Y;
            if (vDelta != 0)
            {
                var newY = Math.Clamp(scroller.Offset.Y - vDelta * 40.0, 0, maxY);
                scroller.Offset = new Vector(scroller.Offset.X, newY);
                e.Handled = true;
            }
            return;
        }

        // Horizontal scrolling takes priority in 2x2 / 4x2
        if (maxX > 0)
        {
            var delta = e.Delta.Y != 0 ? e.Delta.Y : -e.Delta.X;
            if (delta == 0) return;

            // "鼠标上滚（手指往前）则左移（对应滚动条左移）":
            // Wheel up (delta > 0, finger forward) -> scroll left (Offset.X decreases, scrollbar moves left).
            // Wheel down (delta < 0, finger backward) -> scroll right (Offset.X increases, scrollbar moves right).
            const double scrollStep = 50.0;
            var targetX = Math.Clamp(scroller.Offset.X - delta * scrollStep, 0, maxX);
            scroller.Offset = new Vector(targetX, scroller.Offset.Y);
            e.Handled = true;
            return;
        }

        // Fallback to vertical scroll if no horizontal overflow exists
        if (maxY > 0 && e.Delta.Y != 0)
        {
            var newY = Math.Clamp(scroller.Offset.Y - e.Delta.Y * 40.0, 0, maxY);
            scroller.Offset = new Vector(scroller.Offset.X, newY);
            e.Handled = true;
        }
    }
}
