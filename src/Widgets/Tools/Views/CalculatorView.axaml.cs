using System;
using System.Linq;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DeskCanvas.Core.Interfaces;
using DeskCanvas.Core.Models;
using DeskCanvas.Services;
using Tools.Locales;
using Tools.Models;
using Tools.Services;

namespace Tools.Views;

public partial class CalculatorView : UserControl, IWidgetSelfRefreshing
{
    private CalculatorModel model;
    private readonly DispatcherTimer toastTimer;
    private WidgetTier currentTier = (WidgetTier)(-1);
    private bool suppressTextEvent;

    public CalculatorView() : this(new CalculatorModel()) { }

    public CalculatorView(CalculatorModel model)
    {
        this.model = model ?? new CalculatorModel();
        CalculatorStore.Instance.Configure(this.model);

        InitializeComponent();

        toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.4) };
        toastTimer.Tick += (_, _) =>
        {
            ToastBanner.IsVisible = false;
            toastTimer.Stop();
        };

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        SizeChanged += OnSizeChanged;

        ApplyTier(WidgetTier.Small, force: true);
        SyncFromStore();
    }

    public void Refresh(WidgetLayout layout)
    {
        if (layout.Settings is { ValueKind: JsonValueKind.Object } settings)
        {
            try
            {
                var updated = settings.Deserialize<CalculatorModel>();
                if (updated != null)
                {
                    model = updated;
                    CalculatorStore.Instance.Configure(model);
                }
            }
            catch { }
        }

        var size = Bounds.Size;
        var tier = (size.Width > 0 && size.Height > 0) ? ResolveTier(size) : ResolveTierFromSpan();
        ApplyTier(tier, force: true);
        SyncFromStore();
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        SizeChanged -= OnSizeChanged;
        SizeChanged += OnSizeChanged;
        CalculatorStore.Instance.StateChanged -= OnStoreStateChanged;
        CalculatorStore.Instance.StateChanged += OnStoreStateChanged;

        var size = Bounds.Size;
        var tier = (size.Width > 0 && size.Height > 0) ? ResolveTier(size) : ResolveTierFromSpan();
        ApplyTier(tier, force: true);
        UpdateAdaptiveCornerRadii();
        SyncFromStore();
        AdaptSmallControls(size);
    }

    private void OnUnloaded(object? sender, RoutedEventArgs e)
    {
        toastTimer.Stop();
        SizeChanged -= OnSizeChanged;
        CalculatorStore.Instance.StateChanged -= OnStoreStateChanged;
    }

    private void OnSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        var size = e.NewSize;
        if (size.Width <= 0 || size.Height <= 0) return;

        var tier = ResolveTier(size);
        ApplyTier(tier);
        UpdateAdaptiveCornerRadii();
        AdaptSmallControls(size);
    }

    private WidgetTier ResolveTier(Size size)
    {
        try
        {
            var spanTier = SizeTiers.ResolveTier(this, size);
            if (spanTier is WidgetTier.Small or WidgetTier.Medium or WidgetTier.Large)
                return spanTier;
        }
        catch { }

        if (size.Width > 220 && size.Height > 220)
            return WidgetTier.Large;
        if (size.Width > 220 || size.Width > size.Height * 1.35)
            return WidgetTier.Medium;
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
        return WidgetTier.Small;
    }

    private void ApplyTier(WidgetTier tier, bool force = false)
    {
        if (!force && tier == currentTier) return;
        currentTier = tier;

        bool isSmall = tier == WidgetTier.Small || tier == WidgetTier.Cell;
        bool isMedium = tier == WidgetTier.Medium;
        bool isLarge = tier == WidgetTier.Large;

        Classes.Set("tier-small", isSmall);
        Classes.Set("tier-medium", isMedium);
        Classes.Set("tier-large", isLarge);

        SmallLayout.IsVisible = isSmall;
        MediumLayout.IsVisible = isMedium;
        LargeLayout.IsVisible = isLarge;

        UpdateAdaptiveCornerRadii();
    }

    private void AdaptSmallControls(Size size)
    {
        if (size.Width <= 0 || size.Height <= 0) return;
        bool isSmall = currentTier == WidgetTier.Small || currentTier == WidgetTier.Cell;
        if (!isSmall) return;

        // Progressive width adaptation so the secondary-panel expand button (⤢) is never clipped
        SmallClearBtn.IsVisible = size.Width >= 155;
        SmallCopyBtn.IsVisible = size.Width >= 130;
        SmallTitleText.IsVisible = size.Width >= 115;

        // Progressive height adaptation for the history strip in 2x2
        SmallHistoryList.IsVisible = size.Height >= 125;
        RebuildHistoryViews(size.Height);
    }

    private void UpdateAdaptiveCornerRadii()
    {
        CornerRadius? targetInner = null;
        var widget = this.FindAncestorOfType<DeskCanvas.Views.Widget>();
        if (widget != null)
        {
            targetInner = widget.InnerRadius;
        }
        else if (this.TryFindResource("WidgetInnerCornerRadius", ActualThemeVariant, out var ir) && ir is CornerRadius innerCr)
        {
            targetInner = innerCr;
        }

        if (targetInner.HasValue)
        {
            SmallDisplayCard.CornerRadius = targetInner.Value;
            MediumDisplayCard.CornerRadius = targetInner.Value;
            LargeDisplayCard.CornerRadius = targetInner.Value;
            LargeHistoryContainer.CornerRadius = targetInner.Value;
        }
    }

    private void OnStoreStateChanged() => SyncFromStore();

    private void SyncFromStore()
    {
        var store = CalculatorStore.Instance;
        suppressTextEvent = true;

        if (!SmallExpressionBox.IsFocused)
            SmallExpressionBox.Text = store.CurrentExpression;
        if (!MedExpressionBox.IsFocused)
            MedExpressionBox.Text = store.CurrentExpression;
        if (!LargeExpressionBox.IsFocused)
            LargeExpressionBox.Text = store.CurrentExpression;

        SmallResultText.Text = store.CurrentResult;
        MedResultText.Text = store.CurrentResult;
        LargeResultText.Text = store.CurrentResult;

        string angleLabel = store.IsDegrees ? "DEG" : "RAD";
        MedAngleBtn.Content = angleLabel;
        LargeAngleBtn.Content = angleLabel;

        suppressTextEvent = false;
        RebuildHistoryViews(Bounds.Height);
    }

    private static bool IsZh => System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.Equals("zh", StringComparison.OrdinalIgnoreCase);

    private void RebuildHistoryViews(double availableHeight)
    {
        var items = CalculatorStore.Instance.GetHistory();
        LargeHistoryModeBtn.Content = IsZh ? $"历史 ({items.Count})" : $"History ({items.Count})";

        // 1. 2x2 Small History Strip (up to 2 items)
        SmallHistoryList.Children.Clear();
        int maxSmallItems = availableHeight > 0 && availableHeight < 160 ? 1 : 2;
        foreach (var item in items.Take(maxSmallItems))
        {
            var row = new Border { Classes = { "mini-hist-row" } };
            var g = new Grid { ColumnDefinitions = ColumnDefinitions.Parse("*,Auto") };
            g.Children.Add(new TextBlock
            {
                Text = item.Expression,
                FontSize = 10.5,
                Opacity = 0.65,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(0, 0, 6, 0)
            });
            var valBlock = new TextBlock
            {
                Text = item.Result,
                FontSize = 11,
                FontWeight = FontWeight.SemiBold,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(valBlock, 1);
            g.Children.Add(valBlock);
            row.Child = g;
            row.PointerPressed += (_, _) => OnHistoryItemClicked(item);
            SmallHistoryList.Children.Add(row);
        }

        if (items.Count == 0 && (availableHeight <= 0 || availableHeight >= 150))
        {
            var hintRow = new Border { Classes = { "mini-hist-row" }, Opacity = 0.6 };
            hintRow.Child = new TextBlock
            {
                Text = IsZh ? "按 Enter 保存记录 · 右上角展开面板" : "Press Enter to save · Expand panel top-right",
                FontSize = 10,
                HorizontalAlignment = HorizontalAlignment.Center
            };
            hintRow.PointerPressed += OnTitleOpenPanelPressed;
            SmallHistoryList.Children.Add(hintRow);
        }

        // 2. 4x2 Medium History Strip (up to 2 items)
        MedHistoryList.Children.Clear();
        foreach (var item in items.Take(2))
        {
            var g = new Grid
            {
                ColumnDefinitions = ColumnDefinitions.Parse("*,Auto"),
                Cursor = new Cursor(StandardCursorType.Hand),
                Background = Brushes.Transparent
            };
            g.Children.Add(new TextBlock
            {
                Text = item.Expression,
                FontSize = 10.5,
                Opacity = 0.6,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(0, 0, 6, 0)
            });
            var r = new TextBlock
            {
                Text = item.Result,
                FontSize = 11,
                FontWeight = FontWeight.SemiBold
            };
            Grid.SetColumn(r, 1);
            g.Children.Add(r);
            g.PointerPressed += (_, _) => OnHistoryItemClicked(item);
            MedHistoryList.Children.Add(g);
        }
        if (items.Count == 0)
        {
            MedHistoryList.Children.Add(new TextBlock
            {
                Text = Locale.Tools_Calculator_EmptyHistory,
                FontSize = 10,
                Opacity = 0.45
            });
        }

        // 3. 4x4 Large Display Tape Preview (latest 1-2 items)
        LargeTapePreviewList.Children.Clear();
        foreach (var item in items.Take(2).Reverse())
        {
            var g = new Grid
            {
                ColumnDefinitions = ColumnDefinitions.Parse("*,Auto"),
                Cursor = new Cursor(StandardCursorType.Hand),
                Background = Brushes.Transparent
            };
            g.Children.Add(new TextBlock
            {
                Text = item.Expression,
                FontSize = 11,
                Opacity = 0.55,
                TextTrimming = TextTrimming.CharacterEllipsis
            });
            var r = new TextBlock
            {
                Text = $"= {item.Result}",
                FontSize = 11,
                FontWeight = FontWeight.Medium,
                Opacity = 0.75,
                Margin = new Thickness(6, 0, 0, 0)
            };
            Grid.SetColumn(r, 1);
            g.Children.Add(r);
            g.PointerPressed += (_, _) => OnHistoryItemClicked(item);
            LargeTapePreviewList.Children.Add(g);
        }

        // 4. 4x4 Large Full History List
        LargeFullHistoryList.Children.Clear();
        foreach (var item in items)
        {
            var row = new Border
            {
                Classes = { "mini-hist-row" },
                Padding = new Thickness(8, 5)
            };
            var g = new Grid { ColumnDefinitions = ColumnDefinitions.Parse("*,Auto") };
            g.Children.Add(new TextBlock
            {
                Text = $"{item.Expression} =",
                FontSize = 11.5,
                Opacity = 0.65,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(0, 0, 8, 0)
            });
            var r = new TextBlock
            {
                Text = item.Result,
                FontSize = 13.5,
                FontWeight = FontWeight.Bold,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(r, 1);
            g.Children.Add(r);
            row.Child = g;
            row.PointerPressed += (_, _) =>
            {
                OnHistoryItemClicked(item);
                OnLargeKeypadModeClicked(null, new RoutedEventArgs());
            };
            LargeFullHistoryList.Children.Add(row);
        }
    }

    private void OnHistoryItemClicked(CalculatorHistoryItem item)
    {
        if (CalculatorStore.Instance.Settings.ClickHistoryToCopy)
        {
            CopyResultWithToast(item.Result);
        }
        else
        {
            SetActiveExpression(item.Expression, item.Result);
        }
    }

    // ---- Input & Evaluation Handlers ----

    private TextBox ActiveExpressionBox => currentTier switch
    {
        WidgetTier.Medium => MedExpressionBox,
        WidgetTier.Large => LargeExpressionBox,
        _ => SmallExpressionBox,
    };

    private void OnSmallExpressionChanged(object? sender, TextChangedEventArgs e)
    {
        if (suppressTextEvent) return;
        EvaluateLive(SmallExpressionBox.Text);
    }

    private void OnMedExpressionChanged(object? sender, TextChangedEventArgs e)
    {
        if (suppressTextEvent) return;
        EvaluateLive(MedExpressionBox.Text);
    }

    private void OnLargeExpressionChanged(object? sender, TextChangedEventArgs e)
    {
        if (suppressTextEvent) return;
        EvaluateLive(LargeExpressionBox.Text);
    }

    private void EvaluateLive(string? expr)
    {
        var text = expr ?? string.Empty;
        var store = CalculatorStore.Instance;

        if (string.IsNullOrWhiteSpace(text))
        {
            store.UpdateLiveSession(string.Empty, "0", this);
            return;
        }

        if (CalculatorEngine.TryEvaluate(text, store.IsDegrees, store.LastAns, out double val, out _))
        {
            string formatted = CalculatorEngine.FormatNumber(val, store.Settings.DecimalPlaces, store.Settings.UseThousandsSeparator);
            store.UpdateLiveSession(text, formatted, this);
        }
        else
        {
            store.UpdateLiveSession(text, store.CurrentResult, this);
        }
    }

    private void OnExpressionKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            CommitCurrentExpression();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            OnClearClicked(sender, e);
            e.Handled = true;
        }
    }

    private void OnKeypadTokenClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string token }) return;
        var box = ActiveExpressionBox;
        var text = box.Text ?? string.Empty;
        int caret = Math.Clamp(box.CaretIndex, 0, text.Length);
        string updated = text.Insert(caret, token);
        box.Text = updated;
        box.CaretIndex = caret + token.Length;
    }

    private void OnSmartParenClicked(object? sender, RoutedEventArgs e)
    {
        var box = ActiveExpressionBox;
        var text = box.Text ?? string.Empty;
        int openCount = text.Count(c => c == '(');
        int closeCount = text.Count(c => c == ')');
        char last = text.TrimEnd().LastOrDefault();
        bool shouldClose = openCount > closeCount && (char.IsDigit(last) || last == ')' || last == 'π' || last == '%');
        string token = shouldClose ? ")" : "(";
        box.Text = text + token;
        box.CaretIndex = box.Text.Length;
    }

    private void OnBackspaceClicked(object? sender, RoutedEventArgs e)
    {
        var box = ActiveExpressionBox;
        var text = box.Text ?? string.Empty;
        if (text.Length == 0) return;
        box.Text = text.Substring(0, text.Length - 1);
        box.CaretIndex = box.Text.Length;
    }

    private void OnNegateClicked(object? sender, RoutedEventArgs e)
    {
        var box = ActiveExpressionBox;
        var text = box.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(text))
        {
            box.Text = "-";
        }
        else if (text.StartsWith("-(") && text.EndsWith(")"))
        {
            box.Text = text.Substring(2, text.Length - 3);
        }
        else if (text.StartsWith("-"))
        {
            box.Text = text.Substring(1);
        }
        else
        {
            box.Text = $"-({text})";
        }
        box.CaretIndex = box.Text.Length;
    }

    private void OnClearClicked(object? sender, RoutedEventArgs e)
    {
        SetActiveExpression(string.Empty, "0");
    }

    private void OnEvaluateClicked(object? sender, RoutedEventArgs e) => CommitCurrentExpression();

    private void CommitCurrentExpression()
    {
        var box = ActiveExpressionBox;
        var expr = box.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(expr)) return;

        var store = CalculatorStore.Instance;
        if (CalculatorEngine.TryEvaluate(expr, store.IsDegrees, store.LastAns, out double val, out string? error))
        {
            var item = store.CommitCalculation(expr, val);
            // In keypad modes (4x2 / 4x4), advance expression to the raw result for chained calculations
            if (currentTier is WidgetTier.Medium or WidgetTier.Large)
            {
                string nextExpr = CalculatorEngine.FormatRawNumber(val, store.Settings.DecimalPlaces);
                suppressTextEvent = true;
                MedExpressionBox.Text = nextExpr;
                LargeExpressionBox.Text = nextExpr;
                MedExpressionBox.CaretIndex = nextExpr.Length;
                LargeExpressionBox.CaretIndex = nextExpr.Length;
                suppressTextEvent = false;
            }
            else
            {
                ShowToast($"= {item.Result}");
            }
        }
        else if (!string.IsNullOrEmpty(error))
        {
            ShowToast(error);
        }
    }

    private void SetActiveExpression(string expr, string result)
    {
        CalculatorStore.Instance.UpdateLiveSession(expr, result, this);
    }

    private void OnAngleToggleClicked(object? sender, RoutedEventArgs e)
    {
        var store = CalculatorStore.Instance;
        store.SetAngleMode(!store.IsDegrees);
        EvaluateLive(ActiveExpressionBox.Text);
    }

    private void OnLargeKeypadModeClicked(object? sender, RoutedEventArgs e)
    {
        LargeKeypadContainer.IsVisible = true;
        LargeHistoryContainer.IsVisible = false;
        LargeKeypadModeBtn.Classes.Set("active", true);
        LargeHistoryModeBtn.Classes.Set("active", false);
    }

    private void OnLargeHistoryModeClicked(object? sender, RoutedEventArgs e)
    {
        LargeKeypadContainer.IsVisible = false;
        LargeHistoryContainer.IsVisible = true;
        LargeKeypadModeBtn.Classes.Set("active", false);
        LargeHistoryModeBtn.Classes.Set("active", true);
    }

    private void OnClearHistoryClicked(object? sender, RoutedEventArgs e)
    {
        CalculatorStore.Instance.ClearHistory();
        ShowToast(IsZh ? "已清空计算历史" : "Calculation history cleared");
    }

    private void OnCopyResultClicked(object? sender, RoutedEventArgs e)
    {
        CopyResultWithToast(CalculatorStore.Instance.CurrentResult);
    }

    private void OnResultPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        CopyResultWithToast(CalculatorStore.Instance.CurrentResult);
    }

    private void CopyResultWithToast(string? result)
    {
        if (string.IsNullOrWhiteSpace(result)) return;
        ClipboardNative.SetText(result);
        ShowToast(Locale.Tools_Calculator_Copied);
    }

    private void ShowToast(string message)
    {
        ToastText.Text = message;
        ToastBanner.IsVisible = true;
        toastTimer.Stop();
        toastTimer.Start();
    }

    // ---- Secondary Panel Launching ----

    private double? SpawnCornerRadius
    {
        get
        {
            var r = (VisualRoot as DeskCanvas.Views.Widget)?.Radius.TopLeft ?? 0;
            return r > 0 ? r : null;
        }
    }

    private void OnTitleOpenPanelPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            OpenSecondaryPanel();
            e.Handled = true;
        }
    }

    public void OnOpenPanelClicked(object? sender, RoutedEventArgs e) => OpenSecondaryPanel();

    private void OpenSecondaryPanel()
    {
        var (screenCenter, _) = GetScreenCenterAndTopLevel();
        var owner = VisualRoot as Window;
        CalculatorPopupWindow.ShowPopup(screenCenter, owner, SpawnCornerRadius, this);
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
}
