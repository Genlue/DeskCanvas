using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using DeskCanvas.Core.Models.Settings;
using DeskCanvas.Core.Services;
using DeskCanvas.Services;
using DeskCanvas.Views;
using Tools.Locales;
using Tools.Models;
using Tools.Services;
using Grid = Avalonia.Controls.Grid;

namespace Tools.Views;

/// <summary>
/// 计算器组件的二级面板（包含科学计算、程序员进制/位运算、常用单位换算及历史/内存管理侧边栏），
/// 继承自 <see cref="SecondaryPanelWindow"/>，自动支持从小组件中心缩放动画、全主题材质适配与失焦收起。
/// </summary>
public partial class CalculatorPopupWindow : SecondaryPanelWindow
{
    private readonly CalculatorView? ownerView;
    private readonly Point? spawnScreenCenter;
    private readonly DispatcherTimer toastTimer;

    private string activeTab = "Scientific";
    private bool showingMemoryInSidebar;
    private bool suppressTextEvent;

    // Programmer mode state
    private int currentProgRadix = 10;
    private long currentProgValue;

    // Converter mode state
    private UnitCategory currentCategory = CalculatorEngine.UnitCategories[0];
    private bool suppressConverterEvent;

    protected override Visual? PanelCard => CardBorder;
    protected override Point? SpawnScreenCenter => spawnScreenCenter;

    public CalculatorPopupWindow() : this(null) { }

    public CalculatorPopupWindow(Point? screenCenter = null, double? cornerRadius = null, CalculatorView? ownerView = null)
    {
        this.ownerView = ownerView;
        spawnScreenCenter = screenCenter;

        InitializeComponent();
        InitializePanel(cornerRadius);

        toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.4) };
        toastTimer.Tick += (_, _) =>
        {
            ToastBanner.IsVisible = false;
            toastTimer.Stop();
        };

        ApplyTheme();
        InitializeConverterUI();

        suppressTextEvent = true;
        SciExpressionBox.Text = CalculatorStore.Instance.CurrentExpression;
        SciResultText.Text = CalculatorStore.Instance.CurrentResult;
        SciAnsHintText.Text = $"Ans = {CalculatorEngine.FormatNumber(CalculatorStore.Instance.LastAns, CalculatorStore.Instance.Settings.DecimalPlaces, CalculatorStore.Instance.Settings.UseThousandsSeparator)}";
        suppressTextEvent = false;

        UpdateAngleButton();
        UpdateProgrammerDisplay();
        RebuildSidebar();

        var defaultTab = CalculatorStore.Instance.Settings.DefaultPanelTab;
        SwitchMainTab(string.IsNullOrWhiteSpace(defaultTab) ? "Scientific" : defaultTab);

        CalculatorStore.Instance.StateChanged += OnStoreStateChanged;
        if (Application.Current != null)
        {
            Application.Current.ActualThemeVariantChanged += OnActualThemeVariantChanged;
        }
        Closed += OnWindowClosed;
    }

    public static void ShowPopup(Point? screenCenter, Window? owner = null, double? cornerRadius = null, CalculatorView? ownerView = null)
    {
        if (PanelCoolingDown<CalculatorPopupWindow>())
            return;

        if (TryCloseActivePanel<CalculatorPopupWindow>())
            return;

        var popup = new CalculatorPopupWindow(screenCenter, cornerRadius, ownerView);
        popup.ShowAsSecondaryPanel(owner);
    }

    protected override void OnPanelLoaded()
    {
        if (LiquidGlassSurfaceControl.IsVisible)
        {
            LiquidGlassSurfaceControl.RequestRender(immediate: true);
        }

        if (activeTab == "Scientific")
        {
            SciExpressionBox.Focus();
            SciExpressionBox.CaretIndex = SciExpressionBox.Text?.Length ?? 0;
        }
        else if (activeTab == "Programmer")
        {
            ProgExpressionBox.Focus();
        }
        else
        {
            ConvInputBox.Focus();
        }
    }

    private void OnActualThemeVariantChanged(object? sender, EventArgs e) => ApplyTheme();

    private void ApplyTheme()
    {
        Theme theme;
        try
        {
            theme = new AppSettingsProvider().Get().Theme;
        }
        catch
        {
            theme = new Theme(DarkMode: true, AccentColor: null, OpacityLevel: 0.8, Monochrome: false, UseNativeFrame: false, FontFamily: "Inter");
        }

        bool isDark = theme.DarkMode ?? ActualThemeVariant == ThemeVariant.Dark;

        if (theme.UsesRenderedGlass)
        {
            TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
            LiquidGlassSurfaceControl.Material = theme;
            LiquidGlassSurfaceControl.CornerRadius = CardBorder.CornerRadius;
            LiquidGlassSurfaceControl.IsVisible = true;
            CardBorder.Background = Brushes.Transparent;
            CardBorder.BorderBrush = new SolidColorBrush(Color.FromArgb(90, 255, 255, 255));
            LiquidGlassSurfaceControl.RequestRender(immediate: true);
        }
        else if (theme.IsColorful)
        {
            LiquidGlassSurfaceControl.IsVisible = false;
            TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
            CardBorder.Background = new SolidColorBrush(isDark ? Color.Parse("#1C1C1E") : Color.Parse("#FFFFFF"));
            CardBorder.BorderBrush = new SolidColorBrush(isDark ? Color.FromArgb(60, 255, 255, 255) : Color.FromArgb(40, 0, 0, 0));
        }
        else if (theme.EffectiveSurface == SurfaceStyle.Solid)
        {
            LiquidGlassSurfaceControl.IsVisible = false;
            TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
            var hex = isDark ? theme.EffectiveSolidBackgroundDark : theme.EffectiveSolidBackgroundLight;
            var baseColor = Color.TryParse(hex, out var parsed) ? parsed : (isDark ? Color.FromRgb(46, 46, 46) : Colors.White);
            byte alpha = (byte)Math.Clamp(Math.Round(theme.OpacityLevel * 255), 40, 255);
            CardBorder.Background = new SolidColorBrush(Color.FromArgb(alpha, baseColor.R, baseColor.G, baseColor.B));
            CardBorder.BorderBrush = new SolidColorBrush(isDark ? Color.FromArgb(40, 255, 255, 255) : Color.FromArgb(30, 0, 0, 0));
        }
        else // Acrylic
        {
            LiquidGlassSurfaceControl.IsVisible = false;
            TransparencyLevelHint = [WindowTransparencyLevel.AcrylicBlur];
            byte alpha = (byte)Math.Clamp(Math.Round(theme.OpacityLevel * 220), 40, 240);
            CardBorder.Background = new SolidColorBrush(isDark ? Color.FromArgb(alpha, 28, 28, 32) : Color.FromArgb(alpha, 245, 245, 248));
            CardBorder.BorderBrush = new SolidColorBrush(isDark ? Color.FromArgb(55, 255, 255, 255) : Color.FromArgb(35, 0, 0, 0));
        }
    }

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        toastTimer.Stop();
        CalculatorStore.Instance.StateChanged -= OnStoreStateChanged;
        if (Application.Current != null)
        {
            Application.Current.ActualThemeVariantChanged -= OnActualThemeVariantChanged;
        }
        LiquidGlassSurfaceControl.IsVisible = false;
    }

    private void OnRootPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed && e.Source is Visual v && (ReferenceEquals(v, this) || v is Panel))
        {
            BeginMoveDrag(e);
        }
    }

    private void OnCloseClicked(object? sender, RoutedEventArgs e) => Close();

    // ---- Main Mode Switching ----

    private void OnTabScientificClicked(object? sender, RoutedEventArgs e) => SwitchMainTab("Scientific");
    private void OnTabProgrammerClicked(object? sender, RoutedEventArgs e) => SwitchMainTab("Programmer");
    private void OnTabConverterClicked(object? sender, RoutedEventArgs e) => SwitchMainTab("Converter");

    private void SwitchMainTab(string tab)
    {
        activeTab = tab;
        bool isSci = tab == "Scientific";
        bool isProg = tab == "Programmer";
        bool isConv = tab == "Converter";

        ScientificPanel.IsVisible = isSci;
        ProgrammerPanel.IsVisible = isProg;
        ConverterPanel.IsVisible = isConv;
        AngleModeBtn.IsVisible = isSci;

        TabScientificBtn.Classes.Set("active", isSci);
        TabProgrammerBtn.Classes.Set("active", isProg);
        TabConverterBtn.Classes.Set("active", isConv);
    }

    private void OnAngleModeClicked(object? sender, RoutedEventArgs e)
    {
        CalculatorStore.Instance.SetAngleMode(!CalculatorStore.Instance.IsDegrees);
        UpdateAngleButton();
        EvaluateSciLive();
    }

    private void UpdateAngleButton()
    {
        AngleModeBtn.Content = CalculatorStore.Instance.IsDegrees ? "DEG" : "RAD";
    }

    // ---- 1. Scientific Calculator Handlers ----

    private void OnSciExpressionTextChanged(object? sender, TextChangedEventArgs e)
    {
        if (suppressTextEvent) return;
        EvaluateSciLive();
    }

    private void EvaluateSciLive()
    {
        var expr = SciExpressionBox.Text ?? string.Empty;
        var settings = CalculatorStore.Instance.Settings;
        if (string.IsNullOrWhiteSpace(expr))
        {
            SciResultText.Text = "0";
            CalculatorStore.Instance.UpdateLiveSession(string.Empty, "0", this);
            return;
        }

        if (CalculatorEngine.TryEvaluate(expr, CalculatorStore.Instance.IsDegrees, CalculatorStore.Instance.LastAns, out double val, out _))
        {
            string formatted = CalculatorEngine.FormatNumber(val, settings.DecimalPlaces, settings.UseThousandsSeparator);
            SciResultText.Text = formatted;
            CalculatorStore.Instance.UpdateLiveSession(expr, formatted, this);
        }
    }

    private void OnSciExpressionKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            CommitSciCalculation();
            e.Handled = true;
        }
    }

    private void OnSciTokenClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string token }) return;
        InsertIntoTextBox(SciExpressionBox, token);
    }

    private void OnSciBackspaceClicked(object? sender, RoutedEventArgs e)
    {
        var text = SciExpressionBox.Text ?? string.Empty;
        if (text.Length == 0) return;
        SciExpressionBox.Text = text.Substring(0, text.Length - 1);
        SciExpressionBox.CaretIndex = SciExpressionBox.Text.Length;
    }

    private void OnClearSciClicked(object? sender, RoutedEventArgs e)
    {
        SciExpressionBox.Text = string.Empty;
        SciResultText.Text = "0";
        CalculatorStore.Instance.UpdateLiveSession(string.Empty, "0", this);
    }

    private void OnSciEvaluateClicked(object? sender, RoutedEventArgs e) => CommitSciCalculation();

    private void CommitSciCalculation()
    {
        var expr = SciExpressionBox.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(expr)) return;

        if (CalculatorEngine.TryEvaluate(expr, CalculatorStore.Instance.IsDegrees, CalculatorStore.Instance.LastAns, out double val, out string? error))
        {
            var item = CalculatorStore.Instance.CommitCalculation(expr, val);
            suppressTextEvent = true;
            SciResultText.Text = item.Result;
            SciAnsHintText.Text = $"Ans = {item.Result}";
            suppressTextEvent = false;
        }
        else if (!string.IsNullOrEmpty(error))
        {
            ShowToast(error);
        }
    }

    private void OnCopySciResultClicked(object? sender, RoutedEventArgs e) => CopyTextWithToast(SciResultText.Text);
    private void OnSciResultPointerPressed(object? sender, PointerPressedEventArgs e) => CopyTextWithToast(SciResultText.Text);

    // ---- Memory Handlers ----

    private bool TryGetCurrentSciDouble(out double value)
    {
        var expr = SciExpressionBox.Text;
        if (!string.IsNullOrWhiteSpace(expr) &&
            CalculatorEngine.TryEvaluate(expr, CalculatorStore.Instance.IsDegrees, CalculatorStore.Instance.LastAns, out value, out _))
        {
            return true;
        }
        var raw = (SciResultText.Text ?? "0").Replace(",", "");
        return double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    private static bool IsZh => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.Equals("zh", StringComparison.OrdinalIgnoreCase);

    private void OnMemClearClicked(object? sender, RoutedEventArgs e)
    {
        CalculatorStore.Instance.MemoryClear();
        ShowToast(IsZh ? "已清空内存" : "Memory cleared");
    }

    private void OnMemRecallClicked(object? sender, RoutedEventArgs e)
    {
        var top = CalculatorStore.Instance.TopMemory;
        if (top.HasValue)
        {
            InsertIntoTextBox(SciExpressionBox, CalculatorEngine.FormatRawNumber(top.Value, CalculatorStore.Instance.Settings.DecimalPlaces));
        }
        else
        {
            ShowToast(IsZh ? "内存为空" : "Memory is empty");
        }
    }

    private void OnMemAddClicked(object? sender, RoutedEventArgs e)
    {
        if (TryGetCurrentSciDouble(out double v))
        {
            CalculatorStore.Instance.MemoryAdd(v);
            ShowToast($"M+ ({CalculatorEngine.FormatNumber(v, 4)})");
        }
    }

    private void OnMemSubClicked(object? sender, RoutedEventArgs e)
    {
        if (TryGetCurrentSciDouble(out double v))
        {
            CalculatorStore.Instance.MemoryAdd(-v);
            ShowToast($"M− ({CalculatorEngine.FormatNumber(v, 4)})");
        }
    }

    private void OnMemStoreClicked(object? sender, RoutedEventArgs e)
    {
        if (TryGetCurrentSciDouble(out double v))
        {
            CalculatorStore.Instance.MemoryStore(v);
            ShowToast(IsZh ? $"已存入内存: {CalculatorEngine.FormatNumber(v, 4)}" : $"Stored in memory: {CalculatorEngine.FormatNumber(v, 4)}");
        }
    }

    // ---- 2. Programmer Mode Handlers ----

    private void OnBaseHexPressed(object? sender, PointerPressedEventArgs e) => SetProgrammerRadix(16);
    private void OnBaseDecPressed(object? sender, PointerPressedEventArgs e) => SetProgrammerRadix(10);
    private void OnBaseOctPressed(object? sender, PointerPressedEventArgs e) => SetProgrammerRadix(8);
    private void OnBaseBinPressed(object? sender, PointerPressedEventArgs e) => SetProgrammerRadix(2);

    private void SetProgrammerRadix(int newRadix)
    {
        if (currentProgRadix == newRadix) return;
        currentProgRadix = newRadix;

        BaseHexRow.Classes.Set("active", newRadix == 16);
        BaseDecRow.Classes.Set("active", newRadix == 10);
        BaseOctRow.Classes.Set("active", newRadix == 8);
        BaseBinRow.Classes.Set("active", newRadix == 2);

        // Convert current value into the new radix in the input box
        ProgExpressionBox.Text = Convert.ToString(currentProgValue, newRadix).ToUpperInvariant();
        ProgExpressionBox.CaretIndex = ProgExpressionBox.Text.Length;

        UpdateProgrammerKeypadAvailability();
    }

    private void UpdateProgrammerKeypadAvailability()
    {
        bool allowHex = currentProgRadix == 16;
        bool allowDec = currentProgRadix >= 10;
        bool allowOct = currentProgRadix >= 8;

        HexBtnA.IsEnabled = allowHex;
        HexBtnB.IsEnabled = allowHex;
        HexBtnC.IsEnabled = allowHex;
        HexBtnD.IsEnabled = allowHex;
        HexBtnE.IsEnabled = allowHex;
        HexBtnF.IsEnabled = allowHex;

        ProgBtn8.IsEnabled = allowDec;
        ProgBtn9.IsEnabled = allowDec;

        ProgBtn2.IsEnabled = allowOct;
        ProgBtn3.IsEnabled = allowOct;
        ProgBtn4.IsEnabled = allowOct;
        ProgBtn5.IsEnabled = allowOct;
        ProgBtn6.IsEnabled = allowOct;
        ProgBtn7.IsEnabled = allowOct;
    }

    private void OnProgExpressionTextChanged(object? sender, TextChangedEventArgs e)
    {
        var expr = ProgExpressionBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(expr))
        {
            currentProgValue = 0;
            UpdateProgrammerDisplay();
            return;
        }

        if (CalculatorEngine.TryEvaluateProgrammer(expr, currentProgRadix, out long val))
        {
            currentProgValue = val;
            UpdateProgrammerDisplay();
        }
    }

    private void OnProgExpressionKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            OnProgEvaluateClicked(sender, e);
            e.Handled = true;
        }
    }

    private void UpdateProgrammerDisplay()
    {
        ProgHexText.Text = CalculatorEngine.FormatBase(currentProgValue, 16);
        ProgDecText.Text = CalculatorEngine.FormatBase(currentProgValue, 10);
        ProgOctText.Text = CalculatorEngine.FormatBase(currentProgValue, 8);
        ProgBinText.Text = CalculatorEngine.FormatBase(currentProgValue, 2);
        UpdateProgrammerKeypadAvailability();
    }

    private void OnProgTokenClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string token }) return;
        InsertIntoTextBox(ProgExpressionBox, token);
    }

    private void OnProgClearClicked(object? sender, RoutedEventArgs e)
    {
        ProgExpressionBox.Text = string.Empty;
        currentProgValue = 0;
        UpdateProgrammerDisplay();
    }

    private void OnProgBackspaceClicked(object? sender, RoutedEventArgs e)
    {
        var text = ProgExpressionBox.Text ?? string.Empty;
        if (text.Length == 0) return;
        ProgExpressionBox.Text = text.Substring(0, text.Length - 1);
    }

    private void OnProgEvaluateClicked(object? sender, RoutedEventArgs e)
    {
        var expr = ProgExpressionBox.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(expr)) return;

        if (CalculatorEngine.TryEvaluateProgrammer(expr, currentProgRadix, out long val))
        {
            currentProgValue = val;
            UpdateProgrammerDisplay();
            string formattedInBase = Convert.ToString(val, currentProgRadix).ToUpperInvariant();
            CalculatorStore.Instance.CommitCalculation($"[{GetRadixName(currentProgRadix)}] {expr}", val);
            ProgExpressionBox.Text = formattedInBase;
            ProgExpressionBox.CaretIndex = formattedInBase.Length;
        }
    }

    private static string GetRadixName(int r) => r switch
    {
        16 => "HEX",
        8 => "OCT",
        2 => "BIN",
        _ => "DEC"
    };

    private void OnCopyHexClicked(object? sender, RoutedEventArgs e) => CopyTextWithToast(ProgHexText.Text?.Replace(" ", ""));
    private void OnCopyDecClicked(object? sender, RoutedEventArgs e) => CopyTextWithToast(ProgDecText.Text?.Replace(",", ""));
    private void OnCopyOctClicked(object? sender, RoutedEventArgs e) => CopyTextWithToast(ProgOctText.Text?.Replace(" ", ""));
    private void OnCopyBinClicked(object? sender, RoutedEventArgs e) => CopyTextWithToast(ProgBinText.Text?.Replace(" ", ""));

    // ---- 3. Unit Converter Handlers ----

    private void InitializeConverterUI()
    {
        ConverterCategoriesPanel.Children.Clear();
        foreach (var cat in CalculatorEngine.UnitCategories)
        {
            var btn = new Button
            {
                Content = cat.Name,
                Tag = cat,
                Margin = new Thickness(0, 0, 5, 4)
            };
            btn.Classes.Add("cat-pill");
            if (cat == currentCategory) btn.Classes.Add("active");
            btn.Click += OnConverterCategoryClicked;
            ConverterCategoriesPanel.Children.Add(btn);
        }

        PopulateCategoryUnits(currentCategory);
    }

    private void OnConverterCategoryClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: UnitCategory cat }) return;
        currentCategory = cat;

        foreach (var child in ConverterCategoriesPanel.Children.OfType<Button>())
        {
            child.Classes.Set("active", ReferenceEquals(child.Tag, cat));
        }

        PopulateCategoryUnits(cat);
    }

    private void PopulateCategoryUnits(UnitCategory cat)
    {
        suppressConverterEvent = true;
        ConvFromCombo.ItemsSource = cat.Units;
        ConvToCombo.ItemsSource = cat.Units;
        ConvFromCombo.SelectedIndex = cat.Units.Count > 2 ? 2 : 0;
        ConvToCombo.SelectedIndex = cat.Units.Count > 1 ? 1 : 0;
        suppressConverterEvent = false;

        RecalculateConversion();
    }

    private void OnConverterUnitChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (suppressConverterEvent) return;
        RecalculateConversion();
    }

    private void OnConverterInputChanged(object? sender, TextChangedEventArgs e)
    {
        if (suppressConverterEvent) return;
        RecalculateConversion();
    }

    private void OnConverterSwapClicked(object? sender, RoutedEventArgs e)
    {
        suppressConverterEvent = true;
        (ConvFromCombo.SelectedItem, ConvToCombo.SelectedItem) = (ConvToCombo.SelectedItem, ConvFromCombo.SelectedItem);
        suppressConverterEvent = false;
        RecalculateConversion();
    }

    private void OnConverterMul10Clicked(object? sender, RoutedEventArgs e)
    {
        if (double.TryParse(ConvInputBox.Text?.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double v))
        {
            ConvInputBox.Text = CalculatorEngine.FormatRawNumber(v * 10, 8);
        }
    }

    private void OnConverterResetClicked(object? sender, RoutedEventArgs e)
    {
        ConvInputBox.Text = "1";
    }

    private void RecalculateConversion()
    {
        if (ConvFromCombo.SelectedItem is not UnitDefinition fromUnit ||
            ConvToCombo.SelectedItem is not UnitDefinition toUnit)
            return;

        var raw = (ConvInputBox.Text ?? "0").Replace(",", "").Trim();
        if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double inputVal))
        {
            if (!CalculatorEngine.TryEvaluate(raw, true, 0, out inputVal, out _))
            {
                ConvResultText.Text = "—";
                return;
            }
        }

        double baseVal = fromUnit.ToBase(inputVal);
        double converted = toUnit.FromBase(baseVal);
        var settings = CalculatorStore.Instance.Settings;
        string formatted = CalculatorEngine.FormatNumber(converted, Math.Max(settings.DecimalPlaces, 6), settings.UseThousandsSeparator);
        ConvResultText.Text = $"{formatted} {toUnit.Symbol}";

        // Rebuild all-units overview
        ConvAllUnitsList.Children.Clear();
        foreach (var unit in currentCategory.Units)
        {
            double val = unit.FromBase(baseVal);
            string valStr = CalculatorEngine.FormatNumber(val, Math.Max(settings.DecimalPlaces, 6), settings.UseThousandsSeparator);

            var row = new Border
            {
                Classes = { "base-row" },
                Padding = new Thickness(8, 4),
                Tag = valStr
            };
            row.PointerPressed += (_, _) => CopyTextWithToast(valStr);

            var g = new Grid { ColumnDefinitions = ColumnDefinitions.Parse("*,Auto") };
            g.Children.Add(new TextBlock
            {
                Text = $"{unit.Name} ({unit.Symbol})",
                FontSize = 11.5,
                VerticalAlignment = VerticalAlignment.Center
            });
            var rightText = new TextBlock
            {
                Text = valStr,
                FontSize = 12,
                FontWeight = FontWeight.SemiBold,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(rightText, 1);
            g.Children.Add(rightText);

            row.Child = g;
            ConvAllUnitsList.Children.Add(row);
        }
    }

    private void OnCopyConverterResultClicked(object? sender, RoutedEventArgs e) => CopyConverterValue();
    private void OnCopyConverterResultPointerPressed(object? sender, PointerPressedEventArgs e) => CopyConverterValue();

    private void CopyConverterValue()
    {
        var text = ConvResultText.Text ?? string.Empty;
        int spaceIdx = text.LastIndexOf(' ');
        if (spaceIdx > 0) text = text.Substring(0, spaceIdx);
        CopyTextWithToast(text);
    }

    // ---- Right Sidebar: History & Memory ----

    private void OnSidebarHistoryTabClicked(object? sender, RoutedEventArgs e)
    {
        showingMemoryInSidebar = false;
        SidebarHistoryTabBtn.Classes.Set("active", true);
        SidebarMemoryTabBtn.Classes.Set("active", false);
        RebuildSidebar();
    }

    private void OnSidebarMemoryTabClicked(object? sender, RoutedEventArgs e)
    {
        showingMemoryInSidebar = true;
        SidebarHistoryTabBtn.Classes.Set("active", false);
        SidebarMemoryTabBtn.Classes.Set("active", true);
        RebuildSidebar();
    }

    private void OnClearSidebarClicked(object? sender, RoutedEventArgs e)
    {
        if (showingMemoryInSidebar)
        {
            CalculatorStore.Instance.MemoryClear();
            ShowToast(IsZh ? "已清空内存" : "Memory cleared");
        }
        else
        {
            CalculatorStore.Instance.ClearHistory();
            ShowToast(IsZh ? "已清空计算历史" : "Calculation history cleared");
        }
    }

    private void OnStoreStateChanged()
    {
        suppressTextEvent = true;
        if (!SciExpressionBox.IsFocused)
        {
            SciExpressionBox.Text = CalculatorStore.Instance.CurrentExpression;
        }
        SciResultText.Text = CalculatorStore.Instance.CurrentResult;
        SciAnsHintText.Text = $"Ans = {CalculatorEngine.FormatNumber(CalculatorStore.Instance.LastAns, CalculatorStore.Instance.Settings.DecimalPlaces, CalculatorStore.Instance.Settings.UseThousandsSeparator)}";
        suppressTextEvent = false;

        UpdateAngleButton();
        RebuildSidebar();
    }

    private void RebuildSidebar()
    {
        SidebarItemsPanel.Children.Clear();
        var settings = CalculatorStore.Instance.Settings;

        if (showingMemoryInSidebar)
        {
            var slots = CalculatorStore.Instance.GetMemorySlots();
            SidebarEmptyText.Text = IsZh ? "暂无内存数据 (使用 MS / M+ 存入)" : "No memory values (use MS / M+ to store)";
            SidebarEmptyText.IsVisible = slots.Count == 0;

            for (int i = 0; i < slots.Count; i++)
            {
                double val = slots[i];
                string formatted = CalculatorEngine.FormatNumber(val, settings.DecimalPlaces, settings.UseThousandsSeparator);
                var card = new Border { Classes = { "history-item" } };
                var grid = new Grid { ColumnDefinitions = ColumnDefinitions.Parse("Auto,*,Auto") };

                grid.Children.Add(new TextBlock
                {
                    Text = $"M{i + 1}",
                    FontSize = 11,
                    FontWeight = FontWeight.Bold,
                    VerticalAlignment = VerticalAlignment.Center
                });

                var valText = new TextBlock
                {
                    Text = formatted,
                    FontSize = 14,
                    FontWeight = FontWeight.Bold,
                    TextAlignment = TextAlignment.Right,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(6, 0)
                };
                Grid.SetColumn(valText, 1);
                grid.Children.Add(valText);

                var copyBtn = new Button
                {
                    Classes = { "icon-btn" },
                    Width = 22,
                    Height = 22,
                    Content = "📋"
                };
                copyBtn.Click += (_, ev) =>
                {
                    CopyTextWithToast(formatted);
                    ev.Handled = true;
                };
                Grid.SetColumn(copyBtn, 2);
                grid.Children.Add(copyBtn);

                card.Child = grid;
                card.PointerPressed += (_, _) =>
                {
                    InsertIntoTextBox(SciExpressionBox, CalculatorEngine.FormatRawNumber(val, settings.DecimalPlaces));
                };
                SidebarItemsPanel.Children.Add(card);
            }
            return;
        }

        var items = CalculatorStore.Instance.GetHistory();
        SidebarEmptyText.Text = Locale.Tools_Calculator_EmptyHistory;
        SidebarEmptyText.IsVisible = items.Count == 0;

        foreach (var item in items)
        {
            var card = new Border { Classes = { "history-item" } };
            var grid = new Grid { RowDefinitions = RowDefinitions.Parse("Auto,Auto"), ColumnDefinitions = ColumnDefinitions.Parse("*,Auto") };

            var exprText = new TextBlock
            {
                Text = $"{item.Expression} =",
                FontSize = 11,
                Opacity = 0.65,
                TextAlignment = TextAlignment.Right,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            Grid.SetRow(exprText, 0);
            Grid.SetColumn(exprText, 0);
            grid.Children.Add(exprText);

            var resText = new TextBlock
            {
                Text = item.Result,
                FontSize = 14.5,
                FontWeight = FontWeight.Bold,
                TextAlignment = TextAlignment.Right,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            Grid.SetRow(resText, 1);
            Grid.SetColumn(resText, 0);
            grid.Children.Add(resText);

            var actions = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 2,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(6, 0, 0, 0)
            };
            var copyBtn = new Button
            {
                Classes = { "icon-btn" },
                Width = 22,
                Height = 22,
                Content = "📋"
            };
            ToolTip.SetTip(copyBtn, Locale.Tools_Calculator_Copied);
            copyBtn.Click += (_, ev) =>
            {
                CopyTextWithToast(item.Result);
                ev.Handled = true;
            };
            var delBtn = new Button
            {
                Classes = { "icon-btn" },
                Width = 22,
                Height = 22,
                Content = "✕",
                Opacity = 0.55
            };
            delBtn.Click += (_, ev) =>
            {
                CalculatorStore.Instance.DeleteHistoryItem(item);
                ev.Handled = true;
            };
            actions.Children.Add(copyBtn);
            actions.Children.Add(delBtn);
            Grid.SetRowSpan(actions, 2);
            Grid.SetColumn(actions, 1);
            grid.Children.Add(actions);

            card.Child = grid;
            card.PointerPressed += (_, _) =>
            {
                if (CalculatorStore.Instance.Settings.ClickHistoryToCopy)
                {
                    CopyTextWithToast(item.Result);
                }
                else
                {
                    SciExpressionBox.Text = item.Expression;
                    SciExpressionBox.CaretIndex = SciExpressionBox.Text.Length;
                    SciResultText.Text = item.Result;
                    CalculatorStore.Instance.UpdateLiveSession(item.Expression, item.Result, this);
                }
            };
            SidebarItemsPanel.Children.Add(card);
        }
    }

    private static void InsertIntoTextBox(TextBox box, string token)
    {
        var current = box.Text ?? string.Empty;
        int caret = Math.Clamp(box.CaretIndex, 0, current.Length);
        box.Text = current.Insert(caret, token);
        box.CaretIndex = caret + token.Length;
        box.Focus();
    }

    private void CopyTextWithToast(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        ClipboardNative.SetText(text);
        ShowToast(Locale.Tools_Calculator_Copied);
    }

    private void ShowToast(string message)
    {
        ToastText.Text = message;
        ToastBanner.IsVisible = true;
        toastTimer.Stop();
        toastTimer.Start();
    }
}
